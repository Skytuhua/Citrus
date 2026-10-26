using Citrus.Core;
using Citrus.Core.Editing;
using ImageMagick;

namespace Citrus.Conversion.Editing.Image;

internal static class ImageRasterSource
{
    internal static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".tif" or ".tiff" or ".bmp" or ".gif" or ".avif" || (Path.GetExtension(path).ToLowerInvariant() is ".heic" or ".heif" && HeicCodec.Available);
    internal static MagickFormat Format(string format) => Supports("input." + format) ? ValidatedImageLoader.Formats["." + format] : throw new NotSupportedException("Choose PNG, JPEG, WebP, TIFF, BMP, single-frame GIF or AVIF.");
    internal static void CheckAvailable(IEnumerable<SourceSnapshot> sources)
    {
        foreach (var source in sources)
        {
            if (!Supports(source.Path)) throw new NotSupportedException("The image editor accepts supported single-frame rasters; SVG, HEIC and animated/multipage inputs are unavailable.");
            if (!DropValidation.AttributesAllowed(File.GetAttributes(source.Path), OperationMode.Tools)) throw new IOException("Source is unavailable locally. Download it before opening the editor.");
        }
    }
    internal static SizeI Inspect(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(Path.GetExtension(path).ToLowerInvariant() is ".heic" or ".heif"){using var heic=HeicCodec.Load(path,ct);return new((int)heic.Width,(int)heic.Height);}
        if (!Supports(path)) throw new NotSupportedException("Unsupported raster format.");
        if (!DropValidation.AttributesAllowed(File.GetAttributes(path), OperationMode.Tools)) throw new IOException("Raster is not locally available.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        CheckSignature(input, Format(Path.GetExtension(path)[1..])); input.Position = 0;
        using var frames = new MagickImageCollection();
        frames.Ping(input, new MagickReadSettings { Format = Format(Path.GetExtension(path)[1..]) });
        ct.ThrowIfCancellationRequested();
        if (frames.Count != 1) throw new NotSupportedException("Animated images and multipage rasters are not supported.");
        var image = frames[0]; var size = new SizeI(checked((int)image.Width), checked((int)image.Height)); ImageGeometryMapper.CheckSize(size);
        if (image.Orientation is Orientation.LeftTop or Orientation.RightTop or Orientation.RightBottom or Orientation.LeftBottom) size = new(size.Height, size.Width);
        return size;
    }
    internal static MagickImage Load(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // The HEIC helper bounds the image before decoding; Load validates the returned
        // pixel lengths and normalizes color/orientation. Inspection would decode it again.
        if (Path.GetExtension(path).ToLowerInvariant() is ".heic" or ".heif") return HeicCodec.Load(path, ct);
        var expected = Inspect(path, ct); // Explicit, bounded ping MUST precede pixel allocation.
        var image = ValidatedImageLoader.Load(path, ct);
        try
        {
            if (image.Width != expected.Width || image.Height != expected.Height) throw new InvalidDataException("Decoded source dimensions disagree with inspection.");
            if (image.GetColorProfile() is not null) image.TransformColorSpace(ColorProfiles.SRGB);
            else { image.ColorSpace = ColorSpace.sRGB; image.SetProfile(ColorProfiles.SRGB); }
            image.Orientation = Orientation.TopLeft;
            ct.ThrowIfCancellationRequested(); return image;
        }
        catch { image.Dispose(); throw; }
    }
    internal static MagickImage LoadEncoded(string path,string format,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();if(format is "heic" or "heif")return HeicCodec.Load(path,ct);
        // Native filename decoders may open inheritable handles. Keep every encoded
        // verifier on a managed, non-inheritable stream until decoding has completed.
        using var input = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        var image=new MagickImage(input,new MagickReadSettings{Format=Format(format)});
        try{ImageGeometryMapper.CheckSize(new((int)image.Width,(int)image.Height));using var pixels=image.GetPixels();_=pixels.GetPixel(0,0);ct.ThrowIfCancellationRequested();return image;}catch{image.Dispose();throw;}
    }
    private static void CheckSignature(Stream stream, MagickFormat format)
    {
        Span<byte> header = stackalloc byte[64]; int count = stream.Read(header); header = header[..count];
        bool ok = format switch
        {
            MagickFormat.Png => header.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            MagickFormat.Jpeg => header.StartsWith(new byte[] { 255, 216, 255 }),
            MagickFormat.Tiff => header.StartsWith(new byte[] { 73, 73, 42, 0 }) || header.StartsWith(new byte[] { 77, 77, 0, 42 }) || header.StartsWith(new byte[] { 73, 73, 43, 0 }) || header.StartsWith(new byte[] { 77, 77, 0, 43 }),
            MagickFormat.Gif => header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8),
            MagickFormat.Bmp => header.StartsWith("BM"u8),
            MagickFormat.WebP => count >= 12 && header[..4].SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8),
            MagickFormat.Avif => count >= 16 && header.Slice(4, 4).SequenceEqual("ftyp"u8) && HasAvifBrand(header),
            _ => false
        };
        if (!ok) throw new InvalidDataException("Raster content does not match its declared format.");
    }
    private static bool HasAvifBrand(ReadOnlySpan<byte> header)
    {
        if (header.Slice(8, 4).SequenceEqual("avif"u8)) return true;
        for (int i = 16; i + 4 <= header.Length; i += 4) if (header.Slice(i, 4).SequenceEqual("avif"u8)) return true;
        return false;
    }
}
