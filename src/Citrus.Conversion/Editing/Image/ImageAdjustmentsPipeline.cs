using PointD = Citrus.Core.Editing.PointD;
using Citrus.Core.Editing;
using ImageMagick;

namespace Citrus.Conversion.Editing.Image;

/// <summary>Citrus SDR v1 kernels. Exact rendering uses cropped full-resolution pixels.
/// Bounded viewport rendering scales spatial radii and maps noise back to source coordinates.</summary>
public static class ImageAdjustmentsPipeline
{
    public static void Apply(MagickImage image, ImageRecipe recipe, SizeI sourceSize, CancellationToken ct) => ApplyCore(image, recipe, sourceSize, ct, 1, false);
    internal static void ApplyScaled(MagickImage image, ImageRecipe recipe, SizeI sourceSize, CancellationToken ct, double renderScale) => ApplyCore(image, recipe, sourceSize, ct, renderScale, true);
    private static void ApplyCore(MagickImage image, ImageRecipe recipe, SizeI sourceSize, CancellationToken ct, double renderScale, bool approximate)
    {
        var a = recipe.Adjustments;
        if (a.AlgorithmVersion != 1) throw new NotSupportedException("Unknown adjustment algorithm.");
        ct.ThrowIfCancellationRequested();
        if (IsIdentity(a)) return;
        int width = (int)image.Width, height = (int)image.Height;
        byte[] pixels = image.GetPixels().ToByteArray(PixelMapping.RGBA)!;
        double[] tone = new double[256]; for (int i = 0; i < tone.Length; i++) tone[i] = Tone(i / 255d, a);
        double redGain = Math.Pow(2, .5 * a.Warmth - .25 * a.Tint), greenGain = Math.Pow(2, .5 * a.Tint), blueGain = Math.Pow(2, -.5 * a.Warmth - .25 * a.Tint);
        // Tone/color kernel uses double precision with one Q8 boundary per stage.
        for (int y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                double r = tone[pixels[i]], g = tone[pixels[i + 1]], b = tone[pixels[i + 2]];
                double l = Luma(r, g, b), delta = .25 * (a.Shadows * (1 - l) * (1 - l) + a.Highlights * l * l)
                    + .25 * (a.Blacks * Math.Pow(1 - l, 4) + a.Whites * Math.Pow(l, 4));
                if (l > 1e-12) { double gain = Math.Max(0, l + delta) / l; r *= gain; g *= gain; b *= gain; } else r = g = b = Math.Max(0, delta);
                l = Luma(r, g, b);
                double chroma = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                double saturation = a.Saturation * (1 + a.Vibrance * (1 - Clamp(chroma)));
                r = l + saturation * (r - l); g = l + saturation * (g - l); b = l + saturation * (b - l);
                if (a.HueDegrees != 0) RotateHue(ref r, ref g, ref b, a.HueDegrees);
                r *= redGain; g *= greenGain; b *= blueGain;
                pixels[i] = Byte(r); pixels[i + 1] = Byte(g); pixels[i + 2] = Byte(b);
            }
        }
        if (a.Clarity > 0)
        {
            byte[] low = Blur(pixels, width, height, 12 * renderScale, ct, luminance: true);
            for (int i = 0; i < pixels.Length; i += 4) { if ((i & 65535) == 0) ct.ThrowIfCancellationRequested(); double delta = a.Clarity * (Luma(pixels[i], pixels[i + 1], pixels[i + 2]) - low[i]); for (int c = 0; c < 3; c++) pixels[i + c] = Byte255(pixels[i + c] + delta); }
        }
        if (a.Dehaze > 0) Dehaze(pixels, width, height, a.Dehaze, (int)Math.Round(7 * renderScale, MidpointRounding.AwayFromZero), ct);
        if (a.Sharpen > 0)
        {
            byte[] low = Blur(pixels, width, height, renderScale, ct);
            for (int i = 0; i < pixels.Length; i += 4) { if ((i & 65535) == 0) ct.ThrowIfCancellationRequested(); for (int c = 0; c < 3; c++) pixels[i + c] = Byte255(pixels[i + c] + 2 * a.Sharpen * (pixels[i + c] - low[i + c])); }
        }
        if (a.Denoise > 0)
        {
            using var median = FromPixels(pixels, width, height); uint radius = (uint)Math.Round(Math.Clamp((int)Math.Ceiling(3 * a.Denoise), 1, 3) * renderScale, MidpointRounding.AwayFromZero);
            median.Statistic(StatisticType.Median, 2 * radius + 1, 2 * radius + 1);
            Blend(pixels, median.GetPixels().ToByteArray(PixelMapping.RGBA)!, a.Denoise, ct);
        }
        if (a.BlurSigma > 0) Blend(pixels, Blur(pixels, width, height, a.BlurSigma * renderScale, ct), 1, ct);
        if (a.Vignette != 0 || a.Grain > 0 || (a.Effect != PhotoEffect.None && a.EffectAmount > 0))
        {
            var unscaled = recipe with { Canvas=null,Geometry = recipe.Geometry with { OutputSize = approximate ? new SizeI(width, height) : null } };
            // Cache the affine source-coordinate map once; no per-pixel geometry validation.
            PointD origin = ImageGeometryMapper.MapFromCanvas(unscaled, sourceSize, new(.5, .5));
            PointD dx = ImageGeometryMapper.MapFromCanvas(unscaled, sourceSize, new(1.5, .5));
            PointD dy = ImageGeometryMapper.MapFromCanvas(unscaled, sourceSize, new(.5, 1.5));
            for (int y = 0; y < height; y++)
            {
                ct.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    double r = pixels[i] / 255d, g = pixels[i + 1] / 255d, b = pixels[i + 2] / 255d;
                    double nx = 2 * (x + .5) / width - 1, ny = 2 * (y + .5) / height - 1;
                    double vignette = 1 - a.Vignette * Math.Clamp((nx * nx + ny * ny) / 2, 0, 1);
                    r *= vignette; g *= vignette; b *= vignette;
                    if (a.Grain > 0)
                    {
                        int sx = (int)Math.Floor(origin.X + x * (dx.X - origin.X) + y * (dy.X - origin.X));
                        int sy = (int)Math.Floor(origin.Y + x * (dx.Y - origin.Y) + y * (dy.Y - origin.Y));
                        double noise = Noise(sx, sy, a.GrainSeed) * a.Grain * .1; r += noise; g += noise; b += noise;
                    }
                    if (a.EffectAmount > 0)
                    {
                        double er = r, eg = g, eb = b;
                        switch (a.Effect)
                        {
                            case PhotoEffect.Grayscale: er = eg = eb = Luma(r, g, b); break;
                            case PhotoEffect.Sepia: er = .393 * r + .769 * g + .189 * b; eg = .349 * r + .686 * g + .168 * b; eb = .272 * r + .534 * g + .131 * b; break;
                            case PhotoEffect.Invert: er = 1 - r; eg = 1 - g; eb = 1 - b; break;
                            case PhotoEffect.Posterize: er = Math.Round(Clamp(r) * 3, MidpointRounding.AwayFromZero) / 3; eg = Math.Round(Clamp(g) * 3, MidpointRounding.AwayFromZero) / 3; eb = Math.Round(Clamp(b) * 3, MidpointRounding.AwayFromZero) / 3; break;
                        }
                        r += (er - r) * a.EffectAmount; g += (eg - g) * a.EffectAmount; b += (eb - b) * a.EffectAmount;
                    }
                    pixels[i] = Byte(r); pixels[i + 1] = Byte(g); pixels[i + 2] = Byte(b);
                }
            }
        }
        ct.ThrowIfCancellationRequested();
        // Replace pixel storage without ReadPixels, which resets the source metadata profiles.
        image.ColorType = ColorType.TrueColorAlpha;
        using var target = image.GetPixels();
        target.SetPixels(pixels);
    }
    private static bool IsIdentity(ImageAdjustments a) => a.ExposureEv == 0 && a.Brightness == 0 && a.Contrast == 0 && a.Gamma == 1 && a.Highlights == 0 && a.Shadows == 0 && a.Whites == 0 && a.Blacks == 0 && a.Saturation == 1 && a.Vibrance == 0 && a.HueDegrees == 0 && a.Warmth == 0 && a.Tint == 0 && a.Clarity == 0 && a.Dehaze == 0 && a.Sharpen == 0 && a.Denoise == 0 && a.BlurSigma == 0 && a.Vignette == 0 && a.Grain == 0 && (a.Effect == PhotoEffect.None || a.EffectAmount == 0);
    private static double Tone(double v, ImageAdjustments a)
    {
        if (a.ExposureEv != 0) { double linear = v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); linear *= Math.Pow(2, a.ExposureEv); v = linear <= .0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - .055; }
        v += a.Brightness; v = Clamp((v - .5) * Math.Pow(2, 2 * a.Contrast) + .5); return Math.Pow(v, 1 / a.Gamma);
    }
    private static void RotateHue(ref double r, ref double g, ref double b, double degrees)
    {
        r = Clamp(r); g = Clamp(g); b = Clamp(b); double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), chroma = max - min;
        if (chroma == 0) return;
        double h = max == r ? (g - b) / chroma : max == g ? (b - r) / chroma + 2 : (r - g) / chroma + 4;
        h = ((h + degrees / 60) % 6 + 6) % 6; double secondary = chroma * (1 - Math.Abs(h % 2 - 1));
        (r, g, b) = (int)h switch { 0 => (chroma, secondary, 0d), 1 => (secondary, chroma, 0d), 2 => (0d, chroma, secondary), 3 => (0d, secondary, chroma), 4 => (secondary, 0d, chroma), _ => (chroma, 0d, secondary) }; r += min; g += min; b += min;
    }
    private static MagickImage FromPixels(byte[] p, int width, int height) => new(p, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGBA));
    private static byte[] Blur(byte[] p, int width, int height, double sigma, CancellationToken ct, bool luminance = false)
    {
        byte[] weighted = new byte[width * height * 3], weights = new byte[width * height];
        for (int n = 0; n < weights.Length; n++)
        {
            if ((n & 16383) == 0) ct.ThrowIfCancellationRequested();
            int i = n * 4; weights[n] = p[i + 3]; double l = luminance ? Byte255(Luma(p[i], p[i + 1], p[i + 2])) : 0;
            for (int c = 0; c < 3; c++) weighted[n * 3 + c] = Byte255((luminance ? l : p[i + c]) * p[i + 3] / 255d);
        }
        // Filter numerator and alpha weights separately as opaque planes. Native RGBA Gaussian
        // already associates alpha, so passing premultiplied RGBA would apply alpha twice.
        using var numerator = new MagickImage(weighted, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGB));
        using var denominator = new MagickImage(weights, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, "I"));
        ct.ThrowIfCancellationRequested(); numerator.GaussianBlur(0, sigma, Channels.RGB); ct.ThrowIfCancellationRequested(); denominator.GaussianBlur(0, sigma, Channels.RGB); ct.ThrowIfCancellationRequested();
        weighted = numerator.GetPixels().ToByteArray(PixelMapping.RGB)!; weights = denominator.GetPixels().ToByteArray("I")!;
        byte[] output = (byte[])p.Clone();
        for (int n = 0; n < weights.Length; n++) for (int c = 0; c < 3; c++) output[n * 4 + c] = weights[n] == 0 ? (byte)0 : Byte255(weighted[n * 3 + c] * 255d / weights[n]);
        return output;
    }

    private static void Blend(byte[] p, byte[] effect, double amount, CancellationToken ct)
    { for (int i = 0; i < p.Length; i += 4) { if ((i & 65535) == 0) ct.ThrowIfCancellationRequested(); for (int c = 0; c < 3; c++) p[i + c] = Byte255(p[i + c] + amount * (effect[i + c] - p[i + c])); } }
    private static void Dehaze(byte[] p, int width, int height, double amount, int radius, CancellationToken ct)
    {
        float[] dark = DarkChannel(p, width, height, 1, 1, 1, radius, ct);
        // The first dark channel has only 256 values. Histogram selection preserves the
        // exact descending-dark / ascending-row-major top 0.1% without a per-pixel heap.
        int[] histogram = new int[256]; int visible = 0;
        for (int i = 0; i < dark.Length; i++) { if ((i & 16383) == 0) ct.ThrowIfCancellationRequested(); if (p[i * 4 + 3] == 0) continue; visible++; histogram[(int)Math.Round(dark[i] * 255)]++; }
        if (visible == 0) return;
        int count = Math.Max(1, (int)Math.Ceiling(visible * .001)), higher = 0, threshold = 255;
        while (higher + histogram[threshold] < count) { higher += histogram[threshold]; threshold--; }
        int ties = count - higher, atmospheric = 0; double brightest = -1;
        for (int i = 0; i < dark.Length; i++)
        {
            if ((i & 16383) == 0) ct.ThrowIfCancellationRequested(); if (p[i * 4 + 3] == 0) continue;
            int level = (int)Math.Round(dark[i] * 255);
            if (level < threshold || (level == threshold && ties-- <= 0)) continue;
            int j = i * 4; double l = Luma(p[j], p[j + 1], p[j + 2]); if (l > brightest) { brightest = l; atmospheric = j; }
        }
        double ar = Math.Max(1 / 255d, p[atmospheric] / 255d), ag = Math.Max(1 / 255d, p[atmospheric + 1] / 255d), ab = Math.Max(1 / 255d, p[atmospheric + 2] / 255d);
        dark = DarkChannel(p, width, height, ar, ag, ab, radius, ct, dark);
        for (int i = 0; i < dark.Length; i++) { if ((i & 16383) == 0) ct.ThrowIfCancellationRequested(); double t = Math.Max(.1, 1 - .95 * amount * dark[i]); int j = i * 4; p[j] = Byte((p[j] / 255d - ar) / t + ar); p[j + 1] = Byte((p[j + 1] / 255d - ag) / t + ag); p[j + 2] = Byte((p[j + 2] / 255d - ab) / t + ab); }
    }
    private static float[] DarkChannel(byte[] p, int width, int height, double ar, double ag, double ab, int radius, CancellationToken ct, float[]? reuse = null)
    {
        float[] values = reuse ?? new float[width * height];
        for (int i = 0; i < values.Length; i++) { if ((i & 16383) == 0) ct.ThrowIfCancellationRequested(); int j = i * 4; values[i] = p[j + 3] == 0 ? float.PositiveInfinity : (float)Math.Min(p[j] / 255d / ar, Math.Min(p[j + 1] / 255d / ag, p[j + 2] / 255d / ab)); }
        float[] horizontal = new float[values.Length], result = values; int[] queue = new int[Math.Max(width, height)];
        for (int y = 0; y < height; y++) { ct.ThrowIfCancellationRequested(); MinLine(values, horizontal, y * width, 1, width, radius, queue); }
        for (int x = 0; x < width; x++) { ct.ThrowIfCancellationRequested(); MinLine(horizontal, result, x, width, height, radius, queue); }
        for (int i = 0; i < result.Length; i++) if (!float.IsFinite(result[i])) result[i] = 0;
        return result;
    }
    private static void MinLine(float[] input, float[] output, int start, int step, int length, int radius, int[] q)
    {
        int head = 0, tail = 0, next = 0;
        for (int x = 0; x < length; x++)
        {
            while (next < length && next <= x + radius) { while (tail > head && input[start + q[tail - 1] * step] >= input[start + next * step]) tail--; q[tail++] = next++; }
            while (head < tail && q[head] < x - radius) head++;
            output[start + x * step] = input[start + q[head] * step];
        }
    }
    private static double Noise(int x, int y, uint seed) { uint h = unchecked((uint)x * 0x9e3779b9u ^ (uint)y * 0x85ebca6bu ^ seed); h ^= h >> 16; h = unchecked(h * 0x7feb352du); h ^= h >> 15; h = unchecked(h * 0x846ca68bu); h ^= h >> 16; return h / (double)uint.MaxValue * 2 - 1; }
    private static double Luma(double r, double g, double b) => .2126 * r + .7152 * g + .0722 * b;
    private static double Clamp(double v) => Math.Clamp(v, 0, 1);
    private static byte Byte(double v) => Byte255(Clamp(v) * 255);
    private static byte Byte255(double v) => (byte)Math.Clamp(Math.Round(v, MidpointRounding.AwayFromZero), 0, 255);
}
