using System.Reflection;
using Citrus.Windows.Presentation;

namespace Citrus.Windows.Tests;

public sealed class GlassWarmupEfficiencyTests
{
    [Theory]
    [InlineData(1d, 287)]
    [InlineData(1.25d, 358)]
    [InlineData(1.5d, 430)]
    [InlineData(2d, 574)]
    public void Warmup_raster_matches_the_native_truncated_wheel_width(double monitorScale, int pixels)
    {
        MethodInfo method = RasterScaleMethod();
        double actual = Assert.IsType<double>(method.Invoke(null, [monitorScale]));
        Assert.Equal(pixels / 368d, actual);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Warmup_rejects_an_invalid_monitor_scale(double monitorScale)
    {
        MethodInfo method = RasterScaleMethod();
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [monitorScale]));
        Assert.IsType<ArgumentOutOfRangeException>(error.InnerException);
    }

    [Fact]
    public async Task Resident_preparation_completes_the_optical_pipeline_before_allocation_reuse()
    {
        var runtime = new StudioGlassRuntime();
        var scene = new GlassScene(32, 32, 1, [GlassRegion.Circle(1, 16, 16, 14)]);
        try
        {
            await runtime.PrewarmAsync(scene);
            using var lease = runtime.Rent(scene);
            Assert.True(lease.Prepared);
            Assert.Equal(1, lease.Renderer.SourcePassCount);
            Assert.Equal(1, lease.Renderer.OpticalPassCount);
            // Prepared GPU execution must not make a zero/generated source reusable
            // as though it were a valid desktop capture.
            Assert.Throws<InvalidOperationException>(() => lease.Renderer.RenderCached(new(), new byte[32 * 32 * 4]));
        }
        finally { await runtime.ShutdownAsync(); }
    }

    [Fact]
    public void Execution_preparation_is_idempotent_and_clears_every_intermediate_and_presentation_pixel()
    {
        var scene = new GlassScene(32, 32, 1, [GlassRegion.Circle(1, 16, 16, 14)]);
        using var renderer = D3D11StudioRenderer.CreateForTesting(scene, software: true);
        var method = typeof(D3D11StudioRenderer).GetMethod("PrewarmExecution", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(renderer, null);
        Assert.Equal(1, renderer.SourcePassCount);
        Assert.Equal(1, renderer.OpticalPassCount);
        Assert.All(renderer.ReadClearedContentForTesting(), channel => Assert.Equal(0, channel));
        method.Invoke(renderer, null);
        Assert.Equal(1, renderer.SourcePassCount);
        Assert.Equal(1, renderer.OpticalPassCount);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(1d)]
    [InlineData(2d)]
    [InlineData(19.5d)]
    [InlineData(23d)]
    [InlineData(39.0625d)]
    [InlineData(46d)]
    public void Gaussian_passes_preserve_the_CPU_kernel_with_odd_even_and_production_support(double radius)
    {
        var material = new GlassMaterial
        {
            ThicknessDip = 0, RefractionDistance = 0, BlurRadiusDip = radius,
            ShadowStrength = 0, FresnelIntensity = 0, GlareIntensity = 0
        };
        var scene = new GlassScene(48, 32, 1, [GlassRegion.RoundedRect(1, 2, 2, 44, 28, 6)], material);
        using var renderer = D3D11StudioRenderer.CreateForTesting(scene, software: true);
        byte[] source = new byte[scene.CaptureWidth * scene.CaptureHeight * 4];
        for (int y = 0; y < scene.CaptureHeight; y++)
        for (int x = 0; x < scene.CaptureWidth; x++)
        {
            int offset = (y * scene.CaptureWidth + x) * 4;
            source[offset] = (byte)((x * 31 + y * 17) % 256);
            source[offset + 1] = (byte)((x + y) % 2 == 0 ? 255 : 0);
            source[offset + 2] = (byte)((x * 7 + y * 13) % 256);
            source[offset + 3] = 255;
        }
        renderer.RenderPixels(source, scene.CaptureWidth, scene.CaptureHeight, 0, 0, new(), new byte[48 * 32 * 4]);
        float[] background = renderer.ReadIntermediateForTesting(StudioPass.Background);
        float[] horizontal = renderer.ReadIntermediateForTesting(StudioPass.GaussianX);
        float[] vertical = renderer.ReadIntermediateForTesting(StudioPass.GaussianY);
        float[] kernel = StudioGlassMath.GaussianKernel(radius);
        int support = kernel.Length / 2;
        int[] xs = [0, 1, scene.CaptureWidth / 3, scene.CaptureWidth / 2, scene.CaptureWidth - 2, scene.CaptureWidth - 1];
        int[] ys = [0, 1, scene.CaptureHeight / 3, scene.CaptureHeight / 2, scene.CaptureHeight - 2, scene.CaptureHeight - 1];
        foreach (int y in ys)
        foreach (int x in xs)
        for (int channel = 0; channel < 3; channel++)
        {
            double expectedX = 0, expectedY = 0;
            for (int offset = -support; offset <= support; offset++)
            {
                expectedX += background[(y * scene.CaptureWidth + Math.Clamp(x + offset, 0, scene.CaptureWidth - 1)) * 4 + channel] * kernel[offset + support];
                expectedY += horizontal[(Math.Clamp(y + offset, 0, scene.CaptureHeight - 1) * scene.CaptureWidth + x) * 4 + channel] * kernel[offset + support];
            }
            int index = (y * scene.CaptureWidth + x) * 4 + channel;
            Assert.InRange(Math.Abs(horizontal[index] - expectedX), 0, .002);
            Assert.InRange(Math.Abs(vertical[index] - expectedY), 0, .002);
        }
    }

    private static MethodInfo RasterScaleMethod()
    {
        var method = typeof(WheelPresentationMetrics).GetMethod("RasterScale", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method;
    }
}
