using Citrus.Core;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFtoImage;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Citrus.Conversion;

/// <summary>Offline PDF creation, structural operations, rendering and text extraction.</summary>
public sealed partial class PdfEngine(OutputWriter? outputWriter = null) : IConversionEngine, Citrus.Core.Editing.IEditorAdmissionInspector
{
    private static readonly SemaphoreSlim RenderGate = new(1, 1);
    private readonly OutputWriter writer = outputWriter ?? new();
    public IReadOnlyList<OperationDescriptor> Operations { get; } = Array.AsReadOnly(new[]
    {
        new OperationDescriptor("images.pdf", "PDF", OperationMode.Convert, ".pdf", true, false),
        new OperationDescriptor("pdf.merge", "Merge PDFs", OperationMode.Tools, ".pdf", true, false),
        new OperationDescriptor("pdf.split", "Split pages", OperationMode.Tools, ".pdf", false, true),
        new OperationDescriptor("pdf.images", "Page images", OperationMode.Convert, ".png", false, true),
        new OperationDescriptor("pdf.text", "Text", OperationMode.Convert, ".txt", false, true),
        new OperationDescriptor("pdf.jpg", "JPG", OperationMode.Convert, ".jpg", false, true),
        new OperationDescriptor("pdf.docx", "DOCX", OperationMode.Convert, ".docx", false, true),
        new OperationDescriptor("pdf.pptx", "PPTX", OperationMode.Convert, ".pptx", false, true),
        new OperationDescriptor("pdf.xlsx", "XLSX", OperationMode.Convert, ".xlsx", false, true),
        new OperationDescriptor("pdf.epub", "EPUB", OperationMode.Convert, ".epub", false, true),
        new OperationDescriptor("pdf.markdown", "MD", OperationMode.Convert, ".md", false, true),
        new OperationDescriptor("pdf.editor", "Organize", OperationMode.Tools, ".pdf", false, true),
        new OperationDescriptor("pdf.organize", "Organize", OperationMode.Tools, ".pdf", true, true),
        new OperationDescriptor("pdf.compress", "Compress", OperationMode.Tools, ".pdf", false, true),
        new OperationDescriptor("pdf.metadata", "Metadata", OperationMode.Tools, ".pdf", false, true)
    });

