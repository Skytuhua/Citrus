using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;

namespace Citrus.Windows.Tests;

/// <summary>Native WPF evidence only. Raster DPI is an export scale, not a monitor/DPI transition.</summary>
internal static class VisualArtifacts
{
    public static string DirectoryPath => Path.Combine(Environment.CurrentDirectory, "TestResults", "visual");

    public static void Json(string name, object evidence)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(Path.Combine(DirectoryPath, name + ".json"),
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static BitmapSource Capture(FrameworkElement root, string name, double scale, ThemeChoice theme, object? context = null)
    {
        var rendered = RenderCrop(root, scale);
        var bitmap = rendered.Bitmap;
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        Directory.CreateDirectory(DirectoryPath);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(DirectoryPath, name + ".png"))) encoder.Save(stream);
        var nativeDpi = VisualTreeHelper.GetDpi(root);
        var window = Window.GetWindow(root);
        var rootOrigin = window is null ? new Point() : root.TransformToAncestor(window).Transform(new Point());
        var screen = window is null ? null : System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(window).Handle);
        Json(name, new
        {
            capturedUtc = DateTimeOffset.UtcNow, os = RuntimeInformation.OSDescription,
            runtime = RuntimeInformation.FrameworkDescription,
            context,
            captureCoordinates = "Measured root rectangle cropped from the actual ancestor Window render; no subtree offset normalization or stretching",
            renderingLimit = "WPF visual tree only; not a screen capture. Native nonclient chrome, DWM composition and desktop backdrops are not captured or simulated.",
            cropRounding = "Outward raster bounds intersected with Window pixels; at most one native pixel of layout rounding allowed",
            rootBoundsInWindowDip = new { x = rendered.Bounds.X, y = rendered.Bounds.Y, width = rendered.Bounds.Width, height = rendered.Bounds.Height },
            cropInWindowPixels = new { x = rendered.Crop.X, y = rendered.Crop.Y, width = rendered.Crop.Width, height = rendered.Crop.Height },
            rootOriginInCropPixels = new { x = rendered.Bounds.X * scale - rendered.Crop.X, y = rendered.Bounds.Y * scale - rendered.Crop.Y },
            rootOriginInWindowDip = new { x = rootOrigin.X, y = rootOrigin.Y },
            windowBackground = window?.Background?.ToString(),
            actualWindowLeftDip = window?.Left, actualWindowTopDip = window?.Top,
            actualWindowWidthDip = window?.ActualWidth, actualWindowHeightDip = window?.ActualHeight,
            monitor = screen?.DeviceName, monitorBoundsPixels = screen?.Bounds.ToString(), monitorWorkAreaPixels = screen?.WorkingArea.ToString(),
            requestedTheme = theme.ToString(), highContrast = SystemParameters.HighContrast,
            clientAreaAnimation = SystemParameters.ClientAreaAnimation,
            themeInterpretation = SystemParameters.HighContrast ? "OS high contrast overrides requested palette" : "Production ThemeManager palette",
            dpiEvidence = "RenderTargetBitmap raster scale only; no monitor DPI or OS settings changed",
            rasterScale = scale, rasterDpi = 96 * scale, pixelWidth = width, pixelHeight = height,
            layoutWidthDip = root.ActualWidth, layoutHeightDip = root.ActualHeight,
            actualVisualDpiX = nativeDpi.PixelsPerInchX, actualVisualDpiY = nativeDpi.PixelsPerInchY,
            inputEvidence = "Synthetic WPF routed callbacks; not Explorer or native OLE initiation"
        });
        // A blank/transparent image cannot masquerade as visual evidence.
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
        Assert.True(Enumerable.Range(0, width * height).Any(i => pixels[i * 4 + 3] > 16), "Rendered view has no visible pixels.");
        return bitmap;
    }

    public static BitmapSource Render(FrameworkElement root, double scale) => RenderCrop(root, scale).Bitmap;

    private static (BitmapSource Bitmap, Rect Bounds, Int32Rect Crop) RenderCrop(FrameworkElement root, double scale)
    {
        var window = Window.GetWindow(root) ?? throw new InvalidOperationException("Capture requires an attached Window visual.");
        window.UpdateLayout();
        var bounds = root.TransformToAncestor(window).TransformBounds(new Rect(new Point(), root.RenderSize));
        Assert.True(bounds.Width > 0 && bounds.Height > 0, "The actual production view must have layout before rendering.");
        var dpi = VisualTreeHelper.GetDpi(window);
        // Native window sizes round fractional layout edges to whole monitor pixels.
        Assert.True(bounds.Left >= -1 / dpi.DpiScaleX && bounds.Top >= -1 / dpi.DpiScaleY &&
            bounds.Right <= window.RenderSize.Width + 1 / dpi.DpiScaleX && bounds.Bottom <= window.RenderSize.Height + 1 / dpi.DpiScaleY,
            $"Measured root {bounds} exceeds Window layout {window.RenderSize} beyond native pixel rounding.");
        // Render the parentless Window once so offsets and ancestor backgrounds are painted by WPF itself.
        // Cropping the measured root rect neither remaps VisualBrush content bounds nor changes layout.
        var full = new RenderTargetBitmap((int)Math.Ceiling(window.RenderSize.Width * scale),
            (int)Math.Ceiling(window.RenderSize.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        full.Render(window);
        int left = Math.Max(0, (int)Math.Floor(bounds.Left * scale)), top = Math.Max(0, (int)Math.Floor(bounds.Top * scale));
        int right = Math.Min(full.PixelWidth, (int)Math.Ceiling(bounds.Right * scale)), bottom = Math.Min(full.PixelHeight, (int)Math.Ceiling(bounds.Bottom * scale));
        var crop = new Int32Rect(left, top, right - left, bottom - top);
        var bitmap = new CroppedBitmap(full, crop);
        bitmap.Freeze();
        return (bitmap, bounds, crop);
    }

    public static void AssertInside(FrameworkElement element, FrameworkElement root)
    {
        var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(new Point(), element.RenderSize));
        Assert.True(bounds.Width > 0 && bounds.Height > 0, $"{element.Name} has no rendered size: {bounds}");
        Assert.True(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1,
            $"{element.Name} {bounds} exceeds root {root.ActualWidth} × {root.ActualHeight} DIP.");
        // A button inside the window can still be hidden behind a scroll viewport.
        for (var ancestor = VisualTreeHelper.GetParent(element); ancestor is not null && ancestor != root; ancestor = VisualTreeHelper.GetParent(ancestor))
        {
            if (ancestor is not FrameworkElement { ClipToBounds: true } viewport) continue;
            var visible = element.TransformToAncestor(viewport).TransformBounds(new Rect(new Point(), element.RenderSize));
            Assert.True(visible.Left >= -1 && visible.Top >= -1 && visible.Right <= viewport.ActualWidth + 1 && visible.Bottom <= viewport.ActualHeight + 1,
                $"{element.Name} {visible} is clipped by {viewport.GetType().Name} {viewport.ActualWidth} × {viewport.ActualHeight} DIP.");
        }
    }
}
