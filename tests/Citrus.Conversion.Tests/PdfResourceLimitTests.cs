using System.IO.Compression;
using System.Text;
using Citrus.Core;
using Citrus.Core.Editing;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Citrus.Conversion.Tests;

public sealed class PdfResourceLimitTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "citrus-pdf-limits-" + Guid.NewGuid().ToString("N"));
    private readonly PdfEngine engine = new();
    public PdfResourceLimitTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData("pdf.images", "png")]
    [InlineData("pdf.jpg", "jpg")]
    public async Task Direct_raster_export_rejects_oversized_pages_and_rolls_back_only_owned_outputs(string id, string extension)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(144, 72).AddText("Small page", 12, new PdfPoint(5, 30), font);
        builder.AddPage(1400, 1400).AddText("Large drawing", 12, new PdfPoint(5, 30), font);
        string input = Path.Combine(root, "drawing.pdf");
        byte[] original = builder.Build();
        await File.WriteAllBytesAsync(input, original);
        string existing = Path.Combine(root, "drawing-page-1." + extension);
        await File.WriteAllTextAsync(existing, "existing user file");
        string[] before = Directory.GetFileSystemEntries(root).Order().ToArray();

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => Execute(id, input));

        Assert.Contains("32-megapixel", error.Message);
        Assert.Equal(before, Directory.GetFileSystemEntries(root).Order().ToArray());
        Assert.Equal(original, await File.ReadAllBytesAsync(input));
        Assert.Equal("existing user file", await File.ReadAllTextAsync(existing));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Text_export_ignores_unneeded_oversized_artwork_in_direct_and_editor_routes(bool editor)
    {
        string input = NativeTextWithOversizedArtwork();
        byte[] original = await File.ReadAllBytesAsync(input);
        var result = await Execute("pdf.text", input, editor);

        Assert.Contains("Native text survives", await File.ReadAllTextAsync(Assert.Single(result.Outputs)));
        Assert.Equal(original, await File.ReadAllBytesAsync(input));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Visual_reconstruction_keeps_oversized_artwork_guard_in_direct_and_editor_routes(bool editor)
    {
        string input = NativeTextWithOversizedArtwork();
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => Execute("pdf.markdown", input, editor));

        Assert.Contains("32-megapixel reconstruction", error.Message);
        Assert.Equal(new[] { input }, Directory.GetFileSystemEntries(root));
    }

    private async Task<ConversionResult> Execute(string id, string input, bool editor = false)
    {
        var operation = engine.Operations.Single(o => o.Id == id);
        var request = new OperationRequest(operation, [input], new(), root);
        if (editor)
        {
            var source = (await engine.Sources.CaptureAsync([input], default)).Single().Snapshot!;
            bool text = id == "pdf.text";
            var recipe = new PdfRecipe([new(Guid.NewGuid(), source.Id, 1, 0)], text ? PdfOutputMode.Text : PdfOutputMode.Markdown,
                150, PdfCompressionMode.PreserveStructure, 85, []);
            var document = new EditorDocument(1, 0, [source], [], recipe,
                new(text ? "txt" : "md", root, "converted", new QualityGoal(85), MetadataPolicy.PreserveSupported, new(255, 255, 255, 255)));
            request = request with { Editor = document, Operation = text ? operation : engine.Operations.Single(o => o.Id == "pdf.editor") };
        }
        return await engine.ExecuteAsync(request, null, default);
    }

    private string NativeTextWithOversizedArtwork()
    {
        byte[] compressed;
        using (var memory = new MemoryStream())
        {
            using (var deflate = new ZLibStream(memory, CompressionLevel.SmallestSize, true))
            {
                byte[] row = new byte[10000 * 3];
                for (int y = 0; y < 3300; y++) deflate.Write(row);
            }
            compressed = memory.ToArray();
        }
        string path = Path.Combine(root, "native-with-artwork.pdf");
        using var pdf = File.Create(path);
        var offsets = new long[7];
        void Write(string value) => pdf.Write(Encoding.ASCII.GetBytes(value));
        void Object(int number, string value)
        {
            offsets[number] = pdf.Position;
            Write($"{number} 0 obj\n{value}\nendobj\n");
        }
        const string content = "BT /F1 12 Tf 20 120 Td (Native text survives) Tj ET q 10 0 0 10 5 5 cm /Im0 Do Q";
        Write("%PDF-1.4\n");
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Resources << /Font << /F1 4 0 R >> /XObject << /Im0 6 0 R >> >> /Contents 5 0 R >>");
        Object(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        Object(5, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        offsets[6] = pdf.Position;
        Write($"6 0 obj\n<< /Type /XObject /Subtype /Image /Width 10000 /Height 3300 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n");
        pdf.Write(compressed);
        Write("\nendstream\nendobj\n");
        long xref = pdf.Position;
        Write("xref\n0 7\n0000000000 65535 f \n");
        for (int i = 1; i <= 6; i++) Write($"{offsets[i]:D10} 00000 n \n");
        Write($"trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return path;
    }
}
