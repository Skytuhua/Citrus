using System.Security.Cryptography;
using Citrus.Core.Editing;
using Citrus.Conversion.Editing.Image;
using ImageMagick;

namespace Citrus.Conversion.Tests;

public sealed class ImageAdjustmentKernelPerformanceTests
{
    private static readonly ImageAdjustments Identity = new(1, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 7, PhotoEffect.None, 0);

    // Captured from SDR v1 with Magick.NET 15.0.0 / .NET 10 on Linux before removing
    // neutral color work. The ramp covers every
    // channel and alpha byte, including hidden RGB at alpha zero, to protect Q8 stages.
    public static IEnumerable<object[]> GoldenRecipes()
    {
        yield return ["brightness", Identity with { Brightness = .1 }, "C307C532FAA531A7FC6E555228D1A14E23A81BE5C079E19D123B96F732560EBE", "C307C532FAA531A7FC6E555228D1A14E23A81BE5C079E19D123B96F732560EBE"];
        yield return ["whites", Identity with { Whites = .7 }, "DAD04EBA94BC512A6E32D6271378ED58FF3D5EDC15207BD605FC5796761E98C2", "DAD04EBA94BC512A6E32D6271378ED58FF3D5EDC15207BD605FC5796761E98C2"];
        yield return ["negative-whites", Identity with { Whites = -.7 }, "D1D867A56DFEFFFC1C219325EA1BDB09A2F222775949DAD0A11B085DAE3C7414", "D1D867A56DFEFFFC1C219325EA1BDB09A2F222775949DAD0A11B085DAE3C7414"];
        yield return ["blacks", Identity with { Blacks = .7 }, "70966FF2034F875C2700A618D577725DD4076392DE8AC4C97559A4853D0D08B3", "70966FF2034F875C2700A618D577725DD4076392DE8AC4C97559A4853D0D08B3"];
        yield return ["negative-blacks", Identity with { Blacks = -.7 }, "4D99CCB8957F954ECFECD05D40D17E26D2C70C4F7336A66C8DB90FE44A9956EC", "4D99CCB8957F954ECFECD05D40D17E26D2C70C4F7336A66C8DB90FE44A9956EC"];
        yield return ["white-black", Identity with { Whites = .45, Blacks = -.35 }, "3CD7A6B409E57F12A8B018D16DE559275B615457D1DDA565101951BDD767ADEF", "3CD7A6B409E57F12A8B018D16DE559275B615457D1DDA565101951BDD767ADEF"];
        yield return ["mixed-tone", Identity with { ExposureEv = .35, Brightness = -.1, Contrast = .2, Gamma = 1.3, Shadows = .3, Highlights = -.4, Whites = .45, Blacks = -.35, Saturation = 1.15, Vibrance = .25, HueDegrees = 17, Warmth = .2, Tint = -.1 }, "DA83C90FFA6A80C4594D32C0C8940B634A081A206808FC2C614FB188701FDA71", "DA83C90FFA6A80C4594D32C0C8940B634A081A206808FC2C614FB188701FDA71"];
        yield return ["grain", Identity with { Grain = .8 }, "EF97F1774D26D33BF693EBF52FA4364B52B9CE7E07677733A39DB284B382CBE9", "05A575A12AF911641D066F56A0E247D7088D90B4604B087802A95ABDC55723EC"];
        yield return ["vignette", Identity with { Vignette = .7 }, "03771CEBC5EC9C95BAE91C167E4856201EA5B96AE6F969D8ADD6975A8ABFA233", "03771CEBC5EC9C95BAE91C167E4856201EA5B96AE6F969D8ADD6975A8ABFA233"];
        yield return ["invert", Identity with { Effect = PhotoEffect.Invert, EffectAmount = .65 }, "8A5F5402F03723D20EDCC27ECF75168B6AA1CD8416DAD34F49037E84E9B3CEB9", "8A5F5402F03723D20EDCC27ECF75168B6AA1CD8416DAD34F49037E84E9B3CEB9"];
        yield return ["grayscale", Identity with { Effect = PhotoEffect.Grayscale, EffectAmount = 1 }, "CA2223CC8BB27A7456A1E6748259A0BD9E18FA65B84C73E2AC871C28A25C316F", "CA2223CC8BB27A7456A1E6748259A0BD9E18FA65B84C73E2AC871C28A25C316F"];
        yield return ["clarity", Identity with { Clarity = .4 }, "F05551B8BE499F67388F7B227E5A20DC3868921AC8BA8BD1833FC95140B9B5C0", "F03CA8AC917AD1006D8A84CD4CEC944937F62294C1A107F4254936369EB43FD9"];
        yield return ["sharpen", Identity with { Sharpen = .6 }, "A75D0F3928B0AA73D9271A7B45D401110BE5C9C2A2A83A92275FDDB0BE9DC940", "E5A1CD061D00757CC929F54753FA8A4F043E1CB333AE55F4B29003FC19C69D92"];
        yield return ["blur", Identity with { BlurSigma = 2 }, "55CF448FCD1262469247699D64FAFFC0C70C088BAF8D00F325AFD880AFFD958D", "92B7321471E071AEC8787BA44496A9F946809B355406CFE1AD9E0387F4BE0D44"];
        yield return ["denoise", Identity with { Denoise = .4 }, "DDD0F7CEE2F5B2621F2780113925240DC864F5EF0613516C554AC0381C14A31A", "06E2C96E3EE52E8B906ACBF929585796FFAB08BCECA00911D537ED2DCAFD35D4"];
        yield return ["dehaze", Identity with { Dehaze = .5 }, "3DC2A5B1E597996190A34C7EA85DB5B5D55BD1A3C5BC550ABB2CBD90F11D927B", "5448397B919B038CFEF05EA827293BC1DC3A14954E8187C654FC97B123D2B456"];
        yield return ["mixed-spatial", Identity with { Clarity = .3, Sharpen = .4, BlurSigma = 1.5, Grain = .2, Vignette = .2, Effect = PhotoEffect.Sepia, EffectAmount = .3 }, "D4D37650AB00288C29B86196A6EDC1EE9ABC97207B7D291A7453B4B99C6C4DDF", "4907921A6B074511C4F6639D45A14A3B15A88F94A4B742D4D8120342974899DE"];
        yield return ["mixed-all", Identity with { ExposureEv = .2, Brightness = .05, Contrast = .1, Gamma = 1.1, Highlights = .1, Shadows = .1, Whites = .3, Blacks = -.2, Saturation = 1.1, Vibrance = .1, HueDegrees = 5, Warmth = .1, Tint = .1, Clarity = .2, Dehaze = .2, Sharpen = .2, Denoise = .2, BlurSigma = 1, Vignette = .1, Grain = .1, Effect = PhotoEffect.Sepia, EffectAmount = .1 }, "4A9D7BFD068E4057189C62E7D26CF8117EC47F6B3E5218EED6238A92C358DD87", "529B6326856D1F669129A2243A2501482BFDA5741F3CCC2AEA51F28B491A6F68"];
    }

