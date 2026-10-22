using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Citrus.Core;
using Citrus.Conversion.Editing;
using Citrus.Conversion.Editing.Pdf;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W=DocumentFormat.OpenXml.Wordprocessing;

namespace Citrus.Conversion;

/// <summary>Offline Office/EPUB conversion through pinned, OS-isolated workers.</summary>
public sealed class DocumentConversionEngine(OutputWriter? outputWriter=null,string? featurePackDirectory=null):IConversionEngine
{
    private readonly OutputWriter writer=outputWriter??new();
    private readonly string pack=featurePackDirectory??FeaturePackLocator.Find();
    private readonly SourceSnapshotService sources=new();
    private static readonly string[] RequiredPackFiles=[Path.Combine("libreoffice","program","python.exe"),Path.Combine("libreoffice","program","mergedlo.dll"),Path.Combine("libreoffice","program","python-core-3.13.15","lib","os.py"),Path.Combine("pandoc","pandoc-3.12.1","pandoc.exe"),"office-worker.py"];
    /// <summary>Cheap availability only. Packaging/clean-machine checks use VerifyAsync; each job independently validates its output.</summary>
    public bool HasRequiredPackFiles=>RequiredPackFiles.All(file=>File.Exists(Path.Combine(pack,file)));
    private static readonly string[] Documents=[".docx",".doc",".odt",".rtf",".txt",".md",".html",".htm",".pages"];
    private static readonly string[] Presentations=[".pptx",".ppt",".odp"];
    private static readonly string[] Spreadsheets=[".xlsx",".xls",".ods",".csv",".tsv"];
    public IReadOnlyList<OperationDescriptor> Operations {get;}=Array.AsReadOnly(new[]
    {
        Op("document","docx","DOCX (Pages compatible)"),Op("document","pdf"),Op("document","txt"),Op("document","md"),Op("document","html"),Op("document","epub"),Op("document","png"),Op("document","jpg"),
        Op("presentation","pdf"),Op("presentation","jpg"),Op("presentation","png"),
        Op("spreadsheet","ods"),Op("spreadsheet","csv"),Op("spreadsheet","tsv"),Op("spreadsheet","pdf"),
        Op("epub","pdf"),Op("epub","docx","DOCX (Pages compatible)"),Op("epub","txt"),Op("epub","md"),Op("epub","html")
    });
    private static OperationDescriptor Op(string family,string format,string? label=null)=>new(family+".convert."+format,label??format.ToUpperInvariant(),OperationMode.Convert,"."+format,false,false);
    public bool CanExecute(OperationRequest request)
    {
        if(request.Editor is not null||request.Inputs.Count!=1||!Operations.Any(o=>o==request.Operation)||!DropValidation.Accepts(request.Inputs,OperationMode.Convert))return false;
        string extension=Path.GetExtension(request.Inputs[0]);string family=request.Operation.Id.Split('.')[0];
        return family switch {"document"=>Documents.Contains(extension,StringComparer.OrdinalIgnoreCase),"presentation"=>Presentations.Contains(extension,StringComparer.OrdinalIgnoreCase),"spreadsheet"=>Spreadsheets.Contains(extension,StringComparer.OrdinalIgnoreCase),"epub"=>extension.Equals(".epub",StringComparison.OrdinalIgnoreCase),_=>false};
    }
    public async Task VerifyAsync(CancellationToken ct)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("The offline document pack requires Windows.");
        RequirePack();string scope=Path.Combine(Path.GetTempPath(),"citrus-office-capability-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scope);
        try
        {
            string input=Path.Combine(scope,"probe.docx");
            using(var package=WordprocessingDocument.Create(input,WordprocessingDocumentType.Document))package.AddMainDocumentPart().Document=new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text("Citrus offline capability")))));
            foreach(string format in new[]{"pdf","txt"})
            {
                var result=await ExecuteAsync(new(Operations.Single(o=>o.Id=="document.convert."+format),[input],new(),scope),null,ct).ConfigureAwait(false);
                if(result.Outputs.Count!=1)throw new InvalidDataException("The offline document capability fixture did not produce one verified output.");
            }
        }
        finally{OfflineStagingFiles.DeletePrivateTree(scope);}
    }
    public Task<ConversionResult> ExecuteAsync(OperationRequest request,IProgress<ConversionProgress>? progress,CancellationToken ct)=>Task.Run(async()=>
    {
        ct.ThrowIfCancellationRequested();if(!CanExecute(request))throw new NotSupportedException("Choose a supported Office document, presentation, spreadsheet or unencrypted EPUB.");RequirePack();
        string scope=Path.Combine(Path.GetTempPath(),"citrus-office-export-"+Guid.NewGuid().ToString("N"));
        var published=new List<string>();bool publishedDirectory=false;
        Exception? primaryFailure=null;try
        {
            var capture=(await sources.CaptureAsync([request.Inputs[0]],ct).ConfigureAwait(false)).Single();var source=capture.Snapshot??throw new IOException(capture.Error);
            var staged=await sources.StageVerifiedAsync([source],scope,ct).ConfigureAwait(false);string input=staged[source.Id];
            string payload=Path.Combine(scope,"payload");Directory.CreateDirectory(payload);
            string format=request.Operation.OutputExtension![1..],family=request.Operation.Id.Split('.')[0];
            progress?.Report(new(null,"Converting…"));
            if(format is "png" or "jpg")
            {
                string interim=Path.Combine(scope,"pdf");Directory.CreateDirectory(interim);await OfficeAsync(input,"pdf",interim,scope,ct).ConfigureAwait(false);
                string pdf=OfflineStagingFiles.ValidatePayload(interim,"pdf").Single();ValidateOutput(pdf,"pdf");var renderer=new PdfEngine();string id=format=="jpg"?"pdf.jpg":"pdf.images";
                await renderer.ExecuteAsync(new(renderer.Operations.Single(o=>o.Id==id),[pdf],request.Options,payload),progress,ct).ConfigureAwait(false);
            }
            else if(family=="epub"||format is "md" or "epub"||format is "txt" or "html" && !Spreadsheets.Contains(Path.GetExtension(input),StringComparer.OrdinalIgnoreCase))
            {
                string readable=input;
                if(Path.GetExtension(input).Equals(".doc",StringComparison.OrdinalIgnoreCase)||Path.GetExtension(input).Equals(".pages",StringComparison.OrdinalIgnoreCase))
                { string interim=Path.Combine(scope,"docx");Directory.CreateDirectory(interim);await OfficeAsync(input,"docx",interim,scope,ct).ConfigureAwait(false);readable=Path.Combine(interim,"converted.docx"); }
                if(family=="epub"&&format=="pdf")
                { string docx=Path.Combine(scope,"intermediate.docx");await PandocAsync(readable,"docx",docx,scope,ct).ConfigureAwait(false);await OfficeAsync(docx,"pdf",payload,scope,ct).ConfigureAwait(false); }
                else await PandocAsync(readable,format,Path.Combine(payload,"converted."+format),scope,ct).ConfigureAwait(false);
            }
            else await OfficeAsync(input,format,payload,scope,ct).ConfigureAwait(false);
            var files=OfflineStagingFiles.ValidatePayload(payload,format);
            long total=0;foreach(string file in files){ct.ThrowIfCancellationRequested();total=checked(total+new FileInfo(file).Length);if(total>2L*1024*1024*1024)throw new InvalidDataException("The conversion exceeds the 2-GiB output limit.");ValidateOutput(file,format);}
            string destination=request.DestinationDirectory??Path.GetDirectoryName(source.Path)!;string stem=Path.GetFileNameWithoutExtension(source.Path)+"-converted";
            // A multi-sheet/page result is one atomic directory publication.
            if(files.Length>1)
            {
                publishedDirectory=true;
                published.Add(await writer.WriteDirectoryAsync(Path.Combine(destination,stem),async(path,token)=>{foreach(string file in files)await CopyAsync(file,Path.Combine(path,Path.GetFileName(file)),token).ConfigureAwait(false);},ct).ConfigureAwait(false));
            }
            else published.Add(await writer.WriteFileAsync(Path.Combine(destination,stem+"."+format),(path,token)=>CopyAsync(files[0],path,token),ct).ConfigureAwait(false));
            ct.ThrowIfCancellationRequested();progress?.Report(new(1,"Converted copy ready"));
            return new ConversionResult(published,Warnings:["Office layout and font substitution are best effort. Review the converted copy; external resources and macros were not executed."]);
        }
        catch(Exception error)
        {
            primaryFailure=error;List<Exception>? rollbackFailures=null;
            foreach(string path in published)
            {
                try{if(publishedDirectory)OfflineStagingFiles.DeletePrivateTree(path);else File.Delete(path);}
                catch(Exception failure)when(failure is IOException or UnauthorizedAccessException){(rollbackFailures??=[]).Add(failure);}
            }
            if(rollbackFailures is not null)throw new AggregateException("The document conversion failed and an owned published copy could not be removed.",new[]{error}.Concat(rollbackFailures));
            throw;
        }
        finally
        {
            try{OfflineStagingFiles.DeletePrivateTree(scope);}
            catch(Exception cleanup)when(primaryFailure is not null && cleanup is IOException or UnauthorizedAccessException){primaryFailure.Data["CleanupFailure"]=cleanup.Message;}
        }
    },ct);
    private async Task OfficeAsync(string input,string format,string output,string scope,CancellationToken ct)
    {
        string request=Path.Combine(scope,"office-request-"+Guid.NewGuid().ToString("N")+".json");
        await File.WriteAllTextAsync(request,JsonSerializer.Serialize(new {Input=input,Format=format,OutputDirectory=output}),new UTF8Encoding(false),ct).ConfigureAwait(false);
        var result=await OfflineToolRunner.RunAsync(Path.Combine(pack,"libreoffice","program","python.exe"),[Path.Combine(pack,"office-worker.py"),request],pack,scope,TimeSpan.FromMinutes(2),ct).ConfigureAwait(false);
        if(result.ExitCode!=0)throw new InvalidDataException($"The isolated Office conversion failed with exit code {result.ExitCode} (0x{unchecked((uint)result.ExitCode):X8}). "+result.Log);
    }
    private async Task PandocAsync(string input,string format,string output,string scope,CancellationToken ct)
    {
        string target=format switch {"txt"=>"plain","md"=>"gfm","html"=>"html5","epub"=>"epub3",_=>format};
        var args=new List<string>{"--sandbox","--standalone","--to",target,"--output",output,input};
        if(format=="html")args.InsertRange(0,["--embed-resources"]);
        var result=await OfflineToolRunner.RunAsync(Path.Combine(pack,"pandoc","pandoc-3.12.1","pandoc.exe"),args,pack,scope,TimeSpan.FromMinutes(2),ct).ConfigureAwait(false);
        if(result.ExitCode!=0)throw new InvalidDataException($"The isolated document conversion failed with exit code {result.ExitCode} (0x{unchecked((uint)result.ExitCode):X8}). "+result.Log);
    }
    private void RequirePack()
    {foreach(string file in RequiredPackFiles)if(!File.Exists(Path.Combine(pack,file)))throw new NotSupportedException("Document tools are missing. Repair Citrus.");}
    private static async Task CopyAsync(string source,string destination,CancellationToken ct)
    {SafeEditorFiles.RejectLinks(source);await using var input=File.OpenRead(source);await using var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None);await input.CopyToAsync(output,ct).ConfigureAwait(false);}
    private static void ValidateOutput(string path,string format)
    {
        SafeEditorFiles.RejectLinks(path);
        if(new FileInfo(path).Length==0)throw new InvalidDataException("The converter returned an empty file.");
        switch(format)
        {
            case "pdf":using(var pdf=UglyToad.PdfPig.PdfDocument.Open(path))if(pdf.IsEncrypted||pdf.NumberOfPages==0)throw new InvalidDataException("The saved PDF is invalid.");break;
            case "docx":using(var word=WordprocessingDocument.Open(path,false))if(word.MainDocumentPart?.Document?.Body is null)throw new InvalidDataException("The saved DOCX is invalid.");break;
            case "ods":using(var zip=ZipFile.OpenRead(path))if(zip.GetEntry("content.xml")is null)throw new InvalidDataException("The saved spreadsheet is invalid.");break;
            case "epub":using(var zip=ZipFile.OpenRead(path))if(zip.GetEntry("mimetype")is null||zip.GetEntry("META-INF/container.xml")is null)throw new InvalidDataException("The saved EPUB is invalid.");break;
            case "png":case "jpg":using(var image=new ImageMagick.MagickImage(path))if(image.Width==0||image.Height==0)throw new InvalidDataException("The saved page image is invalid.");break;
        }
    }
}
