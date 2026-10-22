using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Citrus.Core;
using Citrus.Windows.Presentation;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class BlurActivationTests(DesktopFixture desktop)
{
    [Fact]
    public Task Repeated_matching_start_retargets_the_pending_capture_instead_of_restarting_it() => desktop.RunAsync(async () =>
    {
        var sessions = new List<Session>();
        using var glass = new LiveWheelGlass(Dispatcher.CurrentDispatcher, _ => { }, () => { },
            exclude: _ => new Lease(), createSession: (scene, x, y) =>
            { var session = new Session(scene, x, y); lock (sessions) sessions.Add(session); return session; });
        glass.StartWheelPresentation(0, 0, 184, false, false, 1);
        try
        {
            await Until(() => { lock (sessions) return sessions.Count == 1; });
            var initial = glass.Completion;
            glass.StartWheelPresentation(12, 5, 184, true, true, 1);
            Assert.Same(initial, glass.Completion);
            await Until(() => { lock (sessions) return sessions[0].ScreenX == 12 && sessions[0].ScreenY == 5; });
            lock (sessions) { Assert.Single(sessions); Assert.False(sessions[0].Disposed); }
        }
        finally { glass.Stop(); await glass.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
    });

    [Fact]
    public Task First_blurred_frame_is_published_at_render_priority() => desktop.RunAsync(async () =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        Session? session = null;
        var posted = new TaskCompletionSource<DispatcherPriority>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPosted(object? sender, DispatcherHookEventArgs e)
        { if (session?.Rendered == true) posted.TrySetResult(e.Operation.Priority); }
        dispatcher.Hooks.OperationPosted += OnPosted;
        using var glass = new LiveWheelGlass(dispatcher, _ => { }, () => { },
            exclude: _ => new Lease(), createSession: (scene, x, y) => session = new Session(scene, x, y) { Ready = true });
        try
        {
            glass.StartWheelPresentation(0, 0, 184, false, false, 1);
            Assert.Equal(DispatcherPriority.Render, await posted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            dispatcher.Hooks.OperationPosted -= OnPosted;
            glass.Stop(); await glass.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    });

    [Fact]
    public Task Unchanged_DPI_and_eligible_policy_refresh_keep_the_real_wheel_blur() => desktop.RunAsync(async () =>
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        File.WriteAllText(path, "owned blur activation fixture");
        int sessions = 0;
        var wheel = new WheelWindow(new OperationCatalog([new Engine()]), null, null, null,
            (scene, x, y) => { sessions++; return new Session(scene, x, y) { Ready = true }; });
        try
        {
            wheel.ApplySettings(new() { SoundEnabled = false });
            wheel.ApplyMaterialPolicy(new(true, false, "Owned startup"), false, 1);
            wheel.ShowAtPointer(OperationMode.Convert);
            var surface = (Canvas)wheel.FindName("Surface");
            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            var enter = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                null, [data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, surface, new System.Windows.Point(184, 184)], null)!;
            enter.RoutedEvent = DragDrop.PreviewDragEnterEvent; surface.RaiseEvent(enter);
            var image = (System.Windows.Controls.Image)wheel.FindName("GlassBody");
            await Until(() => image.Source is not null);
            int before = sessions;
            var bitmap = image.Source;
            var handle = new WindowInteropHelper(wheel).Handle;
            object[] message = [handle, 0x02E0, nint.Zero, nint.Zero, false];
            typeof(WheelWindow).GetMethod("WindowMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(wheel, message);
            Assert.Same(bitmap, image.Source);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            wheel.ApplyMaterialPolicy(new(true, true, "Same eligible surface"), true, 1.25);
            Assert.Same(bitmap, image.Source);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(before, sessions);
        }
        finally { wheel.Dismiss(); wheel.Close(); await wheel.GlassCompletion.WaitAsync(TimeSpan.FromSeconds(5)); File.Delete(path); }
    });

    private static async Task Until(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < 5000) await Task.Delay(10);
        Assert.True(condition());
    }
    private sealed class Lease : IDisposable { public void Dispose() { } }
    private sealed class Session(GlassScene scene, int x, int y) : ILiveGlassSession, IRelocatableGlassSession
    {
        public GlassScene Scene => scene;
        public int ScreenX = x, ScreenY = y;
        public volatile bool Ready, Rendered, Disposed;
        public bool TryMoveTo(int screenLeft, int screenTop) { ScreenX = screenLeft; ScreenY = screenTop; return true; }
        public bool TryRenderFrame(GlassFrameState state, byte[] destination)
        {
            if (!Ready) return false;
            Array.Fill(destination, (byte)255); Rendered = true; return true;
        }
        public void Dispose() => Disposed = true;
    }
    private sealed class Engine : IConversionEngine
    {
        public IReadOnlyList<OperationDescriptor> Operations { get; } = [new("owned.convert", "Owned", OperationMode.Convert, ".png", false, false)];
        public bool CanExecute(OperationRequest request) => true;
        public Task<ConversionResult> ExecuteAsync(OperationRequest request, IProgress<ConversionProgress>? progress, CancellationToken token) => throw new NotSupportedException();
    }
}
