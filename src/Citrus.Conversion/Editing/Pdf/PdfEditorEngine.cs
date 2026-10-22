using System.Collections.Immutable;
using Citrus.Core;
using Citrus.Core.Editing;
using Citrus.Conversion.Editing;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Drawing;
using PDFtoImage;
using Pig = UglyToad.PdfPig.PdfDocument;

namespace Citrus.Conversion;

public sealed record PdfSourceInfo(int PageCount, ImmutableArray<MetadataField> Metadata);

public sealed partial class PdfEngine
{
    public ISourceSnapshotService Sources { get; } = new SourceSnapshotService();
    public string FeaturePackDirectory { get; init; } = FeaturePackLocator.Find();
    public EditorRouteRegistry Routes { get; } = new(new[]
    {
        new EditorRoute("pdf.editor","Organize",EditorSubmissionShape.PerRoot), new("pdf.organize","Organize",EditorSubmissionShape.Group),
        new("pdf.merge","Organize",EditorSubmissionShape.Group), new("pdf.split","Split",EditorSubmissionShape.PerRoot),
        new("pdf.metadata","Metadata",EditorSubmissionShape.PerRoot), new("pdf.compress","Compress",EditorSubmissionShape.PerRoot),
        new("pdf.images","Export",EditorSubmissionShape.PerRoot),new("pdf.text","Export",EditorSubmissionShape.PerRoot)
    });

    private bool CanExecuteEditor(OperationRequest request)
    {
        if (request.Editor?.Recipe is not PdfRecipe || !Operations.Any(o => o == request.Operation)) return false;
        try { EditorExportPolicy.Resolve(request); return DropValidation.Accepts(request.Inputs,request.Operation.Mode) && request.Inputs.All(p => Path.GetExtension(p).Equals(".pdf",StringComparison.OrdinalIgnoreCase)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return false; }
    }

    public Task<PdfSourceInfo> InspectAsync(string path,CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested(); using var input = PdfInput.Open(path);
        return new PdfSourceInfo(input.Document.PageCount,ReadMetadata(input.Document));
    },ct);

    public async Task<ImmutableArray<ValidationIssue>> ValidateAsync(OperationRequest request,CancellationToken ct)
    {
        string scope = Path.Combine(Path.GetTempPath(),"citrus-pdf-admission-"+Guid.NewGuid().ToString("N"));
        try
        {
            if (!CanExecuteEditor(request)) throw new ArgumentException("Invalid PDF editor request.");
            var doc = request.Editor!; var recipe = (PdfRecipe)doc.Recipe;
            var staged = await Sources.StageVerifiedAsync(doc.Sources,scope,ct).ConfigureAwait(false);
            foreach (var source in doc.Sources)
            {
                var info = await InspectAsync(staged[source.Id],ct).ConfigureAwait(false);
                if (recipe.Pages.Where(p => p.SourceId == source.Id).Any(p => p.OneBasedPage > info.PageCount)) throw new ArgumentException("A selected PDF page no longer exists.");
            }
            ValidateMetadata(recipe.MetadataEdits); return [];
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { return [new("PDF",error.Message)]; }
        finally { if (Directory.Exists(scope)) Directory.Delete(scope,true); }
    }

    public static async Task<byte[]> RenderPageAsync(string path,int page,int dpi,CancellationToken ct)
    {
        if (page < 1 || dpi is < 36 or > 600) throw new ArgumentOutOfRangeException(nameof(page));
        await RenderGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var source = PdfInput.Open(path);
            ValidateRasterSize(source.Stream, page, dpi);
            using var output = new MemoryStream();
            await Task.Run(() =>
            {
                if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) PDFtoImage.Conversion.SavePng(output,source.Stream,page-1,leaveOpen:true,options:new RenderOptions(Dpi:dpi,WithAnnotations:true,WithFormFill:true));
                else throw new PlatformNotSupportedException("PDF rendering is supported on Windows and Linux.");
            },ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return output.ToArray();
        }
        finally { RenderGate.Release(); }
    }

