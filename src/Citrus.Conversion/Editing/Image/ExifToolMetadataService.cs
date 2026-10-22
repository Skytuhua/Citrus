using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Citrus.Core.Editing;

namespace Citrus.Conversion.Editing.Image;

/// <summary>Typed ExifTool adapter. Reads do not decode/reencode pixels; configuration and arbitrary tag commands are disabled.</summary>
public sealed class ExifToolMetadataService(string executable):IMetadataService
{
    public string Executable{get;}=Path.GetFullPath(executable);
    public bool Available=>File.Exists(Executable);
    private static readonly HashSet<string> WritableKeys=new(StringComparer.Ordinal){"Artist","Copyright","ImageDescription","UserComment","Make","Model","DateTimeOriginal","CreateDate","ModifyDate","Title","Description","Creator","Subject","Rights","Comment","Author","Keywords","Label","Rating","Software","LensModel","LensMake","CopyrightNotice","Caption-Abstract","Headline","City","Country-PrimaryLocationName","Province-State","Credit","Source","OwnerName","SerialNumber","CameraSerialNumber","CameraOwnerName"};
    private static bool WritableScope(string scope)=>scope is "IFD0" or "ExifIFD" or "PNG" or "IPTC" or "QuickTime" or "Keys" or "ItemList" or "ID3v2_3" or "ID3v2_4" or "Vorbis" ||scope.StartsWith("XMP-",StringComparison.Ordinal);
    private static bool TechnicalScope(string scope)=>scope is "File" or "System" or "ExifTool" or "Composite"||scope.StartsWith("ICC",StringComparison.Ordinal);
    private static bool CanRemove(string scope,string key)=>!TechnicalScope(scope)
        && (scope is "IFD0" or "ExifIFD" or "GPS" or "InteropIFD" or "IPTC" or "Photoshop" or "MakerNotes" or "JUMBF" ||scope.StartsWith("XMP-",StringComparison.Ordinal)||WritableKeys.Contains(key))
        && key is not("Orientation" or "ExifImageWidth" or "ExifImageHeight" or "ImageWidth" or "ImageHeight" or "BitsPerSample" or "Compression" or "PhotometricInterpretation" or "StripOffsets" or "RowsPerStrip" or "StripByteCounts" or "SamplesPerPixel" or "XResolution" or "YResolution" or "ResolutionUnit" or "YCbCrPositioning" or "ColorSpace" or "ExifVersion" or "FlashpixVersion" or "ComponentsConfiguration" or "InteropIndex" or "InteropVersion");
    public async Task<ImmutableArray<MetadataField>> ReadAsync(SourceSnapshot source,CancellationToken ct)
    {
        string scope=Path.Combine(Path.GetTempPath(),"citrus-metadata-read-"+Guid.NewGuid().ToString("N"));
        try
        {
            var paths=await new SourceSnapshotService().StageVerifiedAsync([source],scope,ct).ConfigureAwait(false);
            var fields=await ReadPathAsync(paths[source.Id],ct).ConfigureAwait(false);
            // ExifTool inspects the verified private copy. Filesystem rows describe the
            // user's captured source, never the generated staging filename/directory.
            return [..fields.Where(f=>f.Scope!="System"||f.Key is "FileName" or "Directory" or "FileSize" or "FileModifyDate").Select(f=>f.Scope=="System"?f with{Value=f.Key switch{
                "FileName"=>new TextValue(Path.GetFileName(source.Path)),"Directory"=>new TextValue(Path.GetDirectoryName(source.Path)??""),
                "FileModifyDate"=>new TextValue(new DateTime(source.LastWriteUtcTicks,DateTimeKind.Utc).ToLocalTime().ToString("yyyy:MM:dd HH:mm:sszzz",CultureInfo.InvariantCulture)),_=>f.Value}}:f)];
        }
        finally{if(Directory.Exists(scope))Directory.Delete(scope,true);}
    }
    public async Task<ImmutableArray<MetadataField>> ReadPathAsync(string path,CancellationToken ct)
    {
        string json=await RunAsync(["-j","-G1:4","-a","-s","-n","-charset","filename=UTF8","--",Path.GetFullPath(path)],ct).ConfigureAwait(false);
        using var document=JsonDocument.Parse(json,new JsonDocumentOptions{MaxDepth=32});
        if(document.RootElement.ValueKind!=JsonValueKind.Array||document.RootElement.GetArrayLength()!=1)throw new InvalidDataException("Metadata reader returned an invalid document.");
        var properties=document.RootElement[0].EnumerateObject().ToArray();string? format=properties.FirstOrDefault(p=>p.Name.EndsWith(":FileType",StringComparison.Ordinal)).Value.ValueKind==JsonValueKind.String?properties.First(p=>p.Name.EndsWith(":FileType",StringComparison.Ordinal)).Value.GetString():null;
        bool writableFormat=format is "JPEG" or "PNG" or "WEBP" or "TIFF" or "GIF" or "AVIF" or "HEIC" or "HEIF" or "MP4" or "MOV" or "M4A" or "MP3" or "FLAC" or "OGG" or "WAV" or "PDF";
        var fields=ImmutableArray.CreateBuilder<MetadataField>();
        foreach(var property in properties)
        {
            ct.ThrowIfCancellationRequested();if(fields.Count>=4096)throw new NotSupportedException("This file exceeds the 4096-field metadata editing budget.");
            if(property.Name=="SourceFile")continue;int colon=property.Name.LastIndexOf(':');if(colon<1)continue;
            string scope=property.Name[..colon],key=property.Name[(colon+1)..];if(key is "Warning" or "Error")continue;
            bool addressable=Regex.IsMatch(scope,@"^[A-Za-z0-9_-]+$")&&Regex.IsMatch(key,@"^[A-Za-z0-9_-]+$");
            bool writable=writableFormat&&addressable&&WritableScope(scope)&&WritableKeys.Contains(key)&&property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number;
            MetadataValue value=property.Value.ValueKind switch{JsonValueKind.Number when property.Value.TryGetInt64(out long number)=>new IntegerValue(number),JsonValueKind.Number=>new RealValue(property.Value.GetDouble()),JsonValueKind.String=>new TextValue(property.Value.GetString()??""),_=>new TextValue(property.Value.GetRawText())};
            fields.Add(new(scope,key,Regex.Replace(key,"(?<=[a-z])(?=[A-Z])"," "),value,writable,writableFormat&&addressable&&CanRemove(scope,key)));
        }
        return fields.ToImmutable();
    }
    public ImmutableArray<ValidationIssue> ValidateEdits(ImmutableArray<MetadataField> fields,ImmutableArray<MetadataEdit> edits)
    {
        var issues=ImmutableArray.CreateBuilder<ValidationIssue>();var used=new HashSet<(string,string)>();
        foreach(var edit in edits)
        {
            var found=fields.Where(f=>f.Scope==edit.Scope&&f.Key==edit.Key).ToArray();
            if(!used.Add((edit.Scope,edit.Key))||found.Length!=1||!Regex.IsMatch(edit.Scope,@"^[A-Za-z0-9_-]+$")||!Regex.IsMatch(edit.Key,@"^[A-Za-z0-9_-]+$")){issues.Add(new("Metadata","Unknown, ambiguous or repeated metadata field."));continue;}
            if(edit.Action==MetadataEditAction.Set? !found[0].Writable||edit.Value is null:edit.Action!=MetadataEditAction.Remove||!found[0].Removable||edit.Value is not null){issues.Add(new("Metadata",found[0].DisplayName+" cannot be changed this way."));continue;}
            if(edit.Value is {} value&&Text(value).Length>65536)issues.Add(new("Metadata","Metadata values must be at most 65536 characters."));
            if(edit.Value is RealValue real&&!double.IsFinite(real.Value))issues.Add(new("Metadata","Metadata must be finite."));
        }
        return issues.ToImmutable();
    }
    public async Task ApplyAsync(string stagedPath,ImmutableArray<MetadataEdit> edits,CancellationToken ct,bool removeAll=false)
    {
        var before=await ReadPathAsync(stagedPath,ct).ConfigureAwait(false);var issues=ValidateEdits(before,edits);if(!issues.IsEmpty)throw new ArgumentException(string.Join(" ",issues.Select(i=>i.Message)));
        var args=new List<string>{"-overwrite_original","-charset","filename=UTF8"};
        if(removeAll)args.AddRange(["-all=","-tagsFromFile",Path.GetFullPath(stagedPath),"-ICC_Profile","-Orientation"]);
        string? importScope=null;
        try
        {
            var sets=edits.Where(e=>e.Action==MetadataEditAction.Set).ToArray();
            if(sets.Length>0)
            {
                importScope=Path.Combine(Path.GetTempPath(),"citrus-metadata-values-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(importScope);
                var values=new Dictionary<string,object>{{"SourceFile","*"}};
                foreach(var edit in sets)values[edit.Scope+":"+edit.Key]=edit.Value switch{IntegerValue number=>number.Value,RealValue number=>number.Value,_=>Text(edit.Value!)};
                string json=Path.Combine(importScope,"values.json");await File.WriteAllBytesAsync(json,JsonSerializer.SerializeToUtf8Bytes(new[]{values}),ct).ConfigureAwait(false);args.Add("-json="+json);
            }
            foreach(var edit in edits.Where(e=>e.Action==MetadataEditAction.Remove))args.Add("-"+edit.Scope+":"+edit.Key+"=");
            args.Add("--");args.Add(Path.GetFullPath(stagedPath));if(edits.IsEmpty&&!removeAll)return;
            _=await RunAsync([..args],ct).ConfigureAwait(false);
        }
        finally{if(importScope is not null&&Directory.Exists(importScope))Directory.Delete(importScope,true);}
        var after=await ReadPathAsync(stagedPath,ct).ConfigureAwait(false);
        foreach(var edit in edits)
        {
            var fields=after.Where(f=>f.Scope==edit.Scope&&f.Key==edit.Key).ToArray();
            if(edit.Action==MetadataEditAction.Remove?fields.Length!=0:fields.Length!=1||Text(fields[0].Value)!=Text(edit.Value!))throw new InvalidDataException("Could not verify saved metadata: "+edit.Key);
        }
        if(removeAll)
        {
            var remaining=after.Where(f=>f.Removable).Select(f=>f.DisplayName).Take(6).ToArray();
            if(remaining.Length>0)throw new InvalidDataException("The format retained removable metadata: "+string.Join(", ",remaining));
        }
        else foreach(var field in before.Where(f=>f.Writable&&!edits.Any(e=>e.Scope==f.Scope&&e.Key==f.Key)))
            if(!after.Any(f=>f.Scope==field.Scope&&f.Key==field.Key&&Text(f.Value)==Text(field.Value)))throw new InvalidDataException("Saving changed an unrelated metadata field: "+field.DisplayName);
    }
    public static string Text(MetadataValue value)=>value switch{TextValue t=>t.Value,IntegerValue i=>i.Value.ToString(CultureInfo.InvariantCulture),RealValue r=>r.Value.ToString("R",CultureInfo.InvariantCulture),RationalValue r=>$"{r.Numerator}/{r.Denominator}",CaptureTimeValue d=>d.Local.ToString("yyyy:MM:dd HH:mm:ss",CultureInfo.InvariantCulture)+(d.Offset?.ToString(@"hh\:mm",CultureInfo.InvariantCulture)??""),GpsValue g=>$"{g.Latitude.ToString(CultureInfo.InvariantCulture)}, {g.Longitude.ToString(CultureInfo.InvariantCulture)}",_=>throw new ArgumentException("Unsupported metadata value.")};
    private async Task<string> RunAsync(string[] args,CancellationToken ct)
    {
        if(!Available)throw new FileNotFoundException("Metadata tools are missing. Reinstall Citrus.",Executable);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var start=new ProcessStartInfo(Executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true,StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=Path.GetDirectoryName(Executable)!};
        start.Environment["LC_ALL"]="C";start.Environment["LC_CTYPE"]="C";start.Environment["LANG"]="C";start.ArgumentList.Add("-config");start.ArgumentList.Add("");start.ArgumentList.Add("-@");start.ArgumentList.Add("-");
        using var process=Process.Start(start)??throw new IOException("Could not start the metadata reader.");
        var stdout=ReadBounded(process.StandardOutput,16*1024*1024,timeout.Token);var stderr=ReadBounded(process.StandardError,65536,timeout.Token);
        try
        {
            // UTF-8 argument-file stdin bypasses Perl's Windows codepage conversion.
            // User values travel in JSON, so newlines and @/$ never become argument syntax.
            foreach(string arg in args){if(arg.Contains('\r')||arg.Contains('\n'))throw new ArgumentException("Metadata options and paths cannot contain line breaks.");await process.StandardInput.WriteLineAsync(arg.AsMemory(),timeout.Token).ConfigureAwait(false);}
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);string output=await stdout.ConfigureAwait(false),error=await stderr.ConfigureAwait(false);if(process.ExitCode!=0)throw new InvalidDataException("Metadata operation failed: "+error[..Math.Min(error.Length,2048)]);return output;
        }
        catch{if(!process.HasExited)process.Kill(true);await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);throw;}
    }
    private static async Task<string> ReadBounded(StreamReader reader,int maximum,CancellationToken ct)
    {
        var result=new StringBuilder();char[] buffer=new char[4096];int count;while((count=await reader.ReadAsync(buffer,ct).ConfigureAwait(false))>0){if(result.Length+count>maximum)throw new InvalidDataException("Metadata output exceeded its memory budget.");result.Append(buffer,0,count);}return result.ToString();
    }
}