    [Theory]
    [MemberData(nameof(GoldenRecipes))]
    public void Adjustments_preserve_SDR_v1_exact_and_scaled_RGBA_bytes(string control, ImageAdjustments adjustments, string exact, string scaled)
    {
        _ = control;
        byte[] input = Ramp(256, 256);
        foreach ((double scale, string expected) in new[] { (1d, exact), (.25, scaled) })
        {
            using var image = Image(input, 256, 256);
            if (scale == 1) ImageAdjustmentsPipeline.Apply(image, Recipe(adjustments), new(256, 256), default);
            else ImageAdjustmentsPipeline.ApplyScaled(image, Recipe(adjustments), new(1024, 1024), default, scale);
            using var pixels = image.GetPixels();
            byte[] actual = pixels.ToByteArray(PixelMapping.RGBA)!;
            Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(actual)));
            for (int i = 3; i < actual.Length; i += 4) Assert.Equal(input[i], actual[i]);
        }
    }

    [Fact]
    public void Grain_only_render_keeps_managed_scratch_close_to_its_pixel_buffer()
    {
        const int width = 64, height = 64;
        byte[] input = Ramp(width, height);
        var recipe = Recipe(Identity with { Grain = .8 });
        for (int i = 0; i < 4; i++)
        {
            using var warmup = Image(input, width, height);
            ImageAdjustmentsPipeline.Apply(warmup, recipe, new(width, height), default);
        }
        long minimum = long.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            using var image = Image(input, width, height);
            long before = GC.GetAllocatedBytesForCurrentThread();
            ImageAdjustmentsPipeline.Apply(image, recipe, new(width, height), default);
            minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        // Allow native wrapper overhead, but no per-render tone table for a grain-only edit.
        Assert.InRange(minimum, 0, input.LongLength + 1024);
    }

    private static ImageRecipe Recipe(ImageAdjustments adjustments) => new(Guid.Empty, new(0, false, false, null, null, false), adjustments, [], null, [], []);
    private static MagickImage Image(byte[] pixels, int width, int height) => new(pixels, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGBA));
    private static byte[] Ramp(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            pixels[i] = (byte)x; pixels[i + 1] = (byte)y;
            pixels[i + 2] = (byte)((x * 37 + y * 17) % 256); pixels[i + 3] = (byte)((x + y) % 256);
        }
        return pixels;
    }
}
