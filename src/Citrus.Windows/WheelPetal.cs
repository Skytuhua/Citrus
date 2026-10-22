using Citrus.Core;
using System.Windows.Controls;
using System.Windows.Media;
using Citrus.Windows.Presentation;
using System.Windows.Automation.Peers;
using ShapePath = System.Windows.Shapes.Path;

namespace Citrus.Windows;

/// <summary>One retained target. Hover changes brushes/opacity only; geometry and layout stay fixed.</summary>
public sealed class WheelPetal : Canvas
{
    private readonly ShapePath background, selectedSurface, icon;
    private readonly StreamGeometry outline;
    private readonly TextBlock label;
    private bool isSelected;
    private readonly bool formatLabel;
    private readonly string operationName;
    private readonly WheelPoint labelCenter;
    public WheelPetal(OperationDescriptor operation, int index, int count)
    {
        operationName = operation.Label;
        Width = WheelGeometry.Width; Height = WheelGeometry.Width;
        IsHitTestVisible = false;
        WheelForegroundStyle.Apply(Resources, SystemParameters.HighContrast);
        var points = WheelGeometry.Petal(index,count);
        var shape = new StreamGeometry();
        using (var context = shape.Open())
        {
            context.BeginFigure(new Point(points[0].X,points[0].Y),true,true);
            context.PolyLineTo(points.Skip(1).Select(p => new Point(p.X,p.Y)).ToArray(),true,false);
        }
        shape.Freeze();
        outline = shape;
        background = new ShapePath { Data = shape, StrokeThickness = SystemParameters.HighContrast ? 1 : 0 };
        background.SetResourceReference(ShapePath.FillProperty,"PetalBackground");
        background.SetResourceReference(ShapePath.StrokeProperty,"PetalRim");
        selectedSurface = new ShapePath { Data = shape, Opacity = 0 };
        selectedSurface.SetResourceReference(ShapePath.FillProperty,"SelectedPetal");
        Children.Add(background); Children.Add(selectedSurface);
        Unloaded += (_, _) => CancelEmphasis();
        formatLabel = operation.Mode == OperationMode.Convert && !string.IsNullOrEmpty(operation.OutputExtension);
        var center = WheelGeometry.At(118,index*360d/count);
        labelCenter = center;
        icon = new ShapePath { Data = (Geometry)Application.Current.FindResource(IconKey(operation)), Width=24, Height=24,
            Stretch=Stretch.Uniform, StrokeThickness=1.6, StrokeStartLineCap=PenLineCap.Round, StrokeEndLineCap=PenLineCap.Round, StrokeLineJoin=PenLineJoin.Round };
        label = new TextBlock { Text=WheelDisplayLabels.Petal(operation), Width=count>=9?66:74, TextAlignment=TextAlignment.Center };
        SetLeft(icon,center.X-12); SetTop(icon,center.Y-23);
        SetLeft(label,center.X-label.Width/2);
        icon.Visibility = formatLabel ? Visibility.Hidden : Visibility.Visible;
        Children.Add(icon); Children.Add(label);
        System.Windows.Automation.AutomationProperties.SetName(this,operation.Label);
        ToolTip = operation.Label;
        ApplyTextScale(1);
        SetSelected(false,false);
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new PetalAutomationPeer(this);

    // Petals are labels for the wheel's drag targets, not independent keyboard/invoke controls.
    private sealed class PetalAutomationPeer(WheelPetal owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => owner.operationName;
        protected override string GetClassNameCore() => nameof(WheelPetal);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        protected override bool IsKeyboardFocusableCore() => false;
        protected override List<AutomationPeer>? GetChildrenCore() => null;
        public override object? GetPattern(PatternInterface patternInterface) => null;
    }
    /// <summary>The filled rounded outline owns native boundaries; its decorative stroke is not a target.</summary>
    public bool ContainsPoint(Point point) => outline.FillContains(point, .001, ToleranceType.Absolute);

    public void SetSelected(bool selected, bool motion)
    {
        isSelected = selected;
        label.Effect = icon.Effect = null;
        selectedSurface.Opacity = selected ? 1 : 0;
        icon.SetResourceReference(ShapePath.StrokeProperty,selected ? "SelectedPetalText" : "WheelText");
        label.SetResourceReference(TextBlock.ForegroundProperty,selected ? "SelectedPetalText" : "WheelText");
        CancelEmphasis();
    }
    internal void ApplyTextScale(double scale)
    {
        WheelTitleTypography.ApplyPetal(label, formatLabel, scale);
        label.Measure(new System.Windows.Size(label.Width, double.PositiveInfinity));
        double labelTop = labelCenter.Y - label.DesiredSize.Height / 2;
        if (!formatLabel)
        {
            const double iconLabelGap = 5;
            double groupTop = labelCenter.Y - (icon.Height + iconLabelGap + label.DesiredSize.Height) / 2;
            SetTop(icon, groupTop);
            labelTop = groupTop + icon.Height + iconLabelGap;
        }
        SetTop(label, labelTop);
        label.Visibility = Visibility.Visible;
    }
    internal void ApplyGlassContrast(GlassScene scene, byte[] pixels)
    {
        // Kept for the existing owned-render seam. Foreground protection now comes
        // from the retained pale fill, rather than inspecting desktop pixels.
    }
    internal void ClearGlassContrast()
    {
        ApplyForegroundPalette(SystemParameters.HighContrast);
    }
    internal void ApplyForegroundPalette(bool highContrast)
    {
        WheelForegroundStyle.Apply(Resources, highContrast);
        background.StrokeThickness = highContrast ? 1 : 0;
        SetSelected(isSelected, false);
    }
    internal void CancelEmphasis()
    {
        PressFeedback.GetAnimator(this)?.CancelOwner(this, false);
    }
    private static string IconKey(OperationDescriptor operation)
    {
        string id = operation.Id.ToLowerInvariant();
        if (id.EndsWith(".trim", StringComparison.Ordinal)) return "Icon.Trim";
        if (id.EndsWith(".speed", StringComparison.Ordinal)) return "Icon.Speed";
        foreach (string name in new[] { "metadata", "background", "mute", "snapshot", "split", "channels", "normalize", "bleep", "organize" })
            if (id.EndsWith("." + name, StringComparison.Ordinal)) return "Icon." + char.ToUpperInvariant(name[0]) + name[1..];
        if (id.EndsWith(".visualizer", StringComparison.Ordinal)) return "Icon.Visualize";
        if (id.Contains("strip", StringComparison.Ordinal)) return "Icon.Metadata";
        if (id.Contains("editor", StringComparison.Ordinal)) return "Icon.Tools";
        foreach (string name in new[] { "crop","rotate","resize","annotate","redact","compress","terminal","float","paths","copy","move","archive","audio","video" })
            if (id.Contains(name,StringComparison.Ordinal)) return "Icon." + char.ToUpperInvariant(name[0])+name[1..];
        if (id.Contains("reveal",StringComparison.Ordinal) || id.Contains("float",StringComparison.Ordinal)) return "Icon.Folder";
        if (id.Contains("image",StringComparison.Ordinal)) return "Icon.Image";
        if (id.Contains("text",StringComparison.Ordinal)) return "Icon.Text";
        return operation.Mode switch { OperationMode.Convert => "Icon.Convert", OperationMode.Tools => "Icon.Tools", _ => "Icon.File" };
    }
}