    public bool CanExecute(OperationRequest request)
    {
        if (request.Editor is not null) return CanExecuteEditor(request);
        var operation = Operations.FirstOrDefault(op => op.Id == request.Operation.Id);
        if (operation is null || (!operation.IsGroup && request.Inputs.Count != 1) ||
            !DropValidation.Accepts(request.Inputs, operation.Mode)) return false;
        return request.Inputs.All(path => operation.Id == "images.pdf"
            ? ValidatedImageLoader.Formats.ContainsKey(Path.GetExtension(path))
            : Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase));
    }

    public Task<ConversionResult> ExecuteAsync(OperationRequest request, IProgress<ConversionProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => ExecuteCoreAsync(request, progress, cancellationToken), cancellationToken);

    private async Task<ConversionResult> ExecuteCoreAsync(OperationRequest request, IProgress<ConversionProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.Editor is not null) return await ExecuteEditorAsync(request, progress, token).ConfigureAwait(false);
        if (!CanExecute(request)) throw new NotSupportedException("Choose compatible PDF files or supported single-frame images.");
        string first = request.Inputs[0];
        string parent = Path.GetDirectoryName(first)!;
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (request.DestinationDirectory is null && request.Inputs.Any(path => !pathComparer.Equals(parent, Path.GetDirectoryName(path))))
            throw new ArgumentException("Choose an explicit destination when inputs come from different directories.", nameof(request));
        string destination = request.DestinationDirectory ?? parent;
        string stem = Path.GetFileNameWithoutExtension(first);
        var outputs = new List<string>();
        var ownedDirectories = new HashSet<string>(pathComparer);
        var warnings = new List<string>();
        try
        {
            progress?.Report(new(null, "Reading inputs"));
            if (request.Operation.Id == "images.pdf")
            {
                await CreateFromImagesAsync(request.Inputs, Path.Combine(destination, stem + "-combined.pdf"), outputs, token).ConfigureAwait(false);
            }
            else
            {
                var inputs = new List<PdfInput>();
                try
                {
                    // Keep input streams/documents alive through imports/save and lock originals against Windows writes.
                    foreach (string path in request.Inputs)
                    {
                        token.ThrowIfCancellationRequested();
                        inputs.Add(PdfInput.Open(path));
                    }
                    var pages = PdfPageSelection.Parse(request.Options.PageSelection, inputs[0].Document.PageCount);
                    switch (request.Operation.Id)
                    {
                        case "pdf.merge":
                            using (var merged = new PdfDocument())
                            {
                                foreach (var input in inputs)
                                    foreach (PdfPage page in input.Document.Pages)
                                    {
                                        token.ThrowIfCancellationRequested();
                                        merged.AddPage(page);
                                    }
                                outputs.Add(await SaveAsync(merged, Path.Combine(destination, stem + "-merged.pdf"), token).ConfigureAwait(false));
                            }
                            break;
                        case "pdf.split":
                            foreach (int page in pages)
                            {
                                token.ThrowIfCancellationRequested();
                                using var split = new PdfDocument();
                                split.AddPage(inputs[0].Document.Pages[page - 1]);
                                outputs.Add(await SaveAsync(split, Path.Combine(destination, $"{stem}-page-{page}.pdf"), token).ConfigureAwait(false));
                                token.ThrowIfCancellationRequested();
                                progress?.Report(new((double)outputs.Count / pages.Count, "Page ready"));
                            }
                            break;
                        case "pdf.images":
                        case "pdf.jpg":
                            await RenderAsync(inputs[0], pages, destination, stem, outputs, progress, token, request.Operation.Id == "pdf.jpg" ? "jpg" : "png").ConfigureAwait(false);
                            break;
                        case "pdf.docx": case "pdf.pptx": case "pdf.xlsx": case "pdf.epub": case "pdf.markdown": case "pdf.text":
                            var semantic = await Editing.Pdf.PdfSemanticExtractor.ExtractAsync(first, pages, FeaturePackDirectory, token).ConfigureAwait(false);
                            warnings.AddRange(semantic.Warnings);
                            string extension = request.Operation.OutputExtension!;
                            if (extension == ".md" && semantic.Pages.Any(page => !page.Images.IsEmpty))
                            {
                                string bundle=await writer.WriteDirectoryAsync(Path.Combine(destination,stem+"-markdown"),(temporary,ct)=>Editing.Pdf.PdfSemanticExport.WriteMarkdownBundleAsync(semantic,temporary,ct),token).ConfigureAwait(false);
                                outputs.Add(bundle);ownedDirectories.Add(bundle);
                            }
                            else outputs.Add(await writer.WriteFileAsync(Path.Combine(destination, stem + (request.Operation.Id=="pdf.text"?"-text":"-converted") + extension), (temporary, ct) => Editing.Pdf.PdfSemanticExport.WriteAsync(semantic, extension, temporary, ct), token).ConfigureAwait(false));
                            break;
                        default:
                            throw new NotSupportedException("Open this PDF tool and apply its settings before export.");
                    }
                }
                finally { foreach (var input in inputs) input.Dispose(); }
            }
            token.ThrowIfCancellationRequested();
            progress?.Report(new(1, "Copies ready"));
            return new(outputs.ToArray(),Warnings:warnings.Distinct().ToArray());
        }
        catch (Exception failure)
        {
            List<Exception>? cleanupFailures = null;
            foreach (string output in outputs)
            {
                try { if(ownedDirectories.Contains(output))Editing.Pdf.OfflineStagingFiles.DeletePrivateTree(output);else File.Delete(output); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { (cleanupFailures ??= []).Add(error); }
            }
            if (cleanupFailures is not null)
                throw new AggregateException("PDF conversion failed and some owned outputs could not be removed.", new[] { failure }.Concat(cleanupFailures));
            throw;
        }
    }

    private async Task CreateFromImagesAsync(IReadOnlyList<string> paths, string desired, List<string> outputs, CancellationToken token)
    {
        var resources = new List<IDisposable>();
        try
        {
            using var document = new PdfDocument();
            foreach (string path in paths)
            {
                token.ThrowIfCancellationRequested();
                using var image = ValidatedImageLoader.Load(path, token);
                // Fixed 96 pixels/inch avoids untrusted/absent density metadata and preserves aspect ratio.
                var page = document.AddPage();
                page.Width = XUnit.FromPoint(image.Width * 72d / 96);
                page.Height = XUnit.FromPoint(image.Height * 72d / 96);
                var stream = new MemoryStream(image.ToByteArray(MagickFormat.Png));
                resources.Add(stream);
                var pdfImage = XImage.FromStream(stream);
                resources.Add(pdfImage);
                using var graphics = XGraphics.FromPdfPage(page);
                graphics.DrawImage(pdfImage, 0, 0, page.Width.Point, page.Height.Point);
            }
            outputs.Add(await SaveAsync(document, desired, token).ConfigureAwait(false));
        }
        finally
        {
            for (int i = resources.Count - 1; i >= 0; i--) resources[i].Dispose();
        }
    }

    private Task<string> SaveAsync(PdfDocument document, string desired, CancellationToken token) =>
        writer.WriteFileAsync(desired, (temporary, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            document.Save(temporary);
            cancellation.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, token);

    private async Task RenderAsync(PdfInput input, IReadOnlyList<int> pages, string destination, string stem,
        List<string> outputs, IProgress<ConversionProgress>? progress, CancellationToken token, string format = "png")
    {
        foreach (int page in pages)
        {
            token.ThrowIfCancellationRequested();
            outputs.Add(await writer.WriteFileAsync(Path.Combine(destination, $"{stem}-page-{page}.{format}"), async (temporary, cancellation) =>
            {
                await RenderGate.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    input.Stream.Position = 0;
                    // The string overload expects Base64. Render the stream while holding the global engine gate.
                    if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                    {
                        if (format == "jpg") PDFtoImage.Conversion.SaveJpeg(temporary, input.Stream, page - 1, leaveOpen: true, options: new RenderOptions(Dpi: 300, WithAnnotations: true, WithFormFill: true));
                        else PDFtoImage.Conversion.SavePng(temporary, input.Stream, page - 1, leaveOpen: true, options: new RenderOptions(Dpi: 300, WithAnnotations: true, WithFormFill: true));
                    }
                    else throw new PlatformNotSupportedException("PDF rendering is supported on Windows and Linux.");
                    cancellation.ThrowIfCancellationRequested();
                }
                finally { RenderGate.Release(); }
            }, token).ConfigureAwait(false));
            token.ThrowIfCancellationRequested();
            progress?.Report(new((double)outputs.Count / pages.Count, "Page image ready"));
        }
    }

    private sealed class PdfInput(FileStream stream, PdfDocument document) : IDisposable
    {
        public FileStream Stream { get; } = stream;
        public PdfDocument Document { get; } = document;
        public static PdfInput Open(string path)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            PdfDocument? document = null;
            try
            {
                using (var check = PigDocument.Open(stream))
                {
                    if (check.IsEncrypted) throw new NotSupportedException("Encrypted PDFs are not supported. Export an unencrypted copy first.");
                    if (check.NumberOfPages < 1) throw new InvalidDataException("The PDF has no pages.");
                }
                stream.Position = 0;
                document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
                if (document.SecuritySettings.IsEncrypted) throw new NotSupportedException("Encrypted PDFs are not supported. Export an unencrypted copy first.");
                return new(stream, document);
            }
            catch { document?.Dispose(); stream.Dispose(); throw; }
        }
        public void Dispose() { Document.Dispose(); Stream.Dispose(); }
    }
}
