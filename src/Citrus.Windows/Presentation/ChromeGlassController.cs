using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Citrus.Core.Presentation;
using ShapePath = System.Windows.Shapes.Path;

namespace Citrus.Windows.Presentation;

/// <summary>Visible app-owned chrome shares the wheel's optical pipeline and exclusion leases.</summary>
internal sealed class ChromeGlassController : IDisposable
{
    private readonly Window window;
    private readonly MaterialSurface surface;
    private readonly LiveWheelGlass glass;
    private readonly DispatcherTimer placement;
    private readonly Dictionary<TextBlock, GlassContrast> labels = [];
    private readonly Dictionary<ShapePath, GlassContrast> icons = [];
    private readonly List<(FrameworkElement Glyph, Rect Bounds)> glyphs = [];
    private bool layoutDirty = true;
    private MaterialDecision decision;
    private bool dark, disposed;
    private (int X, int Y, double W, double H, double Scale, CornerRadius Radius)? lastPlacement;
    private readonly SceneCache sceneCache = new();

    internal Task Completion => glass.Completion;
    internal ChromeGlassController(Window window, MaterialSurface surface,
        Func<GlassScene, int, int, ILiveGlassSession?>? createSession = null, Func<nint[], IDisposable?>? exclude = null)
    {
        this.window = window; this.surface = surface;
        glass = new(window.Dispatcher, surface.PresentGlass, Clear, exclude: exclude, createSession: createSession);
        glass.FramePresented = ProtectLabels;
        placement = new(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(45) };
        placement.Tick += (_, _) => { placement.Stop(); Synchronize(); };
        window.SourceInitialized += Changed;
        window.LocationChanged += Moved;
        window.SizeChanged += Resized;
        window.StateChanged += Changed;
        window.DpiChanged += DpiChanged;
        window.IsVisibleChanged += VisibilityChanged;
        window.Closed += Closed;
        window.LayoutUpdated += LayoutChanged;
    }

    internal void Apply(MaterialDecision policy, bool isDark)
    {
        if (disposed) return;
        placement.Stop();
        if (dark != isDark || decision != policy) { lastPlacement = null; glass.Stop(); }
        decision = policy; dark = isDark;
        Synchronize();
    }

