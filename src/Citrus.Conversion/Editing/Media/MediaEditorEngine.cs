using System.Collections.Immutable;
using System.Globalization;
using Citrus.Core;
using Citrus.Core.Editing;
using ImageMagick;

namespace Citrus.Conversion.Editing.Media;

/// <summary>Typed media editor admission and verified export. All native work uses captured private sources.</summary>
public sealed partial class MediaEditorEngine(MediaProcess process,MediaCapabilities capabilities,OutputWriter writer) : IConversionEngine,IEditorAdmissionInspector
{
    private static readonly string[] VideoExtensions=[".mp4",".mov",".m4v",".mkv",".webm",".avi",".wmv",".gif"];
    private static readonly string[] AudioExtensions=[".wav",".mp3",".m4a",".aac",".flac",".ogg",".opus",".aif",".aiff",".wma"];
    private static readonly (string Id,string Label)[] Tools=[("video.compress","Compress"),("video.metadata","Metadata"),("video.mute","Mute"),("video.trim","Trim"),("video.speed","Speed"),("video.snapshot","Snapshot"),("video.split","Split"),("video.redact","Redact"),("video.resize","Resize"),("audio.compress","Compress"),("audio.metadata","Metadata"),("audio.normalize","Normalize"),("audio.visualizer","Visualize"),("audio.trim","Trim"),("audio.channels","Channels"),("audio.bleep","Bleep")];
    public MediaProcess Process=>process;
    public MediaCapabilities Capabilities=>capabilities;
    public ISourceSnapshotService Sources {get;}=new SourceSnapshotService();
    public IReadOnlyList<OperationDescriptor> Operations {get;}=Tools.Where(t=>Supported(t.Id,capabilities)).Select(t=>new OperationDescriptor(t.Id,t.Label,OperationMode.Tools,t.Id=="video.snapshot"?".png":t.Id.EndsWith("compress",StringComparison.Ordinal)||t.Id.EndsWith("metadata",StringComparison.Ordinal)||t.Id=="video.split"?null:t.Id.StartsWith("video.",StringComparison.Ordinal)||t.Id=="audio.visualizer"?".mp4":".wav",false,true)).ToArray();
    public EditorRouteRegistry Routes {get;}=new(Tools.Where(t=>Supported(t.Id,capabilities)).Select(t=>new EditorRoute(t.Id,t.Label,EditorSubmissionShape.PerRoot)));
    private static bool Supported(string id,MediaCapabilities c)
    {
        bool video=id.StartsWith("video.",StringComparison.Ordinal)||id=="audio.visualizer";
        if(!c.Encoders.Contains("pcm_s16le")||!c.Muxers.Contains("s16le")||!c.Filters.Contains("atrim"))return false;
        if(video&&(!c.Encoders.Contains("mpeg4")||!c.Muxers.Contains("mp4")||!c.Encoders.Contains("rawvideo")||!c.Muxers.Contains("rawvideo")))return false;
        return id switch
        {
            "audio.compress"=>c.Encoders.Contains("libmp3lame")&&c.Muxers.Contains("mp3"),
            "audio.channels"=>c.Filters.Contains("pan")&&c.Filters.Contains("alimiter"),
            "audio.bleep"=>c.Filters.Contains("aeval"),
            "audio.normalize"=>c.Filters.Contains("loudnorm"),
            "audio.visualizer"=>new[]{"showwaves","showspectrum","showwavespic","format"}.All(c.Filters.Contains),
            "video.redact"=>new[]{"drawbox","gblur","overlay","split"}.All(c.Filters.Contains),
            "video.resize"=>c.Filters.Contains("scale"),
            _=>true
        };
    }
    public bool CanExecute(OperationRequest request)
    {
        if(!Operations.Any(o=>o.Id==request.Operation.Id)||request.Inputs.Count!=1||!DropValidation.Accepts(request.Inputs,OperationMode.Tools)) return false;
        bool video=request.Operation.Id.StartsWith("video.",StringComparison.Ordinal);
        if(!(video?VideoExtensions:AudioExtensions).Contains(Path.GetExtension(request.Inputs[0]),StringComparer.OrdinalIgnoreCase)) return false;
        if(!capabilities.Encoders.Contains(video||request.Operation.Id=="audio.visualizer"?"mpeg4":"pcm_s16le")) return false;
        if(request.Editor is null) return true;
        try { ValidateShape(request); return true; } catch(Exception error) when(error is ArgumentException or NotSupportedException) {return false;}
    }
    public async Task<MediaSourceInfo> InspectAsync(SourceSnapshot source,CancellationToken ct)
    {
        await using var probe=new MediaProbe(process,Sources,Path.Combine(Path.GetTempPath(),"citrus-media-inspect-"+Guid.NewGuid().ToString("N")));
        return await probe.InspectAsync(source,ct).ConfigureAwait(false);
    }
    public async Task<EditorDocument> CreateDocumentAsync(OperationDescriptor operation,SourceSnapshot source,OperationOptions options,string? destination,CancellationToken ct)
    {
        var info=await InspectAsync(source,ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return CreateDocument(operation,source,info,options,destination);
    }
    /// <summary>Creates a draft from the window's verified inspection session; export admission revalidates every source.</summary>
    public EditorDocument CreateDocument(OperationDescriptor operation,SourceSnapshot source,MediaSourceInfo info,OperationOptions options,string? destination)
    {
        if(info.SourceId!=source.Id)throw new ArgumentException("Inspection belongs to a different captured source.",nameof(info));
        if(!Operations.Any(o=>o.Id==operation.Id))throw new ArgumentException("This operation has no media editor.",nameof(operation));
        bool video=operation.Id.StartsWith("video.",StringComparison.Ordinal);
        if(video&&info.Video.IsEmpty||!video&&info.Audio.IsEmpty) throw new NotSupportedException("The selected file does not contain the required stream.");
        var clip=new MediaClip(Guid.NewGuid(),source.Id,video?info.Video[0].Index:null,info.Audio.FirstOrDefault()?.Index,new(new(0,1),info.Duration),operation.Id=="video.speed"?options.Speed:1);
        EditorRecipe recipe;
        string format=video?"mp4":"wav";
        if(video)
        {
            if(operation.Id!="video.metadata"&&info.Video[0].IsHdr) throw new NotSupportedException("HDR visual editing requires a validated tone-map or preservation pipeline.");
            var size=info.Video[0].DisplaySize;
            if(operation.Id is not "video.metadata" and not "video.snapshot")size=new(Math.Max(2,size.Width/2*2),Math.Max(2,size.Height/2*2));
            if(operation.Id=="video.resize")size=ResizeCanvas(size,options);
            RationalTime? snapshot=operation.Id=="video.snapshot"?new(0,1):null;
            ImmutableArray<RationalTime> splits=operation.Id=="video.split"?[MediaTime.Div(info.Duration,new(2,1))]:[];
            recipe=new VideoRecipe([clip],null,size,ImageFit.Contain,operation.Id=="video.mute",[],splits,snapshot,[]);
            if(snapshot is not null) format="png";
        }
        else
        {
            var stream=info.Audio[0]; var names=stream.Channels;
            var matrix=new ChannelMatrix(names,names,Enumerable.Range(0,names.Length*names.Length).Select(i=>i/names.Length==i%names.Length?1d:0d).ToImmutableArray(),false);
            var audio=new AudioRecipe([clip],stream.SampleRate,matrix,[],operation.Id=="audio.normalize"?new(options.NormalizeLufs,11,-1.5):null,[]);
            recipe=audio;
            if(operation.Id=="audio.compress") format="mp3";
            if(operation.Id=="audio.visualizer") {recipe=new VisualizerRecipe(audio,VisualizerMode.Waveform,new(1920,1080),30,new(20,23,26,255),new(255,85,0,255),null,true);format="mp4";}
        }
        string suffix=operation.Id[(operation.Id.IndexOf('.')+1)..];
        if(operation.Id.EndsWith("metadata",StringComparison.Ordinal))format=Path.GetExtension(source.Path).TrimStart('.').ToLowerInvariant();
        return new(1,0,[source],[],recipe,new(format,destination,Path.GetFileNameWithoutExtension(source.Path)+"-"+suffix,operation.Id.EndsWith("compress",StringComparison.Ordinal)?new ReductionGoal(50):new QualityGoal(options.Quality),operation.Id.EndsWith("metadata",StringComparison.Ordinal)?MetadataPolicy.PreserveSupported:MetadataPolicy.StripAll,new(0,0,0,255)));
    }
    private static SizeI ResizeCanvas(SizeI source,OperationOptions options)
    {
        if(options.Width is <2 or >8190||options.Height is <2 or >8190)
            throw new ArgumentException("Video dimensions must be between 2 and 8190 pixels.");
        if(options.Width is null&&options.Height is null)return source;
        double width=options.Width??source.Width,height=options.Height??source.Height;
        if(options.PreserveAspectRatio)
        {
            double scale=options.Width is null?height/source.Height:options.Height is null?width/source.Width:Math.Min(width/source.Width,height/source.Height);
            width=source.Width*scale;height=source.Height*scale;
        }
        if(width>8190||height>8190)throw new ArgumentException("Video dimensions exceed 8190 pixels.");
        return new(Math.Max(2,(int)Math.Floor(width/2)*2),Math.Max(2,(int)Math.Floor(height/2)*2));
    }
    public async Task<ImmutableArray<ValidationIssue>> ValidateAsync(OperationRequest request,CancellationToken ct)
    {
        try
        {
            ValidateShape(request);
            await using var probe=new MediaProbe(process,Sources,Path.Combine(Path.GetTempPath(),"citrus-media-admit-"+Guid.NewGuid().ToString("N")));
            foreach(var source in request.Editor!.Sources) await probe.InspectAsync(source,ct).ConfigureAwait(false);
            ValidateSources(request.Editor,probe,request.Operation.Id);
            return [];
        }
        catch(Exception error) when(error is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) {return [new("Media",error.Message)];}
    }
    public async Task<ConversionResult> ExecuteAsync(OperationRequest request,IProgress<ConversionProgress>? progress,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); ValidateShape(request);
        string scope=Path.Combine(Path.GetTempPath(),"citrus-media-export-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scope);
        try
        {
            await using var probe=new MediaProbe(process,Sources,Path.Combine(scope,"sources"));
                foreach(var source in request.Editor!.Sources) await probe.InspectAsync(source,ct).ConfigureAwait(false);
                ValidateSources(request.Editor!,probe,request.Operation.Id);
                progress?.Report(new(null,"Preparing edited media"));
                var staged=await RenderExportAsync(request.Editor!,request.Operation.Id,scope,probe,progress,ct).ConfigureAwait(false);
            var publisher=new EditorOutputPublisher(writer,new(),new MediaEditorPayloadValidator(process,probe));
            return await publisher.PublishAsync(request,staged,scope,ct).ConfigureAwait(false);
        }
        finally {if(Directory.Exists(scope)) Directory.Delete(scope,true);}
    }
    public async Task<PreviewFrame> RenderPreviewAsync(EditorDocument document,PreviewRequest request,CancellationToken ct)
    {
        await using var probe=new MediaProbe(process,Sources,Path.Combine(Path.GetTempPath(),"citrus-media-frame-"+Guid.NewGuid().ToString("N")));
        foreach(var source in document.Sources) await probe.InspectAsync(source,ct).ConfigureAwait(false);
        var previewDocument=document with {Export=document.Export with {Compression=new QualityGoal(85)}};
        var plan=new MediaGraphCompiler(probe,capabilities).Compile(previewDocument,RenderPurpose.Preview,request,probe.StagedSources);
        return await new MediaBinaryDecoder(process).DecodeFrameAsync(plan,ct).ConfigureAwait(false);
    }
    private async Task<StagedExport> RenderExportAsync(EditorDocument doc,string operationId,string directory,MediaProbe probe,IProgress<ConversionProgress>? progress,CancellationToken ct)
    {
        string payload=Path.Combine(directory,"payload"); Directory.CreateDirectory(payload);
        long original=doc.Sources.Where(s=>EditorRecipeReferences.Roots(doc.Recipe).Contains(s.Id)).Sum(s=>s.Length);
        long? target=doc.Export.Compression switch {ByteGoal b=>b.MaximumBytes,ReductionGoal r=>(long)decimal.Floor(original*(1-r.Percent/100)),_=>null};
        if(operationId.EndsWith("metadata",StringComparison.Ordinal))
        {
            string output=Path.Combine(payload,"metadata."+doc.Export.FormatId);
            var edits=doc.Recipe is VideoRecipe metadataVideo?metadataVideo.MetadataEdits:((AudioRecipe)doc.Recipe).MetadataEdits;
            using var container=await process.ProbeJsonAsync(["-show_streams","-show_chapters","-show_programs","-show_format","-of","json",..MediaProcess.InputArguments(probe.StagedSources[doc.Sources[0].Id])],2*1024*1024,ct).ConfigureAwait(false);
            MediaMetadataScopes.Validate(edits,container.RootElement);
            var mapping=MediaMetadataScopes.StreamMap(container.RootElement);
            string muxer=doc.Export.FormatId switch {"mkv"=>"matroska","wmv" or "wma"=>"asf","m4v"=>"mp4","m4a"=>"ipod","aac"=>"adts","aif"=>"aiff",_=>doc.Export.FormatId};
            var args=new List<string>(MediaProcess.InputArguments(probe.StagedSources[doc.Sources[0].Id]));args.AddRange(["-map","0"]);
            foreach(int chapterStream in MediaMetadataScopes.ChapterStreams(container.RootElement))args.AddRange(["-map","-0:"+N(chapterStream)]);
            args.AddRange(["-map_chapters","0","-c","copy"]);args.AddRange(MediaMetadataScopes.Arguments(edits,doc.Export.Metadata,0,mapping));
            if(doc.Export.Metadata!=MetadataPolicy.PreserveSupported&&container.RootElement.TryGetProperty("programs",out var programs))
                for(int i=0;i<programs.GetArrayLength();i++)args.AddRange(["-map_metadata:p:"+N(i),"-1"]);
            if(doc.Export.Metadata!=MetadataPolicy.PreserveSupported&&muxer is "mp4" or "mov" or "ipod")args.AddRange(["-empty_hdlr_name","true"]);
            args.AddRange(["-f",muxer,output]);await process.RunAsync(args,progress,ct).ConfigureAwait(false);
            long size=new FileInfo(output).Length;
            return new(ExportShape.File,[new(Path.GetFileName(output),size)],new(original,size,target,target is null||size<=target,1),[]);
        }
        var compiler=new MediaGraphCompiler(probe,capabilities);
        if(doc.Recipe is AudioRecipe audio)
        {
            var audition=doc with {Recipe=audio with {Loudness=null},Export=doc.Export with {Compression=new QualityGoal(85)}};
            var plan=compiler.Compile(audition,RenderPurpose.Preview,new(0,new(64,64),null,null,null),probe.StagedSources);
            string pcmPath=Path.Combine(directory,"edited.pcm");
            var pcm=await new MediaBinaryDecoder(process).DecodeExportAudioAsync(plan,pcmPath,ct).ConfigureAwait(false);
            MediaLoudnessMeasurement? before=null;
            string[] pcmInput=["-f","s16le","-ar",N(pcm.SampleRate),"-ac",N(pcm.Channels),"-i",pcm.Path];
            if(audio.Loudness is {} loudness)before=await MediaLoudnessMeasurement.MeasureAsync(process,pcmInput,loudness,ct).ConfigureAwait(false);
            var encoded=await EncodeCandidates(async(path,bitrate,token)=>
            {
                var args=new List<string>(pcmInput);
                args.AddRange(MediaProcess.InputArguments(probe.StagedSources[audio.Clips[0].SourceId]));
                args.AddRange(["-map","0:a:0"]);
                if(before is not null)args.AddRange(["-af",before.Filter(audio.Loudness!)+$",aresample={pcm.SampleRate},apad=whole_len={pcm.SampleFrames},atrim=end_sample={pcm.SampleFrames}"]);
                args.AddRange(AudioEncoding(doc.Export.FormatId,bitrate));
                args.AddRange(["-ar",N(pcm.SampleRate),"-ac",N(pcm.Channels)]);
                args.AddRange(MetadataArguments(audio.MetadataEdits,doc.Export.Metadata,1,AudioMuxer(doc.Export.FormatId)));
                args.AddRange(["-f",AudioMuxer(doc.Export.FormatId),path]);
                await process.RunAsync(args,progress,token).ConfigureAwait(false);
            },doc,payload,original,target,MediaEffectFilters.Seconds(plan.Duration),ct).ConfigureAwait(false);
            if(before is not null)
            {
                var after=await MediaLoudnessMeasurement.MeasureAsync(process,MediaProcess.InputArguments(Path.Combine(payload,encoded.Files[0].RelativePath)),audio.Loudness!,ct).ConfigureAwait(false);
                encoded=encoded with {Warnings=[$"Measured loudness {before.IntegratedLufs:0.0} → {after.IntegratedLufs:0.0} LUFS; true peak {after.TruePeakDbTp:0.0} dBTP; range {after.RangeLu:0.0} LU."]};
            }
            return encoded;
        }
        if(doc.Recipe is VisualizerRecipe visualizer) return await RenderVisualizerAsync(doc,visualizer,payload,probe,original,ct).ConfigureAwait(false);
        var video=(VideoRecipe)doc.Recipe;
        if(video.SnapshotAt is {} at)
        {
            var frame=await new MediaBinaryDecoder(process).DecodeSnapshotAsync(compiler.Compile(doc with {Export=doc.Export with {Compression=new QualityGoal(85)}},RenderPurpose.Preview,new(0,video.Canvas,at,null,null),probe.StagedSources,true),ct).ConfigureAwait(false);
            string path=Path.Combine(payload,"snapshot."+doc.Export.FormatId);
            using(var image=new MagickImage(frame.Bgra32.ToArray(),new PixelReadSettings((uint)frame.Size.Width,(uint)frame.Size.Height,StorageType.Char,PixelMapping.BGRA)))
                await image.WriteAsync(path,doc.Export.FormatId is "jpg" or "jpeg"?MagickFormat.Jpeg:doc.Export.FormatId=="webp"?MagickFormat.WebP:MagickFormat.Png,ct).ConfigureAwait(false);
            long bytes=new FileInfo(path).Length;
            return new(ExportShape.File,[new(Path.GetFileName(path),bytes)],new(original,bytes,target,target is null||bytes<=target,1),[]);
        }
        double duration=video.Clips.Sum(c=>MediaEffectFilters.Seconds(MediaTime.Sub(c.Range.End,c.Range.Start))/c.Speed);
        if(video.SplitAt.IsEmpty)
            return await EncodeCandidates((path,bitrate,token)=>EncodeVideo(doc,video,probe,path,bitrate,0,duration,progress,token),doc,payload,original,target,duration,ct).ConfigureAwait(false);
        var cuts=new[]{0d}.Concat(video.SplitAt.Select(MediaEffectFilters.Seconds)).Append(duration).ToArray();
        var outputs=ImmutableArray.CreateBuilder<StagedOutput>();
        for(int i=0;i<cuts.Length-1;i++)
        {
            string path=Path.Combine(payload,$"part-{i+1:000}.mp4");
            await EncodeVideo(doc,video,probe,path,null,cuts[i],cuts[i+1]-cuts[i],progress,ct).ConfigureAwait(false);
            outputs.Add(new(Path.GetFileName(path),new FileInfo(path).Length));
        }
        long total=outputs.Sum(o=>o.Bytes);
        return new(ExportShape.Directory,outputs.ToImmutable(),new(original,total,target,target is null||total<=target,1),[]);
    }
    private async Task<StagedExport> EncodeCandidates(Func<string,long?,CancellationToken,Task> encode,EditorDocument doc,string payload,long original,long? target,double seconds,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(600));
        long? bitrate=target is null?null:Math.Max(8000,(long)(target.Value*8/Math.Max(.001,seconds)*.90));
        long best=long.MaxValue,totalAllocated=0;
        for(int attempt=1;attempt<=(target is null?1:12);attempt++)
        {
            string path=Path.Combine(payload,"edited."+doc.Export.FormatId);
            File.Delete(path);
            await encode(path,bitrate,timeout.Token).ConfigureAwait(false);
            long actual=new FileInfo(path).Length; best=Math.Min(best,actual); totalAllocated=checked(totalAllocated+actual);
            if(target is null||actual<=target)
                return new(ExportShape.File,[new(Path.GetFileName(path),actual)],new(original,actual,target,true,attempt),[]);
            if(doc.Recipe is AudioRecipe&&doc.Export.FormatId is "wav" or "flac")break;
            if(totalAllocated>2L*1024*1024*1024) break;
            bitrate=Math.Max(8000,(long)(bitrate!.Value*target.Value/Math.Max(1,actual)*.90));
        }
        throw new InvalidOperationException($"Could not reach the requested size while preserving dimensions, duration and channels. Best measured size: {best:N0} bytes.");
    }
    private async Task EncodeVideo(EditorDocument doc,VideoRecipe video,MediaProbe probe,string path,long? bitrate,double start,double length,IProgress<ConversionProgress>? progress,CancellationToken ct)
    {
        if(video.Clips.Length!=1) throw new NotSupportedException("This editor exports one video source at a time.");
        var clip=video.Clips[0]; var info=probe.GetInfo(clip.SourceId); var stream=info.Video.First(s=>s.Index==clip.VideoStream);
        var filters=VideoCropEngine.DisplayFilters(stream);
        filters.Add($"trim=start={F(MediaEffectFilters.Seconds(clip.Range.Start))}:end={F(MediaEffectFilters.Seconds(clip.Range.End))}");
        filters.Add($"setpts=(PTS-STARTPTS)/{F(clip.Speed)}");
        if(video.Crop is {} crop) filters.Add($"crop={N((int)crop.Width)}:{N((int)crop.Height)}:{N((int)crop.X)}:{N((int)crop.Y)}:exact=1");
        filters.AddRange(MediaVideoEffects.Filters(video.Masks));
        filters.Add($"scale={video.Canvas.Width}:{video.Canvas.Height},setsar=1");
        var args=new List<string>{"-noautorotate","-display_rotation:v:0","0"};
        args.AddRange(MediaProcess.InputArguments(probe.StagedSources[clip.SourceId]));
        args.AddRange(["-map",$"0:{stream.Index}","-vf",string.Join(',',filters),"-c:v","mpeg4","-pix_fmt","yuv420p"]);
        if(bitrate is {} budget) args.AddRange(["-b:v",N(Math.Max(8000,budget-(video.Mute||info.Audio.IsEmpty?0:Math.Min(192000,budget/4))))]);
        else args.AddRange(["-q:v",F(Math.Max(2,31-(doc.Export.Compression as QualityGoal)?.Value*.29??6))]);
        if(!video.Mute&&clip.AudioStream is {} audio)
        {
            var a=info.Audio.First(s=>s.Index==audio);
            args.AddRange(["-map",$"0:{audio}","-af",$"atrim=start={F(MediaEffectFilters.Seconds(clip.Range.Start))}:end={F(MediaEffectFilters.Seconds(clip.Range.End))},asetpts=PTS-STARTPTS,"+Tempo(clip.Speed),"-c:a","aac","-b:a",N(bitrate is {} all?Math.Clamp(all/4,8000,192000):192000),"-ar",N(a.SampleRate),"-ac",N(a.Channels.Length)]);
        }
        else args.Add("-an");
        args.AddRange(MetadataArguments(video.MetadataEdits,doc.Export.Metadata,muxer:"mp4"));
        args.AddRange(["-ss",F(start),"-t",F(length),"-threads","2","-f","mp4",path]);
        await process.RunAsync(args,progress,ct).ConfigureAwait(false);
    }
    private async Task<StagedExport> RenderVisualizerAsync(EditorDocument doc,VisualizerRecipe visual,string payload,MediaProbe probe,long original,CancellationToken ct)
    {
        if(visual.BackgroundImageId is not null)throw new NotSupportedException("Image backgrounds are not available in this visualization preset.");
        var audioDoc=doc with {Recipe=visual.Audio,Export=doc.Export with {FormatId="wav",Compression=new QualityGoal(85)}};
        var graph=new MediaGraphCompiler(probe,capabilities).Compile(audioDoc,RenderPurpose.Preview,new(0,new(32,32),null,null,null),probe.StagedSources);
        string pcm=Path.Combine(Path.GetDirectoryName(payload)!,"visualizer.pcm");
        await new MediaBinaryDecoder(process).DecodeExportAudioAsync(graph,pcm,ct).ConfigureAwait(false);
        string fg=$"0x{visual.Foreground.R:X2}{visual.Foreground.G:X2}{visual.Foreground.B:X2}",bg=$"0x{visual.Background.R:X2}{visual.Background.G:X2}{visual.Background.B:X2}";
        string filter=visual.Mode switch {VisualizerMode.Spectrum=>$"showspectrum=s={visual.Size.Width}x{visual.Size.Height}:fps={visual.FramesPerSecond}:legend=disabled",VisualizerMode.StillWaveform=>$"showwavespic=s={visual.Size.Width}x{visual.Size.Height}:colors={fg}",_=>$"showwaves=s={visual.Size.Width}x{visual.Size.Height}:r={visual.FramesPerSecond}:mode=line:colors={fg}"};
        string output=Path.Combine(payload,"visualization."+doc.Export.FormatId);
        string composition=visual.Mode==VisualizerMode.Waveform?$"[0:a]{filter},format=rgba[wave];color=c={bg}:s={visual.Size.Width}x{visual.Size.Height}:r={visual.FramesPerSecond}:d={F(MediaEffectFilters.Seconds(graph.Duration))}[background];[background][wave]overlay=shortest=1:format=auto,format=bgra[image]":$"[0:a]{filter},format=bgra[image]";
        var args=new List<string>{"-f","s16le","-ar",N(visual.Audio.SampleRate),"-ac",N(visual.Audio.Channels.OutputChannels.Length),"-i",pcm,"-filter_complex",composition,"-map","[image]"};
        if(visual.Mode==VisualizerMode.StillWaveform)
        {
            using var memory=new MemoryStream();
            args.AddRange(["-frames:v","1","-c:v","rawvideo","-pix_fmt","bgra","-f","rawvideo","pipe:1"]);
            long count=await process.RunBinaryAsync(args,memory,checked(visual.Size.Width*visual.Size.Height*4L),ct).ConfigureAwait(false);
            if(count!=visual.Size.Width*visual.Size.Height*4L) throw new InvalidDataException("Visualizer returned incomplete pixels.");
            using var image=new MagickImage(memory.ToArray(),new PixelReadSettings((uint)visual.Size.Width,(uint)visual.Size.Height,StorageType.Char,PixelMapping.BGRA));
            image.BackgroundColor=new MagickColor(visual.Background.R,visual.Background.G,visual.Background.B,visual.Background.A); image.Alpha(AlphaAction.Remove);
            await image.WriteAsync(output,doc.Export.FormatId is "jpg" or "jpeg"?MagickFormat.Jpeg:doc.Export.FormatId=="webp"?MagickFormat.WebP:MagickFormat.Png,ct).ConfigureAwait(false);
        }
        else
        {
            args.AddRange(["-c:v","mpeg4","-pix_fmt","yuv420p","-q:v","4"]);
            if(visual.KeepAudio) args.AddRange(["-map","0:a","-c:a","aac","-b:a","192k"]); else args.Add("-an");
            args.AddRange(MetadataArguments([],doc.Export.Metadata,muxer:"mp4"));
            args.AddRange(["-threads","2","-f","mp4",output]);
            await process.RunAsync(args,null,ct).ConfigureAwait(false);
        }
        long bytes=new FileInfo(output).Length;
        return new(ExportShape.File,[new(Path.GetFileName(output),bytes)],new(original,bytes,null,true,1),[]);
    }
    private static void ValidateShape(OperationRequest request)
    {
        if(request.Editor is not {} doc||!EditorRecipeValidator.Validate(doc).IsEmpty||doc.Sources.Length!=1||!doc.Fonts.IsEmpty) throw new ArgumentException("A valid captured single-source media recipe is required.");
        _=EditorExportPolicy.Resolve(request);
        if(request.Operation.Id=="audio.bleep"&&doc.Recipe is AudioRecipe {Bleeps.IsEmpty:true}) throw new ArgumentException("Add at least one bleep range.");
        var edits=doc.Recipe switch {VideoRecipe v=>v.MetadataEdits,AudioRecipe a=>a.MetadataEdits,_=>[]};
        foreach(var edit in edits) if(!MediaMetadataScopes.Writable(edit.Scope,edit.Key)||edit.Value is not null and not TextValue) throw new ArgumentException("Only supported scoped text metadata tags can be edited.");
    }
    private static void ValidateSources(EditorDocument doc,MediaProbe probe,string operationId)
    {
        var clips=doc.Recipe switch {VideoRecipe v=>v.Clips,AudioRecipe a=>a.Clips,VisualizerRecipe v=>v.Audio.Clips,_=>throw new ArgumentException("Not a media recipe.")};
        foreach(var clip in clips)
        {
            var info=probe.GetInfo(clip.SourceId);
            if(clip.Range.End.CompareTo(info.Duration)>0) throw new ArgumentException("Clip ends outside its captured source.");
            if(clip.VideoStream is {} v&&!info.Video.Any(s=>s.Index==v)||clip.AudioStream is {} a&&!info.Audio.Any(s=>s.Index==a)) throw new ArgumentException("The selected stream does not belong to this source.");
        }
        bool metadataOnly=operationId.EndsWith("metadata",StringComparison.Ordinal);
        if(metadataOnly)
        {
            var source=probe.GetInfo(clips[0].SourceId);
            if(clips.Length!=1||clips[0].Range.Start!=new RationalTime(0,1)||clips[0].Range.End!=source.Duration||clips[0].Speed!=1||doc.Export.Compression is not QualityGoal||doc.Export.FormatId!=Path.GetExtension(doc.Sources[0].Path).TrimStart('.').ToLowerInvariant())throw new ArgumentException("Metadata-only saving must preserve the original container and full timeline.");
            if(doc.Recipe is VideoRecipe v&&(v.Crop is not null||v.Mute||!v.Masks.IsEmpty||!v.SplitAt.IsEmpty||v.SnapshotAt is not null))throw new ArgumentException("Metadata-only saving cannot include visual or timeline edits.");
            if(doc.Recipe is AudioRecipe a&&(!a.Bleeps.IsEmpty||a.Loudness is not null||a.SampleRate!=source.Audio[0].SampleRate||!a.Channels.InputChannels.SequenceEqual(a.Channels.OutputChannels)||a.Channels.Coefficients.Where((value,index)=>value!=(index/a.Channels.InputChannels.Length==index%a.Channels.InputChannels.Length?1d:0d)).Any()))throw new ArgumentException("Metadata-only saving cannot include sample processing.");
            return;
        }
        if(doc.Recipe is VideoRecipe video)
        {
            if(video.SnapshotAt is null&&(video.Canvas.Width%2!=0||video.Canvas.Height%2!=0||video.Canvas.Width>8190||video.Canvas.Height>8190)) throw new NotSupportedException("MP4 output requires even dimensions up to 8190 pixels.");
            var plane=probe.GetInfo(video.Clips[0].SourceId).Video[0].DisplaySize;
            if(video.Crop is {} crop&&(crop.X+crop.Width>plane.Width||crop.Y+crop.Height>plane.Height)) throw new ArgumentException("Crop must fit the source display plane.");
            if(probe.GetInfo(video.Clips[0].SourceId).Video[0].IsHdr) throw new NotSupportedException("HDR visual editing is not supported.");
            double maskWidth=video.Crop?.Width??plane.Width,maskHeight=video.Crop?.Height??plane.Height;
            foreach(var mask in video.Masks)foreach(var key in mask.Keys)
                if(key.Bounds.X+key.Bounds.Width>maskWidth||key.Bounds.Y+key.Bounds.Height>maskHeight)throw new ArgumentException("Redaction rectangles must fit the cropped display plane.");
            _=MediaVideoEffects.Filters(video.Masks);
        }
    }
    public static readonly IReadOnlySet<string> WritableTags=new HashSet<string>(["title","artist","album","album_artist","author","comment","description","genre","date","copyright","track"],StringComparer.OrdinalIgnoreCase);
    private static IEnumerable<string> MetadataArguments(ImmutableArray<MetadataEdit> edits,MetadataPolicy policy,int input=0,string? muxer=null)
    {
        foreach(string argument in MediaMetadataScopes.Arguments(edits,policy,input))yield return argument;
        if(policy!=MetadataPolicy.PreserveSupported&&muxer is "mp4" or "mov" or "ipod")
        {
            yield return "-empty_hdlr_name";
            yield return "true";
        }
    }
    private static string[] AudioEncoding(string format,long? bitrate)=>format switch
    {
        "wav"=>["-c:a","pcm_s16le"],"flac"=>["-c:a","flac"],"mp3"=>["-c:a","libmp3lame","-b:a",N(Math.Clamp(bitrate??192000,8000,320000))],"m4a" or "aac"=>["-c:a","aac","-b:a",N(Math.Clamp(bitrate??192000,8000,320000))],"ogg"=>["-c:a","libvorbis","-b:a",N(Math.Clamp(bitrate??192000,32000,320000))],_=>throw new NotSupportedException("Choose a supported audio output format.")
    };
    private static string AudioMuxer(string format)=>format switch {"m4a"=>"ipod","aac"=>"adts",_=>format};
    private static string Tempo(double speed) {var filters=new List<string>();while(speed>2){filters.Add("atempo=2");speed/=2;}while(speed<.5){filters.Add("atempo=.5");speed/=.5;}filters.Add("atempo="+F(speed));return string.Join(',',filters);}
    private static string F(double value)=>MediaEffectFilters.Number(value);
    private static string N(long value)=>value.ToString(CultureInfo.InvariantCulture);
}
