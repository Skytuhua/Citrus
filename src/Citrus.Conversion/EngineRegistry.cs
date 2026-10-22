using Citrus.Core;
using Citrus.Core.Editing;
using Citrus.Conversion.Editing;
using Citrus.Conversion.Editing.Image;
using Citrus.Conversion.Editing.Media;
using ImageMagick;
using System.Collections.Immutable;

namespace Citrus.Conversion;

/// <summary>Offline composition; discovery is always performed on a worker, including native capability probes.</summary>
public sealed record EngineRegistry(IReadOnlyList<IConversionEngine> Engines, IReadOnlyList<string> Warnings, InputInspector Inspector)
{
    public ImageEditorServices? ImageEditors { get; init; }
    public VideoCropEngine? VideoCrop { get; init; }
    public MediaEditorEngine? MediaEditors { get; init; }
    public PdfEngine? PdfEditors { get; init; }
    public ImageMetadataEngine? ImageMetadata { get; init; }
    public static Task<EngineRegistry> Create(OutputWriter writer, string toolsDirectory, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            ArgumentNullException.ThrowIfNull(writer);
            var process = new MediaProcess(Path.Combine(toolsDirectory, "ffmpeg.exe"), Path.Combine(toolsDirectory, "ffprobe.exe"));
            var warnings = new List<string>();
            try { await HeicCodec.VerifyAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException) { warnings.Add("HEIC is unavailable: " + error.Message); }
            var pdf = new PdfEngine(writer);
            var engines = new List<IConversionEngine> { new ImageEngine(writer), pdf, new SubtitleEngine(writer), new ArchiveEngine(outputWriter: writer) };
            var documents = new DocumentConversionEngine(writer);
            if (documents.HasRequiredPackFiles) engines.Add(documents);
            else warnings.Add("Office and EPUB conversion unavailable. Repair Citrus.");
            VideoCropEngine? videoCrop = null;
            MediaEditorEngine? mediaEditors = null;
            try
            {
                var media = await MediaEngine.CreateAsync(process, cancellationToken, writer).ConfigureAwait(false);
                if (media.Operations.Count == 0) throw new InvalidOperationException("No media operations were discovered.");
                if (VideoCropEngine.Available(media.Capabilities))
                {
                    videoCrop = new(process, media.Capabilities, writer);
                    engines.Add(videoCrop);
                }
                mediaEditors = new(process, media.Capabilities, writer);
                engines.Add(mediaEditors);
                engines.Add(media);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is OperationCanceledException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or System.Text.Json.JsonException)
            {
                warnings.Add("Audio and video tools are unavailable. Reinstall Citrus to restore the bundled media tools. Other formats remain available.");
            }
            ImageEditorServices? imageEditors = null;
            try
            {
                imageEditors = new ImageEditorServices(writer);
                await imageEditors.VerifyAsync(cancellationToken).ConfigureAwait(false);
                engines.Add(imageEditors.Engine);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { imageEditors?.Dispose(); throw; }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                imageEditors?.Dispose(); imageEditors = null;
                warnings.Add("Image editing is unavailable: the bundled image renderer or output validation could not complete its capability check. " + error.Message);
            }
            ImageMetadataEngine? imageMetadata = null;
            try
            {
                var metadata = new ExifToolMetadataService(Path.Combine(Path.GetDirectoryName(toolsDirectory)!, "image", "exiftool", "exiftool.exe"));
                if (!metadata.Available) throw new FileNotFoundException("Metadata tools are missing. Reinstall Citrus.");
                string scope = Path.Combine(Path.GetTempPath(), "citrus-metadata-capability-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(scope);
                try
                {
                    string path = Path.Combine(scope, "probe.png");
                    using (var image = new MagickImage(MagickColors.Orange, 2, 2)) await image.WriteAsync(path, MagickFormat.Png, cancellationToken).ConfigureAwait(false);
                    if ((await metadata.ReadPathAsync(path, cancellationToken).ConfigureAwait(false)).IsEmpty) throw new InvalidDataException("The metadata reader returned no fields.");
                }
                finally { Directory.Delete(scope, true); }
                imageMetadata = new(writer, metadata, imageEditors?.Sources ?? new SourceSnapshotService());
                engines.Add(imageMetadata);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { imageEditors?.Dispose(); throw; }
            catch (Exception error) when (error is not OutOfMemoryException) { warnings.Add("Metadata editing is unavailable: " + error.Message); }
            return new EngineRegistry(engines.AsReadOnly(), warnings.AsReadOnly(), new InputInspector(process)) { ImageEditors = imageEditors, VideoCrop = videoCrop, MediaEditors = mediaEditors, PdfEditors = pdf, ImageMetadata = imageMetadata };
        }, cancellationToken);
}


/// <summary>Verified composition boundary. Session renderers never share the export renderer lifetime.</summary>
public sealed class ImageEditorServices : IDisposable
{
    public ISourceSnapshotService Sources { get; } = new SourceSnapshotService();
    public IFontArtifactService Fonts { get; } = new FontArtifactService();
    public IEditorAdmissionInspector Inspector { get; }
    public ImageEditorEngine Engine { get; }
    public EditorRouteRegistry Routes { get; } = new(new[]
    {
        new EditorRoute("image.editor", "Adjust", EditorSubmissionShape.PerRoot),
        new EditorRoute("image.crop", "Crop", EditorSubmissionShape.PerRoot),
        new EditorRoute("image.annotate", "Annotate", EditorSubmissionShape.PerRoot),
        new EditorRoute("image.redact", "Manual masks", EditorSubmissionShape.PerRoot),
        new EditorRoute("image.resize", "Resize", EditorSubmissionShape.PerRoot),
        new EditorRoute("image.rotate", "Rotate", EditorSubmissionShape.PerRoot),
        new EditorRoute("image.compress", "Compress", EditorSubmissionShape.PerRoot),
        new EditorRoute("image.background", "Background", EditorSubmissionShape.PerRoot)
    });
    private readonly ImageRecipeRenderer exportRenderer;
    public ImageEditorServices(OutputWriter writer)
    {
        Inspector = new ImageEditorAdmissionInspector(Sources);
        exportRenderer = new(Sources);
        Engine = new(exportRenderer, new EditorOutputPublisher(writer, new(), new ImageEditorPayloadValidator()), Inspector);
    }
    public ImageRecipeRenderer CreateSessionRenderer() => new(Sources);
    public Task<SizeI> InspectAsync(SourceSnapshot source, CancellationToken ct) => Task.Run(async () =>
    {
        string scope = Path.Combine(Path.GetTempPath(), "citrus-image-host-inspect-" + Guid.NewGuid().ToString("N"));
        try
        {
            ImageRasterSource.CheckAvailable([source]);
            var paths = await Sources.StageVerifiedAsync([source], scope, ct).ConfigureAwait(false);
            return ImageRasterSource.Inspect(paths[source.Id], ct);
        }
        finally { if (Directory.Exists(scope)) Directory.Delete(scope, true); }
    }, ct);
    public async Task VerifyAsync(CancellationToken ct)
    {
        string scope = Path.Combine(Path.GetTempPath(), "citrus-image-capability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scope);
        try
        {
            string input = Path.Combine(scope, "probe.png");
            using (var image = new MagickImage(MagickColors.Orange, 2, 2)) await image.WriteAsync(input, MagickFormat.Png, ct).ConfigureAwait(false);
            var capture = (await Sources.CaptureAsync([input], ct).ConfigureAwait(false)).Single();
            var source = capture.Snapshot ?? throw new IOException(capture.Error);
            var document = new EditorDocument(1, 0, [source], [], new ImageRecipe(source.Id, new(0, false, false, null, null, false),
                new(1, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, PhotoEffect.None, 1), [new(Guid.NewGuid(),new(1,1,1,1),MaskEffect.Solid,new(0,0,0,255),0)], null, [new(Guid.NewGuid(),true,new ShapeGeometry(new(0,0,1,1),false,0),new(new(0,0,0,0),new(0,0,255,255),0,1,null))], []),
                new("png", scope, "verified", new QualityGoal(85), MetadataPolicy.PreserveSupported, new(255,255,255,255)));
            using var previewRenderer = CreateSessionRenderer();
            var preview = await previewRenderer.RenderPreviewAsync(document, new(1, new(2,2), null,null,null), ct).ConfigureAwait(false);
            if (preview.Size != new SizeI(2,2) || preview.Bgra32.Length != 16 || !preview.Bgra32.Take(4).SequenceEqual(new byte[]{255,0,0,255}) || !preview.Bgra32.Skip(12).Take(4).SequenceEqual(new byte[]{0,0,0,255})) throw new InvalidDataException("Image preview capability returned invalid pixels.");
            var result = await Engine.ExecuteAsync(new(Engine.Operations.Single(o => o.Id == "image.editor"), [input], new(), scope, document), null, ct).ConfigureAwait(false);
            if (result.Outputs.Count != 1 || !File.Exists(result.Outputs[0])) throw new InvalidDataException("Image export capability did not publish verified output.");
        }
        finally { if (Directory.Exists(scope)) Directory.Delete(scope, true); }
    }
    public void Dispose() => exportRenderer.Dispose();
}
