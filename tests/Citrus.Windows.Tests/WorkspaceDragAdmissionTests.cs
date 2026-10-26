using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DataObject = System.Windows.DataObject;
using IDataObject = System.Windows.IDataObject;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using ListBox = System.Windows.Controls.ListBox;
using Point = System.Windows.Point;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class WorkspaceDragAdmissionTests(DesktopFixture desktop)
{
    [Theory]
    [InlineData("EmptyState")]
    [InlineData("FilesPanel")]
    [InlineData("LauncherDropZone")]
    public Task Preview_is_cached_but_a_removed_file_is_rejected_at_drop(string zoneName) => desktop.RunAsync(async () =>
    {
        string path = Path.GetTempFileName();
        using var window = new MainWindow((App)System.Windows.Application.Current);
        try
        {
            var zone = (FrameworkElement)window.FindName(zoneName);
            var data = Files(path);
            Assert.Equal(DragDropEffects.Copy, Raise(zone, DragDrop.PreviewDragOverEvent, data).Effects);
            File.Delete(path);

            Assert.Equal(DragDropEffects.Copy, Raise(zone, DragDrop.PreviewDragOverEvent, data).Effects);
            Assert.Equal(DragDropEffects.None, Raise(zone, DragDrop.PreviewDropEvent, data).Effects);
            Assert.Empty(((ListBox)window.FindName("SelectedFiles")).Items);
            Assert.Equal(DragDropEffects.None, Raise(zone, DragDrop.PreviewDragOverEvent, data).Effects);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        finally { window.Dispose(); window.Close(); File.Delete(path); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Drag_boundaries_clear_previous_admission(bool enter) => desktop.RunAsync(async () =>
    {
        string path = Path.GetTempFileName();
        using var window = new MainWindow((App)System.Windows.Application.Current);
        try
        {
            var zone = (FrameworkElement)window.FindName("EmptyState");
            var data = Files(path);
            Assert.Equal(DragDropEffects.Copy, Raise(zone, DragDrop.PreviewDragOverEvent, data).Effects);
            File.Delete(path);

            Raise(zone, enter ? DragDrop.PreviewDragEnterEvent : DragDrop.PreviewDragLeaveEvent, data);

            Assert.Equal(DragDropEffects.None, Raise(zone, DragDrop.PreviewDragOverEvent, data).Effects);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        finally { window.Dispose(); window.Close(); File.Delete(path); }
    });

    [Theory]
    [InlineData(DragDropEffects.Move)]
    [InlineData(DragDropEffects.None)]
    public Task Allowed_effects_are_checked_on_every_callback(DragDropEffects allowed) => desktop.RunAsync(async () =>
    {
        string path = Path.GetTempFileName();
        using var window = new MainWindow((App)System.Windows.Application.Current);
        try
        {
            var zone = (FrameworkElement)window.FindName("EmptyState");
            var data = Files(path);
            Assert.Equal(DragDropEffects.Copy, Raise(zone, DragDrop.PreviewDragOverEvent, data).Effects);
            Assert.Equal(DragDropEffects.None, Raise(zone, DragDrop.PreviewDragOverEvent, data, allowed).Effects);
            Assert.Equal(DragDropEffects.None, Raise(zone, DragDrop.PreviewDropEvent, data, allowed).Effects);
            Assert.Empty(((ListBox)window.FindName("SelectedFiles")).Items);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        finally { window.Dispose(); window.Close(); File.Delete(path); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Unsupported_payloads_remain_rejected(bool malformedFileDrop) => desktop.RunAsync(async () =>
    {
        using var window = new MainWindow((App)System.Windows.Application.Current);
        try
        {
            var zone = (FrameworkElement)window.FindName("EmptyState");
            var data = new DataObject();
            data.SetData(malformedFileDrop ? DataFormats.FileDrop : DataFormats.Text, "unsupported", false);
            Assert.Equal(DragDropEffects.None, Raise(zone, DragDrop.PreviewDragOverEvent, data).Effects);
            Assert.Equal(DragDropEffects.None, Raise(zone, DragDrop.PreviewDropEvent, data).Effects);
            Assert.Empty(((ListBox)window.FindName("SelectedFiles")).Items);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        finally { window.Dispose(); window.Close(); }
    });

    [Fact]
    public Task Drop_reads_the_current_payload_instead_of_hover_paths() => desktop.RunAsync(async () =>
    {
        string path = Path.GetTempFileName();
        using var window = new MainWindow((App)System.Windows.Application.Current);
        try
        {
            var zone = (FrameworkElement)window.FindName("EmptyState");
            var data = Files(path);
            Assert.Equal(DragDropEffects.Copy, Raise(zone, DragDrop.PreviewDragOverEvent, data).Effects);
            data.SetData(DataFormats.FileDrop, new[] { path + ".missing" }, false);

            Assert.Equal(DragDropEffects.None, Raise(zone, DragDrop.PreviewDropEvent, data).Effects);
            Assert.Empty(((ListBox)window.FindName("SelectedFiles")).Items);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        finally { window.Dispose(); window.Close(); File.Delete(path); }
    });

    [Fact]
    public Task Changing_workspace_mode_clears_hover_admission() => desktop.RunAsync(async () =>
    {
        string path = Path.GetTempFileName();
        using var window = new MainWindow((App)System.Windows.Application.Current);
        try
        {
            var zone = (FrameworkElement)window.FindName("EmptyState");
            var data = Files(path);
            Assert.Equal(DragDropEffects.Copy, Raise(zone, DragDrop.PreviewDragOverEvent, data).Effects);
            File.Delete(path);
            ((ListBox)window.FindName("Modes")).SelectedIndex = 1;

            Assert.Equal(DragDropEffects.None, Raise(zone, DragDrop.PreviewDragOverEvent, data).Effects);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        finally { window.Dispose(); window.Close(); File.Delete(path); }
    });

    private static DataObject Files(string path)
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { path }, false);
        return data;
    }

    private static DragEventArgs Raise(FrameworkElement zone, RoutedEvent routedEvent, IDataObject data, DragDropEffects allowed = DragDropEffects.Copy)
    {
        // WPF has no public constructor; match the existing routed drag fixtures.
        var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [data, DragDropKeyStates.LeftMouseButton, allowed, zone, new Point()], null)!;
        args.RoutedEvent = routedEvent;
        zone.RaiseEvent(args);
        Assert.True(routedEvent != DragDrop.PreviewDragOverEvent && routedEvent != DragDrop.PreviewDropEvent || args.Handled);
        return args;
    }
}
