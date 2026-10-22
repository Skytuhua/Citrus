using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Citrus.Conversion;
using Citrus.Conversion.Editing.Media;
using Citrus.Core;
using Citrus.Core.Editing;
using Citrus.Windows.Editors.Media;
using Button = System.Windows.Controls.Button;
using Slider = System.Windows.Controls.Slider;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class VideoFrameNavigationTests(DesktopFixture desktop)
{
    [Fact]
    public Task Trim_strip_drags_the_playhead_without_moving_trim_handles_and_cancels_safely() => desktop.RunAsync(() =>
    {
        var trim = new VideoTrimSelectionView { Duration = 10, SelectionStart = 2, SelectionEnd = 8, Position = 4 };
        var window = new Window { Width = 360, Height = 160, Content = trim };
        var seeks = new List<double>(); var edits = new List<(double Start, double End, bool Commit)>();
        trim.SeekRequested += seeks.Add; trim.SelectionChanged += (start, end, commit) => edits.Add((start, end, commit));
        try
        {
            window.Show(); window.UpdateLayout();
            System.Windows.Point At(double seconds) => new(8 + seconds / 10 * (trim.ActualWidth - 16), 12);
            trim.BeginPointer(At(5)); trim.MovePointer(At(6));
            Assert.True(trim.IsDragging); Assert.Equal(6, trim.Position); Assert.Equal(5, seeks[0], 6); Assert.Equal(6, seeks[^1], 6); Assert.Empty(edits);
            trim.CancelDrag(); Assert.False(trim.IsDragging); Assert.Equal(4, seeks[^1]);
            trim.BeginPointer(At(2)); trim.MovePointer(At(3)); trim.EndPointer(At(3));
            Assert.Equal(3, trim.SelectionStart, 6); Assert.Equal(8, trim.SelectionEnd); Assert.True(edits[^1].Commit);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public void Exact_frame_mapping_roundtrips_a_trimmed_and_retimed_clip()
    {
        var clip = new MediaClip(Guid.NewGuid(), Guid.NewGuid(), 0, null, new(new(7, 30), new(90, 1)), 1.25);
        var actualFrame = new RationalTime(1001, 30000);
        Assert.Equal(actualFrame, VideoFrameNavigation.OutputPosition(clip, VideoFrameNavigation.SourcePosition(clip, actualFrame)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Real_frame_buttons_cross_preview_boundaries_and_seek_back_to_the_previous_source_frame(bool crop) => desktop.RunAsync(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "citrus-frame-navigation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var process = MediaProcess.Bundled(); var caps = await process.DiscoverCapabilitiesAsync(default);
            string raw = Path.Combine(root, "frames.bgra"), input = Path.Combine(root, "source.mp4");
            using (var file = File.Create(raw))
                for (int frame = 0; frame < 36; frame++)
                    for (int pixel = 0; pixel < 32 * 24; pixel++) file.Write([(byte)(frame * 5), 64, 128, 255]);
            await process.RunAsync(["-f", "rawvideo", "-pixel_format", "bgra", "-video_size", "32x24", "-framerate", "1", "-i", raw, "-c:v", "mpeg4", "-q:v", "2", input], null, default);
            var engine = new MediaEditorEngine(process, caps, new());
            Window window = crop ? new VideoCropWindow(new(process, caps, new())){ConfirmDiscard=()=>true} : new MediaEditorWindow(engine){ConfirmDiscard=()=>true};
            var operation = crop ? VideoCropEngine.Operation : engine.Operations.Single(o => o.Id == "video.mute");
            Task? checking = null;
            window.Loaded += (_, _) => checking = Check();
            var request = new EditorOpenRequest(operation, [input], operation.Label, new(), root, SubmissionOrigin.Home);
            if (crop) await ((VideoCropWindow)window).ShowEditorAsync(request, default); else await ((MediaEditorWindow)window).ShowEditorAsync(request, default);
            await checking!;

            async Task Check()
            {
                try
                {
                    await Wait(() => crop ? ((Button)window.FindName("ApplyButton")).IsEnabled : ((MediaEditorWindow)window).IsReady);
                    MediaTransport? transport = crop ? null : (MediaTransport)window.FindName("VideoTransport");
                    var controller = crop ? ((VideoCropWindow)window).Playback! : (MediaPreviewController)typeof(MediaEditorWindow).GetField("playback", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                    var timeline = crop ? (Slider)window.FindName("Timeline") : (Slider)window.FindName("FullTimeline");
                    await Seek(20); Click(-1); await WaitAt(19);
                    // Reopen the 0–15 second audition, then step through its upper edge.
                    await Seek(0);
                    for (int second = 1; second <= 15; second++) { Click(1); await WaitAt(second); }
                    Assert.Equal(new RationalTime(15, 1), controller.Position);

                    async Task Seek(int seconds)
                    {
                        timeline.Value = seconds;
                        if (crop) timeline.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Right) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
                        await WaitAt(seconds);
                    }
                    Task WaitAt(int seconds) => Wait(() => controller.State == MediaPlaybackState.Paused && controller.Position == new RationalTime(seconds, 1) && (crop || transport!.PresentedTime == new RationalTime(seconds, 1)));
                    void Click(int direction) => ((Button)(crop ? window.FindName(direction < 0 ? "PreviousFrameButton" : "NextFrameButton") : transport!.FindName(direction < 0 ? "Previous" : "Next"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                finally { window.Close(); }
            }
        }
        finally { Directory.Delete(root, true); }
    }, TimeSpan.FromSeconds(60));

    private static async Task Wait(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
}
