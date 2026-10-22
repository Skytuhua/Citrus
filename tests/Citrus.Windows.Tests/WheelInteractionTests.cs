using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using Citrus.Core;
using Citrus.Windows.Presentation;
using Forms = System.Windows.Forms;
using DataObject = System.Windows.DataObject;
using IDataObject = System.Windows.IDataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using DataFormats = System.Windows.DataFormats;
using Point = System.Windows.Point;

namespace Citrus.Windows.Tests;

/// <summary>Real WPF windows and FileDrop data on an STA. Routed callbacks exercise the target;
/// native Explorer/OLE initiation, HWND hit testing and hook ordering require Windows acceptance.</summary>
[Collection("Desktop")]
public sealed class WheelInteractionTests(DesktopFixture desktop)
{
    [Theory]
    [InlineData(ModeKey.Shift, OperationMode.Convert)]
    [InlineData(ModeKey.Control, OperationMode.Actions)]
    [InlineData(ModeKey.Alt, OperationMode.Tools)]
    public Task Preheld_key_dispatches_on_the_first_drag_movement_without_waiting(ModeKey key, OperationMode expected) => desktop.RunAsync(async () =>
    {
        using var monitor = new GestureMonitor(Dispatcher.CurrentDispatcher);
        ((ModifierObserver)typeof(GestureMonitor).GetField("modifiers",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(monitor)!).Reset();
        var state = (GestureState)typeof(GestureMonitor).GetField("state",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(monitor)!;
        state.KeyDown(key,Environment.TickCount64-10000);
        var modes = new List<OperationMode>(); monitor.ModeRequested += modes.Add;
        MouseMessage(monitor,0x0201);
        MouseMessage(monitor,0x0200,x:NativeMethods.GetSystemMetrics(68)+1);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(expected,Assert.Single(modes));
        MouseMessage(monitor,0x0200,x:NativeMethods.GetSystemMetrics(68)+20);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Single(modes);
    });

    [Fact]
    public Task Petal_gap_keeps_the_capsule_target_without_decoding_a_thumbnail_until_center_cancel() => desktop.RunAsync(async () =>
    {
        var preview = new DeferredPreview();
        using var fixture = new WheelFixture(preview, new CountingEngine(3));
        await fixture.Enter();
        fixture.Raise(DragDrop.PreviewDragOverEvent, fixture.Data, new Point(184,62));
        string title = ((TextBlock)fixture.Wheel.FindName("Caption")).Text;
        var gap = WheelGeometry.At(118,60);
        Assert.Equal(-1, WheelGeometry.HitTest(gap.X,gap.Y,3));
        Assert.Equal(DragDropEffects.Copy, fixture.Raise(DragDrop.PreviewDragOverEvent,fixture.Data,new Point(gap.X,gap.Y)).Effects);
        Assert.Equal(title,((TextBlock)fixture.Wheel.FindName("Caption")).Text);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Empty(preview.Requests);
        foreach (string name in new[] { "Preview", "FileGlyph", "ThumbnailFrame", "CountBadge" }) Assert.Null(fixture.Wheel.FindName(name));
        Assert.Equal(title,((TextBlock)fixture.Wheel.FindName("Caption")).Text);
        fixture.Raise(DragDrop.PreviewDragOverEvent,fixture.Data,new Point(184,184));
        var capsule = (Border)fixture.Wheel.FindName("CaptionPlate");
        Assert.Equal(160, Canvas.GetTop(capsule));
        Assert.Equal(48, capsule.Height);
        Assert.Equal(WheelPresentationMetrics.HubCapsuleWidth, capsule.Width);
        Assert.Empty(preview.Requests);
        Assert.Null(fixture.Wheel.FindName("ActionDescription"));
        Assert.Equal(-1, typeof(WheelWindow).GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Wheel));
    });

    [Fact]
    public Task Release_in_petal_gap_accepts_the_still_highlighted_target() => desktop.RunAsync(async () =>
    {
        using var fixture = new WheelFixture(engine:new CountingEngine(3));
        await fixture.Enter();
        fixture.Raise(DragDrop.PreviewDragOverEvent,fixture.Data,new Point(184,62));
        var drops = new List<OperationDrop>(); fixture.Wheel.OperationDropped += drops.Add;
        var gap = WheelGeometry.At(118,60);
        Assert.Equal(DragDropEffects.Copy,fixture.Raise(DragDrop.PreviewDropEvent,fixture.Data,new Point(gap.X,gap.Y)).Effects);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("Convert.0",Assert.Single(drops).Operation.Id);
    });

    [Fact]
    public Task Native_wheel_messages_page_the_admitted_menu_and_clear_the_old_target() => desktop.RunAsync(async () =>
    {
        using var fixture = new WheelFixture(engine:new CountingEngine(20));
        using var monitor = new GestureMonitor(Dispatcher.CurrentDispatcher);
        var scroll = typeof(GestureMonitor).GetEvent("WheelScrolled"); Assert.NotNull(scroll);
        scroll.AddEventHandler(monitor,(Action<int>)(delta => ObserveScroll(fixture.Wheel,delta)));
        await fixture.Enter();
        fixture.Raise(DragDrop.PreviewDragOverEvent,fixture.Data,new Point(184,62));
        MouseMessage(monitor,0x020A,-60);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("1 / 3",((TextBlock)fixture.Wheel.FindName("Hint")).Text);
        MouseMessage(monitor,0x020A,-60);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("2 / 3",((TextBlock)fixture.Wheel.FindName("Hint")).Text);
        Assert.Null(fixture.Wheel.FindName("ActionDescription"));
        Assert.Equal(-1, typeof(WheelWindow).GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Wheel));
        MouseMessage(monitor,0x020A,-1200);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("3 / 3",((TextBlock)fixture.Wheel.FindName("Hint")).Text);
        MouseMessage(monitor,0x020A,120);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("2 / 3",((TextBlock)fixture.Wheel.FindName("Hint")).Text);
    });