    private void Changed(object? sender, EventArgs args) => Queue();
    private void Moved(object? sender, EventArgs args)
    {
        if (disposed || placement.IsEnabled) return;
        Synchronize();
    }
    private void LayoutChanged(object? sender, EventArgs args) => layoutDirty = true;
    private void Resized(object sender, SizeChangedEventArgs args) => Queue();
    private void DpiChanged(object sender, DpiChangedEventArgs args)
    {
        if (args.OldDpi.DpiScaleX != args.NewDpi.DpiScaleX || args.OldDpi.DpiScaleY != args.NewDpi.DpiScaleY)
            Queue();
    }
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (!window.IsVisible) { placement.Stop(); lastPlacement = null; glass.Stop(); }
        else Queue();
    }
    private void Queue()
    {
        if (disposed) return;
        if (!window.IsVisible || window.WindowState == WindowState.Minimized) { placement.Stop(); lastPlacement = null; glass.Stop(); return; }
        // Geometry and policy changes invalidate the old contour. Translation uses
        // the persistent optical session instead of clearing it on every mouse move.
        glass.Stop(); lastPlacement = null;
        placement.Stop(); placement.Start();
    }

    private void Synchronize()
    {
        if (disposed || !decision.RequestNativeAcrylic || !window.IsVisible || window.WindowState == WindowState.Minimized ||
            surface.ActualWidth <= 2 || surface.ActualHeight <= 2)
        { lastPlacement = null; glass.Stop(); return; }
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || PresentationSource.FromVisual(surface) is null) return;
        Point origin = surface.PointToScreen(new Point(0, 0));
        double scale = VisualTreeHelper.GetDpi(surface).DpiScaleX;
        var key = ((int)Math.Round(origin.X), (int)Math.Round(origin.Y), surface.ActualWidth, surface.ActualHeight, scale, surface.CornerRadius);
        if (lastPlacement == key) return;
        if (lastPlacement is { } previous && glass.Running &&
            previous.W == key.ActualWidth && previous.H == key.ActualHeight &&
            previous.Scale == key.scale && previous.Radius == key.CornerRadius)
        {
            lastPlacement = key;
            glass.SetPosition(key.Item1, key.Item2);
            return;
        }
        lastPlacement = key;
        var geometry = (key.ActualWidth, key.ActualHeight, key.scale, key.CornerRadius);
        // Copy the precise WPF contour before handing geometry construction to the worker.
        var outline = surface.GlassOutline().ToArray();
        glass.StartSurface(sceneCache.Create(geometry, outline), key.Item1, key.Item2, dark, decision.Animate, handle);
    }

    internal void ProtectLabels(GlassScene scene, byte[] pixels)
    {
        if (layoutDirty)
        {
            glyphs.Clear();
            foreach (var element in Descendants(surface))
                if (element is FrameworkElement glyph && glyph is TextBlock or ShapePath && glyph.Visibility == Visibility.Visible &&
                    glyph.ActualWidth > 0 && glyph.ActualHeight > 0 && !HasOpaqueBacking(glyph))
                    glyphs.Add((glyph, glyph.TransformToAncestor(surface).TransformBounds(new Rect(glyph.RenderSize))));
            var active = glyphs.Select(item => item.Glyph).ToHashSet();
            foreach (var label in labels.Keys.Where(label => !active.Contains(label)).ToArray())
            {
                label.BeginAnimation(TextBlock.ForegroundProperty, null); label.BeginAnimation(TextBlock.BackgroundProperty, null); labels.Remove(label);
            }
            foreach (var icon in icons.Keys.Where(icon => !active.Contains(icon)).ToArray()) { ClearIcon(icon); icons.Remove(icon); }
            layoutDirty = false;
        }
        foreach (var (glyph, bounds) in glyphs)
        {
            var contrast = GlassLabelContrast.Evaluate(pixels, scene.PixelWidth, scene.PixelHeight, scene.Scale, bounds);
            contrast = contrast with { ScrimOpacity = Math.Ceiling(contrast.ScrimOpacity * 255) / 255 };
            if (glyph is ShapePath icon)
            {
                if (icons.TryGetValue(icon, out var previousIcon) && previousIcon == contrast) continue;
                icons[icon] = contrast;
                AnimateValue(icon, ShapePath.StrokeProperty, contrast.LightText ? Brushes.White : Brushes.Black);
                DropShadowEffect? protection = contrast.ScrimOpacity <= 0 ? null : new()
                {
                    Color = contrast.LightText ? Colors.Black : Colors.White,
                    Opacity = 1, BlurRadius = 2, ShadowDepth = 0, RenderingBias = RenderingBias.Quality
                };
                protection?.Freeze();
                AnimateValue(icon, UIElement.EffectProperty, protection);
                continue;
            }
            var label = (TextBlock)glyph;
            if (labels.TryGetValue(label, out var previous) && previous == contrast) continue;
            labels[label] = contrast;
            AnimateValue(label, TextBlock.ForegroundProperty, contrast.LightText ? Brushes.White : Brushes.Black);
            byte channel = contrast.LightText ? (byte)0 : (byte)255;
            var scrim = new SolidColorBrush(Color.FromArgb((byte)Math.Ceiling(contrast.ScrimOpacity * 255), channel, channel, channel)); scrim.Freeze();
            AnimateValue(label, TextBlock.BackgroundProperty, scrim);
        }
    }
    private bool HasOpaqueBacking(DependencyObject child)
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(child); parent is not null && parent != surface; parent = VisualTreeHelper.GetParent(parent))
        {
            Brush? brush = parent switch { Border border => border.Background, Panel panel => panel.Background, Control control => control.Background, _ => null };
            if (brush is SolidColorBrush { Color.A: >= 200 }) return true;
        }
        return false;
    }
    private static void AnimateValue(FrameworkElement label, DependencyProperty property, object? value)
    {
        // Animation values leave local bindings and DynamicResource expressions intact.
        var animation = new ObjectAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1) };
        animation.KeyFrames.Add(new DiscreteObjectKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.Freeze(); label.BeginAnimation(property, animation);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            if (child is WheelPetal || child is FrameworkElement { Name: "DemoWheelCanvas" }) continue;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private void Clear()
    {
        surface.ClearGlass();
        foreach (var label in labels.Keys)
        {
            label.BeginAnimation(TextBlock.ForegroundProperty, null);
            label.BeginAnimation(TextBlock.BackgroundProperty, null);
        }
        labels.Clear();
        foreach (var icon in icons.Keys) ClearIcon(icon);
        icons.Clear();
        glyphs.Clear(); layoutDirty = true;
    }
    private static void ClearIcon(ShapePath icon) { icon.BeginAnimation(ShapePath.StrokeProperty, null); icon.BeginAnimation(UIElement.EffectProperty, null); }
    private void Closed(object? sender, EventArgs args) => Dispose();
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; placement.Stop(); glass.Dispose();
        window.SourceInitialized -= Changed; window.LocationChanged -= Moved; window.SizeChanged -= Resized;
        window.StateChanged -= Changed; window.IsVisibleChanged -= VisibilityChanged; window.Closed -= Closed;
        window.DpiChanged -= DpiChanged;
        window.LayoutUpdated -= LayoutChanged;
    }

    internal sealed class SceneCache
    {
        private GlassScene? scene;
        private (double W, double H, double Scale, CornerRadius Radius)? geometry;
        internal Func<GlassScene> Create((double W, double H, double Scale, CornerRadius Radius) key, IReadOnlyList<Citrus.Core.WheelPoint> outline)
            => () =>
            {
                // Both comparison and cache access occur only on the serialized worker.
                // A canceled B build cannot make a later A request return B's dimensions.
                if (geometry == key && scene is not null) return scene;
                scene = new GlassScene(key.W, key.H, key.Scale, [GlassRegion.Polygon(1, outline)]);
                geometry = key; return scene;
            };
    }
}
