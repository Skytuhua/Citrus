using System.Collections;
using System.Text.Json;
using Citrus.Conversion.Editing.Pdf;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Citrus.Conversion.Tests;

public sealed class PdfOcrReuseTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "citrus-pdf-ocr-reuse-" + Guid.NewGuid().ToString("N"));
    public PdfOcrReuseTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Multi_page_OCR_loads_model_once_and_matches_independent_text_and_table_models(bool recoverArtwork)
    {
        string input = await ScannedDocumentAsync();
        string dataPack = CopyOcrData();
        int[] selected = [1, 2, 3, 4, 1, 2];
        var references = new List<PdfSemanticDocument>();
        foreach (int number in selected)
            references.Add(await PdfSemanticExtractor.ExtractAsync(input, [number], dataPack, default, recoverArtwork));
        var table = Assert.Single(Assert.Single(references[0].Pages).Tables);
        Assert.Equal(new[] { "Identifier", "Amount", "00123", "42.5", "00987", "" }, table.Rows.SelectMany(row => row));
        Assert.Contains(Assert.Single(references[1].Pages).Text, text => text.Text == "Readable scanned paragraph");

        // The model is operation-owned once loaded. Replacing our private data file
        // between pages makes a second initialization fail, without timing assertions.
        var pages = new BetweenPages(selected, () => File.WriteAllText(Path.Combine(dataPack, "tessdata", "eng.traineddata"), "invalid model"));
        var actual = await PdfSemanticExtractor.ExtractAsync(input, pages, dataPack, default, recoverArtwork);

        Assert.Equal(references.Select(document => JsonSerializer.Serialize(Assert.Single(document.Pages))),
            actual.Pages.Select(page => JsonSerializer.Serialize(page)));
        Assert.Equal(references.SelectMany(document => document.Warnings).Distinct().Order(), actual.Warnings.Order());
        // A later extraction must initialize its own engine and observe the bad data.
        await Assert.ThrowsAsync<Tesseract.TesseractException>(() => PdfSemanticExtractor.ExtractAsync(input, [1], dataPack, default));
    }

    [Fact]
    public async Task Native_text_does_not_require_OCR_data()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(400, 300).AddText("Native text", 18, new PdfPoint(30, 200), font);
        string input = Path.Combine(root, "native.pdf");
        await File.WriteAllBytesAsync(input, builder.Build());
        var document = await PdfSemanticExtractor.ExtractAsync(input, [1], Path.Combine(root, "missing-pack"), default);
        var page = Assert.Single(document.Pages);
        Assert.False(page.Ocr);
        Assert.Equal("Native text", Assert.Single(page.Text).Text);
    }

    [Fact]
    public async Task Cancellation_between_OCR_pages_allows_a_fresh_extraction()
    {
        string input = await ScannedDocumentAsync(), dataPack = CopyOcrData();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PdfSemanticExtractor.ExtractAsync(input,
            new BetweenPages([1, 2], cancellation.Cancel), dataPack, cancellation.Token));
        var document = await PdfSemanticExtractor.ExtractAsync(input, [2], dataPack, default);
        Assert.Contains(Assert.Single(document.Pages).Text, text => text.Text == "Readable scanned paragraph");
    }

    private string CopyOcrData()
    {
        string pack = Path.Combine(root, Guid.NewGuid().ToString("N")), data = Path.Combine(pack, "tessdata");
        Directory.CreateDirectory(data);
        File.Copy(Path.Combine(FeaturePackLocator.Find(), "tessdata", "eng.traineddata"), Path.Combine(data, "eng.traineddata"));
        return pack;
    }

    private async Task<string> ScannedDocumentAsync()
    {
        var native = new PdfDocumentBuilder();
        var font = native.AddStandard14Font(Standard14Font.Helvetica);
        var table = native.AddPage(400, 300);
        foreach (double y in new[] { 60d, 110d, 160d, 210d }) table.DrawLine(new(30, y), new(350, y), 1);
        foreach (double x in new[] { 30d, 190d, 350d }) table.DrawLine(new(x, 60), new(x, 210), 1);
        table.AddText("Identifier", 12, new(40, 180), font); table.AddText("Amount", 12, new(200, 180), font);
        table.AddText("00123", 12, new(40, 130), font); table.AddText("42.5", 12, new(200, 130), font);
        table.AddText("00987", 12, new(40, 80), font); // Empty final cell exercises SingleBlock fallback.
        var text = native.AddPage(400, 300);
        text.AddText("Readable scanned paragraph", 18, new(30, 200), font);
        text.AddText("Second independent text block", 14, new(30, 100), font);
        text.AddText("TOTAL 123.45", 14, new(230, 40), font);
        native.AddPage(400, 300);
        string nativePath = Path.Combine(root, "native-scans.pdf");
        await File.WriteAllBytesAsync(nativePath, native.Build());
        var scan = new PdfDocumentBuilder();
        foreach (int number in new[] { 1, 2, 3 })
        {
            byte[] png = await PdfEngine.RenderPageAsync(nativePath, number, 150, default);
            scan.AddPage(400, 300).AddPng(png, new PdfRectangle(0, 0, 400, 300));
        }
        scan.AddPage(400, 300).AddText("Native-only page does not require OCR", 14, new PdfPoint(30, 200), scan.AddStandard14Font(Standard14Font.Helvetica));
        string input = Path.Combine(root, "scanned.pdf");
        await File.WriteAllBytesAsync(input, scan.Build());
        return input;
    }

    private sealed class BetweenPages(int[] pages, Action afterFirst) : IReadOnlyList<int>
    {
        public int Count => pages.Length;
        public int this[int index] => pages[index];
        public IEnumerator<int> GetEnumerator()
        {
            for (int i = 0; i < pages.Length; i++)
            {
                if (i == 1) afterFirst();
                yield return pages[i];
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
