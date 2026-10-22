using System.Reflection;
using System.Windows.Controls;
using System.Windows.Media;
using Citrus.Core;
using Citrus.Windows.Presentation;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class FrostedWheelPresentationTests(DesktopFixture desktop)
{
    [Fact]
    public Task Production_optical_image_covers_the_existing_pagination_and_has_no_duplicate_caption() => desktop.RunAsync(() =>
    {
        var wheel = new WheelWindow(new OperationCatalog([]));
        try
        {
            Assert.Null(wheel.FindName("ActionDescription"));
            var image = Assert.IsType<System.Windows.Controls.Image>(wheel.FindName("GlassBody"));
            Assert.Equal(368, image.Width); Assert.Equal(454, image.Height);
            Assert.Null(wheel.FindName("GlassLighting"));
            var nav = Assert.IsType<Border>(wheel.FindName("NavigationPlate"));
            Assert.Equal(86, Canvas.GetLeft(nav)); Assert.Equal(372, Canvas.GetTop(nav));
            Assert.Equal(196, nav.Width); Assert.Equal(36, nav.Height);
        }
        finally { wheel.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task Pending_backing_keeps_the_final_translucent_white_petal_fill() => desktop.RunAsync(() =>
    {
        var wheel = new WheelWindow(new OperationCatalog([]));
        try
        {
            SetMaterial(wheel, BackdropState.PendingFrosted);
            var shell = Assert.IsType<MaterialSurface>(wheel.FindName("Shell"));
            Assert.Equal(BackdropState.PendingFrosted, shell.State);
            Assert.Equal(173, Assert.IsType<SolidColorBrush>(shell.EffectiveTint).Color.A);
            var petals = Assert.IsType<LinearGradientBrush>(wheel.FindResource("PetalBackground"));
            Assert.Equal(230, petals.GradientStops[0].Color.A);
            Assert.Equal(217, petals.GradientStops[^1].Color.A);
        }
        finally { wheel.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task Live_pagination_does_not_cover_its_optical_surface_with_a_solid_capsule() => desktop.RunAsync(() =>
    {
        var wheel = new WheelWindow(new OperationCatalog([]));
        try
        {
            SetMaterial(wheel, BackdropState.LiveRefractiveGlass);
            var nav = Assert.IsType<Border>(wheel.FindName("NavigationPlate"));
            Assert.Equal(0, Assert.IsType<SolidColorBrush>(nav.Background).Color.A);
        }
        finally { wheel.Close(); }
        return Task.CompletedTask;
    });

    private static void SetMaterial(WheelWindow wheel, BackdropState state) =>
        typeof(WheelWindow).GetMethod("SetMaterialState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(wheel, [state]);
}
