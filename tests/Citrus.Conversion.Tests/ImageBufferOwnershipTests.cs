using System.Collections.Immutable;
using System.Security.Cryptography;
using Citrus.Core.Editing;
using Citrus.Conversion.Editing;
using Citrus.Conversion.Editing.Image;
using ImageMagick;

namespace Citrus.Conversion.Tests;

[Collection(SharedPreviewSchedulerCollection.Name)]
public sealed class ImageBufferOwnershipTests
{
    [Theory]
    [InlineData("spatial", "4F3C0199866785D22D5393E5B8153C137B3C4AD48AAA518F87ABFEFF240CEF1D")]
    [InlineData("vignette", "913EEE5509A5F8DF7CF4EE8280BFB7A74B0F52ECCE6182C69661CEBDD326D381")]
    [InlineData("grain", "9F0A55EFD230C92185349C615EEBCA011926EDD1303DD1B24F4AB25AB384331D")]
    [InlineData("scaled-grain", "A69A8584F22C42EF92D51DBC1FDA2699F090F0AEA58502F952693C17CFB38D09")]
    [InlineData("grayscale", "3E04F8EDCC8FC8ADDDEF9E6F556985CE99EF39D07EA3C5F77635F078B2D9ED89")]
    [InlineData("sepia", "E2479DFC17C4876E2A51C843A64CD9061C7C800AC6D8629407708E7336FCAB25")]
    [InlineData("invert", "3AD190673250A3F6EA6713940D2E159F7C3D285C012028E4F4106D44D056C619")]
    [InlineData("posterize", "0068C1E0E0CFF63F04F330312E50E46A2CD01C86A2992FBDD090F78A770D3E7F")]
    public void Adjustment_pixels_match_pinned_1207_reference_with_alpha_and_source_geometry(string control, string expected)
    {
        var a = ImageLayerFixture.Identity;
        a = control switch
        {
            "spatial" => a with { Clarity = .2, Dehaze = .1, Sharpen = .2, Denoise = .1, BlurSigma = .4 },
            "vignette" => a with { Brightness = .03, Vignette = .45 },
            "grain" or "scaled-grain" => a with { Grain = .6 },
            "grayscale" => a with { Effect = PhotoEffect.Grayscale, EffectAmount = .5 },
            "sepia" => a with { Effect = PhotoEffect.Sepia, EffectAmount = .5 },
            "invert" => a with { Effect = PhotoEffect.Invert, EffectAmount = .5 },
            _ => a with { Effect = PhotoEffect.Posterize, EffectAmount = .5 }
        };
        bool scaled = control == "scaled-grain"; int width = scaled ? 30 : 60, height = scaled ? 120 : 240;
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            pixels[i] = (byte)(x * 37 + y * 19); pixels[i + 1] = (byte)(x * 11 + y * 43); pixels[i + 2] = (byte)(x * 67 + y * 7); pixels[i + 3] = (byte)(x * 73 + y * 17);
        }
        using var image = new MagickImage(pixels, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGBA));
        var recipe = new ImageRecipe(Guid.NewGuid(), new(1, true, false, new(3, 5, 60, 240), null, false), a, [], null, [], []);
        if (scaled) ImageAdjustmentsPipeline.ApplyScaled(image, recipe, new(256, 64), default, .5);
        else ImageAdjustmentsPipeline.Apply(image, recipe, new(256, 64), default);
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(image.GetPixels().ToByteArray(PixelMapping.RGBA)!)));
    }

    [Fact]
    public void Inactive_final_effect_pass_avoids_its_coordinate_map_allocations()
    {
        using var image = new MagickImage(MagickColors.CornflowerBlue, 16, 16);
        var recipe = new ImageRecipe(Guid.NewGuid(), new(0, false, false, null, null, false), ImageLayerFixture.Identity with { Brightness = .05 }, [], null, [], []);
        var active = recipe with { Adjustments = recipe.Adjustments with { Vignette = double.Epsilon } };
        long Allocated(ImageRecipe value)
        {
            using var copy = (MagickImage)image.Clone();
            long before = GC.GetAllocatedBytesForCurrentThread();
            ImageAdjustmentsPipeline.Apply(copy, value, new(16, 16), default);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        for (int i = 0; i < 3; i++) { Allocated(recipe); Allocated(active); }
        Assert.True(Allocated(recipe) + 100 <= Allocated(active), "An inactive final pass should not allocate a source-coordinate recipe/map.");
    }

    [Theory]
    [InlineData(PhotoEffect.None, .7)]
    [InlineData(PhotoEffect.Invert, 0)]
    public void Inactive_final_effect_preserves_all_Q8_values_alpha_and_metadata(PhotoEffect effect, double amount)
    {
        byte[] original = new byte[256 * 4], expected = new byte[original.Length];
        for (int value = 0; value < 256; value++)
        {
            int i = value * 4;
            original[i] = (byte)value; original[i + 1] = (byte)(255 - value); original[i + 2] = (byte)((value * 73) % 256); original[i + 3] = (byte)value;
            for (int c = 0; c < 3; c++) expected[i + c] = (byte)Math.Clamp(Math.Round(original[i + c] + .125 * 255, MidpointRounding.AwayFromZero), 0, 255);
            expected[i + 3] = original[i + 3];
        }
        using var image = new MagickImage(original, new PixelReadSettings(256, 1, StorageType.Char, PixelMapping.RGBA));
        image.Comment = "preserved source comment"; image.SetProfile(ColorProfiles.SRGB);
        byte[] profile = image.GetColorProfile()!.ToByteArray();
        var recipe = new ImageRecipe(Guid.NewGuid(), new(0, false, false, null, null, false), ImageLayerFixture.Identity with { Brightness = .125, Effect = effect, EffectAmount = amount }, [], null, [], []);
        ImageAdjustmentsPipeline.Apply(image, recipe, new(256, 1), default);
        Assert.Equal(expected, image.GetPixels().ToByteArray(PixelMapping.RGBA));
        Assert.Equal("preserved source comment", image.Comment);
        Assert.Equal(profile, image.GetColorProfile()!.ToByteArray());
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ImageAdjustmentsPipeline.Apply(image, recipe, new(256, 1), canceled.Token));
    }

    [Fact]
    public async Task Annotation_frame_avoids_a_second_full_frame_copy_and_outlives_native_layer()
    {
        using var fixture = new ImageLayerFixture(); var document = await fixture.Create(1024, 1024);
        document = document with { Recipe = ((ImageRecipe)document.Recipe) with { Annotations = [new(Guid.NewGuid(), true, new ShapeGeometry(new(20, 20, 64, 64), false, 0), ImageLayerFixture.Red with { Opacity = .5 })] } };
        var sources = ImmutableDictionary<Guid, string>.Empty.Add(document.Sources[0].Id, document.Sources[0].Path);
        var rasterizer = new AnnotationRasterizer(); var request = new PreviewRequest(3, new(1024, 1024), null, null, null);
        rasterizer.Render(document, request, sources);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var frame = rasterizer.Render(document, request, sources);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long bytes = frame.Bgra32.Length;
        Assert.True(allocated < bytes * 2 + 512 * 1024, $"Annotation rendering allocated {allocated:N0} bytes for {bytes:N0} output bytes.");
        byte[] saved = frame.Bgra32.ToArray();
        rasterizer.Render(document with { Recipe = ((ImageRecipe)document.Recipe) with { Annotations = [] } }, request, sources);
        Assert.Equal(saved, frame.Bgra32.ToArray());
        Assert.InRange(frame.Bgra32[(30 * 1024 + 30) * 4 + 3], (byte)127, (byte)128);
        Assert.Equal(0, frame.Bgra32[3]);
    }

    [Fact]
    public async Task Renderer_frame_remains_owned_after_cache_reuse_and_disposal()
    {
        using var fixture = new ImageLayerFixture(); var document = await fixture.Create(64, 32);
        var renderer = new ImageRecipeRenderer(new SourceSnapshotService());
        var request = new PreviewRequest(7, new(64, 32), null, null, null);
        var frame = await renderer.RenderPreviewAsync(document, request, default);
        byte[] saved = frame.Bgra32.ToArray();
        var changed = document with { Recipe = ((ImageRecipe)document.Recipe) with { Adjustments = ImageLayerFixture.Identity with { Brightness = -.5 } } };
        var second = await renderer.RenderPreviewAsync(changed, request, default);
        renderer.Dispose();
        Assert.False(saved.SequenceEqual(second.Bgra32));
        Assert.Equal(saved, frame.Bgra32.ToArray());
        Assert.Equal(document.Revision, frame.Revision); Assert.Equal(7, frame.Generation); Assert.Empty(frame.Warnings);
    }

    [Fact]
    public async Task Cached_exact_renderer_allocates_only_one_output_pixel_array()
    {
        using var fixture = new ImageLayerFixture(); var document = await fixture.Create(1024, 1024);
        using var renderer = new ImageRecipeRenderer(new SourceSnapshotService());
        var request = new PreviewRequest(7, new(1024, 1024), null, null, null);
        for (int i = 0; i < 3; i++) await renderer.RenderPreviewAsync(document, request, default);
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var frame = await renderer.RenderPreviewAsync(document, request, default);
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.True(allocated < frame.Bgra32.Length + 512 * 1024, $"Renderer allocated {allocated:N0} bytes for {frame.Bgra32.Length:N0} output bytes.");
    }
}
