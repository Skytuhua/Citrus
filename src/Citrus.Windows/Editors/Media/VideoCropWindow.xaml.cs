using System.Collections.Immutable;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Citrus.Conversion.Editing.Media;
using Citrus.Core.Editing;
using Citrus.Windows.Presentation;

namespace Citrus.Windows.Editors.Media;

/// <summary>Original preview plane stays fixed while crop geometry changes. Modal result is an immutable recipe, never preview pixels.</summary>
public partial class VideoCropWindow : Window
{
    private readonly VideoCropEngine engine;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer renderTimer, seekTimer;
    private readonly string previewRoot = Path.Combine(Path.GetTempPath(), "citrus-crop-preview-" + Guid.NewGuid().ToString("N"));
    private CancellationTokenSource? seekWork;
    private Task loading = Task.CompletedTask;
    private readonly HashSet<Task> commands = [];
    private EditorOpenRequest? request;
    private EditorHistory? history;
    private EditorDocument? document;
    private MediaSourceInfo? media;
    private MediaPreviewController? playback;
    private MediaProbe? sessionProbe;
    private WriteableBitmap? presentation;
    private TimeRange audition;
    private EditorSubmissionBatch? accepted;
    private bool closed, shown, syncing, scrubbing, resumeAfterScrub, playIntent, continuing, seekPreparing, invalidPercent, silentPreview;
    private double? ratio;
    private RationalTime? displayedTime;
    private object? sliderOwner;
    private string? sliderGesture;
    private bool keyboardSeeking;
    private static readonly Geometry PlayShape = FrozenShape("M1,0 L9,5 1,10 Z");
    private static readonly Geometry PauseShape = FrozenShape("M1,0 L3,0 3,10 1,10 Z M7,0 L9,0 9,10 7,10 Z");
    internal MediaPreviewController? Playback => playback;
    private SizeI Plane => media!.Video[0].DisplaySize;
    private RationalTime PreviewEnd => media!.Video[0].PresentationEnd ?? media.Duration;
    private RectD Crop => ((VideoRecipe)document!.Recipe).Crop!.Value;
    public VideoCropWindow(VideoCropEngine engine)
    {
        this.engine = engine;
        InitializeComponent(); WindowChromeSetup.Apply(this);
        SizeChanged += (_, _) => PreviewPane.Height = Math.Clamp((ActualHeight > 0 ? ActualHeight : Height) * .49, 184, 270);
        CropHandles.Changed += CropChanged;
        foreach (var slider in new[] { WidthSlider, HeightSlider })
        {
            slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => { sliderOwner = slider; sliderGesture = Guid.NewGuid().ToString("N"); }));
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { sliderOwner = null; sliderGesture = null; }));
        }
        Timeline.PreviewKeyDown += (_, e) => { if (IsSeekKey(e.Key)) keyboardSeeking = true; };
        Timeline.LostKeyboardFocus += (_, _) => keyboardSeeking = false;
        renderTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        renderTimer.Tick += (_, _) => RefreshFrame();
        seekTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
        seekTimer.Tick += async (_, _) => { seekTimer.Stop(); await Track(SeekTo(Timeline.Value, false)); };
        PreviewKeyDown += WindowKeyDown;
        Closed += (_, _) => { closed = true; playIntent = false; renderTimer.Stop(); seekTimer.Stop(); lifetime.Cancel(); seekWork?.Cancel(); playback?.Stop(); };
        (Application.Current as App)?.AttachPresentation(this, Chrome);
    }
    public async Task<EditorSubmissionBatch?> ShowEditorAsync(EditorOpenRequest value, CancellationToken ct)
    {
        if (shown) throw new InvalidOperationException("An editor can be shown once.");
        if (value.Operation != VideoCropEngine.Operation || value.InitialPaths.Length != 1) throw new ArgumentException("Select one video to crop.");
        shown = true; request = value;
        Loaded += BeginLoading;
        using var registration = ct.Register(() => Dispatcher.BeginInvoke(new Action(() => { if (!closed) Close(); })));
        try { ct.ThrowIfCancellationRequested(); ShowDialog(); return accepted; }
        finally
        {
            lifetime.Cancel(); seekWork?.Cancel(); renderTimer.Stop(); seekTimer.Stop();
            await loading;
            await Task.WhenAll(commands.ToArray());
            if (playback is not null) { playback.StateChanged -= PlaybackChanged; await playback.DisposeAsync(); }
            if (sessionProbe is not null) await sessionProbe.DisposeAsync();
            seekWork?.Dispose(); lifetime.Dispose();
            if (Directory.Exists(previewRoot)) Directory.Delete(previewRoot, true);
        }
        void BeginLoading(object sender, RoutedEventArgs e) { Loaded -= BeginLoading; loading = LoadAsync(); }
    }
    private async Task LoadAsync()
    {
        try
        {
            var captured = (await engine.Sources.CaptureAsync(request!.InitialPaths, lifetime.Token)).Single();
            var source = captured.Snapshot ?? throw new IOException(captured.Error);
            sessionProbe = new(engine.Process, engine.Sources, Path.Combine(previewRoot, "session-sources"));
            media = await sessionProbe.InspectAsync(source, lifetime.Token);
            var inspected = media.Video.FirstOrDefault() ?? throw new NotSupportedException("The selected file contains no video stream.");
            if (inspected.IsHdr) throw new NotSupportedException("HDR cropping is not supported. Use an SDR copy.");
            if (inspected.DisplaySize.Width < 2 || inspected.DisplaySize.Height < 2) throw new NotSupportedException("Video dimensions must be at least two pixels.");
            silentPreview = media.Audio.FirstOrDefault()?.Channels.Length > 2;
            if (silentPreview) Notice("Surround preview is silent; saved channels are unchanged.");
            var stream = media.Video[0]; var full = VideoCropGeometry.Fit(stream.DisplaySize, null);
            document = new(1, 0, [source], [], new VideoRecipe([new(Guid.NewGuid(), source.Id, stream.Index, media.Audio.FirstOrDefault()?.Index,
                new(new(0, 1), media.Duration), 1)], full, new((int)full.Width, (int)full.Height), ImageFit.Contain, false, [], [], null, []),
                new("mp4", request.DestinationDirectory, Path.GetFileNameWithoutExtension(source.Path) + "-cropped", new QualityGoal(request.InitialOptions.Quality), MetadataPolicy.StripAll, new(0, 0, 0, 255)));
            history = new(document);
            playback = new(new MediaPlaybackPreparation(engine.Process, engine.Sources, previewRoot, new(720, 720), sessionProbe, knownCapabilities: engine.Capabilities), () => new WasapiPreviewSink());
            playback.StateChanged += PlaybackChanged;
            syncing = true;
            Timeline.Maximum = Seconds(PreviewEnd); Timeline.Value = 0;
            DurationText.Text = Timecode(PreviewEnd); syncing = false;
            CropHandles.Plane = Plane; CropHandles.Visibility = Visibility.Visible;
            SyncCrop(); Controls.IsEnabled = true;
            await SeekTo(0, false);
            if (!closed) { LoadingText.Visibility = Visibility.Collapsed; UpdateAdmission(); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!closed) { LoadingText.Visibility = Visibility.Collapsed; Error(error.Message); } }
    }
    private void CropChanged(RectD rect, bool commit) => SetCrop(rect, commit, "drag");
    private void SetCrop(RectD rect, bool commit, string key)
    {
        if (document is null) return;
        var crop = VideoCropGeometry.Normalize(rect, Plane);
        document = document with { Recipe = ((VideoRecipe)document.Recipe) with { Crop = crop, Canvas = new((int)crop.Width, (int)crop.Height) } };
        if (commit && history is not null && ((VideoRecipe)history.Current.Recipe).Crop != crop)
        { history.Commit(document, key is "width" or "height" && sliderOwner is not null && sliderGesture is not null ? key + sliderGesture : key + Guid.NewGuid().ToString("N")); document = history.Current; }
        SyncCrop();
    }
    private void SyncCrop()
    {
        if (document is null) return;
        syncing = true;
        CropHandles.Crop = Crop; CropHandles.AspectRatio = ratio; CropHandles.InvalidateVisual();
        WidthSlider.Value = Crop.Width / Plane.Width * 100; HeightSlider.Value = Crop.Height / Plane.Height * 100;
        WidthPercent.Text = WidthSlider.Value.ToString("0", CultureInfo.InvariantCulture) + "%";
        HeightPercent.Text = HeightSlider.Value.ToString("0", CultureInfo.InvariantCulture) + "%";
        Dimensions.Text = $"{Crop.Width:N0} × {Crop.Height:N0} px";
        syncing = false; invalidPercent = false; Error(""); UpdateAdmission();
    }
    private void UpdateAdmission()
    {
        bool supported = document is not null && Crop.Width <= VideoCropEngine.MaximumOutputDimension && Crop.Height <= VideoCropEngine.MaximumOutputDimension;
        ApplyButton.IsEnabled = supported && !invalidPercent && PreviewFrame.Source is not null && !CropHandles.IsDragging;
        if (document is not null && !supported) Error("Select a crop no larger than 8190 pixels on either side for this MP4 encoder.");
    }
    private void WidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!syncing && document is not null) SetCrop(VideoCropGeometry.Resize(Crop, Plane, e.NewValue, true, ratio), true, "width"); }
    private void HeightChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!syncing && document is not null) SetCrop(VideoCropGeometry.Resize(Crop, Plane, e.NewValue, false, ratio), true, "height"); }
    private void RatioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || document is null) return;
        ratio = AspectRatio.SelectedIndex switch { 1 => (double)Plane.Width / Plane.Height, 2 => 1, 3 => 4d / 3, 4 => 3d / 2, 5 => 16d / 9, 6 => 9d / 16, _ => null };
        if (ratio is not null) SetCrop(VideoCropGeometry.Fit(Plane, ratio), true, "ratio");
        CropHandles.AspectRatio = ratio;
    }
    private void ResetCrop(object sender, RoutedEventArgs e)
    {
        if (document is null) return;
        CropHandles.CancelDrag();
        syncing = true; AspectRatio.SelectedIndex = 0; syncing = false; ratio = null;
        SetCrop(VideoCropGeometry.Fit(Plane, null), true, "reset");
    }
    private void PercentKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { CommitPercent((TextBox)sender); e.Handled = true; } }
    private void PercentCommitted(object sender, RoutedEventArgs e) { if (!syncing) CommitPercent((TextBox)sender); }
    private bool CommitPercent(TextBox box)
    {
        if (document is null) return false;
        if (!double.TryParse(box.Text.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || value is < 0 or > 100)
        { invalidPercent = true; Error("Enter a crop percentage from 0 to 100."); ApplyButton.IsEnabled = false; return false; }
        double current = box == WidthPercent ? WidthSlider.Value : HeightSlider.Value;
        // Display rounds the actual even-pixel extent; an unchanged label must not change its geometry on blur.
        if (box.Text == current.ToString("0", CultureInfo.InvariantCulture) + "%") { invalidPercent = false; Error(""); UpdateAdmission(); return true; }
        SetCrop(VideoCropGeometry.Resize(Crop, Plane, value, box == WidthPercent, ratio), true, "percent"); return true;
    }
    private void ApplyCrop(object sender, RoutedEventArgs e)
    {
        if (document is null || !CommitPercent(WidthPercent) || !CommitPercent(HeightPercent)) return;
        UpdateAdmission(); if (!ApplyButton.IsEnabled) return;
        var source = document.Sources[0];
        accepted = new(request!, [source], [], [], [source.Id], [new(Guid.NewGuid(), [source.Id], document)]);
        Close();
    }
    private void PlaybackChanged(object? sender, EventArgs e)
    { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => { if (!closed) RefreshFrame(); })); }
    private void RefreshFrame()
    {
        if (closed || playback is null) return;
        playback.TryPresentFrame(frame =>
        {
            if (presentation is null || presentation.PixelWidth != frame.Size.Width || presentation.PixelHeight != frame.Size.Height)
            { presentation = new WriteableBitmap(frame.Size.Width, frame.Size.Height, 96, 96, PixelFormats.Bgra32, null); PreviewFrame.Source = presentation; }
            presentation.WritePixels(new Int32Rect(0, 0, frame.Size.Width, frame.Size.Height), MediaPresentation.Pixels(frame), frame.Stride, 0); displayedTime = frame.ActualTime;
        });
        if (!scrubbing && !keyboardSeeking && playback.State is not MediaPlaybackState.Preparing)
        { syncing = true; Timeline.Value = Seconds(playback.Position); syncing = false; }
        PositionText.Text = Timecode(playback.Position);
        bool playing = playback.State == MediaPlaybackState.Playing;
        PlayGlyph.Data = playing ? PauseShape : PlayShape;
        System.Windows.Automation.AutomationProperties.SetName(PlayButton, playing ? "Pause video" : "Play video");
        if (playing) renderTimer.Start(); else renderTimer.Stop();
        if (playback.State == MediaPlaybackState.Failed) { playIntent = false; Error(playback.Error?.Message ?? "Playback could not continue."); }
        UpdateAdmission();
        if (playIntent && !seekPreparing && !continuing && playback.State == MediaPlaybackState.Stopped && media is not null &&
            playback.Position.CompareTo(audition.End) >= 0 && playback.Position.CompareTo(PreviewEnd) < 0)
            ContinuePlayback();
    }
    private async void ContinuePlayback()
    {
        continuing = true;
        try { await Track(SeekTo(Seconds(playback!.Position), true)); }
        finally { continuing = false; }
    }
    private async Task SeekTo(double seconds, bool play, RationalTime? exact = null)
    {
        if (document is null || media is null || playback is null || closed) return;
        seekWork?.Cancel(); seekWork?.Dispose(); seekWork = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var work = seekWork; playIntent = play; seekPreparing = true;
        double duration = Seconds(PreviewEnd);
        // At the half-open end use the inspected final PTS, never an FPS estimate or a fixed offset from EOF.
        var last = media.Video[0].LastFrame!.SourceOffset;
        var start = exact ?? (seconds >= duration || !play && seconds >= Seconds(last) ? last : Time(Math.Max(0, seconds)));
        var end = Seconds(start) + 15 >= duration ? PreviewEnd : Time(Seconds(start) + 15);
        audition = new(start, end);
        var original = document with { Recipe = ((VideoRecipe)document.Recipe) with { Crop = null, Canvas = Plane, Mute = !play || silentPreview } };
        try
        {
            if (play) await playback.PlayAsync(original, audition, false, work.Token);
            else await playback.PreparePausedAsync(original, audition, false, work.Token);
            if (!closed && ReferenceEquals(work, seekWork)) { if (!invalidPercent) Error(""); RefreshFrame(); }
        }
        catch (OperationCanceledException) { }
        catch (AudioDeviceUnavailableException) when (!silentPreview && ReferenceEquals(work, seekWork) && !work.IsCancellationRequested)
        {
            silentPreview = true;
            if (!closed) { Notice("Audio output unavailable. Preview plays silently; saved audio is unchanged."); await SeekTo(seconds, play, exact); }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!closed && ReferenceEquals(work, seekWork)) { playIntent = false; Error(error.Message); } }
        finally { if (ReferenceEquals(work, seekWork)) seekPreparing = false; }
    }
    private async void PlayPause(object sender, RoutedEventArgs e)
    {
        if (playback is null || media is null) return;
        if (playback.State == MediaPlaybackState.Playing) { playIntent = false; seekWork?.Cancel(); playback.Pause(); RefreshFrame(); }
        else await Track(SeekTo(playback.Position.CompareTo(PreviewEnd) >= 0 ? 0 : Seconds(playback.Position), true));
    }
    private async void NextFrame(object sender, RoutedEventArgs e)
        => await Track(StepFrame(1));
    private async void PreviousFrame(object sender, RoutedEventArgs e)
        => await Track(StepFrame(-1));
    private async Task StepFrame(int direction)
    {
        if (playback is null || document is null || media is null) return;
        playIntent = false; playback.Pause();
        seekWork?.Cancel(); seekWork?.Dispose(); seekWork = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var work = seekWork;
        try
        {
            var next = await sessionProbe!.GetAdjacentFrameAsync(document.Sources[0].Id, media.Video[0].Index, new(new(0, 1), PreviewEnd), displayedTime ?? playback.Position, direction, work.Token);
            if (next is not null && !closed && ReferenceEquals(work, seekWork) && !work.IsCancellationRequested)
                await SeekTo(Seconds(next.SourceOffset), false, next.SourceOffset);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!closed && ReferenceEquals(work, seekWork)) Error(error.Message); }
    }
    private void TimelineChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (syncing || document is null || !scrubbing) return; PositionText.Text = Timecode(Time(e.NewValue)); seekTimer.Stop(); seekTimer.Start(); }
    private void ScrubStarted(object sender, MouseButtonEventArgs e) { scrubbing = true; resumeAfterScrub = playback?.State == MediaPlaybackState.Playing; playIntent = false; seekWork?.Cancel(); playback?.Pause(); }
    private async void ScrubReleased(object sender, MouseButtonEventArgs e) { seekTimer.Stop(); scrubbing = false; await Track(SeekTo(Timeline.Value, resumeAfterScrub)); }
    private async void SeekKeyReleased(object sender, KeyEventArgs e)
    { if (IsSeekKey(e.Key)) { keyboardSeeking = false; await Track(SeekTo(Timeline.Value, false)); } }
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || document is null) return;
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Space && e.OriginalSource is not Button) { PlayPause(this, e); e.Handled = true; }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.Z or Key.Y && history is not null)
        {
            if (e.Key == Key.Z ? history.Undo() : history.Redo())
            { CropHandles.CancelDrag(); document = history.Current; syncing = true; AspectRatio.SelectedIndex = 0; syncing = false; ratio = null; SyncCrop(); }
            e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.D0) { ResetCrop(this, e); e.Handled = true; }
    }
    private void Error(string text) { ErrorText.Text = text; ErrorText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }
    private void Notice(string text)
    {
        PlaybackNotice.Text = text;
        PlaybackNoticeOverlay.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        PreviewArea.ToolTip = string.IsNullOrWhiteSpace(text) ? null : text;
    }
    private async Task Track(Task task)
    {
        commands.Add(task);
        try { await task; }
        finally { commands.Remove(task); }
    }
    private static double Seconds(RationalTime time) => (double)time.Numerator / time.Denominator;
    private static bool IsSeekKey(Key key) => key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown;
    private static Geometry FrozenShape(string data) { var geometry = Geometry.Parse(data); geometry.Freeze(); return geometry; }
    private static RationalTime Time(double seconds) => new(checked((long)Math.Round(seconds * 1_000_000)), 1_000_000);
    private static string Timecode(RationalTime time)
    {
        var span = TimeSpan.FromSeconds(Seconds(time));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss\.ff", CultureInfo.InvariantCulture) : ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + ":" + span.ToString(@"ss\.ff", CultureInfo.InvariantCulture);
    }
}
