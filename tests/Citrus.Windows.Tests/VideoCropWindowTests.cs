using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Threading;
using Citrus.Conversion;
using Citrus.Conversion.Editing.Media;
using Citrus.Core;
using Citrus.Core.Editing;
using Citrus.Windows.Editors;
using Citrus.Windows.Editors.Media;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using Slider = System.Windows.Controls.Slider;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class VideoCropWindowTests(DesktopFixture desktop)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Closing_an_uncommitted_crop_field_requires_discard_confirmation(bool escape) => desktop.RunAsync(async () =>
    {
        using var files = new Fixture(); await files.InitializeAsync();int confirmations=0;bool discard=false;
        var result=await Show(files,window=>
        {
            ((VideoCropWindow)window).ConfirmDiscard=()=>{confirmations++;return discard;};
            ((TextBox)window.FindName("WidthPercent")).Text="unfinished";
            if(escape)window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent});
            else window.Close();
            Assert.True(window.IsVisible);Assert.Equal(1,confirmations);
            discard=true;window.Close();Assert.False(window.IsVisible);Assert.Equal(2,confirmations);return Task.CompletedTask;
        });
        Assert.Null(result);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Unchanged_or_applied_crop_closes_without_discard_prompt(bool apply) => desktop.RunAsync(async () =>
    {
        using var files=new Fixture();await files.InitializeAsync();int confirmations=0;
        var result=await Show(files,window=>
        {
            ((VideoCropWindow)window).ConfirmDiscard=()=>{confirmations++;return false;};
            if(apply){((Slider)window.FindName("WidthSlider")).Value=60;Click(window,"ApplyButton");}
            else window.Close();
            Assert.False(window.IsVisible);Assert.Equal(0,confirmations);return Task.CompletedTask;
        });
        Assert.Equal(apply,result is not null);
    });

    [Fact]
    public Task Application_shutdown_closes_a_dirty_crop_editor_without_prompt() => desktop.RunAsync(async () =>
    {
        using var files=new Fixture();await files.InitializeAsync();int confirmations=0;
        await Show(files,window=>
        {
            ((VideoCropWindow)window).ConfirmDiscard=()=>{confirmations++;return false;};
            ((TextBox)window.FindName("WidthPercent")).Text="unfinished";
            var app=(App)System.Windows.Application.Current;var quitting=typeof(App).GetProperty(nameof(App.IsQuitting))!;
            try{quitting.SetValue(app,true);window.Close();Assert.False(window.IsVisible);Assert.Equal(0,confirmations);}
            finally{quitting.SetValue(app,false);}
            return Task.CompletedTask;
        });
    });

    [Fact]
    public Task Surround_input_plays_video_with_explicit_silent_preview_notice() => desktop.RunAsync(async () =>
    {
        using var files = new Fixture(); await files.InitializeAsync(audioSeconds: 1, audioChannels: 6);
        await Show(files, async window =>
        {
            Assert.Contains("surround", ((TextBlock)window.FindName("PlaybackNotice")).Text, StringComparison.OrdinalIgnoreCase);
            var playback = ((VideoCropWindow)window).Playback!;
            Click(window, "PlayButton");
            await Wait(() => playback.State == MediaPlaybackState.Playing && playback.Position.CompareTo(new(1, 10)) > 0);
            window.Close();
        });
    });

    [Theory]
    [InlineData(ThemeChoice.Light, 1d)]
    [InlineData(ThemeChoice.Dark, 1d)]
    [InlineData(ThemeChoice.Light, 2d)]
    public Task Minimum_window_keeps_controls_and_apply_reachable(ThemeChoice choice, double textScale) => desktop.RunAsync(async () =>
    {
        using var files = new Fixture(); await files.InitializeAsync();
        using var theme = new ThemeManager(System.Windows.Application.Current);
        try
        {
            theme.Apply(choice, true); theme.ApplyTypography(textScale);
            await Show(files, async window =>
            {
                Capture("default");
                window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout();
                var apply = (Button)window.FindName("ApplyButton");
                VisualArtifacts.AssertInside(apply, (FrameworkElement)window.Content);
                Capture("minimum");
                var more = (Expander)window.FindName("MoreOptions"); Assert.False(more.IsExpanded); more.IsExpanded = true; window.UpdateLayout();
                ((Slider)window.FindName("HeightSlider")).BringIntoView(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.True(((Slider)window.FindName("HeightSlider")).ActualWidth >= 80);
                VisualArtifacts.Capture((FrameworkElement)window.Content, $"video-crop-{choice}-text{textScale * 100:0}", 1, choice);
                window.Close();
                void Capture(string size)
                {
                    foreach(double raster in new[]{1d,1.5d,2d})
                        VisualArtifacts.Capture((FrameworkElement)window.Content,$"video-crop-reference-{size}-{choice}-text{textScale*100:0}-{raster.ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)}",raster,choice);
                }
            });
        }
        finally { theme.ApplyTypography(1); theme.Apply(ThemeChoice.Light, true); }
    });

    [Fact]
    public Task Next_frame_advances_across_bounded_preview_window() => desktop.RunAsync(async () =>
    {
        using var files = new Fixture(); await files.InitializeAsync(seconds: 17);
        await Show(files, async window =>
        {
            var playback = ((VideoCropWindow)window).Playback!;
            await playback.SeekAsync(new(449, 30), default);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Click(window, "NextFrameButton");
            await Wait(() => playback.State == MediaPlaybackState.Paused && playback.Position.CompareTo(new(15, 1)) >= 0);
            window.Close();
        });
    }, TimeSpan.FromSeconds(60));

    [Fact]
    public Task Video_timeline_ends_with_picture_when_audio_has_a_longer_tail() => desktop.RunAsync(async () =>
    {
        using var files = new Fixture(); await files.InitializeAsync(audioSeconds: 18);
        await Show(files, window =>
        {
            Assert.InRange(((Slider)window.FindName("Timeline")).Maximum, .99, 1.01);
            window.Close(); return Task.CompletedTask;
        });
    });

    [Fact]
    public Task Timeline_end_shows_actual_last_frame_and_closing_releases_playback() => desktop.RunAsync(async () =>
    {
        using var files = new Fixture(); await files.InitializeAsync();
        MediaPreviewController? owner = null;
        var batch = await Show(files, async window =>
        {
            owner = ((VideoCropWindow)window).Playback!;
            var timeline = (Slider)window.FindName("Timeline"); timeline.Value = timeline.Maximum;
            timeline.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.End) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
            await Wait(() => owner.State == MediaPlaybackState.Paused && owner.Position.CompareTo(new(9, 10)) > 0);
            var image = Assert.IsAssignableFrom<BitmapSource>(((Image)window.FindName("PreviewFrame")).Source);
            var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0);
            Assert.InRange(pixels[2], (byte)180, (byte)220); // Only the final actual frame has this red value.
            timeline.Value = 0;
            timeline.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Home) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
            await Wait(() => owner.State == MediaPlaybackState.Paused && owner.Position == new RationalTime(0, 1));
            Click(window, "PlayButton");
            await Wait(() => owner.State == MediaPlaybackState.Playing && owner.Position.CompareTo(new(1, 10)) > 0);
            window.Close();
        });
        Assert.Null(batch); Assert.NotNull(owner);
        Assert.Equal(MediaPlaybackState.Stopped, owner.State);
        Assert.True(owner.WaitForIdleAsync().IsCompleted);
    });

    [Fact]
    public Task Invalid_percent_stays_invalid_during_playback_refresh_and_reset_recovers() => desktop.RunAsync(async () =>
    {
        using var files = new Fixture(); await files.InitializeAsync();
        await Show(files, async window =>
        {
            var percent = (System.Windows.Controls.TextBox)window.FindName("HeightPercent"); percent.Text = "bad";
            percent.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            Click(window, "PlayButton");
            await Task.Delay(250);
            Assert.False(((Button)window.FindName("ApplyButton")).IsEnabled);
            Click(window, "ResetButton"); Assert.True(((Button)window.FindName("ApplyButton")).IsEnabled);
            window.Close();
        });
    });

    [Fact]
    public Task Interactive_crop_reset_seek_and_apply_submit_real_selected_pixels() => desktop.RunAsync(async () =>
    {
        using var files = new Fixture(); await files.InitializeAsync();
        var batch = await Show(files, async window =>
        {
            var preview = (Image)window.FindName("PreviewFrame");
            var initialSize = preview.RenderSize;
            ((Slider)window.FindName("HeightSlider")).Value = 62;
            var handles = (CropOverlay)window.FindName("CropHandles");
            Assert.Equal(new RectD(0, 23, 96, 74), handles.Crop);
            handles.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Down) { RoutedEvent = Keyboard.KeyDownEvent });
            Assert.Equal(24, handles.Crop.Y);
            Assert.Equal(initialSize, preview.RenderSize); // Crop does not shrink/reflow the original preview plane.
            Click(window, "ResetButton"); Assert.Equal(new RectD(0, 0, 96, 120), handles.Crop);
            ((Slider)window.FindName("HeightSlider")).Value = 62;
            var timeline = (Slider)window.FindName("Timeline"); timeline.Value = .5;
            timeline.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Right) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
            var playback = ((VideoCropWindow)window).Playback!;
            await Wait(() => playback.State == MediaPlaybackState.Paused && playback.Position.CompareTo(new(1, 2)) >= 0);
            VisualArtifacts.Capture((FrameworkElement)window.Content, "video-crop-selected", 1, ThemeChoice.Light, new { captureScope = "WPF layout and actual decoded local video, not native glass proof" });
            Click(window, "ApplyButton");
        });
        Assert.NotNull(batch);
        var recipe = Assert.IsType<VideoRecipe>(batch.Items.Single().Document.Recipe);
        Assert.Equal(new RectD(0, 23, 96, 74), recipe.Crop);
        Assert.Equal(new TimeRange(new(0, 1), new(1, 1)), recipe.Clips.Single().Range); // Scrubbing is not trimming.
        var submissions = new EditorSubmissionService(new([files.Engine]), files.Engine.Routes, files.Engine);
        var job = Assert.Single(await submissions.SubmitAsync(batch, default)); await job.Completion;
        Assert.Equal(ConversionJobState.Succeeded, job.State);
        Assert.True(File.Exists(job.Outputs.Single()));
    }, TimeSpan.FromSeconds(45));

    [Fact]
    public Task Cancel_returns_no_batch_and_aspect_lock_couples_both_dimensions() => desktop.RunAsync(async () =>
    {
        using var files = new Fixture(); await files.InitializeAsync();
        var batch = await Show(files, window =>
        {
            ((ComboBox)window.FindName("AspectRatio")).SelectedIndex = 2; // 1:1.
            ((Slider)window.FindName("WidthSlider")).Value = 50;
            var crop = ((CropOverlay)window.FindName("CropHandles")).Crop;
            Assert.Equal(48, crop.Width); Assert.Equal(48, crop.Height);
            window.Close();
            return Task.CompletedTask;
        });
        Assert.Null(batch); Assert.Single(Directory.GetFiles(files.Root, "*.mp4"));
    });

    private static async Task<EditorSubmissionBatch?> Show(Fixture fixture, Func<Window, Task> exercise)
    {
        var window = new VideoCropWindow(fixture.Engine){ConfirmDiscard=()=>true};
        var request = new EditorOpenRequest(VideoCropEngine.Operation, [fixture.Input], "Crop", new(), fixture.Root, SubmissionOrigin.Home);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += async (_, _) =>
        {
            if (window.FindName("PreviewFrame") is not Image { Source: BitmapSource } || window.FindName("ApplyButton") is not Button { IsEnabled: true }) return;
            timer.Stop();
            try { await exercise(window); }
            catch (Exception error) { failure = error; cancel.Cancel(); }
        };
        timer.Start();
        EditorSubmissionBatch? result;
        try { result = await window.ShowEditorAsync(request, cancel.Token); }
        finally { timer.Stop(); }
        if (failure is not null) throw failure;
        Assert.False(cancel.IsCancellationRequested, "Crop editor failed to publish a decoded preview before timeout.");
        return result;
    }
    private static void Click(Window window, string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Wait(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!ready()) await Task.Delay(20, timeout.Token);
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "citrus-crop-window-" + Guid.NewGuid().ToString("N"));
        public string Input => Path.Combine(Root, "portrait.mp4");
        public VideoCropEngine Engine { get; private set; } = null!;
        public async Task InitializeAsync(int seconds = 1, int audioSeconds = 0, int audioChannels = 1)
        {
            Directory.CreateDirectory(Root);
            string ffmpeg = Environment.GetEnvironmentVariable("CITRUS_TEST_FFMPEG") ?? Path.GetFullPath("../../../../../packaging/media/win-x64/ffmpeg.exe", AppContext.BaseDirectory);
            string ffprobe = Environment.GetEnvironmentVariable("CITRUS_TEST_FFPROBE") ?? Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
            var process = new MediaProcess(ffmpeg, ffprobe);
            string raw = Path.Combine(Root, "source.bgra");
            using (var stream = File.Create(raw))
                for (int frame = 0; frame < 30 * seconds; frame++) for (int y = 0; y < 120; y++) for (int x = 0; x < 96; x++)
                    stream.Write([(byte)(x < 48 ? 50 : 180), (byte)(y < 60 ? 170 : 60), (byte)(frame == 30 * seconds - 1 ? 200 : 20), 255]);
            await process.RunAsync(["-f", "rawvideo", "-pixel_format", "bgra", "-video_size", "96x120", "-framerate", "30", "-i", raw, "-c:v", "mpeg4", "-q:v", "2", "-pix_fmt", "yuv420p", Input], null, default);
            if (audioSeconds > 0)
            {
                string wave = Path.Combine(Root, "audio.wav"), muxed = Path.Combine(Root, "muxed.mp4");
                using (var wav = new BinaryWriter(File.Create(wave)))
                {
                    int samples = 48000 * audioSeconds;
                    wav.Write("RIFF"u8); wav.Write(36 + samples * 2 * audioChannels); wav.Write("WAVEfmt "u8); wav.Write(16); wav.Write((short)1); wav.Write((short)audioChannels);
                    wav.Write(48000); wav.Write(96000 * audioChannels); wav.Write((short)(2 * audioChannels)); wav.Write((short)16); wav.Write("data"u8); wav.Write(samples * 2 * audioChannels);
                    for (int i = 0; i < samples * audioChannels; i++) wav.Write((short)0);
                }
                await process.RunAsync(["-i", Input, "-i", wave, "-c:v", "copy", "-c:a", "aac", muxed], null, default);
                File.Move(muxed, Input, true);
            }
            Engine = new(process, await process.DiscoverCapabilitiesAsync(default), new());
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
