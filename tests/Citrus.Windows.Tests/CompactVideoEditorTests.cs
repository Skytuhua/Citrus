using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Citrus.Conversion;
using Citrus.Conversion.Editing.Media;
using Citrus.Core;
using Citrus.Core.Editing;
using Citrus.Windows.Editors.Media;
using Image = System.Windows.Controls.Image;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Point = System.Windows.Point;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class CompactVideoEditorTests(DesktopFixture desktop)
{
    [Fact]
    public Task Failed_source_inspection_keeps_navigation_available_and_can_return_to_a_valid_video() => desktop.RunAsync(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "citrus-source-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var process = MediaProcess.Bundled(); var engine = new MediaEditorEngine(process, await process.DiscoverCapabilitiesAsync(default), new());
            string raw = Path.Combine(root, "frames.bgra"), first = Path.Combine(root, "first.mp4"), second = Path.Combine(root, "second.mp4");
            await File.WriteAllBytesAsync(raw, Enumerable.Repeat(new byte[] { 32, 64, 128, 255 }, 32 * 24 * 12).SelectMany(pixel => pixel).ToArray());
            await process.RunAsync(["-f", "rawvideo", "-pixel_format", "bgra", "-video_size", "32x24", "-framerate", "12", "-i", raw, "-c:v", "mpeg4", first], null, default);
            File.Copy(first, second); var operation = engine.Operations.Single(item => item.Id == "video.mute");
            var window = new MediaEditorWindow(engine){ConfirmDiscard=()=>true}; Task? checking = null;
            window.Loaded += (_, _) => checking = Check();
            await window.ShowEditorAsync(new(operation, [first, second], operation.Label, new(), root, SubmissionOrigin.Home), default); await checking!;
            async Task Check()
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    while (!window.IsReady) await Task.Delay(20, timeout.Token);
                    var picker = Descendants(window).OfType<ComboBox>().Single(item => item.Items.Count == 2 && Equals(item.Items[0], "first.mp4"));
                    await File.AppendAllTextAsync(second, "changed after admission"); picker.SelectedIndex = 1;
                    var status = (TextBlock)window.FindName("StatusText");
                    while (!status.Text.Contains("changed", StringComparison.OrdinalIgnoreCase)) await Task.Delay(20, timeout.Token);
                    Assert.False(((Button)window.FindName("ApplyButton")).IsEnabled); Assert.True(picker.IsEnabled);
                    Assert.False(((MediaTransport)window.FindName("VideoTransport")).IsEnabled);
                    picker.SelectedIndex = 0;
                    while (!window.IsReady) await Task.Delay(20, timeout.Token);
                    Assert.Equal(first, window.CurrentDocument!.Sources[0].Path);
                    Assert.True(((Button)window.FindName("ApplyButton")).IsEnabled);
                    Assert.True(((MediaTransport)window.FindName("VideoTransport")).IsEnabled);
                }
                finally { window.Close(); }
            }
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public Task Media_window_starts_with_a_laptop_sized_default() => desktop.RunAsync(() =>
    {
        var empty = new HashSet<string>();
        string unused = Path.Combine(Path.GetTempPath(), "citrus-layout-only.exe");
        var engine = new MediaEditorEngine(new MediaProcess(unused, unused), new(empty, empty, empty, empty, false), new());
        var window = new MediaEditorWindow(engine){ConfirmDiscard=()=>true};
        Assert.InRange(window.Height, 400, 560);
        Assert.InRange(window.Width, 440, 680);
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Crop_window_does_not_force_a_tall_laptop_menu() => desktop.RunAsync(() =>
    {
        var empty = new HashSet<string>(); string unused = Path.Combine(Path.GetTempPath(), "citrus-layout-only.exe");
        var window = new VideoCropWindow(new(new MediaProcess(unused, unused), new(empty, empty, empty, empty, false), new()));
        Assert.InRange(window.Height, 400, 540); Assert.True(window.MinHeight <= 400);
        window.Close(); return Task.CompletedTask;
    });

    [Theory]
    [InlineData("video.compress")]
    [InlineData("video.metadata")]
    [InlineData("video.mute")]
    [InlineData("video.trim")]
    [InlineData("video.speed")]
    [InlineData("video.snapshot")]
    [InlineData("video.split")]
    [InlineData("video.redact")]
    [InlineData("video.resize")]
    public Task Every_video_tool_has_a_real_pinned_picture_and_no_numeric_trim_form(string id) => desktop.RunAsync(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "citrus-compact-video-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var process = MediaProcess.Bundled();
            var engine = new MediaEditorEngine(process, await process.DiscoverCapabilitiesAsync(default), new());
            string raw = Path.Combine(root, "frames.bgra"), input = Path.Combine(root, "source.mp4");
            using (var file = File.Create(raw))
                for (int frame = 0; frame < 12; frame++)
                    for (int pixel = 0; pixel < 96 * 64; pixel++) file.Write([(byte)(frame * 12), 64, 192, 255]);
            await process.RunAsync(["-f", "rawvideo", "-pixel_format", "bgra", "-video_size", "96x64", "-framerate", "12", "-i", raw, "-c:v", "mpeg4", "-q:v", "2", input], null, default);
            var operation = engine.Operations.Single(o => o.Id == id);
            var window = new MediaEditorWindow(engine){ConfirmDiscard=()=>true};
            EditorDocument? edited = null;
            var opening = Stopwatch.StartNew();
            Task? checking = null;
            window.Loaded += (_, _) => checking = Check();
            await window.ShowEditorAsync(new(operation, [input], operation.Label, new(), root, SubmissionOrigin.Home), default);
            await checking!;
            if (edited is not null)
            {
                byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(input));
                string output = Assert.Single((await engine.ExecuteAsync(new(operation, [input], new(), root, edited), null, default)).Outputs);
                var result = await process.ProbeAsync(output, default);
                if (id == "video.trim") Assert.InRange(result.DurationSeconds, .49, .59);
                else { Assert.InRange(result.DurationSeconds, .99, 1.1); Assert.Equal(40, result.Width); Assert.Equal(20, result.Height); }
                Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(input)));
            }
            async Task Check()
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                    while (!window.IsReady) await Task.Delay(20, timeout.Token);
                    window.UpdateLayout();
                    var transport = Assert.IsType<MediaTransport>(window.FindName("VideoTransport"));
                    Assert.IsAssignableFrom<BitmapSource>(((Image)transport.FindName("Preview")).Source);
                    AssertPicture(transport, 320, 140);
                    VisualArtifacts.Capture((FrameworkElement)window.Content, "compact-video-" + id.Replace('.', '-'), 1, ThemeChoice.Light,
                        new { sourceDurationSeconds = 1, pausedPictureReadyMs = opening.Elapsed.TotalMilliseconds, scope = "One tiny local fixture; complete edited window readiness, not a general latency guarantee or native scanout." });
                    Assert.DoesNotContain(Descendants(window).OfType<TextBox>(), box =>
                        System.Windows.Automation.AutomationProperties.GetName(box).StartsWith("Trim ", StringComparison.Ordinal));
                    var scroll = Assert.IsType<ScrollViewer>(window.FindName("VideoToolsScroll"));
                    Point before = transport.TranslatePoint(new(0, 0), window);
                    scroll.ScrollToEnd(); window.UpdateLayout();
                    Assert.Equal(before, transport.TranslatePoint(new(0, 0), window));
                    window.Width = 440; window.Height = 400; window.UpdateLayout();
                    AssertPicture(transport, 180, 80);
                    VisualArtifacts.AssertInside(transport, (FrameworkElement)window.Content);
                    VisualArtifacts.AssertInside((Button)window.FindName("ApplyButton"), (FrameworkElement)window.Content);
                    if (id == "video.trim")
                    {
                        Assert.Equal(Visibility.Collapsed, ((Slider)window.FindName("FullTimeline")).Visibility);
                        var selection = Assert.IsType<VideoTrimSelectionView>(window.FindName("VideoTrimSelection"));
                        Point At(double seconds) => new(8 + seconds * (selection.ActualWidth - 16), 12);
                        selection.BeginPointer(At(0)); selection.MovePointer(At(.25));
                        Assert.False(((Button)window.FindName("ApplyButton")).IsEnabled);
                        Assert.Equal(new RationalTime(0, 1), ((VideoRecipe)window.CurrentDocument!.Recipe).Clips[0].Range.Start);
                        selection.EndPointer(At(.25));
                        selection.BeginPointer(At(1)); selection.MovePointer(At(.75)); selection.EndPointer(At(.75));
                        var video = Assert.IsType<VideoRecipe>(window.CurrentDocument!.Recipe);
                        Assert.Equal(new RationalTime(1, 4), video.Clips[0].Range.Start);
                        Assert.Equal(new RationalTime(3, 4), video.Clips[0].Range.End);
                        Assert.Equal(1, ((Slider)window.FindName("FullTimeline")).Maximum);
                        edited = window.CurrentDocument;
                    }
                    if (id == "video.resize")
                    {
                        var options = Assert.IsType<Expander>(window.FindName("ResizeOptions")); Assert.False(options.IsExpanded); options.IsExpanded = true;
                        var width = Assert.IsType<TextBox>(window.FindName("ResizeWidth")); var height = Assert.IsType<TextBox>(window.FindName("ResizeHeight"));
                        width.Text = "1"; Assert.False(((Button)window.FindName("ApplyButton")).IsEnabled);
                        width.Text = "40"; Commit(width); Assert.Equal(new SizeI(40, 26), ((VideoRecipe)window.CurrentDocument!.Recipe).Canvas);
                        var beforeInvalid = window.CurrentDocument;
                        height.Text = "8190"; Commit(height); // Locked aspect would infer width 12285, outside the encoder limit.
                        Assert.Same(beforeInvalid, window.CurrentDocument);
                        Assert.False(((Button)window.FindName("ApplyButton")).IsEnabled);
                        Assert.Contains("8190", ((TextBlock)window.FindName("StatusText")).Text);
                        height.Text = "20"; Commit(height); Assert.Equal(new SizeI(30, 20), ((VideoRecipe)window.CurrentDocument!.Recipe).Canvas);
                        width.Text = "40"; Commit(width); Assert.Equal(new SizeI(40, 26), ((VideoRecipe)window.CurrentDocument!.Recipe).Canvas);
                        var preserve = Assert.IsType<System.Windows.Controls.CheckBox>(window.FindName("PreserveVideoAspect")); preserve.IsChecked = false; preserve.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        height.Text = "20"; Commit(height); Assert.Equal(new SizeI(40, 20), ((VideoRecipe)window.CurrentDocument!.Recipe).Canvas);
                        Assert.True(((Button)window.FindName("ApplyButton")).IsEnabled); edited = window.CurrentDocument;
                        Descendants(window).OfType<Button>().Single(button => Equals(button.Content, "Reset")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        while (!window.IsReady) await Task.Delay(20, timeout.Token);
                        Assert.IsType<TextBox>(window.FindName("ResizeWidth"));
                        Assert.Equal(new SizeI(96, 64), ((VideoRecipe)window.CurrentDocument!.Recipe).Canvas);
                        void Commit(TextBox field) => field.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, System.Windows.Input.Key.Enter) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
                    }
                }
                finally { window.Close(); }
            }
        }
        finally { Directory.Delete(root, true); }
    }, TimeSpan.FromSeconds(45));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void AssertPicture(MediaTransport transport, double minimumWidth, double minimumHeight)
    {
        var preview = Assert.IsType<Image>(transport.FindName("Preview"));
        var picture = Assert.IsType<Grid>(transport.FindName("Picture"));
        Assert.True(picture.ActualWidth >= minimumWidth && picture.ActualHeight >= minimumHeight,
            $"Video picture is {picture.ActualWidth:0.##} × {picture.ActualHeight:0.##}, expected at least {minimumWidth} × {minimumHeight}.");
        // A stretch/centering regression can leave the outer transport large while shrinking its image.
        Assert.True(preview.ActualWidth >= minimumWidth && preview.ActualHeight >= minimumHeight,
            $"Video image area is {preview.ActualWidth:0.##} × {preview.ActualHeight:0.##}.");
    }
}
