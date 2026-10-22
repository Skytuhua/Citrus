using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Point = System.Windows.Point;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class VisualArtifactsTests(DesktopFixture desktop)
{
    [Theory]
    [InlineData(ThemeChoice.Light, 1d)]
    [InlineData(ThemeChoice.Dark, 2d)]
    public Task Root_crop_preserves_both_corners_and_actual_window_background(ThemeChoice theme, double scale) => desktop.RunAsync(async () =>
    {
        using var themes = new ThemeManager(Application.Current);
        themes.Apply(theme, reduceMotion: true);
        var root = new Grid { Margin = new Thickness(24) };
        var leading = (SolidColorBrush)Application.Current.FindResource("Accent");
        var trailing = (SolidColorBrush)Application.Current.FindResource("PrimaryText");
        root.Children.Add(new Border { Width = 8, Height = 8, Background = leading,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
        root.Children.Add(new Border { Width = 8, Height = 8, Background = trailing,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom });
        var window = new Window { Width = 300, Height = 220, Content = root, ShowActivated = false };
        window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            root.UpdateLayout();
            var origin = root.TransformToAncestor(window).Transform(new Point());
            Assert.True(origin.X >= 24 && origin.Y >= 24); // Deliberately reproduce a parent layout offset.
            var before = root.RenderSize;
            var bitmap = VisualArtifacts.Render(root, scale);
            Assert.Equal(before, root.RenderSize); // Capture must not arrange/reparent the production visual.
            Assert.Equal((int)Math.Ceiling(root.ActualWidth * scale), bitmap.PixelWidth);
            Assert.Equal((int)Math.Ceiling(root.ActualHeight * scale), bitmap.PixelHeight);
            Assert.Equal(leading.Color, Pixel(bitmap, 2 * scale, 2 * scale));
            Assert.Equal(trailing.Color, Pixel(bitmap, bitmap.PixelWidth - 2 * scale, bitmap.PixelHeight - 2 * scale));
            Assert.Equal(((SolidColorBrush)window.Background).Color, Pixel(bitmap, bitmap.PixelWidth / 2d, bitmap.PixelHeight / 2d));
        }
        finally { window.Close(); themes.Apply(ThemeChoice.Light, reduceMotion: true); }
    });

    [Theory]
    [InlineData(1d)]
    [InlineData(1.5d)]
    [InlineData(2d)]
    public Task Fractional_root_rounding_uses_visible_window_pixels_but_rejects_real_overflow(double scale) => desktop.RunAsync(async () =>
    {
        var root = new Canvas { Width = 100.25, Height = 100.25, Background = Brushes.Orange };
        var window = new Window { Width = 100, Height = 100, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Background = Brushes.Transparent, Content = root, ShowActivated = false,
            ResizeMode = ResizeMode.NoResize, UseLayoutRounding = false };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var before = root.RenderSize;
            var bitmap = VisualArtifacts.Render(root, scale);
            Assert.Equal(before, root.RenderSize);
            Assert.Equal((int)Math.Ceiling(window.RenderSize.Width * scale), bitmap.PixelWidth);
            Assert.Equal((int)Math.Ceiling(window.RenderSize.Height * scale), bitmap.PixelHeight);
            Assert.Equal(Colors.Orange, Pixel(bitmap, bitmap.PixelWidth - 2, bitmap.PixelHeight - 2));
            root.Width = root.Height = 105;
            window.UpdateLayout();
            Assert.ThrowsAny<Exception>(() => VisualArtifacts.Render(root, scale));
        }
        finally { window.Close(); }
    });

    private static Color Pixel(BitmapSource bitmap, double x, double y)
    {
        var bytes = new byte[4];
        bitmap.CopyPixels(new Int32Rect((int)x, (int)y, 1, 1), bytes, 4, 0);
        return Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }
}
