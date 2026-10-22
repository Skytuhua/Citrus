using System.Windows.Controls;
using System.Windows.Media;

namespace Citrus.Windows.Presentation;

internal enum MaterialRole { Chrome, WheelShell, Toast }

/// <summary>Original, static material edges. Native eligibility is supplied by this surface's HWND controller.</summary>
internal sealed class MaterialSurface : Decorator
{
    public static readonly DependencyProperty RoleProperty = DependencyProperty.Register(nameof(Role), typeof(MaterialRole), typeof(MaterialSurface), new FrameworkPropertyMetadata(MaterialRole.Chrome, FrameworkPropertyMetadataOptions.AffectsRender, RoleChanged));
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(BackdropState), typeof(MaterialSurface), new FrameworkPropertyMetadata(BackdropState.Solid, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(MaterialSurface), new FrameworkPropertyMetadata(new CornerRadius(24), FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange, null, CoerceRadius));
    private static DependencyProperty BrushProperty(string name) => DependencyProperty.Register(name, typeof(Brush), typeof(MaterialSurface), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty TintProperty = BrushProperty("Tint"), SolidProperty = BrushProperty("Solid"), OutlineProperty = BrushProperty("Outline"), SolidOutlineProperty = BrushProperty("SolidOutline"), SpecularProperty = BrushProperty("Specular"), InnerProperty = BrushProperty("Inner");
    public MaterialRole Role { get => (MaterialRole)GetValue(RoleProperty); set => SetValue(RoleProperty, value); }
    public BackdropState State { get => (BackdropState)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    private ImageSource? opticalFrame;
    internal void PresentGlass(ImageSource frame)
    {
        // WriteableBitmap updates its retained drawing resource directly. Re-arranging
        // the visual on every pixel update would invalidate all glyph layout caches.
        if (ReferenceEquals(opticalFrame, frame) && State == BackdropState.LiveRefractiveGlass) return;
        opticalFrame = frame; State = BackdropState.LiveRefractiveGlass; InvalidateVisual();
    }
    internal void ClearGlass()
    {
        if (opticalFrame is null && State == BackdropState.Solid) return;
        opticalFrame = null; State = BackdropState.Solid; InvalidateVisual();
    }
    internal IReadOnlyList<Citrus.Core.WheelPoint> GlassOutline()
    {
        var flattened = Contour(0).GetFlattenedPathGeometry(.03, ToleranceType.Absolute);
        var points = new List<Citrus.Core.WheelPoint>();
        foreach (var figure in flattened.Figures)
        {
            points.Add(new(figure.StartPoint.X, figure.StartPoint.Y));
            foreach (var segment in figure.Segments)
                if (segment is PolyLineSegment polyline)
                    foreach (var point in polyline.Points) points.Add(new(point.X, point.Y));
                else if (segment is LineSegment line) points.Add(new(line.Point.X, line.Point.Y));
        }
        return points;
    }
    internal Brush EffectiveTint => (Brush)GetValue(State is BackdropState.ConfiguredNativeAcrylic or BackdropState.PendingFrosted ? TintProperty : SolidProperty);
    public MaterialSurface()
    {
        SetResourceReference(SolidProperty, "WindowBackground");
        SetResourceReference(SolidOutlineProperty, "BorderBrush");
        SetResourceReference(OutlineProperty, "MaterialOutline");
        SetResourceReference(SpecularProperty, "MaterialDepth");
        SetResourceReference(InnerProperty, "MaterialInnerEdge");
        UpdateRole();
    }
    private static void RoleChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args) => ((MaterialSurface)owner).UpdateRole();
    private void UpdateRole()
    {
        SetResourceReference(TintProperty, Role == MaterialRole.WheelShell ? "MaterialShellTint" : "MaterialChromeTint");
        SetResourceReference(SolidProperty, Role == MaterialRole.WheelShell ? "WheelShell" : "WindowBackground");
    }
    private static object CoerceRadius(DependencyObject owner, object value) =>
        Window.GetWindow(owner) is { WindowState: WindowState.Maximized } window && ReferenceEquals(window.Content, owner) ? new CornerRadius(0) : value;
    protected override Geometry? GetLayoutClip(Size layoutSlotSize)
    {
        var layoutClip = base.GetLayoutClip(layoutSlotSize);
        // The layered wheel and its circular backing retain their own geometry.
        if (Role == MaterialRole.WheelShell) return layoutClip;
        var contour = Contour(0);
        return layoutClip is null ? contour : new CombinedGeometry(GeometryCombineMode.Intersect, contour, layoutClip);
    }
    private Geometry Contour(double inset)
    {
        double width = Math.Max(0, RenderSize.Width - 2 * inset), height = Math.Max(0, RenderSize.Height - 2 * inset);
        var rect = new Rect(inset, inset, width, height);
        double Radius(double value) => Math.Clamp(value - inset, 0, Math.Min(width, height) / 2);
        double tl = Radius(CornerRadius.TopLeft), tr = Radius(CornerRadius.TopRight), br = Radius(CornerRadius.BottomRight), bl = Radius(CornerRadius.BottomLeft);
        var shape = new StreamGeometry();
        using (var path = shape.Open())
        {
            path.BeginFigure(new Point(rect.Left + tl, rect.Top), true, true);
            path.LineTo(new Point(rect.Right - tr, rect.Top), true, false);
            Corner(new Point(rect.Right, rect.Top + tr), tr);
            path.LineTo(new Point(rect.Right, rect.Bottom - br), true, false);
            Corner(new Point(rect.Right - br, rect.Bottom), br);
            path.LineTo(new Point(rect.Left + bl, rect.Bottom), true, false);
            Corner(new Point(rect.Left, rect.Bottom - bl), bl);
            path.LineTo(new Point(rect.Left, rect.Top + tl), true, false);
            Corner(new Point(rect.Left + tl, rect.Top), tl);
            void Corner(Point end, double radius)
            {
                if (radius == 0) path.LineTo(end, true, false);
                else path.ArcTo(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
        }
        shape.Freeze(); return shape;
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 2 || ActualHeight <= 2) return;
        if (State == BackdropState.LiveRefractiveGlass && opticalFrame is not null)
        {
            dc.PushClip(Contour(0));
            dc.DrawImage(opticalFrame, new Rect(0, 0, ActualWidth, ActualHeight));
            dc.Pop(); return;
        }
        // The sampled lens owns its premultiplied contour and edge light. No rectangular native brush.
        if (Role == MaterialRole.WheelShell && State == BackdropState.LiveRefractiveGlass) return;
        bool tinted = State is BackdropState.ConfiguredNativeAcrylic or BackdropState.PendingFrosted;
        Draw(dc, EffectiveTint, new Pen((Brush)GetValue(tinted ? OutlineProperty : SolidOutlineProperty), 1), .5);
        // Static edge lighting also belongs to the honest opaque fallback.
        if (SystemParameters.HighContrast || Role != MaterialRole.WheelShell) return;
        Draw(dc, null, new Pen((Brush)GetValue(SpecularProperty), 1), 1.5);
        Draw(dc, null, new Pen((Brush)GetValue(InnerProperty), .5), 2.5);
    }
    private void Draw(DrawingContext dc, Brush? fill, Pen pen, double inset)
    {
        var rect = new Rect(inset, inset, Math.Max(0, ActualWidth - inset * 2), Math.Max(0, ActualHeight - inset * 2));
        if (Role == MaterialRole.WheelShell)
            dc.DrawEllipse(fill, pen, new Point(ActualWidth / 2, ActualHeight / 2), rect.Width / 2, rect.Height / 2);
        else dc.DrawGeometry(fill, pen, Contour(inset));
    }
}
