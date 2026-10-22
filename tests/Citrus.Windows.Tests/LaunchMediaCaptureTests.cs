using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Citrus.Conversion;
using Citrus.Conversion.Editing.Media;
using Citrus.Core;
using Citrus.Core.Editing;
using Citrus.Windows.Editors;
using Citrus.Windows.Editors.Media;
using Citrus.Windows.Presentation;
using ImageMagick;
using Image = System.Windows.Controls.Image;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Path = System.IO.Path;

namespace Citrus.Windows.Tests;

/// <summary>
/// Opt-in launch authoring. Exports real retained WPF windows and the production glass shader
/// over an owned source. These edited demonstrations do not record Explorer, desktop pixels,
/// native input timing, DWM scanout or undisclosed user material.
/// </summary>
[Collection("Desktop")]
public sealed class LaunchMediaCaptureTests(DesktopFixture desktop)
{
    [Fact]
    public Task Owned_launch_samples_use_current_native_UI_and_reopened_engine_outputs()
    {
        if (Environment.GetEnvironmentVariable("CITRUS_BUILD_LAUNCH_MEDIA") != "1") return Task.CompletedTask;
        return desktop.RunAsync(CaptureAsync, TimeSpan.FromMinutes(5));
    }

    private static async Task CaptureAsync()
    {
        string photo = RequiredFile("CITRUS_LAUNCH_PHOTO");
        string video = RequiredFile("CITRUS_LAUNCH_VIDEO");
        string output = Environment.GetEnvironmentVariable("CITRUS_LAUNCH_OUTPUT")
            ?? throw new InvalidOperationException("Set an absolute CITRUS_LAUNCH_OUTPUT authoring directory.");
        if (!Path.IsPathFullyQualified(output)) throw new ArgumentException("Launch output must be absolute.");
        Directory.CreateDirectory(output);
        byte[] photoHash = SHA256.HashData(await File.ReadAllBytesAsync(photo));
        byte[] videoHash = SHA256.HashData(await File.ReadAllBytesAsync(video));
        using var themes = new ThemeManager(Application.Current);
        using var presentation = new WindowPresentationService(Application.Current, themes, () => new CitrusSettings { ReduceMotion = true, SoundEnabled = false });
        var conversion = new ImageEngine();
        var webp = conversion.Operations.Single(item => item.Id == "image.convert.webp");
        string converted = Assert.Single((await conversion.ExecuteAsync(new(webp, [photo], new(), output), null, default)).Outputs);
        using (var decoded = new MagickImage(converted)) { Assert.Equal(MagickFormat.WebP, decoded.Format); Assert.True(decoded.Width > 100); }
        File.Copy(converted, Path.Combine(output, "converted-output.webp"), true);
        await CaptureImage("image.editor", "Adjust", "image", photo, output, presentation);
        await CaptureImage("image.background", "Background", "background", photo, output, presentation);
        CaptureWheel(photo, output);
        await CaptureVideo(video, output, presentation);
        Assert.Equal(photoHash, SHA256.HashData(await File.ReadAllBytesAsync(photo)));
        Assert.Equal(videoHash, SHA256.HashData(await File.ReadAllBytesAsync(video)));
        Json(Path.Combine(output, "capture-record.json"), new
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            Renderer = "Current production WPF visual trees at 2x raster scale; wheel also uses current production D3D11 shader on WARP",
            Scope = "Edited product demonstrations using owned sample files; not live Explorer recordings or native timing/scanout evidence",
            OriginalPhotoSha256 = Convert.ToHexString(photoHash), OriginalVideoSha256 = Convert.ToHexString(videoHash),
            SourcePreservationVerified = true, DesktopPixelsRead = false, CaptureExclusionChanged = false,
            Outputs = Directory.GetFiles(output, "*-output.*").Select(Path.GetFileName).ToArray()
        });
    }

    private static async Task CaptureImage(string id, string tool, string name, string input, string output, WindowPresentationService presentation)
    {
        using var services = new ImageEditorServices(new());
        var operation = services.Engine.Operations.Single(item => item.Id == id);
        var window = new EditorHostWindow(services);
        presentation.Attach(window, (MaterialSurface)window.Content);
        using var close = new CancellationTokenSource();
        Task? exercise = null; EditorDocument? edited = null;
        window.Loaded += (_, _) => exercise = Exercise();
        await window.ShowEditorAsync(new(operation, [input], tool, new(), output, SubmissionOrigin.Home), close.Token);
        if (exercise is not null) await exercise;
        Assert.NotNull(edited);
        var result = await services.Engine.ExecuteAsync(new(operation, [input], new(), output, edited), null, default);
        string published = Assert.Single(result.Outputs);
        using var reopened = new MagickImage(published);
        Assert.True(reopened.Width > 100 && reopened.Height > 100);
        string extension = Path.GetExtension(published);
        File.Copy(published, Path.Combine(output, name + "-output" + extension), true);
        Json(Path.Combine(output, name + "-output.json"), new { PublishedFile = Path.GetFileName(published), ReopenedFormat = reopened.Format.ToString(), reopened.Width, reopened.Height, Sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(published))) });

        async Task Exercise()
        {
            try
            {
                var preview = (Image)window.FindName("PreviewImage");
                await Until(() => preview.Source is not null);
                SaveWindow(window, Path.Combine(output, name + "-before.png"));
                if (id == "image.editor")
                {
                    var before = preview.Source;
                    var exposure = Descendants(window).OfType<Slider>().Single(item => AutomationProperties.GetName(item) == "Exposure · EV");
                    exposure.Value = .2;
                    await Until(() => preview.Source is not null && !ReferenceEquals(preview.Source, before));
                    var brightness = Descendants(window).OfType<Slider>().Single(item => AutomationProperties.GetName(item) == "Brightness");
                    before = preview.Source; brightness.Value = .03;
                    await Until(() => preview.Source is not null && !ReferenceEquals(preview.Source, before));
                }
                SaveWindow(window, Path.Combine(output, name + "-after.png"));
                edited = window.Model!.Selected!.Draft!;
            }
            finally { close.Cancel(); }
        }
    }

    private static void CaptureWheel(string photo, string output)
    {
        var wheel = new WheelWindow(new OperationCatalog([]));
        try
        {
            wheel.ApplySettings(new CitrusSettings { ReduceMotion = true, SoundEnabled = false });
            SingleBackingVisualEvidenceTests.Populate(wheel); wheel.Show(); wheel.UpdateLayout();
            Invoke(wheel, "SetMaterialState", BackdropState.LiveRefractiveGlass);
            var scene = GlassScene.WheelPresentation(7, WheelPresentationMetrics.Scale * 2, false);
            var sourceBitmap = BitmapFrame.Create(new Uri(photo));
            double fit = Math.Max((double)scene.CaptureWidth / sourceBitmap.PixelWidth, (double)scene.CaptureHeight / sourceBitmap.PixelHeight);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                dc.DrawImage(sourceBitmap, new Rect((scene.CaptureWidth - sourceBitmap.PixelWidth * fit) / 2, (scene.CaptureHeight - sourceBitmap.PixelHeight * fit) / 2, sourceBitmap.PixelWidth * fit, sourceBitmap.PixelHeight * fit));
            var source = new RenderTargetBitmap(scene.CaptureWidth, scene.CaptureHeight, 96, 96, PixelFormats.Pbgra32); source.Render(visual);
            byte[] bgra = new byte[scene.CaptureWidth * scene.CaptureHeight * 4]; source.CopyPixels(bgra, scene.CaptureWidth * 4, 0);
            byte[] optical = new byte[scene.PixelWidth * scene.PixelHeight * 4];
            using (var renderer = D3D11StudioRenderer.CreateForTesting(scene, software: true))
                renderer.RenderPixels(bgra, scene.CaptureWidth, scene.CaptureHeight, 0, 0, new(NavigationVisible: false), optical);
            var glass = (Image)wheel.FindName("GlassBody");
            glass.Source = BitmapSource.Create(scene.PixelWidth, scene.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, optical, scene.PixelWidth * 4); glass.Visibility = Visibility.Visible;
            for (int selection = 0; selection < 7; selection++)
            {
                Invoke(wheel, "Select", selection); wheel.UpdateLayout();
                var rendered = new RenderTargetBitmap(scene.PixelWidth, scene.PixelHeight, 192, 192, PixelFormats.Pbgra32); rendered.Render(wheel);
                Save(rendered, Path.Combine(output, $"wheel-{selection}.png"));
            }
            Json(Path.Combine(output, "wheel.json"), new { RasterScale = 2, scene.Scale, scene.PixelWidth, scene.PixelHeight, Material = "Production shader over the owned photograph. Continuous glass backing with actual WPF petals/labels. Transparent outside contour.", Selection = new[] { "PNG", "WEBP", "HEIC", "TIFF", "AVIF", "BMP", "PDF" }, Input = "Production selection method invoked by authoring harness; not low-level input or Explorer." });
        }
        finally { wheel.Close(); }
    }

    private static async Task CaptureVideo(string input, string output, WindowPresentationService presentation)
    {
        var process = MediaProcess.Bundled(); var engine = new MediaEditorEngine(process, await process.DiscoverCapabilitiesAsync(default), new());
        var operation = engine.Operations.Single(item => item.Id == "video.trim"); var window = new MediaEditorWindow(engine){ConfirmDiscard=()=>true};
        presentation.Attach(window, (MaterialSurface)window.Content);
        Task? exercise = null; EditorDocument? edited = null; var observed = new List<object>();
        window.Loaded += (_, _) => exercise = Exercise();
        await window.ShowEditorAsync(new(operation, [input], operation.Label, new(), output, SubmissionOrigin.Home), default);
        if (exercise is not null) await exercise;
        Assert.NotNull(edited);
        string published = Assert.Single((await engine.ExecuteAsync(new(operation, [input], new(), output, edited), null, default)).Outputs);
        var reopened = await process.ProbeAsync(published, default);
        Assert.InRange(reopened.DurationSeconds, 5.2, 5.5);
        File.Copy(published, Path.Combine(output, "video-trim-output.mp4"), true);
        Json(Path.Combine(output, "video.json"), new { InputDuration = 8, TrimStart = 1.2, TrimEnd = 6.5, ReopenedDurationSeconds = reopened.DurationSeconds, reopened.Width, reopened.Height, PlaybackSamples = observed, OutputSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(published))), Qualification = "Actual native UI visual-tree frames during real processed local playback. Edited into a paced demonstration, not a real-time screen recording or performance measurement." });
        async Task Exercise()
        {
            try
            {
                await Until(() => window.IsReady);
                SaveWindow(window, Path.Combine(output, "video-before.png"));
                var trim = (VideoTrimSelectionView)window.FindName("VideoTrimSelection");
                for (int frame = 0; frame <= 12; frame++)
                {
                    trim.SetHandle(true, 1.2 * frame / 12, false);
                    SaveWindow(window, Path.Combine(output, $"trim-handle-{frame:00}.png"));
                }
                trim.SetHandle(true, 1.2, true); trim.SetHandle(false, 6.5, true);
                var transport = (MediaTransport)window.FindName("VideoTransport");
                await Until(() => transport.Controller!.State == MediaPlaybackState.Paused && transport.PresentedTime is { } time && (double)time.Numerator / time.Denominator >= 1.15);
                SaveWindow(window, Path.Combine(output, "video-after.png")); edited = window.CurrentDocument;
                ((Button)transport.FindName("Play")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => transport.Controller!.State == MediaPlaybackState.Playing);
                for (int frame = 0; frame < 42; frame++)
                {
                    await Task.Delay(100);
                    SaveWindow(window, Path.Combine(output, $"playback-{frame:00}.png"));
                    var position = transport.Controller!.Position;
                    observed.Add(new { Frame = frame, ControllerSeconds = (double)position.Numerator / position.Denominator, PresentedTime = transport.PresentedTime });
                }
                transport.Controller!.Pause();
            }
            finally { window.Close(); }
        }
    }

    private static string RequiredFile(string name)
    {
        string value = Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException("Set " + name);
        if (!Path.IsPathFullyQualified(value) || !File.Exists(value)) throw new FileNotFoundException("Missing owned launch sample", value);
        return value;
    }
    private static void SaveWindow(Window window, string path) => Save(VisualArtifacts.Render((FrameworkElement)window.Content, 2), path);
    private static void Save(BitmapSource bitmap, string path) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream); }
    private static void Json(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    private static void Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static async Task Until(Func<bool> condition) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40)); while (!condition()) await Task.Delay(20, timeout.Token); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject value) { yield return value; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(value, i))) yield return child; }
}
