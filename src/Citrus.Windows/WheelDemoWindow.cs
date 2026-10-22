using System.Windows.Controls;
using System.Windows.Input;
using Citrus.Core;
using Citrus.Core.Presentation;
using Citrus.Windows.Presentation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Citrus.Windows;

/// <summary>Local demonstration only: no file paths, OLE handlers, audio or submission callback.</summary>
public sealed class WheelDemoWindow : Window
{
    private readonly LiveWheelGlass glass;
    private DispatcherOperation? placement;
    private readonly Canvas wheelCanvas;
    private readonly MaterialSurface wheelShell;
    private readonly WheelPetal[] wheelPetals;
    private readonly System.Windows.Controls.Image glassImage;
    private readonly ScrollViewer viewport;
    private MaterialDecision materialDecision = new(false, false, "Demo default opaque");
    private bool dark, disposed, rightCancelPending;
    private (int X, int Y, int Size)? lastPlacement;
    internal Task GlassCompletion => glass.Completion;
    internal Task GlassRetirementCompletion => glass.RetirementCompletion;

    internal void ApplyMaterialPolicy(MaterialDecision decision, bool dark)
    {
        Dispatcher.VerifyAccess();
        if (disposed) return;
        // The sample uses the same pale retained controls as the real wheel.
        WheelForegroundStyle.Apply(wheelCanvas.Resources, SystemParameters.HighContrast);
        // The wheel backing is pale in both themes and has no selection lighting.
        // A descriptive policy refresh must not discard a capture already starting.
        if (materialDecision.RequestNativeAcrylic != decision.RequestNativeAcrylic)
        { lastPlacement = null; glass.Stop(); }
        materialDecision = decision; this.dark = dark;
        SynchronizeGlass();
    }
    public WheelDemoWindow() : this(null, null) { }
    internal WheelDemoWindow(Func<GlassScene, int, int, ILiveGlassSession?>? createSession, Func<nint[], IDisposable?>? exclude)
    {
        WheelForegroundStyle.Apply(Resources, SystemParameters.HighContrast);
        Title = "Try the wheel · demonstration"; Width = 330; Height = 480;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBackground"); SetResourceReference(ForegroundProperty, "PrimaryText");
        SetResourceReference(FontFamilyProperty, "AppFont"); SetResourceReference(FontSizeProperty, "BodySize");
        var panel = new StackPanel { Margin = new(16, 12, 16, 16) };
        panel.Children.Add(new TextBlock { Text = "Sample · choose a format", HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0,0,0,8) });
        var canvas = new Canvas { Name = "DemoWheelCanvas", Width = WheelGeometry.Width, Height = WheelGeometry.Height, Background = System.Windows.Media.Brushes.Transparent, Focusable = true };
        wheelCanvas = canvas;
        var shell = new Citrus.Windows.Presentation.MaterialSurface { Role = Citrus.Windows.Presentation.MaterialRole.WheelShell, Width = 352, Height = 352, IsHitTestVisible = false };
        wheelShell = shell;
        Canvas.SetLeft(shell, 8); Canvas.SetTop(shell, 8); canvas.Children.Add(shell);
        glassImage = new System.Windows.Controls.Image { Width = 368, Height = 368, Stretch = Stretch.Fill,
            IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        canvas.Children.Add(glassImage);
        OperationDescriptor[] operations = new[] { "PNG", "WEBP", "HEIC", "TIFF", "AVIF", "BMP", "PDF" }.Select(format => new OperationDescriptor("demo." + format, format, OperationMode.Convert, "." + format.ToLowerInvariant(), false, false)).ToArray();
        var petals = operations.Select((operation, index) => new WheelPetal(operation, index, operations.Length)).ToArray();
        wheelPetals = petals;
        foreach (var petal in petals) canvas.Children.Add(petal);
        var center = new TextBlock { Text = "2.2 MB", Width = WheelPresentationMetrics.HubCapsuleWidth, TextAlignment = TextAlignment.Center };
        center.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryText");
        center.VerticalAlignment = VerticalAlignment.Center;
        var centerPlate = new Border { Width = WheelPresentationMetrics.HubCapsuleWidth, Height = 48, CornerRadius = new(24), Child = center };
        Canvas.SetLeft(centerPlate, (WheelGeometry.Width-WheelPresentationMetrics.HubCapsuleWidth)/2); Canvas.SetTop(centerPlate, 160); canvas.Children.Add(centerPlate);
        glass = new LiveWheelGlass(Dispatcher, body =>
        {
            glassImage.Source = body; glassImage.Visibility = Visibility.Visible;
        }, () =>
        {
            glassImage.Source = null; glassImage.Visibility = Visibility.Collapsed;
        }, exclude: exclude, createSession: createSession);
        glass.StageChanged = SetGlassStage;
        glass.SetPetalCount(petals.Length);
        int active = -1;
        void Select(int selected)
        {
            if (active == selected) return;
            active = selected; for (int i = 0; i < petals.Length; i++) petals[i].SetSelected(i == active, false);
            center.Text = selected < 0 ? "2.2 MB" : operations[selected].Label;
            Citrus.Windows.Presentation.WheelTitleTypography.ApplyHub(center, 1);
            if (selected < 0) { center.FontSize = 16.5; center.FontWeight = FontWeights.Normal; }
            centerPlate.SetResourceReference(Border.BackgroundProperty, "CapsuleBackground");
            if (selected >= 0 && !SystemParameters.HighContrast) centerPlate.SetResourceReference(EffectProperty, "WheelCaptionShadow");
            else centerPlate.Effect = null;
        }
        Select(0);
        canvas.MouseMove += (_, e) =>
        {
            var point = e.GetPosition(canvas);
            glass.SetPointer(point);
            int hit = WheelGeometry.HitTest(point.X, point.Y, petals.Length);
            if (hit >= 0) Select(hit);
            else if (Math.Pow(point.X - 184, 2) + Math.Pow(point.Y - 184, 2) < WheelGeometry.InnerRadius * WheelGeometry.InnerRadius)
                Select(-1);
        };
        PreviewMouseRightButtonDown += (_, e) =>
        {
            e.Handled = true;
            rightCancelPending = true;
            Mouse.Capture(this, CaptureMode.SubTree);
        };
        PreviewMouseRightButtonUp += (_, e) =>
        {
            if (!rightCancelPending) return;
            e.Handled = true;
            rightCancelPending = false;
            if (ReferenceEquals(Mouse.Captured, this)) Mouse.Capture(null);
            Close();
        };
        LostMouseCapture += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, this)) rightCancelPending = false;
        };
        canvas.KeyDown += (_, e) => { if (e.Key is Key.Right or Key.Down) { Select((active + 1) % petals.Length); e.Handled = true; } else if (e.Key is Key.Left or Key.Up) { Select((active + petals.Length - 1) % petals.Length); e.Handled = true; } };
        System.Windows.Automation.AutomationProperties.SetName(canvas, "Wheel demonstration. Use arrow keys to choose a format.");
        panel.Children.Add(new Viewbox { Width = WheelPresentationMetrics.Width, Height = WheelPresentationMetrics.Height,
            Stretch = Stretch.Uniform, Child = canvas, HorizontalAlignment = HorizontalAlignment.Center });
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        var root = new DockPanel();
        var header = new Citrus.Windows.Presentation.ChromeHeader { Content = new TextBlock { Text = "Try the wheel", FontSize = 16, FontWeight = FontWeights.SemiBold } };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        viewport = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        viewport.ScrollChanged += (_, e) =>
        {
            if (e.VerticalChange != 0 || e.HorizontalChange != 0 || e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0)
                QueueGlassPlacement();
        };
        root.Children.Add(viewport);
        Content = new Citrus.Windows.Presentation.MaterialSurface { Child = root };
        Loaded += (_, _) => { canvas.Focus(); QueueGlassPlacement(); };
        SourceInitialized += (_, _) => QueueGlassPlacement();
        LocationChanged += (_, _) => QueueGlassPlacement();
        SizeChanged += (_, _) => QueueGlassPlacement();
        DpiChanged += (_, e) =>
        {
            if (e.OldDpi.DpiScaleX != e.NewDpi.DpiScaleX || e.OldDpi.DpiScaleY != e.NewDpi.DpiScaleY)
                QueueGlassPlacement();
        };
        StateChanged += (_, _) => QueueGlassPlacement();
        IsVisibleChanged += (_, _) => QueueGlassPlacement();
        Closed += (_, _) =>
        {
            rightCancelPending = false;
            if (ReferenceEquals(Mouse.Captured, this)) Mouse.Capture(null);
            disposed = true; CancelQueuedPlacement(); glass.Dispose();
        };
        Citrus.Windows.Presentation.WindowChromeSetup.Apply(this);
    }

    private void SetGlassStage(GlassPresentationStage stage)
    {
        wheelCanvas.Opacity = stage == GlassPresentationStage.Pending && glass.UsesBootstrap ? 0 : 1;
        bool highContrast = SystemParameters.HighContrast;
        WheelForegroundStyle.Apply(Resources, highContrast);
        WheelForegroundStyle.Apply(wheelCanvas.Resources, highContrast);
        foreach (var petal in wheelPetals) petal.ApplyForegroundPalette(highContrast);
        wheelShell.State = stage switch
        {
            GlassPresentationStage.Pending => BackdropState.PendingFrosted,
            GlassPresentationStage.Bootstrap or GlassPresentationStage.Live => BackdropState.LiveRefractiveGlass,
            _ => BackdropState.Solid
        };
        if (stage == GlassPresentationStage.Pending)
        {
            var material = GlassMaterial.Wheel;
            var tint = new SolidColorBrush(System.Windows.Media.Color.FromArgb(
                Channel(material.TintAlpha), Channel(material.TintRed), Channel(material.TintGreen), Channel(material.TintBlue)));
            tint.Freeze();
            Resources["MaterialShellTint"] = tint;
        }
        else Resources.Remove("MaterialShellTint");
        static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
    }

    private void QueueGlassPlacement()
    {
        if (disposed) return;
        if (!IsVisible || WindowState == WindowState.Minimized || !materialDecision.RequestNativeAcrylic)
        {
            CancelQueuedPlacement();
            lastPlacement = null;
            glass.Stop();
            return;
        }
        if (wheelCanvas.IsLoaded && PresentationSource.FromVisual(wheelCanvas) is not null && !WheelIsInViewport())
        {
            CancelQueuedPlacement();
            lastPlacement = null;
            glass.Stop();
            return;
        }
        // Initial layout can notify visibility, size and viewport changes in one
        // frame. Observe the final arrangement without canceling the first capture.
        if (placement is { Status: DispatcherOperationStatus.Pending }) return;
        placement = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            placement = null;
            SynchronizeGlass();
        }));
    }

    private void CancelQueuedPlacement()
    {
        placement?.Abort();
        placement = null;
    }

    private void SynchronizeGlass()
    {
        if (disposed || !materialDecision.RequestNativeAcrylic || !IsVisible || WindowState == WindowState.Minimized ||
            !wheelCanvas.IsLoaded || PresentationSource.FromVisual(wheelCanvas) is null)
        { lastPlacement = null; glass.Stop(); return; }
        if (!WheelIsInViewport())
        { lastPlacement = null; glass.Stop(); return; }
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        Point origin = wheelCanvas.PointToScreen(new Point());
        Point right = wheelCanvas.PointToScreen(new Point(WheelGeometry.Width, 0));
        var key = ((int)Math.Round(origin.X), (int)Math.Round(origin.Y), (int)Math.Round(right.X - origin.X));
        if (lastPlacement == key) return;
        if (lastPlacement is { } previous && previous.Size == key.Item3 && glass.Running)
        {
            lastPlacement = key;
            glass.SetPosition(key.Item1, key.Item2);
            return;
        }
        lastPlacement = key;
        glass.Start(key.Item1, key.Item2, key.Item3, dark, materialDecision.Animate, handle);
    }

    private bool WheelIsInViewport()
    {
        var bounds = wheelCanvas.TransformToAncestor(viewport).TransformBounds(new Rect(0, 0, 368, 368));
        return bounds.IntersectsWith(new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight));
    }
}
