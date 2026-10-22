using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Citrus.Core.Editing;

namespace Citrus.Windows.Editors.Media;

/// <summary>Reusable view; host owns controller disposal. No routes, queue or conversion submission.</summary>
public partial class MediaTransport : UserControl
{
    private readonly DispatcherTimer timer;
    private MediaPreviewController? controller;
    private CancellationTokenSource lifetime = new();
    private EditorDocument? document;
    private TimeRange range;
    private bool attached;
    private bool videoMode, updatingTimeline;
    private Guid? sourceId;
    private WriteableBitmap? bitmap;
    private bool silentPreview, playIntent, requestingWindow;
    private string? previewNotice, presentationError;
    public string? PreviewNotice { get => previewNotice; set { previewNotice = value; Refresh(); } }
    public Func<double, bool, Task>? WindowRequested { get; set; }
    public Func<int, Task>? FrameRequested { get; set; }
    public RationalTime? PresentedTime { get; private set; }
    public bool ExternalTimeline { get; set; }
    public bool IsUpdatingTimeline => updatingTimeline;
    public Slider TimelineControl => Timeline;
    public UIElement? TimelineAdornment { get => SelectionAdornment.Content as UIElement; set => SelectionAdornment.Content = value; }
    public bool VideoMode
    {
        get => videoMode;
        set
        {
            videoMode = value;
            Waveform.Visibility = Pause.Visibility = Stop.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            ((Grid)Waveform.Parent).RowDefinitions[1].Height = value ? new GridLength(0) : new GridLength(64);
            Previous.Content = value ? "‹" : "Previous frame"; Next.Content = value ? "›" : "Next frame";
            Refresh();
        }
    }
    public MediaTransport()
    {
        InitializeComponent();
        timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }
    public MediaPreviewController? Controller
    {
        get => controller;
        set
        {
            Detach(); controller = value;
            // A programmatic host may call LoadAsync before WPF raises Loaded.
            // Give that request the same lifetime which Attach will subsequently use.
            lifetime.Dispose(); lifetime = new();
            if (IsLoaded) Attach();
        }
    }
    public async Task LoadAsync(EditorDocument value, TimeRange selection, CancellationToken ct, bool play = false)
    {
        document = value; range = selection; playIntent = play; presentationError = null;
        if (!ExternalTimeline)
        { Timeline.Minimum = Seconds(selection.Start); Timeline.Maximum = Seconds(selection.End); Timeline.Value = Timeline.Minimum; }
        Guid? incoming = value.Sources.IsEmpty ? null : value.Sources[0].Id;
        if (sourceId != incoming) { Preview.Source = null; bitmap = null; PresentedTime = null; sourceId = incoming; }
        if (controller is null) throw new InvalidOperationException("Assign the playback controller before loading media.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        if (play) await controller.PlayAsync(PlaybackDocument(value), selection, Original.IsChecked == true, linked.Token);
        else await controller.PreparePausedAsync(PlaybackDocument(value), selection, Original.IsChecked == true, linked.Token);
        Refresh();
    }
    private void Attach()
    {
        if (attached || controller is null) return;
        attached = true;
        if (lifetime.IsCancellationRequested) { lifetime.Dispose(); lifetime = new(); }
        controller.StateChanged += StateChanged;
        Refresh();
    }
    private void Detach()
    {
        timer.Stop();
        playIntent = false;
        var detached = attached ? controller : null;
        attached = false;
        if (detached is not null) detached.StateChanged -= StateChanged;
        lifetime.Cancel();
        detached?.Stop();
    }
    private void StateChanged(object? sender, EventArgs e)
    {
        // State transitions are sparse. Pixel presentation is pulled by the active-only dispatcher timer.
        if (Dispatcher.CheckAccess()) Refresh();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Refresh);
    }
    private void Refresh()
    {
        if (!attached || controller is null || !IsLoaded) return;
        if (controller.State == MediaPlaybackState.Playing) timer.Start(); else timer.Stop();
        if (!updatingTimeline && (!ExternalTimeline || controller.State is MediaPlaybackState.Playing or MediaPlaybackState.Paused) && !Timeline.IsMouseCaptureWithin && !Timeline.IsKeyboardFocusWithin)
        {
            updatingTimeline = true;
            try { Timeline.Value = Seconds(controller.Position); }
            finally { updatingTimeline = false; }
        }
        Timecode.Text = MediaRangeView.Timecode(Seconds(controller.Position));
        if (SelectionAdornment.Content is VideoTrimSelectionView {IsDragging:false} trim) trim.Position = Seconds(controller.Position);
        if (videoMode)
        {
            Play.Content = controller.State == MediaPlaybackState.Playing ? "Ⅱ" : "▶";
            System.Windows.Automation.AutomationProperties.SetName(Play, controller.State == MediaPlaybackState.Playing ? "Pause video" : "Play video");
        }
        UpdateStatus();
        Waveform.Buckets = controller.Waveform;
        if (range.End.Denominator > 0 && range.Start.CompareTo(range.End) < 0)
        {
            double duration = Seconds(range.End) - Seconds(range.Start);
            Waveform.StartFraction = Math.Clamp((Seconds(controller.WaveformRange.Start) - Seconds(range.Start)) / duration, 0, 1);
            Waveform.EndFraction = Math.Clamp((Seconds(controller.WaveformRange.End) - Seconds(range.Start)) / duration, 0, 1);
        }
        controller.TryPresentFrame(frame =>
        {
            PresentedTime = frame.ActualTime;
            var pixels = MediaPresentation.Pixels(frame);
            if (bitmap is null || bitmap.PixelWidth != frame.Size.Width || bitmap.PixelHeight != frame.Size.Height)
            { bitmap = new WriteableBitmap(frame.Size.Width, frame.Size.Height, 96, 96, PixelFormats.Bgra32, null); Preview.Source = bitmap; }
            bitmap.WritePixels(new Int32Rect(0, 0, frame.Size.Width, frame.Size.Height), pixels, frame.Stride, 0);
        });
        if (ExternalTimeline && playIntent && !requestingWindow && controller.State == MediaPlaybackState.Stopped && controller.Position.CompareTo(range.End) >= 0 && Seconds(controller.Position) < Timeline.Maximum)
            _ = RequestWindowAsync(Seconds(controller.Position), true);
    }
    private async void PlayClicked(object sender, RoutedEventArgs e) => await Act(async () =>
    {
        if (controller is null || document is null) return;
        if (videoMode && controller.State == MediaPlaybackState.Playing) { playIntent = false; controller.Pause(); Refresh(); return; }
        playIntent = true;
        if (ExternalTimeline && controller.Position.CompareTo(range.End) >= 0 && WindowRequested is not null)
        { await RequestWindowAsync(Seconds(controller.Position) >= Timeline.Maximum ? 0 : Seconds(controller.Position), true); return; }
        if (controller.State == MediaPlaybackState.Paused && controller.Position.CompareTo(range.End) < 0)
            await controller.ResumeAsync(lifetime.Token);
        else await controller.PlayAsync(PlaybackDocument(document), range, Original.IsChecked == true, lifetime.Token);
    });
    private void PauseClicked(object sender, RoutedEventArgs e) { playIntent = false; controller?.Pause(); Refresh(); }
    private void StopClicked(object sender, RoutedEventArgs e) { playIntent = false; controller?.Stop(); Refresh(); }
    public void PauseForSeek()
    {
        playIntent = false;
        // Pause reports the old decoded position synchronously; retain the new user cursor.
        updatingTimeline = true;
        try { controller?.Pause(); }
        finally { updatingTimeline = false; }
    }
    public void SetTimelinePosition(double value)
    { updatingTimeline = true; try { Timeline.Value = value; } finally { updatingTimeline = false; } }
    private async Task RequestWindowAsync(double position, bool play)
    {
        if (WindowRequested is null || requestingWindow || lifetime.IsCancellationRequested) return;
        requestingWindow = true;
        try { await WindowRequested(position, play); }
        finally { requestingWindow = false; }
    }
    private async void OriginalClicked(object sender, RoutedEventArgs e) => await Act(async () =>
    {
        if (controller is null || document is null) return;
        if (controller.Position.CompareTo(range.End) >= 0) await controller.PreparePausedAsync(document, range, Original.IsChecked == true, lifetime.Token);
        else await controller.SetOriginalAsync(Original.IsChecked == true, lifetime.Token);
    });
    private async void PreviousClicked(object sender, RoutedEventArgs e) => await Step(-1);
    private async void NextClicked(object sender, RoutedEventArgs e) => await Step(1);
    private Task Step(int direction) => Act(async () =>
    {
        playIntent = false;
        if (videoMode && FrameRequested is not null) await FrameRequested(direction);
        else if (controller is not null) await controller.StepAsync(direction, lifetime.Token);
    });
    private async void SeekReleased(object sender, MouseButtonEventArgs e) => await Seek();
    private async void SeekKeyReleased(object sender, KeyEventArgs e)
    { if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown) await Seek(); }
    private Task Seek() => Act(async () =>
    {
        if (controller is null || document is null) return;
        if (ExternalTimeline) return; // The owning editor prepares the bounded source window at this full-timeline position.
        var position = MediaPresentation.SeekPosition(Timeline.Value, range);
        if (position.CompareTo(range.End) >= 0)
        {
            // The range is half-open. The right edge pauses without issuing an invalid seek.
            controller.Pause(); Timeline.Value = Seconds(controller.Position); return;
        }
        await controller.SeekAsync(position, lifetime.Token);
        Refresh();
    });
    private async Task Act(Func<Task> action)
    {
        presentationError = null;
        UpdateStatus();
        try { await action(); UpdateStatus(); }
        catch (OperationCanceledException) { }
        catch (AudioDeviceUnavailableException) when (videoMode && document?.Recipe is VideoRecipe && controller is not null && !lifetime.IsCancellationRequested)
        {
            silentPreview = true;
            PreviewNotice = "Audio output unavailable. Preview plays silently; saved audio is unchanged.";
            try
            {
                var at = controller.Position.CompareTo(range.End) < 0 ? controller.Position : range.Start;
                await controller.PlayAsync(PlaybackDocument(document), new(at, range.End), Original.IsChecked == true, lifetime.Token);
                Refresh();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ReportFailure(ex.Message); }
        }
        catch (Exception ex) { ReportFailure(ex.Message); }
    }
    private void ReportFailure(string message) { presentationError = message; UpdateStatus(); }
    private void UpdateStatus()
    {
        if (controller is null) return;
        var state = controller.State;
        Status.Text = presentationError ?? (state switch
        {
            MediaPlaybackState.Preparing => videoMode ? "Updating preview…" : "Preparing preview…",
            MediaPlaybackState.Failed => controller.Error?.Message ?? "Playback failed.",
            _ => videoMode && PreviewNotice is not null ? PreviewNotice : state.ToString()
        });
        // Video messages belong to the picture, so preparing/error/notice changes never
        // resize the image, the trim strip or the transport. Audio retains its footer row.
        Status.Visibility = videoMode ? Visibility.Collapsed : Visibility.Visible;
        VideoStatusOverlay.Visibility = videoMode && (presentationError is not null || PreviewNotice is not null || state is MediaPlaybackState.Preparing or MediaPlaybackState.Failed)
            ? Visibility.Visible : Visibility.Collapsed;
        Picture.ToolTip = VideoStatusOverlay.Visibility == Visibility.Visible ? Status.Text : null;
    }
    private EditorDocument PlaybackDocument(EditorDocument value) => silentPreview && value.Recipe is VideoRecipe video
        ? value with { Recipe = video with { Mute = true, Clips = video.Clips.Select(clip => clip with { AudioStream = null }).ToImmutableArray() } } : value;
    private static double Seconds(RationalTime value) => (double)value.Numerator / value.Denominator;
}
