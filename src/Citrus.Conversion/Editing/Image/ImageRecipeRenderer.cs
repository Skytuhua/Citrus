using PointD = Citrus.Core.Editing.PointD;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Citrus.Core;
using Citrus.Core.Editing;
using ImageMagick;

namespace Citrus.Conversion.Editing.Image;

/// <summary>Own one instance per editor session. Verified source copies and unadjusted pixels use bounded LRU caches;
/// export always stages and verifies the original snapshots again in the supplied private scope.</summary>
public sealed class ImageRecipeRenderer : IEditorRenderer, IDisposable, IAsyncDisposable
{
    private readonly ISourceSnapshotService sources;
    private readonly string cacheDirectory;
    private readonly SemaphoreSlim serial = new(1, 1);
    private sealed record StagedSource(string Scope, ImmutableDictionary<Guid, string> Paths, SizeI Size, long LastUse);
    private readonly Dictionary<string, StagedSource> cached = new();
    private readonly ImagePreviewWorkingCache workingCache = new();
    private long sourceUse;
    private ImagePreviewDiagnostics diagnostics = new(default, 0, 0, 0, 0, 0, 0);
    internal ImagePreviewDiagnostics Diagnostics => Volatile.Read(ref diagnostics);
    private void Observe(SizeI size) => Volatile.Write(ref diagnostics, new(size, workingCache.Bytes, workingCache.Count, workingCache.Hits, workingCache.Misses, workingCache.Evictions, cached.Count));
    private bool ownsCache, disposed;
    public ImageRecipeRenderer(ISourceSnapshotService sources, string? cacheDirectory = null)
    {
        this.sources = sources ?? throw new ArgumentNullException(nameof(sources));
        this.cacheDirectory = Path.GetFullPath(cacheDirectory ?? Path.Combine(Path.GetTempPath(), "citrus-image-preview-" + Guid.NewGuid().ToString("N")));
        SafeEditorFiles.RejectLinks(this.cacheDirectory);
        if (File.Exists(this.cacheDirectory) || Directory.Exists(this.cacheDirectory)) throw new IOException("Preview cache must be a fresh owned directory.");
    }
    /// <summary>Fast viewport preview, at most 512 pixels per working edge when zoomed out.
    /// Native-size and explicit source-detail requests always use exact rendering.</summary>
    public Task<PreviewFrame> RenderPreviewAsync(EditorDocument document, PreviewRequest request, CancellationToken ct) => RenderPreviewCoreAsync(document, request, ct, 512);
    /// <summary>Optional idle/gesture-end refinement, at most 1024 pixels per working edge
    /// when zoomed out. Shares this session's cache and worker leases with fast previews.</summary>
    public Task<PreviewFrame> RenderRefinedPreviewAsync(EditorDocument document, PreviewRequest request, CancellationToken ct) => RenderPreviewCoreAsync(document, request, ct, 1024);
    private async Task<PreviewFrame> RenderPreviewCoreAsync(EditorDocument document, PreviewRequest request, CancellationToken ct, int workingEdge)
    {
        Validate(document);
        if (request.Bounds.Width is <= 0 or > 2048 || request.Bounds.Height is <= 0 or > 2048 || request.Time is not null || request.PageId is not null) throw new ArgumentException("Invalid raster preview request.");
        using var worker = await PreviewWorkScheduler.AcquireAsync(ct).ConfigureAwait(false);
        await serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await Task.Run(async () =>
            {
                ct.ThrowIfCancellationRequested(); ImageRasterSource.CheckAvailable(document.Sources);
                string key = ((ImageRecipe)document.Recipe).SourceId + "|" + string.Join("|", document.Sources.Select(s => $"{s.Id}:{s.Length}:{s.Sha256}:{s.Path}"));
                var recipe = (ImageRecipe)document.Recipe;
                if (!cached.TryGetValue(key, out var staged))
                {
                    if (!ownsCache) { Directory.CreateDirectory(cacheDirectory); ownsCache = true; }
                    string scope = Path.Combine(cacheDirectory, Guid.NewGuid().ToString("N"));
                    try
                    {
                        var paths = await sources.StageVerifiedAsync(document.Sources, scope, ct).ConfigureAwait(false);
                        var size = ImageRasterSource.Inspect(paths[recipe.SourceId], ct);
                        foreach (var source in document.Sources.Where(s => s.Id != recipe.SourceId)) ImageRasterSource.Inspect(paths[source.Id], ct);
                        ct.ThrowIfCancellationRequested();
                        while (cached.Count >= 4)
                        {
                            var oldest = cached.MinBy(pair => pair.Value.LastUse); workingCache.RemoveSource(oldest.Key);
                            Directory.Delete(oldest.Value.Scope, true); cached.Remove(oldest.Key);
                        }
                        staged = new(scope, paths, size, ++sourceUse); cached.Add(key, staged);
                    }
                    catch { if (Directory.Exists(scope)) Directory.Delete(scope, true); throw; }
                }
                else cached[key] = staged = staged with { LastUse = ++sourceUse };
                SizeI sourceSize = staged.Size; var (crop, outputSize) = ImageGeometryMapper.Resolve(recipe, sourceSize);
                SizeI canvasSize=recipe.Canvas?.Size??outputSize;
                bool approximate = request.SourceTile is null && (canvasSize.Width > request.Bounds.Width || canvasSize.Height > request.Bounds.Height);
                SizeI croppedSize = new((int)crop.Width, (int)crop.Height);
                SizeI viewportBounds = new(Math.Min(request.Bounds.Width, workingEdge), Math.Min(request.Bounds.Height, workingEdge));
                double canvasScale=approximate?Math.Min(1,Math.Min(viewportBounds.Width/(double)canvasSize.Width,viewportBounds.Height/(double)canvasSize.Height)):1;
                SizeI workSize = approximate ? recipe.Canvas is null ? Fit(croppedSize,viewportBounds) : new(Math.Max(1,(int)Math.Floor(croppedSize.Width*canvasScale)),Math.Max(1,(int)Math.Floor(croppedSize.Height*canvasScale))) : croppedSize;
                var workingKey = new ImagePreviewWorkingKey(key, recipe.Geometry, workSize);
                using var image = workingCache.GetOrCreate(workingKey, () => PrepareWorkingBase(recipe, staged.Paths[recipe.SourceId], sourceSize, workSize, ct), ct);
                Observe(new((int)image.Width, (int)image.Height));
                if (approximate) ImageAdjustmentsPipeline.ApplyScaled(image, recipe, sourceSize, ct, Math.Sqrt(workSize.Width / crop.Width * (workSize.Height / crop.Height)));
                else ImageAdjustmentsPipeline.Apply(image, recipe, sourceSize, ct);
                PixelMaskRenderer.Apply(image, recipe, sourceSize, ct);
                SizeI renderedSize = approximate ? recipe.Canvas is null ? Fit(outputSize,workSize) : new(Math.Max(1,(int)Math.Floor(outputSize.Width*canvasScale)),Math.Max(1,(int)Math.Floor(outputSize.Height*canvasScale))) : outputSize;
                if (image.Width != renderedSize.Width || image.Height != renderedSize.Height) Resize(image, renderedSize);
                using var composed=recipe.Canvas is {} canvas?ImageCanvasComposer.Compose(image,canvas,outputSize,staged.Paths,canvasScale,ct):null;
                var final=composed??image;
                string fontScope = Path.Combine(cacheDirectory, "font-render-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var fonts = await new FontArtifactService().StageVerifiedAsync(document.Fonts, fontScope, ct).ConfigureAwait(false);
                    FontTextValidator.ValidateDocument(document, fonts, ct);
                    AnnotationRasterizer.Apply(document, final, canvasSize, staged.Paths, fonts, ct);
                }
                finally { if (Directory.Exists(fontScope)) Directory.Delete(fontScope, true); }
                PixelMaskRenderer.RemoveOriginalMetadata(final, recipe);
                PrepareOutput(final, document.Export);
                if (request.SourceTile is { } tile) CropDetail(final, recipe, sourceSize, tile);
                double scale = Math.Min(1, Math.Min(request.Bounds.Width / (double)final.Width, request.Bounds.Height / (double)final.Height));
                if (scale < 1) final.Resize(new MagickGeometry((uint)Math.Max(1, Math.Floor(final.Width * scale)), (uint)Math.Max(1, Math.Floor(final.Height * scale))) { IgnoreAspectRatio = true });
                ct.ThrowIfCancellationRequested();
                byte[] bytes = final.GetPixels().ToByteArray(PixelMapping.BGRA)!;
                return new PreviewFrame(document.Revision, request.Generation, new((int)final.Width, (int)final.Height), checked((int)final.Width * 4), ImmutableCollectionsMarshal.AsImmutableArray(bytes), null, null, PixelMaskRenderer.Warnings(recipe).AddRange(approximate ? new[] { "Scaled preview; use a 100% source detail tile or export for exact pixels." } : Array.Empty<string>()));
            }, ct).ConfigureAwait(false);
        }
        finally { serial.Release(); }
    }
    public async Task<StagedExport> RenderExportAsync(EditorDocument document, string privateDirectory, IProgress<ConversionProgress>? progress, CancellationToken ct)
    {
        Validate(document); ct.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(privateDirectory); SafeEditorFiles.RejectLinks(root);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() || File.Exists(root)) throw new IOException("Export requires a fresh private directory.");
        await serial.WaitAsync(ct).ConfigureAwait(false);
        bool createdRoot = false;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await Task.Run(async () =>
            {
                ct.ThrowIfCancellationRequested(); ImageRasterSource.CheckAvailable(document.Sources);
                if (!Directory.Exists(root)) { Directory.CreateDirectory(root); createdRoot = true; }
                var staged = await sources.StageVerifiedAsync(document.Sources, root, ct).ConfigureAwait(false);
                var recipe = (ImageRecipe)document.Recipe;
                progress?.Report(new(null, "Rendering original pixels"));
                var fonts = await new FontArtifactService().StageVerifiedAsync(document.Fonts, root, ct).ConfigureAwait(false);
                FontTextValidator.ValidateDocument(document, fonts, ct);
                foreach (var source in document.Sources.Where(s => s.Id != recipe.SourceId)) ImageRasterSource.Inspect(staged[source.Id], ct);
                using var photo = RenderBeforeAnnotations(document, staged[recipe.SourceId], ct, out _);
                using var composed=recipe.Canvas is {} canvas?ImageCanvasComposer.Compose(photo,canvas,new((int)photo.Width,(int)photo.Height),staged,1,ct):null;
                var image=composed??photo;
                AnnotationRasterizer.Apply(document, image, new((int)image.Width, (int)image.Height), staged, fonts, ct);
                PixelMaskRenderer.RemoveOriginalMetadata(image, recipe);
                PrepareOutput(image, document.Export);
                string payload = Path.Combine(root, "payload"); Directory.CreateDirectory(payload);
                string name = "image." + document.Export.FormatId, path = Path.Combine(payload, name);
                var outcome=await ImageCompressionEncoder.WriteAsync(image,path,document.Export,document.Sources.Single(s=>s.Id==recipe.SourceId).Length,progress,ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                long bytes = new FileInfo(path).Length;
                return new StagedExport(ExportShape.File, [new(name, bytes)], outcome, PixelMaskRenderer.Warnings(recipe));
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            foreach (string name in new[] { "sources", "fonts", "payload" }) { string path = Path.Combine(root, name); if (Directory.Exists(path)) Directory.Delete(path, true); }
            if (createdRoot && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
            throw;
        }
        finally { serial.Release(); }
    }
    internal static ImageRecipe Validate(EditorDocument document)
    {
        var errors = EditorRecipeValidator.Validate(document);
        if (!errors.IsEmpty) throw new ArgumentException(string.Join(" ", errors.Select(e => $"{e.FieldPath}: {e.Message}")));
        if (document.Recipe is not ImageRecipe recipe) throw new NotSupportedException("An image recipe is required.");
        if (!recipe.MetadataEdits.IsEmpty) throw new NotSupportedException("Use the metadata editor to save metadata without re-encoding pixels.");
        ImageRasterSource.Format(document.Export.FormatId);
        var referenced = EditorRecipeReferences.Sources(recipe).ToHashSet();
        if (document.Sources.Any(s => !referenced.Contains(s.Id))) throw new ArgumentException("Image sources must be the root or referenced inset images.");
        foreach (var layer in recipe.Annotations)
            if (layer.Geometry is TextGeometry text && (!double.IsFinite(text.Origin.X) || !double.IsFinite(text.Origin.Y) || text.Origin.X is < -16384 or > 32768 || text.Origin.Y is < -16384 or > 32768)) throw new ArgumentException("Text origin must be finite and in canvas coordinate bounds.");
        if (document.Export.Matte.A != 255 && document.Export.FormatId is "jpg" or "jpeg" or "bmp" or "gif") throw new ArgumentException("This output format requires an opaque matte.");
        return recipe;
    }
    internal static MagickImage RenderBeforeAnnotations(EditorDocument document, string source, CancellationToken ct, out SizeI sourceSize)
    {
        var recipe = (ImageRecipe)document.Recipe; var image = ImageRasterSource.Load(source, ct);
        try
        {
            sourceSize = new((int)image.Width, (int)image.Height); var (crop, size) = ImageGeometryMapper.Resolve(recipe, sourceSize);
            if (recipe.Geometry.FlipX) image.Flop(); if (recipe.Geometry.FlipY) image.Flip();
            if (recipe.Geometry.QuarterTurns != 0) image.Rotate(90 * recipe.Geometry.QuarterTurns);
            image.Crop(new MagickGeometry((int)crop.X, (int)crop.Y, (uint)crop.Width, (uint)crop.Height)); image.ResetPage();
            ImageAdjustmentsPipeline.Apply(image, recipe, sourceSize, ct);
            PixelMaskRenderer.Apply(image, recipe, sourceSize, ct);
            if (image.Width != size.Width || image.Height != size.Height) { image.FilterType = FilterType.Lanczos; image.Resize(new MagickGeometry((uint)size.Width, (uint)size.Height) { IgnoreAspectRatio = true }); }
            ct.ThrowIfCancellationRequested(); return image;
        }
        catch { image.Dispose(); throw; }
    }
    private static SizeI Fit(SizeI size, SizeI bounds)
    {
        double scale = Math.Min(1, Math.Min(bounds.Width / (double)size.Width, bounds.Height / (double)size.Height));
        return new(Math.Max(1, (int)Math.Floor(size.Width * scale)), Math.Max(1, (int)Math.Floor(size.Height * scale)));
    }
    private static void Resize(MagickImage image, SizeI size)
    { image.FilterType = FilterType.Lanczos; image.Resize(new MagickGeometry((uint)size.Width, (uint)size.Height) { IgnoreAspectRatio = true }); }
    private static MagickImage PrepareWorkingBase(ImageRecipe recipe, string source, SizeI sourceSize, SizeI size, CancellationToken ct)
    {
        var image = ImageRasterSource.Load(source, ct);
        try
        {
            var (crop, _) = ImageGeometryMapper.Resolve(recipe, sourceSize);
            if (recipe.Geometry.FlipX) image.Flop(); if (recipe.Geometry.FlipY) image.Flip(); if (recipe.Geometry.QuarterTurns != 0) image.Rotate(90 * recipe.Geometry.QuarterTurns);
            image.Crop(new MagickGeometry((int)crop.X, (int)crop.Y, (uint)crop.Width, (uint)crop.Height)); image.ResetPage(); ct.ThrowIfCancellationRequested();
            if (image.Width != size.Width || image.Height != size.Height) Resize(image, size);
            ct.ThrowIfCancellationRequested(); return image;
        }
        catch { image.Dispose(); throw; }
    }
    internal static void CropDetail(MagickImage image, ImageRecipe recipe, SizeI sourceSize, RectD tile)
    {
        if (!double.IsFinite(tile.X) || !double.IsFinite(tile.Y) || !double.IsFinite(tile.Width) || !double.IsFinite(tile.Height) || tile.Width <= 0 || tile.Height <= 0 || tile.X < 0 || tile.Y < 0 || tile.X + tile.Width > sourceSize.Width || tile.Y + tile.Height > sourceSize.Height) throw new ArgumentException("Detail tile must fit normalized source-plane S.");
        PointD[] corners = [new(tile.X, tile.Y), new(tile.X + tile.Width, tile.Y), new(tile.X, tile.Y + tile.Height), new(tile.X + tile.Width, tile.Y + tile.Height)];
        var mapped = corners.Select(p => ImageGeometryMapper.MapToCanvas(recipe, sourceSize, p)).ToArray();
        int left = (int)Math.Max(0, Math.Floor(mapped.Min(p => p.X))), top = (int)Math.Max(0, Math.Floor(mapped.Min(p => p.Y))), right = (int)Math.Min(image.Width, Math.Ceiling(mapped.Max(p => p.X))), bottom = (int)Math.Min(image.Height, Math.Ceiling(mapped.Max(p => p.Y)));
        if (right <= left || bottom <= top) throw new ArgumentException("Source tile does not intersect the visible output.");
        image.Crop(new MagickGeometry(left, top, (uint)(right - left), (uint)(bottom - top))); image.ResetPage();
    }
    private static void PrepareOutput(MagickImage image, ExportPolicy policy)
    {
        if (policy.FormatId is "jpg" or "jpeg" or "bmp" or "gif") { image.BackgroundColor = new MagickColor(policy.Matte.R, policy.Matte.G, policy.Matte.B); image.Alpha(AlphaAction.Remove); }
        if (policy.Metadata != MetadataPolicy.PreserveSupported) image.Strip();
        if (policy.Metadata != MetadataPolicy.StripAll)
        {
            image.SetProfile(ColorProfiles.SRGB);
            image.Settings.SetDefine(MagickFormat.Png, "preserve-iCCP", "true");
            // Strip() already removed profiles/comments, but also leaves this encoder artifact
            // suppressing iCCP. Remove it only when restoring the required working profile.
            if (policy.Metadata == MetadataPolicy.StripDescriptive && policy.FormatId == "png")
                image.RemoveArtifact("png:exclude-chunk");
        }
        image.Orientation = Orientation.TopLeft;
        var exif = image.GetExifProfile(); if (exif is not null) { exif.SetValue(ExifTag.Orientation, (ushort)1); exif.RemoveValue(ExifTag.PixelXDimension); exif.RemoveValue(ExifTag.PixelYDimension); image.SetProfile(exif); }
    }
    public void Dispose()
    {
        serial.Wait(); try { if (disposed) return; disposed = true; if (ownsCache && Directory.Exists(cacheDirectory)) Directory.Delete(cacheDirectory, true); cached.Clear(); workingCache.Dispose(); Observe(default); } finally { serial.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await serial.WaitAsync().ConfigureAwait(false); try { if (disposed) return; disposed = true; if (ownsCache && Directory.Exists(cacheDirectory)) Directory.Delete(cacheDirectory, true); cached.Clear(); workingCache.Dispose(); Observe(default); } finally { serial.Release(); }
    }
}
