using Citrus.Core;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Citrus.Conversion.Tests;

public sealed class PdfEngineTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Citrus PDF ü " + Guid.NewGuid().ToString("N"));
    private readonly PdfEngine engine = new();
    public PdfEngineTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private string Pdf(string name, params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (string text in pages) builder.AddPage(144, 72).AddText(text, 12, new PdfPoint(5, 30), font);
        string path = Path.Combine(directory, name + ".pdf");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }
    private string Image(string name, uint width, uint height, MagickColor color)
    {
        string path = Path.Combine(directory, name + ".png");
        using var image = new MagickImage(color, width, height);
        image.Write(path);
        return path;
    }
    private OperationRequest Request(string id, string[] paths, string? selection = null) =>
        new(engine.Operations.Single(o => o.Id == id), paths, new() { PageSelection = selection }, null);
    private Task<ConversionResult> Execute(string id, params string[] paths) => engine.ExecuteAsync(Request(id, paths), null, default);
    private string[] Files() => Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order().ToArray();

    [Fact]
    public async Task Ordered_images_create_exact_page_sizes_real_pixels_and_preserve_sources()
    {
        string first = Image("red", 96, 48, MagickColors.Red), second = Image("blue", 48, 96, MagickColors.Blue);
        byte[] original = File.ReadAllBytes(first);
        string output = Assert.Single((await Execute("images.pdf", first, second)).Outputs);
        using (var document = PigDocument.Open(output))
        {
            Assert.Equal(2, document.NumberOfPages);
            Assert.Equal(72d, document.GetPage(1).Width, 2);
            Assert.Equal(36d, document.GetPage(1).Height, 2);
            Assert.Equal(36d, document.GetPage(2).Width, 2);
            Assert.Equal(72d, document.GetPage(2).Height, 2);
        }
        var raster = await Execute("pdf.images", output);
        Assert.Equal(2, raster.Outputs.Count);
        using var red = new MagickImage(raster.Outputs[0]);
        using var blue = new MagickImage(raster.Outputs[1]);
        Assert.Equal(300u, red.Width);
        Assert.Equal(150u, red.Height);
        using var redPixels = red.GetPixels();
        using var bluePixels = blue.GetPixels();
        Assert.Equal((byte)255, redPixels.GetPixel(20, 20).ToArray()[0]);
        Assert.Equal((byte)255, bluePixels.GetPixel(20, 20).ToArray()[2]);
        Assert.Equal(original, File.ReadAllBytes(first));
    }

    [Fact]
    public async Task Photo_batch_preserves_every_page_and_source()
    {
        var paths = Enumerable.Range(0, 24).Select(index => Image("photo-" + index, 2048, 1536,
            index % 2 == 0 ? MagickColors.Red : MagickColors.Blue)).ToArray();
        var originals = paths.Select(path => System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToArray();
        string output = Assert.Single((await Execute("images.pdf", paths)).Outputs);
        using var document = PigDocument.Open(output);
        Assert.Equal(paths.Length, document.NumberOfPages);
        Assert.All(document.GetPages(), page => { Assert.Equal(1536d, page.Width, 2); Assert.Equal(1152d, page.Height, 2); });
        for (int index = 0; index < paths.Length; index++)
            Assert.Equal(originals[index], System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(paths[index])));
    }

    [Fact]
    public async Task Merge_preserves_group_and_internal_page_order_without_replacing_existing_output()
    {
        string a = Pdf("first", "First", "Second"), b = Pdf("last", "Last");
        string existing = Path.Combine(directory, "last-merged.pdf");
        File.WriteAllText(existing, "existing user file");
        string output = Assert.Single((await Execute("pdf.merge", b, a)).Outputs);
        Assert.NotEqual(existing, output);
        using var document = PigDocument.Open(output);
        Assert.Equal(3, document.NumberOfPages);
        Assert.Equal(new[] { "Last", "First", "Second" }, document.GetPages().Select(p => p.Text));
        Assert.Equal("existing user file", File.ReadAllText(existing));
    }

    [Fact]
    public async Task Split_selected_one_based_pages_preserves_requested_order_and_all_source_pages()
    {
        string source = Pdf("original", "One", "Two", "Three");
        byte[] original = File.ReadAllBytes(source);
        var selected = await engine.ExecuteAsync(Request("pdf.split", [source], "3, 1-2"), null, default);
        Assert.Equal(3, selected.Outputs.Count);
        Assert.Equal(new[] { "Three", "One", "Two" }, selected.Outputs.Select(path =>
        {
            using var document = PigDocument.Open(path);
            Assert.Equal(1, document.NumberOfPages);
            return document.GetPage(1).Text;
        }));
        Assert.Equal(original, File.ReadAllBytes(source));
        var all = await Execute("pdf.split", source);
        Assert.Equal(3, all.Outputs.Count);
    }

    [Theory]
    [InlineData("0")][InlineData("4")][InlineData("2-1")][InlineData("1,")]
    [InlineData("1-4")][InlineData("-1")][InlineData("1,1")][InlineData("one")][InlineData("1--2")]
    public async Task Invalid_page_ranges_fail_before_any_output(string selection)
    {
        string source = Pdf("source", "One", "Two", "Three");
        string[] before = Files();
        await Assert.ThrowsAsync<ArgumentException>(() => engine.ExecuteAsync(Request("pdf.split", [source], selection), null, default));
        Assert.Equal(before, Files());
    }

    [Fact]
    public async Task Text_export_contains_known_text_in_selected_page_order()
    {
        string source = Pdf("text", "Hello World!", "Second page");
        var result = await engine.ExecuteAsync(Request("pdf.text", [source], "2,1"), null, default);
        string text = File.ReadAllText(Assert.Single(result.Outputs));
        Assert.Contains("Hello World!", text);
        Assert.Contains("Second page", text);
        Assert.True(text.IndexOf("Second page", StringComparison.Ordinal) < text.IndexOf("Hello", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Raster_exports_one_300_dpi_png_per_page_and_honors_selection()
    {
        string source = Pdf("raster", "First", "Second");
        var result = await Execute("pdf.images", source);
        Assert.Equal(2, result.Outputs.Count);
        foreach (string path in result.Outputs)
        {
            using var image = new MagickImage(path);
            Assert.Equal(MagickFormat.Png, image.Format);
            Assert.Equal(600u, image.Width);
            Assert.Equal(300u, image.Height);
            using var pixels = image.GetPixels();
            Assert.True(pixels.ToByteArray(PixelMapping.RGB)!.Any(value => value < 100), "Raster should contain the fixture's text ink.");
        }
        Assert.Single((await engine.ExecuteAsync(Request("pdf.images", [source], "2"), null, default)).Outputs);
    }

    [Theory]
    [InlineData("pdf.merge")][InlineData("pdf.split")][InlineData("pdf.images")][InlineData("pdf.text")]
    public async Task Malformed_and_encrypted_input_fail_without_group_outputs(string id)
    {
        string valid = Pdf("valid", "Valid"), bad = Path.Combine(directory, "bad.pdf");
        File.WriteAllText(bad, "%PDF-1.7 broken");
        using (var encrypted = new PdfDocument())
        {
            encrypted.AddPage();
            encrypted.SecuritySettings.OwnerPassword = "owner";
            encrypted.Save(Path.Combine(directory, "encrypted.pdf"));
        }
        string[] before = Files();
        foreach (string invalid in new[] { bad, Path.Combine(directory, "encrypted.pdf") })
        {
            string[] inputs = id == "pdf.merge" ? [valid, invalid] : [invalid];
            var error = await Assert.ThrowsAnyAsync<Exception>(() => Execute(id, inputs));
            Assert.IsNotType<NotImplementedException>(error);
            if (invalid.EndsWith("encrypted.pdf", StringComparison.Ordinal))
            {
                Assert.IsType<NotSupportedException>(error);
                Assert.Contains("Encrypted", error.Message);
            }
            Assert.Equal(before, Files());
        }
    }

    [Theory]
    [InlineData("pdf.split")][InlineData("pdf.images")]
    public async Task Cancellation_after_first_committed_page_removes_only_owned_outputs(string id)
    {
        string source = Pdf("cancel", "One", "Two", "Three");
        string existing = Path.Combine(directory, id == "pdf.split" ? "cancel-page-1.pdf" : "cancel-page-1.png");
        File.WriteAllText(existing, "user data");
        string[] before = Files();
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(value => { if (value.Fraction is > 0 and < 1) cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ExecuteAsync(Request(id, [source]), progress, cancellation.Token));
        Assert.Equal(before, Files());
        Assert.Equal("user data", File.ReadAllText(existing));
    }

    [Fact]
    public async Task Later_output_failure_rolls_back_committed_pages_and_keeps_existing_files()
    {
        string source = Pdf("rollback", "One", "Two", "Three");
        string existing = Path.Combine(directory, "rollback-page-1.pdf");
        File.WriteAllText(existing, "keep me");
        string[] before = Files();
        var progress = new InlineProgress(value => { if (value.Fraction is > 0 and < 1) throw new IOException("destination became unavailable"); });
        await Assert.ThrowsAsync<IOException>(() => engine.ExecuteAsync(Request("pdf.split", [source]), progress, default));
        Assert.Equal(before, Files());
        Assert.Equal("keep me", File.ReadAllText(existing));
    }

    [Theory]
    [InlineData("gif")][InlineData("tiff")][InlineData("webp")]
    public async Task Images_to_pdf_rejects_multiframe_sources_before_output(string extension)
    {
        string first = Image("first", 20, 10, MagickColors.Red), path = Path.Combine(directory, "animated." + extension);
        using (var frames = new MagickImageCollection())
        {
            frames.Add(new MagickImage(MagickColors.Red, 20, 10));
            frames.Add(new MagickImage(MagickColors.Blue, 20, 10));
            foreach (var frame in frames) frame.AnimationDelay = 10;
            frames.Write(path);
        }
        string[] before = Files();
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => Execute("images.pdf", first, path));
        Assert.Contains("single-frame", error.Message);
        Assert.Equal(before, Files());
    }

    [Fact]
    public async Task Images_to_pdf_uses_internal_svg_renderer_and_rejects_external_resources_and_disguised_pdf()
    {
        string svg = Path.Combine(directory, "shapes.svg");
        File.WriteAllText(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"96\" height=\"48\"><rect width=\"96\" height=\"48\" fill=\"red\"/></svg>");
        string output = Assert.Single((await Execute("images.pdf", svg)).Outputs);
        using (var pdf = PigDocument.Open(output)) Assert.Equal(72d, pdf.GetPage(1).Width, 2);
        string raster = Assert.Single((await Execute("pdf.images", output)).Outputs);
        using (var image = new MagickImage(raster))
        using (var pixels = image.GetPixels())
        {
            var red = pixels.GetPixel(20, 20).ToArray();
            Assert.Equal((byte)255, red[0]);
            Assert.Equal((byte)0, red[1]);
            Assert.Equal((byte)0, red[2]);
        }
        File.WriteAllText(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"96\" height=\"48\"><image HREF=\"file:///private.png\"/></svg>");
        string fake = Path.Combine(directory, "fake.png");
        File.Copy(output, fake);
        string[] before = Files();
        await Assert.ThrowsAsync<NotSupportedException>(() => Execute("images.pdf", svg));
        await Assert.ThrowsAnyAsync<MagickException>(() => Execute("images.pdf", fake));
        Assert.Equal(before, Files());
    }

    [Fact]
    public void Catalog_exposes_only_compatible_operations_and_group_metadata()
    {
        string pdf = Pdf("source", "Hi"), image = Image("image", 20, 10, MagickColors.Red);
        var catalog = new OperationCatalog([engine]);
        Assert.Contains(catalog.GetOperations([pdf, pdf], OperationMode.Tools), op => op.Id == "pdf.merge" && op.IsGroup);
        Assert.Contains(catalog.GetOperations([image], OperationMode.Convert), op => op.Id == "images.pdf" && op.IsGroup);
        Assert.Empty(catalog.GetOperations([image, pdf], OperationMode.Convert));
        Assert.False(engine.CanExecute(Request("pdf.split", [pdf, pdf])));
        Assert.False(engine.CanExecute(Request("pdf.merge", [image])));
    }

    [Fact]
    public async Task Pre_canceled_group_creates_no_output_and_mixed_directories_require_destination()
    {
        string source = Pdf("source", "Hi");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ExecuteAsync(Request("pdf.merge", [source]), null, cancellation.Token));
        string otherDirectory = Path.Combine(directory, "other");
        Directory.CreateDirectory(otherDirectory);
        string other = Path.Combine(otherDirectory, "other.pdf");
        File.Copy(source, other);
        string[] before = Files();
        await Assert.ThrowsAsync<ArgumentException>(() => Execute("pdf.merge", source, other));
        Assert.Equal(before, Files());
        Assert.Single((await engine.ExecuteAsync(Request("pdf.merge", [source, other]) with { DestinationDirectory = directory }, null, default)).Outputs);
    }

    private sealed class InlineProgress(Action<ConversionProgress> report) : IProgress<ConversionProgress>
    {
        public void Report(ConversionProgress value) => report(value);
    }
}
