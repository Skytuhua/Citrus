using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Citrus.Windows.Presentation;
using Citrus.Core;
using SystemColors = System.Windows.SystemColors;
using Application = System.Windows.Application;
namespace Citrus.Windows.Tests;
[Collection("Desktop")]
public sealed class GlassSurfaceIntegrationTests(DesktopFixture desktop)
{
    [Fact]
    public Task Native_material_state_not_inferred_from_alpha_color() => desktop.RunAsync(() =>
    {
        var native = new MaterialSurface { State = BackdropState.ConfiguredNativeAcrylic };
        var fallback = new MaterialSurface();
        Assert.Equal(BackdropState.Solid, fallback.State);
        Assert.Equal(BackdropState.ConfiguredNativeAcrylic, native.State);
        Assert.Equal(255, ((SolidColorBrush)fallback.EffectiveTint).Color.A);
        Assert.True(((SolidColorBrush)native.EffectiveTint).Color.A < 255);
        return Task.CompletedTask;
    });
    [Fact]
    public Task Twice_text_scale_reflows_without_double_dpi_scaling() => desktop.RunAsync(() =>
    {
        using var theme = new ThemeManager(Application.Current);
        try
        {
            theme.ApplyTypography(2);
            Assert.Equal(28d, Application.Current.FindResource("BodySize"));
            Assert.Equal(40d, Application.Current.FindResource("BodyLeading"));
            theme.ApplyTypography(2);
            Assert.Equal(28d, Application.Current.FindResource("BodySize"));
            var settings = new SettingsWindow(new(), _ => Task.CompletedTask);
            Assert.Null(PressFeedback.GetAnimator(settings));
            settings.Close();
        }
        finally { theme.ApplyTypography(1); }
        return Task.CompletedTask;
    });
    [Fact]
    public Task Highcontrast_has_opaque_system_surfaces_and_focus() => desktop.RunAsync(() =>
    {
        using var theme = new ThemeManager(Application.Current);
        try
        {
            theme.ApplyPalette(ThemeChoice.Dark, false, true);
            foreach (string key in new[] { "WindowBackground", "PanelBackground", "ControlBackground", "WheelShell", "PetalBackground" })
            {
                Assert.Same(SystemColors.WindowBrush, Application.Current.FindResource(key));
                Assert.Equal(255, ((SolidColorBrush)Application.Current.FindResource(key)).Color.A);
            }
            Assert.Same(SystemColors.HighlightBrush, Application.Current.FindResource("SelectedPetal"));
            Assert.Same(SystemColors.HighlightTextBrush, Application.Current.FindResource("SelectedPetalText"));
            Assert.False((bool)Application.Current.FindResource("MotionEnabled"));
            var button = new System.Windows.Controls.Button();
            button.Style = (Style)Application.Current.FindResource("CitrusButton");
            Assert.NotNull(button.FocusVisualStyle);
        }
        finally { theme.Apply(ThemeChoice.Light, true); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task Content_cards_remain_solid_inside_native_chrome() => desktop.RunAsync(() =>
    {
        var window = new MainWindow((App)Application.Current);
        try
        {
            var chrome = (MaterialSurface)window.FindName("Chrome");
            chrome.State = BackdropState.ConfiguredNativeAcrylic;
            Assert.True(((SolidColorBrush)chrome.EffectiveTint).Color.A < 255);
            Assert.Equal(255, ((SolidColorBrush)((Border)window.FindName("Inspector")).Background).Color.A);
            Assert.Equal(255, ((SolidColorBrush)Application.Current.FindResource("MaterialContentSurface")).Color.A);
        }
        finally { window.Dispose(); window.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task Shared_services_attach_each_window_and_clear_animator_on_teardown() => desktop.RunAsync(() =>
    {
        using var theme = new ThemeManager(Application.Current);
        using var services = new WindowPresentationService(Application.Current, theme, () => new CitrusSettings { ReduceTransparency = true });
        var first = new SettingsWindow(new(), _ => Task.CompletedTask);
        var second = new ShelfWindow();
        try
        {
            services.Attach(first, (MaterialSurface)first.FindName("Chrome"));
            services.Attach(second, (MaterialSurface)second.FindName("Chrome"));
            Assert.Same(PressFeedback.GetAnimator(first), PressFeedback.GetAnimator(second));
            Assert.Same(services.Animator, PressFeedback.GetAnimator(first));
            first.Show(); second.Show();
            Assert.Equal(BackdropState.Solid, ((MaterialSurface)first.FindName("Chrome")).State);
            first.Close();
            Assert.Null(PressFeedback.GetAnimator(first));
            Assert.Same(services.Animator, PressFeedback.GetAnimator(second));
            services.Dispose();
            Assert.Null(PressFeedback.GetAnimator(second));
        }
        finally { first.Close(); second.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task Twice_text_scale_keeps_narrow_workspace_inspector_reachable() => desktop.RunAsync(async () =>
    {
        using var files = new NativeFiles();
        string path = System.IO.Path.Combine(files.Root, "citrus.png");
        CitrusPreviewFixture.Save(path);
        using var theme = new ThemeManager(Application.Current);
        var window = new MainWindow((App)Application.Current) { Width = 860, Height = 640, ShowActivated = false };
        try
        {
            window.Configure(new OperationCatalog([new Citrus.Conversion.ImageEngine()]), []);
            window.AddFiles([path]);
            theme.ApplyTypography(2);
            window.ApplyTextScale(2);
            window.Show(); window.UpdateLayout();
            var run = (FrameworkElement)window.FindName("RunButton");
            Assert.True(run.IsVisible);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var scroll = (ScrollViewer)window.FindName("WorkspaceScroll");
            Assert.True(scroll.ScrollableHeight > 0);
            var root = (FrameworkElement)window.Content;
            VisualArtifacts.AssertInside(run, root);
            var position = run.TransformToAncestor(root).Transform(new System.Windows.Point());
            scroll.ScrollToEnd();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.Equal(position, run.TransformToAncestor(root).Transform(new System.Windows.Point()));
            VisualArtifacts.AssertInside(run, root);
        }
        finally { window.Dispose(); window.Close(); theme.ApplyTypography(1); }
    });

    [Theory]
    [InlineData(420, 330)]
    [InlineData(340, 250)]
    public Task Twice_text_scale_keeps_shelf_references_and_actions_reachable(int width, int height) => desktop.RunAsync(async () =>
    {
        using var theme = new ThemeManager(Application.Current);
        using var files = new NativeFiles();
        string path = files.File("shelf-reference.txt", "owned physical reference");
        theme.Apply(ThemeChoice.Light, true);
        theme.ApplyTypography(2);
        using var shelf = new ShelfWindow { Width = width, Height = height, ShowActivated = false };
        try
        {
            shelf.AddPaths([path]); shelf.Show(); shelf.UpdateLayout();
            var list = (System.Windows.Controls.ListBox)shelf.FindName("References");
            Assert.True(list.ActualHeight >= 120, "Large text must not consume the reference viewport.");
            list.SelectedItem = path; list.ScrollIntoView(path);
            var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(path);
            item.BringIntoView();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.Equal(path, list.SelectedItem);
            Assert.True(item.IsSelected);
            VisualArtifacts.AssertInside(item, (FrameworkElement)shelf.Content);
            VisualArtifacts.Capture((FrameworkElement)shelf.Content, $"shelf-{width}x{height}-text200-reference", 1, ThemeChoice.Light,
                new { textScaleFactor = 2, width, height });
            foreach (string name in new[] { "CopyReferences", "RemoveReferences" })
            {
                var button = (System.Windows.Controls.Button)shelf.FindName(name);
                button.BringIntoView();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.True(button.IsEnabled);
                VisualArtifacts.AssertInside(button, (FrameworkElement)shelf.Content);
                VisualArtifacts.Capture((FrameworkElement)shelf.Content, $"shelf-{width}x{height}-text200-{name}", 1, ThemeChoice.Light,
                    new { textScaleFactor = 2, width, height });
            }
            ((System.Windows.Controls.Button)shelf.FindName("RemoveReferences")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.Empty(shelf.Paths); // Reachable selection still drives the existing real action.
        }
        finally { shelf.Dispose(); theme.ApplyTypography(1); }
    });

    [Fact]
    public Task Owned_fixture_activation_establishes_baseline_before_nonactivating_wheel() => desktop.RunAsync(() =>
    {
        using var scene = new GlassOwnedScene();
        nint original = BackdropNativeMethods.GetForegroundWindow();
        scene.Show(activateOwnedBackground: true);
        Assert.Equal(original.ToInt64(), scene.OriginalForeground!.Handle);
        Assert.True(scene.BackgroundActivationAttempted);
        Assert.Equal(scene.BackgroundHandle == scene.ForegroundBefore, scene.BackgroundActivationSucceeded);
        Assert.Equal(scene.ForegroundBefore.ToInt64(), scene.BaselineForeground!.Handle);
        Assert.Equal(scene.ForegroundBefore, BackdropNativeMethods.GetForegroundWindow());
        long wheelStyle = BackdropNativeMethods.GetWindowLongPtr(scene.WheelHandle, -20).ToInt64();
        Assert.NotEqual(0, wheelStyle & BackdropNativeMethods.WsExNoActivate);
        // Activation may be refused by Windows; the unmodified capture guard still decides eligibility.
        return Task.CompletedTask;
    });

    [Fact]
    public Task Compositor_fixture_changes_named_shell_state_without_cutting_the_center_backing() => desktop.RunAsync(() =>
    {
        using var scene = new GlassOwnedScene();
        var shell = (MaterialSurface)scene.Wheel.FindName("Shell");
        Assert.Empty(((Canvas)scene.Wheel.FindName("Presentation")).Children.OfType<System.Windows.Shapes.Ellipse>());
        scene.SetShellState(BackdropState.ConfiguredNativeAcrylic);
        Assert.Equal(BackdropState.ConfiguredNativeAcrylic, shell.State);
        Assert.True(((SolidColorBrush)shell.EffectiveTint).Color.A < 255);
        Assert.Null(shell.Clip);
        scene.SetShellState(BackdropState.Solid);
        Assert.Equal(BackdropState.Solid, shell.State);
        Assert.Equal(255, ((SolidColorBrush)shell.EffectiveTint).Color.A);
        Assert.Null(shell.Clip);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Hub_metadata_retains_its_label_without_a_center_disc_or_second_pill() => desktop.RunAsync(() =>
    {
        var wheel = new WheelWindow(new OperationCatalog([]));
        try
        {
            var plate = (Border)wheel.FindName("CaptionPlate");
            Assert.InRange(Canvas.GetLeft(plate), 124, 144);
            Assert.True(Canvas.GetLeft(plate) + plate.Width <= 244);
            Assert.InRange(((SolidColorBrush)plate.Background).Color.A, 240, 255);
            Assert.Empty(((Canvas)wheel.FindName("Presentation")).Children.OfType<System.Windows.Shapes.Ellipse>());
            Assert.Null(((MaterialSurface)wheel.FindName("Shell")).Clip);
        }
        finally { wheel.Close(); }
        return Task.CompletedTask;
    });
    [Fact]
    public Task Gif_short_petal_preserves_full_label_and_operation_identity() => desktop.RunAsync(() =>
    {
        var operation = new OperationDescriptor("image.convert.gif", "GIF (still image)", OperationMode.Convert, "gif", false, false);
        var petal = new WheelPetal(operation, 0, 9);
        Assert.Equal("GIF", petal.Children.OfType<TextBlock>().Single().Text);
        Assert.Equal(operation.Label, System.Windows.Automation.AutomationProperties.GetName(petal));
        Assert.Equal("image.convert.gif", operation.Id);
        return Task.CompletedTask;
    });
}
