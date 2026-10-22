using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using PDFtoImage;
using SkiaSharp;

namespace Citrus.Conversion.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PdfThumbnailIoCollection
{
    public const string Name = "PDF thumbnail temporary I/O";
}

[Collection(PdfThumbnailIoCollection.Name)]
public sealed class PdfThumbnailStreamTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "citrus-thumbnail-test-" + Guid.NewGuid().ToString("N"));
    public PdfThumbnailStreamTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public async Task Rendering_a_thumbnail_does_not_write_temporary_output_files()
    {
        string path = Fixture(), sentinel = Path.Combine(Path.GetTempPath(), "citrus-thumbnail-sentinel-" + Guid.NewGuid().ToString("N"));
        var writes = new ConcurrentQueue<string>();
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(Path.GetTempPath()) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
        watcher.Created += (_, change) =>
        {
            if (change.Name!.StartsWith("citrus-pdf-thumbnail-", StringComparison.Ordinal)) writes.Enqueue(change.FullPath);
            if (change.FullPath == sentinel) drained.TrySetResult();
        };
        watcher.Error += (_, error) => drained.TrySetException(error.GetException());
        watcher.EnableRaisingEvents = true;
        try
        {
            byte[] png = await PdfEngine.RenderPageAsync(path, 1, 100, default);
            Assert.NotEmpty(png);
            File.WriteAllBytes(sentinel, []);
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(writes);
        }
        finally { File.Delete(sentinel); }
    }

    [Theory]
    [InlineData(1, 48, 96, 48)]
    [InlineData(1, 150, 300, 150)]
    [InlineData(1, 300, 600, 300)]
    [InlineData(2, 48, 96, 144)]
    [InlineData(2, 150, 300, 449)]
    [InlineData(2, 300, 600, 899)]
    public async Task Thumbnail_bytes_match_file_rendering_with_rotation_annotations_and_forms(int page, int dpi, int width, int height)
    {
        string path = Fixture();
        byte[] original = File.ReadAllBytes(path), expected = FileRender(path, page, dpi);
        byte[] actual = await PdfEngine.RenderPageAsync(path, page, dpi, default);
        Assert.Equal(SHA256.HashData(expected), SHA256.HashData(actual));
        using var bitmap = SKBitmap.Decode(actual);
        Assert.Equal(width, bitmap.Width);
        Assert.Equal(height, bitmap.Height);
        Assert.Equal(original, File.ReadAllBytes(path));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void Fixture_renders_visible_annotation_and_form_appearances()
    {
        string path = Fixture();
        byte[] both = FileRender(path, 1, 100);
        Assert.NotEqual(SHA256.HashData(both), SHA256.HashData(FileRender(path, 1, 100, annotations: false)));
        Assert.NotEqual(SHA256.HashData(both), SHA256.HashData(FileRender(path, 1, 100, forms: false)));
    }

    [Fact]
    public async Task Canceled_and_oversized_renders_release_the_gate_and_input()
    {
        string path = Fixture();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PdfEngine.RenderPageAsync(path, 1, 100, canceled.Token));
        string large = Fixture(10000);
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => PdfEngine.RenderPageAsync(large, 1, 600, default));
        Assert.Contains("32-megapixel", error.Message);
        using (var exclusive = new FileStream(large, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Assert.NotEmpty(await PdfEngine.RenderPageAsync(path, 2, 48, default).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    internal string Fixture(int firstPageWidth = 144)
    {
        string path = Path.Combine(root, $"pages-{firstPageWidth}.pdf");
        using var pdf = File.Create(path);
        var offsets = new long[13];
        void Write(string value) => pdf.Write(Encoding.ASCII.GetBytes(value));
        void Object(int number, string value)
        {
            offsets[number] = pdf.Position;
            Write($"{number} 0 obj\n{value}\nendobj\n");
        }
        void Stream(int number, string dictionary, string content) => Object(number, $"<< {dictionary} /Length {content.Length} >>\nstream\n{content}\nendstream");
        Write("%PDF-1.4\n");
        Object(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 12 0 R >>");
        Object(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
        Object(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {firstPageWidth} 72] /Resources << /Font << /F1 5 0 R >> >> /Contents 6 0 R /Annots [8 0 R 10 0 R] >>");
        Object(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 216 144] /Rotate 90 /Resources << /Font << /F1 5 0 R >> >> /Contents 7 0 R >>");
        Object(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        Stream(6, "", "q 1 0.8 0.2 rg 0 0 144 72 re f Q BT /F1 12 Tf 5 54 Td (Thumbnail page one) Tj ET");
        Stream(7, "", "q 0.2 0.8 1 rg 0 0 216 144 re f Q BT /F1 16 Tf 12 100 Td (Rotated page two) Tj ET");
        Object(8, "<< /Type /Annot /Subtype /Square /Rect [10 10 40 40] /F 4 /AP << /N 9 0 R >> >>");
        Stream(9, "/Type /XObject /Subtype /Form /BBox [0 0 30 30]", "0 0 1 rg 0 0 30 30 re f");
        Object(10, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /V (Filled) /Rect [60 10 100 40] /F 4 /P 3 0 R /AP << /N 11 0 R >> >>");
        Stream(11, "/Type /XObject /Subtype /Form /BBox [0 0 40 30]", "0 1 0 rg 0 0 40 30 re f");
        Object(12, "<< /Fields [10 0 R] /NeedAppearances false /DA (/F1 12 Tf 0 g) /DR << /Font << /F1 5 0 R >> >> >>");
        long xref = pdf.Position;
        Write("xref\n0 13\n0000000000 65535 f \n");
        for (int i = 1; i <= 12; i++) Write($"{offsets[i]:D10} 00000 n \n");
        Write($"trailer\n<< /Size 13 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return path;
    }

    private byte[] FileRender(string path, int page, int dpi, bool annotations = true, bool forms = true)
    {
        string output = Path.Combine(root, "reference.png");
        using var input = File.OpenRead(path);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            PDFtoImage.Conversion.SavePng(output, input, page - 1, leaveOpen: true,
                options: new RenderOptions(Dpi: dpi, WithAnnotations: annotations, WithFormFill: forms));
        else throw new PlatformNotSupportedException();
        return File.ReadAllBytes(output);
    }
}
