using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Citrus.Core;
using Citrus.Windows.Presentation;
using Application = System.Windows.Application;
using Image = System.Windows.Controls.Image;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class WheelCenterBackingTests(DesktopFixture desktop)
{
    [Fact]
    public Task Center_contains_only_the_small_capsule_in_neutral_and_selected_states() => desktop.RunAsync(() =>
    {
        var wheel = new WheelWindow(new OperationCatalog([]));
        var demo = new WheelDemoWindow();
        try
        {
            wheel.ApplySettings(new() { ReduceMotion = true, SoundEnabled = false });
            SingleBackingVisualEvidenceTests.Populate(wheel);
            foreach (int selection in new[] { -1, 0, -1 })
            {
                Invoke(wheel, "Select", selection);
                foreach (string removed in new[] { "ThumbnailFrame", "Preview", "FileGlyph", "CountBadge", "CountLabel" }) Assert.Null(wheel.FindName(removed));
                var capsule = (Border)wheel.FindName("CaptionPlate");
                Assert.Equal(110, capsule.Width); Assert.Equal(48, capsule.Height);
                Assert.Equal(new CornerRadius(24), capsule.CornerRadius);
                Assert.Equal(160, Canvas.GetTop(capsule));
                Assert.InRange(Assert.IsType<SolidColorBrush>(capsule.Background).Color.A, 240, 255);
            }
            var demoCapsule = Assert.Single(Get<Canvas>(demo, "wheelCanvas").Children.OfType<Border>());
            Assert.IsType<TextBlock>(demoCapsule.Child);
            Assert.Equal(110, demoCapsule.Width); Assert.Equal(48, demoCapsule.Height);
        }
        finally { wheel.Close(); demo.Close(); }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(1d)]
    [InlineData(1.5d)]
    [InlineData(2d)]
    public Task Real_wheel_fills_the_center_and_keeps_the_selected_label_visible_in_every_material(double scale) => desktop.RunAsync(() =>
    {
        using var theme = new ThemeManager(Application.Current);
        try
        {
            foreach (var palette in new[] { (ThemeChoice.Light, false), (ThemeChoice.Dark, false), (ThemeChoice.Light, true) })
            {
                theme.ApplyPalette(palette.Item1, true, palette.Item2);
                var wheel = new WheelWindow(new OperationCatalog([]));
                try
                {
                    wheel.ApplySettings(new() { ReduceMotion = true, SoundEnabled = false });
                    SingleBackingVisualEvidenceTests.Populate(wheel);
                    wheel.Show(); wheel.UpdateLayout(); Invoke(wheel, "Select", 0);
                    foreach (var state in new[] { BackdropState.Solid, BackdropState.PendingFrosted, BackdropState.ConfiguredNativeAcrylic, BackdropState.LiveRefractiveGlass })
                    {
                        Invoke(wheel, "SetMaterialState", state);
                        WheelForegroundStyle.Apply(wheel.Resources, palette.Item2);
                        var presentation = (Canvas)wheel.FindName("Presentation");
                        if (state != BackdropState.LiveRefractiveGlass) AssertFilledCenter(presentation, scale);
                        // Live material is supplied by its optical frame, not the fallback shell.
                        foreach (string name in new[] { "GlassBody", "GlassLighting" })
                        {
                            var image = (Image)wheel.FindName(name);
                            image.Source = OpaqueFrame(); image.Visibility = Visibility.Visible;
                        }
                        AssertFilledCenter(presentation, scale);
                        Assert.InRange(Pixel(Render(presentation, scale), 184, 184, scale)[3], 240, 255);
                        Assert.Equal("PNG", ((TextBlock)wheel.FindName("Caption")).Text);
                        foreach (string name in new[] { "GlassBody", "GlassLighting" }) ((Image)wheel.FindName(name)).Visibility = Visibility.Collapsed;
                    }
                }
                finally { wheel.Close(); }
            }
        }
        finally { theme.Apply(ThemeChoice.Light, true); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task Page_visibility_and_reopening_preserve_the_continuous_center_backing() => desktop.RunAsync(() =>
    {
        var wheel = new WheelWindow(new OperationCatalog([]));
        try
        {
            wheel.ApplySettings(new() { ReduceMotion = true, SoundEnabled = false });
            SingleBackingVisualEvidenceTests.Populate(wheel);
            wheel.Show(); wheel.UpdateLayout(); Invoke(wheel, "Select", 0);
            var operations = Get<IReadOnlyList<OperationDescriptor>>(wheel, "operations");
            var image = (Image)wheel.FindName("GlassBody");
            image.Source = OpaqueFrame(); image.Visibility = Visibility.Visible;
            foreach (int count in new[] { 1, 2, 1 })
            {
                typeof(WheelWindow).GetField("pages", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(wheel,
                    Enumerable.Repeat(operations, count).ToArray());
                Invoke(wheel, "UpdateNavigationPresentation"); wheel.UpdateLayout();
                AssertFilledCenter((Canvas)wheel.FindName("Presentation"), 1);
                Assert.Equal(count > 1 ? Visibility.Visible : Visibility.Collapsed, ((Border)wheel.FindName("NavigationPlate")).Visibility);
            }
            wheel.Hide(); wheel.Show(); wheel.UpdateLayout();
            AssertFilledCenter((Canvas)wheel.FindName("Presentation"), 1);
        }
        finally { wheel.Close(); }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(1d)]
    [InlineData(1.5d)]
    [InlineData(2d)]
    public Task Demo_fills_the_center_while_its_selected_label_and_backing_change(double scale) => desktop.RunAsync(() =>
    {
        var demo = new WheelDemoWindow();
        try
        {
            demo.Show(); demo.UpdateLayout();
            var canvas = Get<Canvas>(demo, "wheelCanvas");
            foreach (var stage in new[] { GlassPresentationStage.Bootstrap, GlassPresentationStage.Live, GlassPresentationStage.Fallback })
            {
                Invoke(demo, "SetGlassStage", stage); demo.UpdateLayout();
                if (stage == GlassPresentationStage.Fallback) AssertFilledCenter(canvas, scale);
                var image = Get<Image>(demo, "glassImage"); image.Source = OpaqueFrame(); image.Visibility = Visibility.Visible;
                AssertFilledCenter(canvas, scale);
                Assert.InRange(Pixel(Render(canvas, scale), 184, 184, scale)[3], 240, 255);
                image.Visibility = Visibility.Collapsed;
            }
        }
        finally { demo.Close(); }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(.78d)]
    [InlineData(1.17d)]
    [InlineData(1.56d)]
    public Task Native_backing_region_includes_the_center_without_filling_outer_corners(double scale) => desktop.RunAsync(() =>
    {
        var wheel = new Window { Width = 368 * scale, Height = 454 * scale, ShowActivated = false };
        using var companion = new NativeBackdropWindow();
        nint region = BackdropNativeMethods.CreateRectRgn(0, 0, 0, 0);
        try
        {
            Assert.NotEqual(nint.Zero, region);
            nint hwnd = new System.Windows.Interop.WindowInteropHelper(wheel).EnsureHandle();
            companion.PlaceBehind(hwnd, new ScreenRect(0, 0, Math.Ceiling(368 * scale), Math.Ceiling(454 * scale)), scale);
            Assert.NotEqual(0, BackdropNativeMethods.GetWindowRgn(companion.Handle, region));
            Assert.True(BackdropNativeMethods.PtInRegion(region, (int)(184 * scale), (int)(184 * scale)));
            Assert.True(BackdropNativeMethods.PtInRegion(region, (int)(184 * scale), (int)(135 * scale)));
            Assert.False(BackdropNativeMethods.PtInRegion(region, 0, 0));
            Assert.NotEqual(0L, BackdropNativeMethods.GetWindowLongPtr(companion.Handle, -16).ToInt64() & BackdropNativeMethods.WsDisabled);
            Assert.NotEqual(0L, BackdropNativeMethods.GetWindowLongPtr(companion.Handle, -20).ToInt64() & BackdropNativeMethods.WsExNoActivate);
        }
        finally { BackdropNativeMethods.DeleteObject(region); wheel.Close(); }
        return Task.CompletedTask;
    });

    private static void AssertFilledCenter(Canvas canvas, double scale)
    {
        var bitmap = Render(canvas, scale);
        Assert.InRange(Pixel(bitmap, 184, 135, scale)[3], 1, 255);
        Assert.InRange(Pixel(bitmap, 184, 233, scale)[3], 1, 255);
        Assert.InRange(Pixel(bitmap, 184, 62, scale)[3], 200, 255); // The selected petal stays visible.
    }

    private static RenderTargetBitmap Render(Canvas canvas, double scale)
    {
        canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(368 * scale), (int)Math.Ceiling(454 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(canvas); return bitmap;
    }

    private static byte[] Pixel(BitmapSource bitmap, int x, int y, double scale)
    {
        var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect((int)(x * scale), (int)(y * scale), 1, 1), pixel, 4, 0); return pixel;
    }
    private static BitmapSource OpaqueFrame() => BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, new byte[] { 60, 120, 180, 255 }, 4);
    private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Invoke(object target, string method, params object[] arguments) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
}