    private async Task<ConversionResult> ExecuteEditorAsync(OperationRequest request,IProgress<ConversionProgress>? progress,CancellationToken ct)
    {
        if (!CanExecuteEditor(request)) throw new NotSupportedException("Open the PDF editor and apply valid settings.");
        var doc=request.Editor!; var recipe=(PdfRecipe)doc.Recipe; var target=EditorExportPolicy.Resolve(request);
        ValidateMetadata(recipe.MetadataEdits);
        string scope=Path.Combine(Path.GetTempPath(),"citrus-pdf-edit-"+Guid.NewGuid().ToString("N"));
        var opened = new Dictionary<Guid,PdfInput>();
        var warnings=ImmutableArray.CreateBuilder<string>();string? published=null;
        try
        {
            progress?.Report(new(null,"Verifying original PDFs"));
            var staged=await Sources.StageVerifiedAsync(doc.Sources,scope,ct).ConfigureAwait(false);
            foreach (var source in doc.Sources) opened.Add(source.Id,PdfInput.Open(staged[source.Id]));
            foreach (var page in recipe.Pages) if (!opened.TryGetValue(page.SourceId,out var source) || page.OneBasedPage > source.Document.PageCount) throw new ArgumentException("A selected PDF page does not exist.");
            string payload=Path.Combine(scope,"payload"); Directory.CreateDirectory(payload);
            var members=new List<string>();
            if (recipe.Mode is PdfOutputMode.Document or PdfOutputMode.SeparatePageDocuments)
            {
                if (recipe.Mode == PdfOutputMode.Document) await SavePages(recipe.Pages,"edited.pdf").ConfigureAwait(false);
                else for (int index=0;index<recipe.Pages.Length;index++) await SavePages([recipe.Pages[index]],$"page-{index+1:D4}.pdf").ConfigureAwait(false);
            }
            else if (recipe.Mode == PdfOutputMode.PageImages)
            {
                for (int index=0;index<recipe.Pages.Length;index++)
                {
                    ct.ThrowIfCancellationRequested(); var page=recipe.Pages[index];
                    byte[] png=await RenderPageAsync(staged[page.SourceId],page.OneBasedPage,recipe.Dpi,ct).ConfigureAwait(false);
                    using var image=new MagickImage(png); if (page.QuarterTurns != 0) image.Rotate(page.QuarterTurns*90);
                    image.Quality=(uint)recipe.RasterQuality;
                    string name=$"page-{index+1:D4}."+doc.Export.FormatId;
                    await image.WriteAsync(Path.Combine(payload,name),ResolveRaster(doc.Export.FormatId),ct).ConfigureAwait(false); members.Add(name);
                }
            }
            else
            {
                var pages=ImmutableArray.CreateBuilder<Editing.Pdf.PdfSemanticPage>();
                foreach (var page in recipe.Pages)
                {
                    var semantic=await Editing.Pdf.PdfSemanticExtractor.ExtractAsync(staged[page.SourceId],[page.OneBasedPage],FeaturePackDirectory,ct,recoverArtwork:recipe.Mode!=PdfOutputMode.Text).ConfigureAwait(false);
                    pages.AddRange(semantic.Pages); warnings.AddRange(semantic.Warnings);
                }
                string name="edited."+doc.Export.FormatId;
                await Editing.Pdf.PdfSemanticExport.WriteAsync(new(pages.ToImmutable(),warnings.ToImmutable()),"."+doc.Export.FormatId,Path.Combine(payload,name),ct).ConfigureAwait(false); members.Add(name);
            }
            long actual=members.Sum(name=>new FileInfo(Path.Combine(payload,name)).Length), original=doc.Sources.Sum(s=>s.Length);
            long? limit=doc.Export.Compression switch { ByteGoal goal=>goal.MaximumBytes, ReductionGoal goal=>(long)decimal.Floor(original*(1-goal.Percent/100)), _=>null };
            if (limit is {} maximum && actual > maximum) throw new InvalidDataException($"Could not reach the requested size while preserving PDF structure. Best measured size: {actual:N0} bytes; target: {maximum:N0} bytes. No copy was saved.");
            var outcome=new CompressionOutcome(original,actual,limit,true,1);
            string output=target.Shape == ExportShape.File
                ? await writer.WriteFileAsync(Path.Combine(target.Directory,target.Stem+target.Extension),async(path,token)=> { await using var input=File.OpenRead(Path.Combine(payload,members.Single())); await using var sink=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None); await input.CopyToAsync(sink,token).ConfigureAwait(false); },ct).ConfigureAwait(false)
                : await writer.WriteDirectoryAsync(Path.Combine(target.Directory,target.Stem),async(path,token)=> { foreach(string member in members) { await using var input=File.OpenRead(Path.Combine(payload,member)); await using var sink=new FileStream(Path.Combine(path,member),FileMode.CreateNew,FileAccess.Write,FileShare.None); await input.CopyToAsync(sink,token).ConfigureAwait(false); } },ct).ConfigureAwait(false);
            published=output;ct.ThrowIfCancellationRequested();progress?.Report(new(1,"PDF copy ready")); return new([output],outcome,warnings.Distinct().ToArray());

            async Task SavePages(ImmutableArray<PdfPageRef> pages,string name)
            {
                using var assembled=new PdfDocument(); assembled.Options.CompressContentStreams=true;
                foreach(var page in pages)
                {
                    ct.ThrowIfCancellationRequested();
                    if (recipe.CompressionMode == PdfCompressionMode.RasterRebuild)
                    {
                        var png=await RenderPageAsync(staged[page.SourceId],page.OneBasedPage,recipe.Dpi,ct).ConfigureAwait(false);
                        using var raster=new MagickImage(png); if(page.QuarterTurns!=0) raster.Rotate(page.QuarterTurns*90); raster.Quality=(uint)recipe.RasterQuality;
                        using var stream=new MemoryStream(raster.ToByteArray(MagickFormat.Jpeg)); using var image=XImage.FromStream(stream);
                        var added=assembled.AddPage(); added.Width=XUnit.FromPoint(raster.Width*72d/recipe.Dpi); added.Height=XUnit.FromPoint(raster.Height*72d/recipe.Dpi);
                        using var graphics=XGraphics.FromPdfPage(added); graphics.DrawImage(image,0,0,added.Width.Point,added.Height.Point);
                    }
                    else
                    { var added=assembled.AddPage(opened[page.SourceId].Document.Pages[page.OneBasedPage-1]); added.Rotate=(added.Rotate+page.QuarterTurns*90)%360; }
                }
                ApplyMetadata(assembled,opened.Values.First().Document,doc.Export.Metadata,recipe.MetadataEdits);
                string path=Path.Combine(payload,name); assembled.Save(path);
                using var verified=Pig.Open(path); if(verified.IsEncrypted || verified.NumberOfPages!=pages.Length) throw new InvalidDataException("The saved PDF failed page validation.");
                members.Add(name);
            }
        }
        catch(Exception failure)
        {
            if(published is not null)
            {
                try { if(target.Shape==ExportShape.File)File.Delete(published);else Editing.Pdf.OfflineStagingFiles.DeletePrivateTree(published); }
                catch(Exception cleanup)when(cleanup is IOException or UnauthorizedAccessException){throw new AggregateException("PDF export failed and its owned copy could not be removed.",failure,cleanup);}
            }
            throw;
        }
        finally { foreach(var input in opened.Values) input.Dispose(); if(Directory.Exists(scope)) Directory.Delete(scope,true); }
    }
    private static MagickFormat ResolveRaster(string format)=> format switch { "jpg" or "jpeg"=>MagickFormat.Jpeg,"png"=>MagickFormat.Png,"webp"=>MagickFormat.WebP,"tif" or "tiff"=>MagickFormat.Tiff,_=>throw new NotSupportedException("Unsupported PDF page image format.") };
    private static readonly string[] MetadataKeys=["Title","Author","Subject","Keywords","Creator"];
    private static ImmutableArray<MetadataField> ReadMetadata(PdfDocument doc)=>[.. MetadataKeys.Select(key=>new MetadataField("PDF",key,key,new TextValue(doc.Info.Elements.GetString("/"+key)),true,true))];
    private static void ValidateMetadata(ImmutableArray<MetadataEdit> edits)
    { foreach(var edit in edits) if(edit.Scope!="PDF" || !MetadataKeys.Contains(edit.Key,StringComparer.Ordinal) || edit.Action==MetadataEditAction.Set && edit.Value is not TextValue) throw new ArgumentException("Unsupported PDF metadata edit."); }
    private static void ApplyMetadata(PdfDocument target,PdfDocument source,MetadataPolicy policy,ImmutableArray<MetadataEdit> edits)
    {
        foreach(string key in MetadataKeys) target.Info.Elements.SetString("/"+key,policy==MetadataPolicy.PreserveSupported?source.Info.Elements.GetString("/"+key):"");
        foreach(var edit in edits) if(edit.Action==MetadataEditAction.Remove) target.Info.Elements.Remove("/"+edit.Key); else target.Info.Elements.SetString("/"+edit.Key,((TextValue)edit.Value!).Value);
    }
}

public static class FeaturePackLocator
{
    public static string Find()
    {
        string installed=Path.Combine(AppContext.BaseDirectory,"feature-pack"); if(Directory.Exists(installed)) return installed;
        for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)
        { string candidate=Path.Combine(directory.FullName,"packaging","feature-pack"); if(Directory.Exists(candidate)) return candidate; }
        return installed;
    }
}
