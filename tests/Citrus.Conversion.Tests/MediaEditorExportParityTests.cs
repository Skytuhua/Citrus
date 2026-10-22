using Citrus.Conversion.Editing.Media;
using Citrus.Core;
using Citrus.Core.Editing;
using ImageMagick;

namespace Citrus.Conversion.Tests;

public sealed class MediaEditorExportParityTests : IAsyncLifetime
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"citrus-media-export-parity-"+Guid.NewGuid().ToString("N"));
    private MediaProcess process=null!;
    private MediaEditorEngine engine=null!;
    private string video="",audio="";
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        process=new(Environment.GetEnvironmentVariable("CITRUS_TEST_FFMPEG")??"/usr/bin/ffmpeg",Environment.GetEnvironmentVariable("CITRUS_TEST_FFPROBE")??"/usr/bin/ffprobe");
        engine=new(process,await process.DiscoverCapabilitiesAsync(default),new());
        audio=Path.Combine(root,"tone.wav");
        MediaFixture.WriteWave(audio,Enumerable.Range(0,48000).Select(i=>(short)(6000*Math.Sin(i*2*Math.PI*440/48000))).ToArray());
        string raw=Path.Combine(root,"frames.bgra");
        using(var file=File.Create(raw)) for(int f=0;f<12;f++) for(int i=0;i<32*24;i++) file.Write([32,64,192,255]);
        video=Path.Combine(root,"clip.mp4");
        await process.RunAsync(["-f","rawvideo","-pixel_format","bgra","-video_size","32x24","-framerate","12","-i",raw,..MediaProcess.InputArguments(audio),"-c:v","mpeg4","-c:a","aac","-shortest",video],null,default);
    }
    public Task DisposeAsync() { Directory.Delete(root,true); return Task.CompletedTask; }
    private async Task<EditorDocument> Document(string id,string sourcePath)
    {
        var snapshot=(await engine.Sources.CaptureAsync([sourcePath],default)).Single().Snapshot!;
        return await engine.CreateDocumentAsync(engine.Operations.Single(o=>o.Id==id),snapshot,new(),root,default);
    }
    private Task<ConversionResult> Export(string id,string path,EditorDocument document)=>engine.ExecuteAsync(new(engine.Operations.Single(o=>o.Id==id),[path],new(),root,document),null,default);

    [Fact]
    public async Task Audio_export_streams_beyond_the_preview_PCM_cache_limit()
    {
        string input=Path.Combine(root,"long.flac");
        string silence=Path.Combine(root,"silence.wav");
        MediaFixture.WriteWave(silence,new short[48000]);
        const int seconds=2800;
        await process.RunAsync(["-stream_loop","-1",..MediaProcess.InputArguments(silence),"-t",seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),"-ac","2","-c:a","flac",input],null,default);
        var doc=await Document("audio.compress",input);
        doc=doc with {Export=doc.Export with {FormatId="flac",Compression=new QualityGoal(85)}};
        Assert.True((long)seconds*48000*2*2>MediaBinaryDecoder.MaximumPcmBytes);
        await using(var preview=new MediaProbe(process,engine.Sources,Path.Combine(root,"long-preview")))
        {
            await preview.InspectAsync(doc.Sources[0],default);
            var plan=new MediaGraphCompiler(preview,engine.Capabilities).Compile(doc,RenderPurpose.Preview,new(1,new(32,32),null,null,null),preview.StagedSources);
            string pcm=Path.Combine(root,"preview.pcm");
            await Assert.ThrowsAsync<NotSupportedException>(()=>new MediaBinaryDecoder(process).DecodeAudioAsync(plan,pcm,default));
            Assert.False(File.Exists(pcm));
        }
        string output=Assert.Single((await Export("audio.compress",input,doc)).Outputs);
        var info=await process.ProbeAsync(output,default);
        Assert.True(info.HasAudio);
        Assert.InRange(info.DurationSeconds,seconds-.01,seconds+.01);
    }

    [Fact]
    public async Task Mute_really_removes_audio_and_preserves_geometry()
    {
        var doc=await Document("video.mute",video);
        string output=Assert.Single((await Export("video.mute",video,doc)).Outputs);
        var info=await process.ProbeAsync(output,default);
        Assert.True(info.HasVideo); Assert.False(info.HasAudio); Assert.Equal(32,info.Width); Assert.Equal(24,info.Height);
    }
    [Fact]
    public async Task Typed_resize_exports_the_preview_canvas_and_preserves_duration_audio_and_original()
    {
        string originalHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(video)));
        var doc=await Document("video.resize",video);
        doc=doc with {Recipe=((VideoRecipe)doc.Recipe) with {Canvas=new(16,12)}};
        string output=Assert.Single((await Export("video.resize",video,doc)).Outputs);
        var reopened=await process.ProbeAsync(output,default);
        Assert.Equal(16,reopened.Width);Assert.Equal(12,reopened.Height);Assert.True(reopened.HasAudio);
        Assert.InRange(reopened.DurationSeconds,.95,1.1);
        Assert.Equal(originalHash,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(video))));
    }
    [Fact]
    public async Task Snapshot_is_actual_png_and_split_publishes_all_segments_together()
    {
        var shot=await Document("video.snapshot",video);
        string image=Assert.Single((await Export("video.snapshot",video,shot)).Outputs);
        using(var check=new MagickImage(image)) { Assert.Equal(MagickFormat.Png,check.Format); Assert.Equal(32u,check.Width); }
        var doc=await Document("video.split",video);
        doc=doc with {Recipe=((VideoRecipe)doc.Recipe) with {SplitAt=[new(1,2)]}};
        string directory=Assert.Single((await Export("video.split",video,doc)).Outputs);
        var parts=Directory.GetFiles(directory,"*.mp4"); Assert.Equal(2,parts.Length);
        foreach(string part in parts) Assert.InRange((await process.ProbeAsync(part,default)).DurationSeconds,.45,.65);
    }
    [Fact]
    public async Task Edited_audio_exports_the_same_channel_and_bleep_contract()
    {
        var doc=await Document("audio.bleep",audio);
        var recipe=(AudioRecipe)doc.Recipe;
        doc=doc with {Recipe=recipe with {Channels=new(["FC"],["FL","FR"],[1,0],false),Bleeps=[new(Guid.NewGuid(),new(new(1,4),new(3,4)),BleepMode.Silence,1000,-12,0,[0,1])]}};
        string output=Assert.Single((await Export("audio.bleep",audio,doc)).Outputs);
        string pcm=Path.Combine(root,"check.pcm");
        await process.RunAsync([..MediaProcess.InputArguments(output),"-c:a","pcm_s16le","-f","s16le",pcm],null,default);
        byte[] samples=await File.ReadAllBytesAsync(pcm);
        Assert.Equal(48000*4,samples.Length);
        Assert.All(samples.Skip(16000*4).Take(8000*4),b=>Assert.Equal(0,b));
    }
    [Fact]
    public async Task Unreachable_compression_and_changed_source_publish_nothing()
    {
        var doc=await Document("video.compress",video);
        doc=doc with {Export=doc.Export with {Compression=new ByteGoal(1),OutputStem="unreachable"}};
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Export("video.compress",video,doc));
        Assert.Empty(Directory.GetFiles(root,"unreachable*"));
        doc=await Document("video.mute",video);
        await File.AppendAllTextAsync(video,"changed");
        await Assert.ThrowsAsync<IOException>(()=>Export("video.mute",video,doc));
    }
    [Fact]
    public async Task Metadata_is_read_back_and_normalization_reports_measured_values()
    {
        var doc=await Document("video.metadata",video);
        doc=doc with {Recipe=((VideoRecipe)doc.Recipe) with {MetadataEdits=[new("format","title",MetadataEditAction.Set,new TextValue("A real title"))]}};
        string output=Assert.Single((await Export("video.metadata",video,doc)).Outputs);
        var source=(await engine.Sources.CaptureAsync([output],default)).Single().Snapshot!;
        Assert.Contains(await engine.ReadMetadataAsync(source,default),field=>field.Key=="title"&&field.Value is TextValue {Value:"A real title"});
        var normalization=await Document("audio.normalize",audio);
        string normalized=Assert.Single((await Export("audio.normalize",audio,normalization)).Outputs);
        var measurement=await MediaLoudnessMeasurement.MeasureAsync(process,MediaProcess.InputArguments(normalized),new(-16,11,-1.5),default);
        Assert.InRange(measurement.IntegratedLufs,-16.5,-15.5);
        Assert.True(measurement.TruePeakDbTp<=-1.4);
    }
    [Fact]
    public async Task Snapshot_retains_resolution_above_preview_limit_and_visualizer_is_real_media()
    {
        string wide=Path.Combine(root,"wide.mp4");
        await process.RunAsync([..MediaProcess.InputArguments(video),"-vf","scale=2050:24,setsar=1","-c:v","mpeg4","-an",wide],null,default);
        var snapshot=await Document("video.snapshot",wide);
        string output=Assert.Single((await Export("video.snapshot",wide,snapshot)).Outputs);
        using(var image=new MagickImage(output))Assert.Equal(2050u,image.Width);
        var visualization=await Document("audio.visualizer",audio);
        visualization=visualization with {Recipe=((VisualizerRecipe)visualization.Recipe) with {Mode=VisualizerMode.StillWaveform,Size=new(320,180)},Export=visualization.Export with {FormatId="png"}};
        string rendered=Assert.Single((await Export("audio.visualizer",audio,visualization)).Outputs);
        using var check=new MagickImage(rendered);Assert.Equal(320u,check.Width);Assert.Equal(180u,check.Height);
        Assert.True(check.TotalColors>1);
    }
    [Theory]
    [InlineData(17,11)]
    [InlineData(8191,3)]
    public async Task Snapshot_preserves_odd_source_dimensions_instead_of_using_MP4_limits(int width,int height)
    {
        string input=Path.Combine(root,"odd.gif");
        using(var image=new MagickImage(MagickColors.Orange,(uint)width,(uint)height))
        { image.AnimationDelay=10; await image.WriteAsync(input,MagickFormat.Gif); }
        var snapshot=await Document("video.snapshot",input);
        string output=Assert.Single((await Export("video.snapshot",input,snapshot)).Outputs);
        using var saved=new MagickImage(output);
        Assert.Equal((uint)width,saved.Width);Assert.Equal((uint)height,saved.Height);
    }
    [Fact]
    public async Task Compressed_audio_metadata_keeps_container_and_codec()
    {
        string input=Path.Combine(root,"compressed.mp3");
        await process.RunAsync([..MediaProcess.InputArguments(audio),"-c:a","libmp3lame","-b:a","128k",input],null,default);
        var doc=await Document("audio.metadata",input);
        Assert.Equal("mp3",doc.Export.FormatId);
        doc=doc with {Recipe=((AudioRecipe)doc.Recipe) with {MetadataEdits=[new("format","title",MetadataEditAction.Set,new TextValue("Keep encoded audio"))]}};
        string output=Assert.Single((await Export("audio.metadata",input,doc)).Outputs);
        Assert.EndsWith(".mp3",output);
        using var check=await process.ProbeJsonAsync(["-show_streams","-of","json",..MediaProcess.InputArguments(output)],1024*1024,default);
        Assert.Equal("mp3",check.RootElement.GetProperty("streams")[0].GetProperty("codec_name").GetString());
    }
    [Theory]
    [InlineData("audio.compress","m4a")]
    [InlineData("audio.visualizer","mp4")]
    public async Task Encoded_MP4_families_remove_source_tags_and_suppress_generated_handler_names(string operation,string format)
    {
        string input=Path.Combine(root,"tagged-audio.wav");
        await process.RunAsync([..MediaProcess.InputArguments(audio),"-c:a","copy","-metadata","title=Private source title","-metadata","comment=Private source comment",input],null,default);
        var doc=await Document(operation,input);
        doc=doc with {Export=doc.Export with {FormatId=format,Metadata=MetadataPolicy.StripAll}};
        if(doc.Recipe is VisualizerRecipe visual)doc=doc with {Recipe=visual with {Size=new(64,48)}};
        string output=Assert.Single((await Export(operation,input,doc)).Outputs);
        using var check=await process.ProbeJsonAsync(["-show_streams","-show_format","-of","json",..MediaProcess.InputArguments(output)],1024*1024,default);
        Assert.DoesNotContain("Private",check.RootElement.GetRawText(),StringComparison.OrdinalIgnoreCase);
        foreach(var stream in check.RootElement.GetProperty("streams").EnumerateArray())
            if(stream.TryGetProperty("tags",out var tags)&&tags.TryGetProperty("handler_name",out var handler))Assert.Equal("",handler.GetString());
    }
}
