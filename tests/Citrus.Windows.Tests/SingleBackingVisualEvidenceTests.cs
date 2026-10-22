using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Citrus.Core;
using Citrus.Windows.Presentation;
using Brush = System.Windows.Media.Brush;
using Image = System.Windows.Controls.Image;
using ShapePath = System.Windows.Shapes.Path;

namespace Citrus.Windows.Tests;

/// <summary>Owned generated sources and the actual WPF foreground; never reads desktop pixels.</summary>
[Collection("Desktop")]
public sealed class SingleBackingVisualEvidenceTests(DesktopFixture desktop)
{
    [Theory]
    [InlineData("white", 1d)][InlineData("white", 1.5d)][InlineData("white", 2d)]
    [InlineData("dark", 1d)][InlineData("dark", 1.5d)][InlineData("dark", 2d)]
    [InlineData("colored", 1d)][InlineData("colored", 1.5d)][InlineData("colored", 2d)]
    [InlineData("dense", 1d)][InlineData("dense", 1.5d)][InlineData("dense", 2d)]
    public Task Production_GPU_backing_and_retained_foreground_generate_reference_comparisons(string pattern, double rasterScale) => desktop.RunAsync(() =>
    {
        var wheel = new WheelWindow(new OperationCatalog([]));
        try
        {
            Populate(wheel);
            wheel.Show();
            wheel.UpdateLayout();
            SetMaterial(wheel, BackdropState.LiveRefractiveGlass);
            Invoke(wheel, "Select", 6); // PDF is on the upper-left, as in the supplied reference.
            var scene = GlassScene.WheelPresentation(7, WheelPresentationMetrics.Scale * rasterScale, false);
            Assert.Equal(2, scene.Regions.Count); // One backing and the reusable, currently hidden page capsule.
            Assert.DoesNotContain(scene.Regions, region => region.Id is >= 2 and < 32);
            byte[] source = Source(scene, pattern);
            byte[] optical = new byte[scene.PixelWidth * scene.PixelHeight * 4];
            using (var renderer = D3D11StudioRenderer.CreateForTesting(scene, software: true))
                renderer.RenderPixels(source, scene.CaptureWidth, scene.CaptureHeight, 0, 0, new(NavigationVisible: false), optical);
            var glass = (Image)wheel.FindName("GlassBody");
            glass.Source = BitmapSource.Create(scene.PixelWidth, scene.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, optical, scene.PixelWidth * 4);
            glass.Visibility = Visibility.Visible;
            wheel.UpdateLayout();
            var petals = ((Canvas)wheel.FindName("Sectors")).Children.Cast<WheelPetal>().ToArray();
            Assert.Equal(7, petals.Length);
            foreach (var petal in petals)
            {
                Assert.All(petal.Children.OfType<ShapePath>().Take(2), shape => Assert.True(shape.Stroke is null || shape.StrokeThickness == 0));
                Assert.Null(petal.Children.OfType<TextBlock>().Single().Background);
            }
            // A native window may round its layout rectangle by a fraction of a DIP.
            // Export the nominal production canvas without stretching that retained tree.
            var rendered = new RenderTargetBitmap(scene.PixelWidth,
                scene.PixelHeight, 96 * rasterScale, 96 * rasterScale, PixelFormats.Pbgra32);
            rendered.Render(wheel);
            byte[] foreground = new byte[rendered.PixelWidth * rendered.PixelHeight * 4];
            rendered.CopyPixels(foreground, rendered.PixelWidth * 4, 0);
            Assert.Equal(scene.PixelWidth, rendered.PixelWidth);
            Assert.Equal(scene.PixelHeight, rendered.PixelHeight);
            byte[] final = Composite(foreground, source, scene);
            // The solid selected foreground must be immediate, independent of the GPU's accent state.
            var selectedPoint = WheelGeometry.At(118, 6 * 360d / 7 + 10);
            int selected = ((int)(selectedPoint.Y * scene.Scale) * scene.PixelWidth + (int)(selectedPoint.X * scene.Scale)) * 4;
            Assert.InRange(final[selected], 0, 2);
            Assert.InRange(final[selected + 1], 83, 87);
            Assert.InRange(final[selected + 2], 253, 255);
            string folder = Path.Combine(AppContext.BaseDirectory, "TestResults", "single-backing-wheel");
            Directory.CreateDirectory(folder);
            string name = $"wheel-{pattern}-{rasterScale:0.0}x";
            Save(Path.Combine(folder, name + ".png"), final, scene.PixelWidth, scene.PixelHeight);
            File.WriteAllText(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(new
            {
                Pattern = pattern, RasterScale = rasterScale, scene.Scale, scene.PixelWidth, scene.PixelHeight,
                Reference = "First supplied Tangerine wheel image; PDF selected at upper-left",
                Renderer = "Production D3D11 shader on WARP plus actual retained WheelWindow WPF foreground",
                DesktopPixelsRead = false, NativeCompositionQualified = false,
                DpiScope = "Nominal production canvas export at requested raster scale; actual WPF tree is not stretched; no monitor/DPI transition",
                InputScope = "Selection invoked through production method; no Explorer drag or audio audition"
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { wheel.Close(); }
        return Task.CompletedTask;
    }, TimeSpan.FromSeconds(45));

    [Fact]
    public Task Half_second_capture_delay_does_not_change_the_foreground_or_delay_selection() => desktop.RunAsync(async () =>
    {
        var wheel = new WheelWindow(new OperationCatalog([]));
        var frames = new FrameGate();
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var live = new LiveWheelGlass(Dispatcher.CurrentDispatcher, _ => SetMaterial(wheel, BackdropState.LiveRefractiveGlass),
            () => SetMaterial(wheel, BackdropState.Solid), exclude: _ => new Lease(), createSession: (scene, _, _) =>
            { created.TrySetResult(); return new ControlledSession(scene, frames); });
        live.StageChanged = stage => SetMaterial(wheel, stage == GlassPresentationStage.Pending ? BackdropState.PendingFrosted :
            stage == GlassPresentationStage.Live ? BackdropState.LiveRefractiveGlass : BackdropState.Solid);
        try
        {
            Populate(wheel);
            wheel.Show();
            live.SetPetalCount(7);
            live.StartWheelPresentation(0, 0, 128, false, false, 1);
            await created.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(BackdropState.PendingFrosted, wheel.MaterialState);
            var petals = ((Canvas)wheel.FindName("Sectors")).Children.Cast<WheelPetal>().ToArray();
            var pendingBrushes = BrushesOf(petals);
            Assert.All(petals, petal => Assert.IsType<LinearGradientBrush>(((ShapePath)petal.Children[0]).Fill));
            var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            { Invoke(wheel, "Select", 6); response.SetResult(); }));
            await response.Task.WaitAsync(TimeSpan.FromMilliseconds(250));
            Assert.Equal("PDF", ((TextBlock)wheel.FindName("Caption")).Text);
            var selected = (ShapePath)petals[6].Children[1];
            Assert.Equal(1, selected.Opacity);
            Assert.Equal(System.Windows.Media.Color.FromRgb(255, 85, 0), Assert.IsType<SolidColorBrush>(selected.Fill).Color);
            await Task.Delay(500);
            Assert.Equal(BackdropState.PendingFrosted, wheel.MaterialState);
            Assert.Equal(pendingBrushes, BrushesOf(petals));
            frames.Available = true;
            await Until(() => wheel.MaterialState == BackdropState.LiveRefractiveGlass);
            Assert.Equal(pendingBrushes, BrushesOf(petals));
            Assert.Equal(1, selected.Opacity);
        }
        finally { live.Stop(); await live.Completion.WaitAsync(TimeSpan.FromSeconds(5)); wheel.Close(); }
    });

    private static Brush[] BrushesOf(WheelPetal[] petals) => petals.SelectMany(petal => petal.Children.OfType<ShapePath>().Take(2)).Select(shape => shape.Fill).ToArray();
    internal static void Populate(WheelWindow wheel)
    {
        var operations = new[] { "PNG", "WEBP", "HEIC", "TIFF", "AVIF", "BMP", "PDF" }
            .Select(format => new OperationDescriptor("image.convert." + format.ToLowerInvariant(), format, OperationMode.Convert, "." + format.ToLowerInvariant(), false, false)).ToArray();
        typeof(WheelWindow).GetField("operations", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(wheel, operations);
        Invoke(wheel, "RenderSectors");
        ((FrameworkElement)wheel.FindName("Presentation")).Opacity = 1;
    }
    private static void SetMaterial(WheelWindow wheel, BackdropState state) => Invoke(wheel, "SetMaterialState", state);
    private static void Invoke(WheelWindow wheel, string method, params object[] args) => typeof(WheelWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(wheel, args);
    private static async Task Until(Func<bool> ready)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!ready() && clock.ElapsedMilliseconds < 5000) await Task.Delay(10);
        Assert.True(ready());
    }
    private static byte[] Source(GlassScene scene, string pattern)
    {
        var bytes = new byte[scene.CaptureWidth * scene.CaptureHeight * 4];
        for (int y = 0; y < scene.CaptureHeight; y++)
        for (int x = 0; x < scene.CaptureWidth; x++)
        {
            int i = (y * scene.CaptureWidth + x) * 4;
            double px = (x - scene.PaddingPixels) / scene.Scale, py = (y - scene.PaddingPixels) / scene.Scale;
            (byte r, byte g, byte b) = pattern switch
            {
                "dark" => ((byte)24, (byte)27, (byte)32),
                "colored" => ((byte)Math.Clamp(230 - py / 3, 24, 245), (byte)Math.Clamp(70 + px / 2, 30, 230), (byte)Math.Clamp(180 + py / 5, 40, 245)),
                "dense" => ((int)Math.Floor(py) % 15 is >= 6 and <= 8 && (int)Math.Floor(px) % 41 < 31) ||
                    ((int)Math.Floor(px) % 83 is >= 5 and <= 8 && (int)Math.Floor(py) % 23 < 17) ? ((byte)36, (byte)42, (byte)48) : ((byte)247, (byte)248, (byte)249),
                _ => ((byte)255, (byte)255, (byte)255)
            };
            bytes[i] = b; bytes[i + 1] = g; bytes[i + 2] = r; bytes[i + 3] = 255;
        }
        return bytes;
    }
    private static byte[] Composite(byte[] foreground, byte[] background, GlassScene scene)
    {
        var result = new byte[foreground.Length];
        for (int y = 0; y < scene.PixelHeight; y++)
        for (int x = 0; x < scene.PixelWidth; x++)
        {
            int i = (y * scene.PixelWidth + x) * 4;
            int s = ((y + scene.PaddingPixels) * scene.CaptureWidth + x + scene.PaddingPixels) * 4;
            double remainder = 1 - foreground[i + 3] / 255d;
            for (int channel = 0; channel < 3; channel++) result[i + channel] = (byte)Math.Clamp(Math.Round(foreground[i + channel] + background[s + channel] * remainder), 0, 255);
            result[i + 3] = 255;
        }
        return result;
    }
    private static void Save(string path, byte[] pixels, int width, int height)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4)));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private sealed class FrameGate { internal volatile bool Available; }
    private sealed class ControlledSession(GlassScene scene, FrameGate gate) : ILiveGlassSession
    {
        public GlassScene Scene => scene;
        public bool TryRenderFrame(GlassFrameState state, byte[] destination) { if (!gate.Available) return false; Array.Fill(destination, (byte)255); return true; }
        public void Dispose() { }
    }
    private sealed class Lease : IDisposable { public void Dispose() { } }
}
