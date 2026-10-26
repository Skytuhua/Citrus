using System.Security.Cryptography;
using Citrus.Core.Editing;
using Citrus.Conversion.Editing;
using Citrus.Conversion.Editing.Image;
using ImageMagick;
using Xunit.Abstractions;

namespace Citrus.Conversion.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HeicHelperConfigurationCollection
{
    public const string Name = "HEIC helper process configuration";
}

// The shell surrogate tests the managed helper boundary. It does not establish
// genuine HEVC fidelity or Windows offline-pack acceptance.
public sealed class LinuxHeicHelperFactAttribute : FactAttribute
{
    public LinuxHeicHelperFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Counted /bin/sh helper surrogate requires Linux; native HEIC acceptance is separate.";
    }
}

public sealed class LinuxHeicHelperTheoryAttribute : TheoryAttribute
{
    public LinuxHeicHelperTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Counted /bin/sh helper surrogate requires Linux; native HEIC acceptance is separate.";
    }
}

[Collection(HeicHelperConfigurationCollection.Name)]
public sealed class HeicRasterLoadingTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "citrus-counted-heic-" + Guid.NewGuid().ToString("N"));
    private readonly string originalHelper = HeicCodec.Executable;
    private readonly bool originallyAvailable = HeicCodec.Available;
    private readonly ITestOutputHelper output;
    private string Response => Path.Combine(root, "response.rgba");
    private string Log => Path.Combine(root, "invocations.log");
    private static readonly ImageAdjustments Identity = new(1, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 7, PhotoEffect.None, 0);

    public HeicRasterLoadingTests(ITestOutputHelper output) => this.output = output;

    public async Task InitializeAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        Directory.CreateDirectory(root);
        WriteResponse(Path.Combine(root, "probe.rgba"), 9, 7, [], new byte[9 * 7 * 4]);
        string helper = Path.Combine(root, "counted-heic.sh");
        File.WriteAllText(helper, """
            #!/bin/sh
            case "$1" in
              encode) printf 'owned hvcC surrogate' > "$6" ;;
              decode)
                printf '%s\n' "$3" >> invocations.log
                if [ ! -s "$2" ]; then printf 'Missing source' >&2; exit 7; fi
                if [ -f slow ]; then sleep 60 & wait; fi
                case "$2" in
                  */probe.heic) cp probe.rgba "$3" ;;
                  *) cp response.rgba "$3" ;;
                esac ;;
              *) exit 8 ;;
            esac
            """ + "\n");
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        HeicCodec.Configure(helper);
        await HeicCodec.VerifyAsync(default);
        File.Delete(Log);
    }

    public async Task DisposeAsync()
    {
        try
        {
            HeicCodec.Configure(originalHelper);
            if (originallyAvailable) await HeicCodec.VerifyAsync(default);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [LinuxHeicHelperTheory]
    [InlineData(".heic", "none")]
    [InlineData(".HEIF", "none")]
    [InlineData(".heic", "srgb")]
    [InlineData(".HEIF", "srgb")]
    [InlineData(".heic", "adobe")]
    [InlineData(".HEIF", "adobe")]
    public async Task Raster_load_decodes_once_and_preserves_normalized_pixels(string extension, string profileName)
    {
        byte[] pixels = Pattern(), original = [1, 2, 3, 4];
        byte[] profile = profileName switch { "srgb" => ColorProfiles.SRGB.ToByteArray(), "adobe" => ColorProfiles.AdobeRGB1998.ToByteArray(), _ => [] };
        WriteResponse(Response, 32, 16, profile, pixels);
        string source = Path.Combine(root, "owned" + extension);
        File.WriteAllBytes(source, original);
        var snapshot = (await new SourceSnapshotService().CaptureAsync([source], default))[0].Snapshot!;
        ImageRasterSource.CheckAvailable([snapshot]);
        using var expected = new MagickImage(pixels, new PixelReadSettings(32, 16, StorageType.Char, PixelMapping.RGBA));
        expected.SetProfile(profile.Length == 0 ? ColorProfiles.SRGB : new ColorProfile(profile));
        expected.TransformColorSpace(ColorProfiles.SRGB);
        using var actual = ImageRasterSource.Load(source, default);
        Assert.Equal(32u, actual.Width); Assert.Equal(16u, actual.Height);
        Assert.Equal(Orientation.TopLeft, actual.Orientation);
        Assert.Equal(ColorProfiles.SRGB.ToByteArray(), actual.GetColorProfile()!.ToByteArray());
        using var expectedPixels = expected.GetPixels(); using var actualPixels = actual.GetPixels();
        byte[] rgba = actualPixels.ToByteArray(PixelMapping.RGBA)!;
        Assert.Equal(expectedPixels.ToByteArray(PixelMapping.RGBA), rgba);
        for (int i = 3; i < pixels.Length; i += 4) Assert.Equal(pixels[i], rgba[i]);
        Assert.Equal(original, File.ReadAllBytes(source));
        AssertPrivateDecodesRemoved();
        output.WriteLine($"Decode calls: {DecodeCount}; raw RGBA bytes per call: {pixels.Length}; SHA256: {Convert.ToHexString(SHA256.HashData(rgba))}");
        Assert.Equal(1, DecodeCount);
    }

    [LinuxHeicHelperFact]
    public async Task Export_uses_one_decode_and_retains_its_source_snapshot()
    {
        byte[] pixels = Pattern(); WriteResponse(Response, 32, 16, [], pixels);
        string source = Path.Combine(root, "owned.heic"); File.WriteAllBytes(source, [1, 2, 3, 4]);
        var snapshot = (await new SourceSnapshotService().CaptureAsync([source], default))[0].Snapshot!;
        var document = new EditorDocument(1, 0, [snapshot], [], new ImageRecipe(snapshot.Id, new(0, false, false, null, null, false), Identity, [], null, [], []), new("png", null, "edited", new QualityGoal(85), MetadataPolicy.PreserveSupported, new(255, 255, 255, 255)));
        using var renderer = new ImageRecipeRenderer(new SourceSnapshotService());
        string stage = Path.Combine(root, "export");
        var result = await renderer.RenderExportAsync(document, stage, null, default);
        using var encoded = new MagickImage(Path.Combine(stage, "payload", result.Files.Single().RelativePath));
        Assert.Equal(32u, encoded.Width); Assert.Equal(16u, encoded.Height);
        Assert.Equal(pixels, encoded.GetPixels().ToByteArray(PixelMapping.RGBA));
        Assert.Equal(snapshot.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))));
        AssertPrivateDecodesRemoved();
        output.WriteLine($"Export decode calls: {DecodeCount}; raw RGBA bytes per call: {pixels.Length}");
        Assert.Equal(1, DecodeCount);
    }

    [LinuxHeicHelperTheory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(16385, 1)]
    [InlineData(8001, 8000)]
    public void Invalid_decoded_dimensions_reject_before_pixel_allocation(int width, int height)
    {
        WriteHeader(width, height, 0, []);
        Assert.Throws<NotSupportedException>(() => ImageRasterSource.Load(Source(), default));
        Assert.Equal(1, DecodeCount); AssertPrivateDecodesRemoved();
    }

    [LinuxHeicHelperTheory]
    [InlineData(-1, 4)]
    [InlineData(4194305, 4)]
    [InlineData(0, 3)]
    [InlineData(0, 5)]
    [InlineData(1, 4)]
    public void Invalid_profile_or_pixel_lengths_reject_before_image_construction(int profileLength, int payloadLength)
    {
        WriteHeader(1, 1, profileLength, new byte[payloadLength]);
        Assert.Throws<InvalidDataException>(() => ImageRasterSource.Load(Source(), default));
        Assert.Equal(1, DecodeCount); AssertPrivateDecodesRemoved();
    }

    [LinuxHeicHelperFact]
    public void Already_canceled_load_does_not_launch_a_decoder()
    {
        Assert.ThrowsAny<OperationCanceledException>(() => ImageRasterSource.Load(Source(), new CancellationToken(true)));
        Assert.Equal(0, DecodeCount);
    }

    [LinuxHeicHelperFact]
    public async Task Canceling_a_live_decode_joins_the_helper_and_removes_private_pixels()
    {
        File.WriteAllText(Path.Combine(root, "slow"), "slow");
        string source = Source(); using var cancel = new CancellationTokenSource();
        var load = Task.Run(() => ImageRasterSource.Load(source, cancel.Token));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (DecodeCount == 0 && !load.IsCompleted) await Task.Delay(10, timeout.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, DecodeCount); AssertPrivateDecodesRemoved();
    }

    [LinuxHeicHelperFact]
    public void Missing_sources_still_fail_in_the_decoder_and_remove_private_pixels()
    {
        Assert.Throws<InvalidDataException>(() => ImageRasterSource.Load(Path.Combine(root, "missing.heic"), default));
        Assert.Equal(1, DecodeCount); AssertPrivateDecodesRemoved();
    }

    private int DecodeCount => File.Exists(Log) ? File.ReadAllLines(Log).Length : 0;
    private void AssertPrivateDecodesRemoved()
    {
        foreach (string path in File.ReadAllLines(Log)) Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }
    private string Source()
    {
        string source = Path.Combine(root, "owned.heic"); File.WriteAllBytes(source, [1, 2, 3, 4]); return source;
    }
    private static byte[] Pattern()
    {
        var pixels = new byte[32 * 16 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { int n = i / 4; pixels[i] = (byte)(n * 37); pixels[i + 1] = (byte)(n * 11); pixels[i + 2] = (byte)(n * 67); pixels[i + 3] = (byte)n; }
        return pixels;
    }
    private static void WriteResponse(string path, int width, int height, byte[] profile, byte[] pixels)
    {
        using var writer = new BinaryWriter(File.Create(path)); writer.Write(width); writer.Write(height); writer.Write(profile.Length); writer.Write(profile); writer.Write(pixels);
    }
    private void WriteHeader(int width, int height, int profileLength, byte[] payload)
    {
        using var writer = new BinaryWriter(File.Create(Response)); writer.Write(width); writer.Write(height); writer.Write(profileLength); writer.Write(payload);
    }
}