    [Fact]
    public Task Queued_scroll_cannot_page_a_reopened_or_changed_mode_menu() => desktop.RunAsync(async () =>
    {
        using var fixture = new WheelFixture(engine:new CountingEngine(20));
        await fixture.Enter();
        ObserveScroll(fixture.Wheel,-120);
        fixture.Wheel.ShowAtPointer(OperationMode.Tools);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("1 / 3",((TextBlock)fixture.Wheel.FindName("Hint")).Text);
        ObserveScroll(fixture.Wheel,-60);
        fixture.Wheel.Dismiss(); await fixture.Enter();
        ObserveScroll(fixture.Wheel,-60);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("1 / 3",((TextBlock)fixture.Wheel.FindName("Hint")).Text);
    });

    [Fact]
    public Task Right_button_dismisses_capture_without_executing_or_reopening() => desktop.RunAsync(async () =>
    {
        using var fixture = new WheelFixture();
        using var monitor = new GestureMonitor(Dispatcher.CurrentDispatcher);
        monitor.RightButtonCancellationRequested += fixture.Wheel.TryObserveCancellation;
        await fixture.Enter();
        fixture.Raise(DragDrop.PreviewDragOverEvent,fixture.Data,new Point(184,62));
        var capture = (WheelDragCapture)typeof(WheelWindow).GetField("outsideCapture",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(fixture.Wheel)!;
        var drops = new List<OperationDrop>(); fixture.Wheel.OperationDropped += drops.Add;
        MouseMessage(monitor,0x0204);
        // Explorer/OLE can deliver Drop before the queued cancellation callback runs.
        Assert.Equal(DragDropEffects.None,fixture.Raise(DragDrop.PreviewDropEvent,fixture.Data,new Point(184,62)).Effects);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(fixture.Wheel.IsVisible); Assert.False(capture.IsVisible);
        Assert.Empty(drops);
    });

    [Fact]
    public Task Cancel_drain_blocks_a_new_drag_until_terminal_drop_and_old_cleanup_cannot_hide_replacement() => desktop.RunAsync(async () =>
    {
        using var fixture = new WheelFixture();
        using var monitor = new GestureMonitor(Dispatcher.CurrentDispatcher);
        monitor.RightButtonCancellationRequested += fixture.Wheel.TryObserveCancellation;
        await fixture.Enter();
        MouseMessage(monitor,0x0204);
        fixture.Wheel.ShowAtPointer(OperationMode.Convert);
        Assert.Equal(DragDropEffects.None,fixture.Raise(DragDrop.PreviewDragEnterEvent,fixture.Data).Effects);
        Assert.Equal(0,((Canvas)fixture.Wheel.FindName("Presentation")).Opacity);
        fixture.Raise(DragDrop.PreviewDropEvent,fixture.Data);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(fixture.Wheel.IsVisible);
        await fixture.Enter();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.True(fixture.Wheel.IsVisible);
    });

    private static void ObserveScroll(WheelWindow wheel,int delta)
    {
        var method = typeof(WheelWindow).GetMethod("ObserveScroll"); Assert.NotNull(method);
        method.Invoke(wheel,[delta]);
    }

    private static void MouseMessage(GestureMonitor monitor,int message,short delta=0,int x=0,int y=0)
    {
        var pointer = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MouseData>());
        try
        {
            System.Runtime.InteropServices.Marshal.StructureToPtr(new NativeMethods.MouseData {Mouse=(uint)(ushort)delta<<16,Point=new NativeMethods.Point {X=x,Y=y}},pointer,false);
            typeof(GestureMonitor).GetMethod("Mouse",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(monitor,[0,(nint)message,pointer]);
        }
        finally {System.Runtime.InteropServices.Marshal.FreeHGlobal(pointer);}
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Selected_action_survives_the_outer_rim_and_outside_drop_revalidates_files(bool sourceRemoved) => desktop.RunAsync(async () =>
    {
        using var fixture = new WheelFixture();
        await fixture.Enter();
        fixture.Raise(DragDrop.PreviewDragOverEvent, fixture.Data, new Point(184, 62));
        string title = ((TextBlock)fixture.Wheel.FindName("Caption")).Text;
        var outside = new Point(-180, -180);
        Assert.Equal(DragDropEffects.Copy, fixture.Raise(DragDrop.PreviewDragOverEvent, fixture.Data, outside).Effects);
        Assert.Equal(title, ((TextBlock)fixture.Wheel.FindName("Caption")).Text);
        Assert.True(fixture.Wheel.IsVisible);
        if (sourceRemoved) File.Delete(fixture.A);
        var drops = new List<OperationDrop>(); fixture.Wheel.OperationDropped += drops.Add;
        Assert.Equal(sourceRemoved ? DragDropEffects.None : DragDropEffects.Copy, fixture.Raise(DragDrop.PreviewDropEvent, fixture.Data, outside).Effects);
        Assert.False(fixture.Wheel.IsVisible);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (sourceRemoved) Assert.Empty(drops);
        else Assert.Equal(new[] { fixture.A, fixture.B }, Assert.Single(drops).Paths);
    });

    [Fact]
    public Task Confirmed_wheel_keeps_an_owned_nonactivating_outside_drop_target_until_teardown() => desktop.RunAsync(async () =>
    {
        using var fixture = new WheelFixture();
        fixture.Wheel.ShowAtPointer(OperationMode.Convert);
        var captureField = typeof(WheelWindow).GetField("outsideCapture", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(captureField);
        Assert.Null(captureField.GetValue(fixture.Wheel));
        fixture.Raise(DragDrop.PreviewDragEnterEvent, fixture.Data);
        await fixture.Ready();
        var capture = Assert.IsType<WheelDragCapture>(captureField.GetValue(fixture.Wheel));
        Assert.True(capture.IsVisible); Assert.Same(fixture.Wheel, capture.Owner);
        Assert.NotEqual(0, NativeMethods.GetWindowLongPtr(capture.Handle, -20).ToInt64() & 0x08000000);
        Assert.True(capture.AllowDrop);
        fixture.Raise(DragDrop.PreviewDragLeaveEvent, fixture.Data);
        Assert.True(fixture.Wheel.IsVisible); Assert.True(capture.IsVisible);
        fixture.Wheel.Dismiss();
        Assert.False(capture.IsVisible); Assert.Null(captureField.GetValue(fixture.Wheel));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Desktop_capture_forwards_real_drop_arguments_and_center_cancel_is_preserved(bool cancelInCenter) => desktop.RunAsync(async () =>
    {
        using var fixture = new WheelFixture();
        await fixture.Enter();
        fixture.Raise(DragDrop.PreviewDragOverEvent, fixture.Data, new Point(184, 62));
        var capture = Assert.IsType<WheelDragCapture>(typeof(WheelWindow).GetField("outsideCapture", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fixture.Wheel));
        var hwnd = capture.Handle;
        Assert.True(Citrus.Windows.Presentation.BackdropNativeMethods.GetWindowRect(hwnd, out var bounds));
        Assert.Equal(NativeMethods.GetSystemMetrics(76), bounds.Left);
        Assert.Equal(NativeMethods.GetSystemMetrics(77), bounds.Top);
        Assert.Equal(NativeMethods.GetSystemMetrics(78), bounds.Right - bounds.Left);
        Assert.Equal(NativeMethods.GetSystemMetrics(79), bounds.Bottom - bounds.Top);
        if (cancelInCenter) fixture.Raise(DragDrop.PreviewDragOverEvent, fixture.Data, new Point(184, 184));
        var center = fixture.Surface.PointToScreen(new Point(184, 184));
        var corner = new[] { new Point(bounds.Left + 4, bounds.Top + 4), new Point(bounds.Right - 4, bounds.Top + 4),
            new Point(bounds.Left + 4, bounds.Bottom - 4), new Point(bounds.Right - 4, bounds.Bottom - 4) }
            .OrderByDescending(point => (point - center).LengthSquared).First();
        var dropped = new List<OperationDrop>(); fixture.Wheel.OperationDropped += dropped.Add;
        var data = new Forms.DataObject(Forms.DataFormats.FileDrop, new[] { fixture.A, fixture.B });
        var args = new Forms.DragEventArgs(data, 1, (int)corner.X, (int)corner.Y,
            Forms.DragDropEffects.Copy, Forms.DragDropEffects.None);
        capture.Dispatch(WheelDragEventKind.Drop, WheelDragInput.FromForms(args, Dispatcher.CurrentDispatcher));
        Assert.Equal(cancelInCenter ? Forms.DragDropEffects.None : Forms.DragDropEffects.Copy, args.Effect);
        Assert.False(capture.IsVisible); Assert.False(fixture.Wheel.IsVisible);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (cancelInCenter) Assert.Empty(dropped); else Assert.Single(dropped);
    });

    [Fact]
    public Task Aborted_global_release_closes_capture_without_submitting() => desktop.RunAsync(async () =>
    {
        using var fixture = new WheelFixture();
        await fixture.Enter();
        var dropped = new List<OperationDrop>(); fixture.Wheel.OperationDropped += dropped.Add;
        fixture.Raise(DragDrop.PreviewDragOverEvent, fixture.Data, new Point(184, 62));
        var hidden = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        { if (!fixture.Wheel.IsVisible) hidden.TrySetResult(); }
        fixture.Wheel.IsVisibleChanged += OnVisibilityChanged;
        try
        {
            fixture.Wheel.ObserveGlobalRelease();
            // Await the cleanup outcome; a busy dispatcher can resume a fixed sleep
            // before delivering its lower-priority cleanup timer.
            await hidden.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { fixture.Wheel.IsVisibleChanged -= OnVisibilityChanged; }
        Assert.False(fixture.Wheel.IsVisible); Assert.Empty(dropped);
    });

    [Theory]
    [InlineData("release")]
    [InlineData("new-button-down")]
    [InlineData("disable-and-reenable")]
    [InlineData("escape")]
    [InlineData("dispose")]
    public Task Queued_mode_request_cannot_reopen_after_invalidation(string invalidation) => desktop.RunAsync(async () =>
    {
        using var monitor = new GestureMonitor(Dispatcher.CurrentDispatcher);
        var state = (GestureState)typeof(GestureMonitor).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(monitor)!;
        state.ButtonDown(0, 0);
        state.Move(1000, 1000);
        int requests = 0, escapes = 0;
        monitor.ModeRequested += _ => requests++;
        monitor.EscapePressed += () => escapes++;
        var dispatch = typeof(GestureMonitor).GetMethod("Dispatch", BindingFlags.Instance | BindingFlags.NonPublic)!;
        dispatch.Invoke(monitor, [OperationMode.Convert]);
        switch (invalidation)
        {
            case "release": state.ButtonUp(); break;
            case "new-button-down": state.ButtonDown(2000, 2000); break;
            case "disable-and-reenable": monitor.SetEnabled(false); monitor.SetEnabled(true); break;
            case "escape":
                typeof(GestureMonitor).GetMethod("DispatchCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(monitor, null);
                state.ButtonUp(); // release after Escape must not suppress its cancellation callback
                break;
            case "dispose": monitor.Dispose(); break;
        }
        await Dispatcher.Yield(DispatcherPriority.Background);
        Assert.Equal(0, requests);
        Assert.Equal(invalidation == "escape" ? 1 : 0, escapes);
    });

    [Fact]
    public Task Current_drag_mode_request_is_dispatched() => desktop.RunAsync(async () =>
    {
        using var monitor = new GestureMonitor(Dispatcher.CurrentDispatcher);
        var state = (GestureState)typeof(GestureMonitor).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(monitor)!;
        state.ButtonDown(0, 0);
        state.Move(1000, 1000);
        var requested = new List<OperationMode>();
        monitor.ModeRequested += requested.Add;
        typeof(GestureMonitor).GetMethod("Dispatch", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(monitor, [OperationMode.Convert]);
        await Dispatcher.Yield(DispatcherPriority.Background);
        Assert.Equal(OperationMode.Convert, Assert.Single(requested));
    });

    [Fact]
    public Task Threshold_candidate_and_non_file_entry_never_show_menu_content() => desktop.RunAsync(async () =>
    {
        using var fixture = new WheelFixture();
        fixture.Wheel.ShowAtPointer(OperationMode.Convert);
        Assert.True(fixture.Wheel.IsVisible); // A real target HWND must exist before OLE can enter.
        Assert.All(fixture.Surface.Children.Cast<UIElement>(), child => Assert.Equal(0, child.Opacity));
        var text = new DataObject(DataFormats.Text, "not a physical FileDrop");
        Assert.Equal(DragDropEffects.None, fixture.Raise(DragDrop.PreviewDragEnterEvent, text).Effects);
        Assert.False(fixture.Wheel.IsVisible);
        fixture.Wheel.ShowAtPointer(OperationMode.Actions);
        Assert.All(fixture.Surface.Children.Cast<UIElement>(), child => Assert.Equal(0, child.Opacity));
        await Task.CompletedTask;
    });

    [Fact]
    public Task Physical_FileDrop_data_object_reveals_targets_only_after_entry() => desktop.RunAsync(async () =>
    {
        using var fixture = new WheelFixture();
        fixture.Wheel.ShowAtPointer(OperationMode.Convert);
        var physical = new DataObject(DataFormats.FileDrop, new[] { fixture.A, fixture.B });
        fixture.Raise(DragDrop.PreviewDragEnterEvent, physical);
        await fixture.Ready();
        Assert.True(fixture.Wheel.IsVisible);
        Assert.True(fixture.Sectors.Opacity > 0);
        Assert.NotEmpty(fixture.Sectors.Children.Cast<UIElement>());
        Assert.Equal(DragDropEffects.Copy, fixture.Raise(DragDrop.PreviewDragOverEvent, physical, new Point(168, 50)).Effects);
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData(DragDropEffects.None)]
    [InlineData(DragDropEffects.Move)]
    [InlineData(DragDropEffects.Link)]
    public Task FileDrop_without_copy_permission_never_reveals_the_wheel(DragDropEffects effects) => desktop.RunAsync(async () =>
    {
        using var fixture = new WheelFixture();
        fixture.Wheel.ShowAtPointer(OperationMode.Convert);
        Assert.Equal(DragDropEffects.None, fixture.Raise(DragDrop.PreviewDragEnterEvent, fixture.Data, effects: effects).Effects);
        Assert.False(fixture.Wheel.IsVisible);
        await Task.CompletedTask;
    });

    [Fact]
    public Task Repeated_hover_and_mode_switches_do_not_extract_payload_or_recheck_engines() => desktop.RunAsync(async () =>
    {
        using var fixture = new WheelFixture();
        await fixture.Enter();
        Assert.Equal(1, fixture.Data.Reads);
        int checks = fixture.Engine.Checks;
        Assert.True(checks > 0);
        fixture.Data.ThrowOnRead = true;
        for (int i = 0; i < 100; i++)
            Assert.Equal(DragDropEffects.Copy, fixture.Raise(DragDrop.PreviewDragOverEvent, fixture.Data, new Point(168, 50)).Effects);
        fixture.Wheel.ShowAtPointer(OperationMode.Tools);
        fixture.Wheel.ShowAtPointer(OperationMode.Actions);
        fixture.Wheel.ShowAtPointer(OperationMode.Convert);
        Assert.Equal(checks, fixture.Engine.Checks);
        Assert.Equal(1, fixture.Data.Reads);
        Assert.True(fixture.Wheel.IsVisible);
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData("expanded")]
    [InlineData("reordered")]
    [InlineData("shrunk")]
    public Task Changed_payload_cannot_replace_the_first_selection_or_complete_drop(string change) => desktop.RunAsync(async () =>
    {
        using var fixture = new WheelFixture();
        await fixture.Enter();
        var changed = change switch
        {
            "expanded" => new[] { fixture.A, fixture.B, fixture.C },
            "reordered" => new[] { fixture.B, fixture.A },
            _ => new[] { fixture.A }
        };
        fixture.Data.Paths = changed;
        fixture.Raise(DragDrop.PreviewDragOverEvent, fixture.Data, new Point(168, 50));
        var dropped = new List<OperationDrop>();
        fixture.Wheel.OperationDropped += dropped.Add;
        Assert.Equal(DragDropEffects.None, fixture.Raise(DragDrop.PreviewDropEvent, fixture.Data, new Point(168, 50)).Effects);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Empty(dropped);
        Assert.False(fixture.Wheel.IsVisible);
        Assert.Equal(2, fixture.Data.Reads); // first entry + freshly read final drop only
    });

    [Fact]
    public Task Directory_actions_convert_actions_retains_ownership_and_cached_targets() => desktop.RunAsync(async () =>
    {
        using var fixture = new WheelFixture();
        fixture.Data.Paths = [fixture.Root];
        await fixture.Enter(OperationMode.Actions);
        Assert.NotEmpty(fixture.Sectors.Children.Cast<UIElement>());
        int checks = fixture.Engine.Checks;
        fixture.Wheel.ShowAtPointer(OperationMode.Convert);
        fixture.Raise(DragDrop.PreviewDragOverEvent, fixture.Data, new Point(168, 50));
        Assert.True(fixture.Wheel.IsVisible);
        Assert.Empty(fixture.Sectors.Children.Cast<UIElement>());
        fixture.Wheel.ShowAtPointer(OperationMode.Actions);
        Assert.NotEmpty(fixture.Sectors.Children.Cast<UIElement>());
        Assert.Equal(checks, fixture.Engine.Checks);
        Assert.Equal(1, fixture.Data.Reads);
        await Task.CompletedTask;
    });

    [Fact]
    public Task Confirmed_drop_survives_global_release_and_is_dispatched_once_after_callback() => desktop.RunAsync(async () =>
    {
        using var fixture = new WheelFixture();
        await fixture.Enter();
        fixture.Data.Paths[0] = fixture.C; // mutating the original array cannot mutate admission
        fixture.Data.Paths = [fixture.A, fixture.B];
        fixture.Raise(DragDrop.PreviewDragOverEvent, fixture.Data, new Point(168, 50));
        fixture.Wheel.ObserveGlobalRelease();
        await Dispatcher.Yield(DispatcherPriority.Background);
        Assert.True(fixture.Wheel.IsVisible);
        var dropped = new List<OperationDrop>();
        fixture.Wheel.OperationDropped += dropped.Add;
        Assert.Equal(DragDropEffects.Copy, fixture.Raise(DragDrop.PreviewDropEvent, fixture.Data, new Point(168, 50)).Effects);
        Assert.Empty(dropped);
        Assert.False(fixture.Wheel.IsVisible);
        fixture.Raise(DragDrop.PreviewDropEvent, fixture.Data, new Point(168, 50));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(new[] { fixture.A, fixture.B }, Assert.Single(dropped).Paths);
    });

    [Fact]
    public Task Source_disappearing_after_admission_is_rejected_on_drop() => desktop.RunAsync(async () =>
    {
        using var fixture = new WheelFixture();
        await fixture.Enter();
        fixture.Raise(DragDrop.PreviewDragOverEvent, fixture.Data, new Point(168, 50));
        File.Delete(fixture.A);
        Assert.Equal(DragDropEffects.None, fixture.Raise(DragDrop.PreviewDropEvent, fixture.Data, new Point(168, 50)).Effects);
        Assert.False(fixture.Wheel.IsVisible);
        await Task.CompletedTask;
    });

    [Fact]
    public Task Hover_retains_every_visual_object() => desktop.RunAsync(async () =>
    {
        using var fixture = new WheelFixture();
        await fixture.Enter();
        var children = fixture.Sectors.Children.Cast<UIElement>().ToArray();
        for (int i=0;i<100;i++)
        {
            fixture.Raise(DragDrop.PreviewDragOverEvent, fixture.Data, new Point(184, 62));
            fixture.Raise(DragDrop.PreviewDragOverEvent, fixture.Data, new Point(184, 184));
        }
        Assert.Equal(children.Length,fixture.Sectors.Children.Count);
        for (int i=0;i<children.Length;i++) Assert.Same(children[i],fixture.Sectors.Children[i]);
    });

    [Fact]
    public Task Reopening_for_a_new_drag_keeps_only_the_capsule_and_never_requests_thumbnail_decoding() => desktop.RunAsync(async () =>
    {
        var previews = new DeferredPreview();
        using var fixture = new WheelFixture(previews);
        await fixture.Enter();
        Assert.Empty(previews.Requests);
        fixture.Wheel.Dismiss();
        fixture.Data.Paths = [fixture.C];
        await fixture.Enter();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Empty(previews.Requests);
        Assert.Equal(Path.GetFileName(fixture.C), ((TextBlock)fixture.Wheel.FindName("Badge")).Text);
        foreach (string name in new[] { "Preview", "FileGlyph", "ThumbnailFrame", "CountBadge" }) Assert.Null(fixture.Wheel.FindName(name));
        var capsule = (Border)fixture.Wheel.FindName("CaptionPlate");
        Assert.Equal(160, Canvas.GetTop(capsule));
        Assert.Equal(48, capsule.Height);
        Assert.Equal(24, capsule.CornerRadius.TopLeft);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Late_catalog_cannot_reopen_after_cancel_or_early_drop(bool drop) => desktop.RunAsync(async () =>
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var engine = new CountingEngine { CheckGate = release, CheckEntered = entered };
        using var fixture = new WheelFixture(engine: engine);
        fixture.Wheel.ShowAtPointer(OperationMode.Convert);
        fixture.Raise(DragDrop.PreviewDragEnterEvent,fixture.Data);
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
            if (drop) Assert.Equal(DragDropEffects.None,fixture.Raise(DragDrop.PreviewDropEvent,fixture.Data,new Point(184,62)).Effects);
            else fixture.Wheel.Dismiss();
            Assert.False(fixture.Wheel.IsVisible);
        }
        finally { release.Set(); }
        await Task.Delay(100);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(fixture.Wheel.IsVisible);
        Assert.Empty(fixture.Sectors.Children.Cast<UIElement>());
    });

    [Fact]
    public Task Mode_changed_during_catalog_preparation_uses_current_mode() => desktop.RunAsync(async () =>
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var engine = new CountingEngine { CheckGate=release, CheckEntered=entered };
        using var fixture = new WheelFixture(engine:engine);
        fixture.Wheel.ShowAtPointer(OperationMode.Convert);
        fixture.Raise(DragDrop.PreviewDragEnterEvent,fixture.Data);
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
            fixture.Wheel.ShowAtPointer(OperationMode.Tools);
            Assert.Empty(fixture.Sectors.Children.Cast<UIElement>());
        }
        finally { release.Set(); }
        await fixture.Ready();
        var petal = Assert.Single(fixture.Sectors.Children.Cast<WheelPetal>());
        Assert.Equal("Tools",System.Windows.Automation.AutomationProperties.GetName(petal));
    });

    private sealed class DeferredPreview : IFilePreviewService
    {
        public List<(CancellationToken Token,TaskCompletionSource<ImageSource?> Completion)> Requests { get; } = [];
        public Task<ImageSource?> LoadAsync(string path,int limit,CancellationToken token)
        {
            var completion = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add((token,completion));
            return completion.Task;
        }
    }

    private sealed class WheelFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "citrus-wheel-" + Guid.NewGuid().ToString("N"));
        public string A { get; }
        public string B { get; }
        public string C { get; }
        public CountingData Data { get; }
        public CountingEngine Engine { get; } = new();
        public WheelWindow Wheel { get; }
        public Canvas Surface => (Canvas)Wheel.FindName("Surface");
        public Canvas Sectors => (Canvas)Wheel.FindName("Sectors");
        public WheelFixture(IFilePreviewService? previewService = null, CountingEngine? engine = null)
        {
            if (engine is not null) Engine = engine;
            Directory.CreateDirectory(Root);
            A = Path.Combine(Root, "a.txt"); B = Path.Combine(Root, "b.txt"); C = Path.Combine(Root, "c.txt");
            foreach (string path in new[] { A, B, C }) File.WriteAllText(path, "physical file");
            Data = new CountingData([A, B]);
            Wheel = new WheelWindow(new OperationCatalog([Engine]), previewService: previewService);
            Wheel.ApplySettings(new CitrusSettings { ReduceMotion = true, SoundEnabled = false });
        }
        public async Task Enter(OperationMode mode = OperationMode.Convert)
        {
            Wheel.ShowAtPointer(mode);
            Raise(DragDrop.PreviewDragEnterEvent, Data);
            await Ready();
            Assert.True(Wheel.IsVisible);
            Assert.True(Sectors.Opacity > 0);
        }
        public async Task Ready()
        {
            for (int i = 0; i < 500 && ((TextBlock)Wheel.FindName("Hint")).Text.StartsWith("Preparing"); i++) await Task.Delay(10);
            Assert.False(((TextBlock)Wheel.FindName("Hint")).Text.StartsWith("Preparing"));
        }
        public DragEventArgs Raise(RoutedEvent routedEvent, IDataObject data, Point? point = null,
            DragDropEffects effects = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link)
        {
            // WPF exposes no public constructor. Keep callback construction strictly in test code.
            var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                null, [data, DragDropKeyStates.LeftMouseButton | DragDropKeyStates.ShiftKey | DragDropKeyStates.ControlKey,
                    effects, Surface, point ?? new Point(168, 162)], null)!;
            args.RoutedEvent = routedEvent;
            Surface.RaiseEvent(args);
            Assert.True(args.Handled);
            return args;
        }
        public void Dispose() { Wheel.Dismiss(); Wheel.Close(); Directory.Delete(Root, true); }
    }

    private sealed class CountingEngine(int count = 1) : IConversionEngine
    {
        public ManualResetEventSlim? CheckGate { get; init; }
        public ManualResetEventSlim? CheckEntered { get; init; }
        public int Checks { get; private set; }
        public IReadOnlyList<OperationDescriptor> Operations { get; } = Enum.GetValues<OperationMode>()
            .SelectMany(mode => Enumerable.Range(0,count).Select(index => new OperationDescriptor(count == 1 ? mode.ToString() : $"{mode}.{index}", count == 1 ? mode.ToString() : $"{mode} {index}", mode, null, false, false))).ToArray();
        public bool CanExecute(OperationRequest request) { Checks++; CheckEntered?.Set(); CheckGate?.Wait(TimeSpan.FromSeconds(10)); return true; }
        public Task<ConversionResult> ExecuteAsync(OperationRequest request, IProgress<ConversionProgress>? progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Target tests must never execute file operations.");
    }

    private sealed class CountingData(string[] paths) : IDataObject
    {
        public string[] Paths { get; set; } = paths;
        public int Reads { get; private set; }
        public bool ThrowOnRead { get; set; }
        public object GetData(string format, bool autoConvert)
        {
            Reads++;
            if (ThrowOnRead) throw new InvalidOperationException("Unexpected data extraction on hover.");
            return Paths;
        }
        public object GetData(string format) => GetData(format, false);
        public object GetData(Type format) => GetData(DataFormats.FileDrop, false);
        public bool GetDataPresent(string format, bool autoConvert) => format == DataFormats.FileDrop;
        public bool GetDataPresent(string format) => GetDataPresent(format, false);
        public bool GetDataPresent(Type format) => format == typeof(string[]);
        public string[] GetFormats(bool autoConvert) => [DataFormats.FileDrop];
        public string[] GetFormats() => GetFormats(false);
        public void SetData(string format, object data, bool autoConvert) => throw new NotSupportedException();
        public void SetData(string format, object data) => throw new NotSupportedException();
        public void SetData(Type format, object data) => throw new NotSupportedException();
        public void SetData(object data) => throw new NotSupportedException();
    }
}
