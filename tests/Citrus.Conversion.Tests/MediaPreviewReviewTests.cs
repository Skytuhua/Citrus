using System.Buffers.Binary;
using System.Collections.Immutable;
using Citrus.Conversion.Editing;
using Citrus.Conversion.Editing.Media;
using Citrus.Core.Editing;

namespace Citrus.Conversion.Tests;

// Real media regressions for Task 6a review findings.
[Collection(SharedPreviewSchedulerCollection.Name)]
public sealed class MediaPreviewReviewTests : IAsyncLifetime
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"Citrus-media-review-"+Guid.NewGuid().ToString("N"));
    private readonly MediaProcess process=new(Environment.GetEnvironmentVariable("CITRUS_TEST_FFMPEG")??"/usr/bin/ffmpeg",Environment.GetEnvironmentVariable("CITRUS_TEST_FFPROBE")??"/usr/bin/ffprobe");
    private MediaCapabilities capabilities=null!;
    public async Task InitializeAsync() { Directory.CreateDirectory(directory); capabilities=await process.DiscoverCapabilitiesAsync(default); }
    private async Task<(MediaProbe Probe,EditorDocument Document,MediaSourceInfo Info)> Audio(string suffix,string offset="0",bool video=false,double speed=1)
    {
        string input=Path.Combine(directory,"source-input.wav"),path=Path.Combine(directory,"source."+suffix);
        MediaFixture.WriteWave(input,Enumerable.Range(0,48000).Select(i=>(short)(i<24000?10000:20000)).ToArray());
        var args=new List<string>{"-copyts","-itsoffset",offset,"-i",input};
        if(video) args.AddRange(["-filter_complex","color=black:s=16x8:r=25:d=2[v]","-map","[v]","-map","0:a:0","-c:v","mpeg4"]);
        args.AddRange(["-c:a","pcm_s16le","-avoid_negative_ts","disabled",path]);
        await process.RunAsync(args,null,default);
        var source=(await new SourceSnapshotService().CaptureAsync([path],default))[0].Snapshot!;
        var probe=new MediaProbe(process,new SourceSnapshotService(),Path.Combine(directory,"private"));
        try
        {
            var info=await probe.InspectAsync(source,default);
            var clip=new MediaClip(Guid.NewGuid(),source.Id,null,info.Audio[0].Index,new(new(1,2),new(3,4)),speed);
            var document=new EditorDocument(1,1,[source],[],new AudioRecipe([clip],48000,new(["FC"],["FC"],[1],false),[],null,[]),new("wav",null,"audio",new QualityGoal(80),MetadataPolicy.StripAll,new()));
            return(probe,document,info);
        }
        catch { await probe.DisposeAsync(); throw; }
    }
    private MediaGraphPlan Compile(MediaProbe probe,EditorDocument document)=>new MediaGraphCompiler(probe,capabilities).Compile(document,RenderPurpose.Preview,new(1,new(16,8),new(0,1),null,null),probe.StagedSources);
    [Fact]
    public async Task Export_PCM_does_not_take_a_preview_worker()
    {
        var (probe,document,_)=await Audio("mkv");
        await using(probe)
        {
            var plan=Compile(probe,document);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var first=await PreviewWorkScheduler.AcquireAsync(deadline.Token);
            using var second=await PreviewWorkScheduler.AcquireAsync(deadline.Token);
            var pcm=await new MediaBinaryDecoder(process).DecodeExportAudioAsync(plan,Path.Combine(directory,"export.pcm"),deadline.Token);
            Assert.Equal(12000,pcm.SampleFrames);
        }
    }
    [Theory]
    [InlineData("mka","0")]
    [InlineData("mka","10")]
    [InlineData("mka","-0.25")]
    [InlineData("wav","0")]
    [InlineData("mov","0")]
    public async Task Container_time_base_does_not_change_trimmed_samples(string suffix,string offset)
    {
        var (probe,document,info)=await Audio(suffix,offset);
        await using(probe)
        {
            if(suffix=="mka") Assert.Equal(new RationalTime(1,1000),info.Audio[0].TimeBase);
            var missing=capabilities with {Filters=capabilities.Filters.Where(f=>f!="asettb").ToHashSet(StringComparer.Ordinal)};
            Assert.Throws<NotSupportedException>(()=>new MediaGraphCompiler(probe,missing).Compile(document,RenderPurpose.Preview,new(1,new(16,8),null,null,null),probe.StagedSources));
            Assert.Equal(offset switch {"10"=>new(10,1),"-0.25"=>new(-1,4),_=>new RationalTime(0,1)},info.PresentationOrigin);
            var pcm=await new MediaBinaryDecoder(process).DecodeAudioAsync(Compile(probe,document),Path.Combine(directory,"out.pcm"),default);
            Assert.Equal(12000,pcm.SampleFrames);
            var bytes=await File.ReadAllBytesAsync(pcm.Path);
            Assert.DoesNotContain(Enumerable.Range(0,12000),i=>BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i*2,2))!=20000);
        }
    }
    [Theory]
    [InlineData("0.25",1.0)]
    [InlineData("-0.25",1.0)]
    [InlineData("0.25",0.25)]
    [InlineData("-0.25",4.0)]
    public async Task Matroska_stream_offset_and_tempo_select_audible_requested_region(string offset,double speed)
    {
        var (probe,document,info)=await Audio("mkv",offset,true,speed);
        await using(probe)
        {
            Assert.Equal(new RationalTime(1,1000),info.Audio[0].TimeBase);
            // Positive audio starts .25 after video; negative audio establishes the source origin itself.
            Assert.Equal(offset=="0.25"?new RationalTime(0,1):new(-1,4),info.PresentationOrigin);
            var recipe=(AudioRecipe)document.Recipe;
            if(offset=="0.25") document=document with {Recipe=recipe with {Clips=[recipe.Clips[0] with {Range=new(new(3,4),new(1,1))}]}};
            var pcm=await new MediaBinaryDecoder(process).DecodeAudioAsync(Compile(probe,document),Path.Combine(directory,"out.pcm"),default);
            Assert.Equal((long)(12000/speed),pcm.SampleFrames);
            var bytes=await File.ReadAllBytesAsync(pcm.Path);
            var samples=Enumerable.Range(0,bytes.Length/2).Select(i=>BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i*2,2))).ToArray();
            if(speed==1) Assert.DoesNotContain(samples,s=>s!=20000);
            else
            {
                // Tempo can pad a final algorithmic window. The middle must still contain the chosen audible region.
                Assert.DoesNotContain(samples.Skip(samples.Length/4).Take(samples.Length/2),s=>s<19900||s>20100);
                Assert.True(samples.Count(s=>s>19000)>samples.Length*3/4);
            }
        }
    }
    [Fact]
    public async Task Webm_opus_trim_selects_second_tone_with_millisecond_container_timestamps()
    {
        string raw=Path.Combine(directory,"tones.pcm"),path=Path.Combine(directory,"tones.webm");
        using(var file=new BinaryWriter(File.Create(raw)))
            for(int i=0;i<48000;i++) file.Write((short)(12000*Math.Sin(2*Math.PI*(i<24000?700:1400)*i/48000)));
        await process.RunAsync(["-f","s16le","-ar","48000","-ac","1","-i",raw,"-c:a","libopus","-b:a","128k",path],null,default);
        var source=(await new SourceSnapshotService().CaptureAsync([path],default))[0].Snapshot!;
        await using var probe=new MediaProbe(process,new SourceSnapshotService(),Path.Combine(directory,"private"));
        var info=await probe.InspectAsync(source,default);
        Assert.Equal(new RationalTime(1,1000),info.Audio[0].TimeBase);
        var clip=new MediaClip(Guid.NewGuid(),source.Id,null,info.Audio[0].Index,new(new(1,2),new(3,4)),1);
        var document=new EditorDocument(1,1,[source],[],new AudioRecipe([clip],48000,new(["FC"],["FC"],[1],false),[],null,[]),new("wav",null,"audio",new QualityGoal(80),MetadataPolicy.StripAll,new()));
        var pcm=await new MediaBinaryDecoder(process).DecodeAudioAsync(Compile(probe,document),Path.Combine(directory,"out.pcm"),default);
        Assert.Equal(12000,pcm.SampleFrames);
        var bytes=await File.ReadAllBytesAsync(pcm.Path);
        var samples=Enumerable.Range(1024,8192).Select(i=>(double)BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i*2,2))).ToArray();
        double Energy(int frequency)
        {
            double sine=0,cosine=0;
            for(int i=0;i<samples.Length;i++) { double phase=2*Math.PI*frequency*i/48000; sine+=samples[i]*Math.Sin(phase); cosine+=samples[i]*Math.Cos(phase); }
            return Math.Sqrt(sine*sine+cosine*cosine)/samples.Length;
        }
        Assert.InRange(Math.Sqrt(samples.Average(s=>s*s)),7500,9500);
        Assert.True(Energy(1400)>8*Energy(700));
    }
    [Fact]
    public void Audio_time_base_normalizer_and_webm_fixture_codec_exist_in_bundled_manifest()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null&&!File.Exists(Path.Combine(root.FullName,"packaging/media/source/citrus-media-source.tar.gz"))) root=root.Parent;
        Assert.NotNull(root);
        using var file=File.OpenRead(Path.Combine(root.FullName,"packaging/media/source/citrus-media-source.tar.gz"));
        using var gzip=new System.IO.Compression.GZipStream(file,System.IO.Compression.CompressionMode.Decompress);
        using var tar=new System.Formats.Tar.TarReader(gzip);
        string? manifest=null;
        while(tar.GetNextEntry() is {} entry) if(entry.Name=="provenance/config_components.h") { using var reader=new StreamReader(entry.DataStream!); manifest=reader.ReadToEnd(); break; }
        Assert.NotNull(manifest);
        foreach(string name in new[]{"ASETTB_FILTER","LIBOPUS_ENCODER","WEBM_MUXER","MATROSKA_DEMUXER"}) Assert.Contains("#define CONFIG_"+name+" 1",manifest,StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("frame")]
    [InlineData("audio")]
    [InlineData("waveform")]
    public async Task Shared_preview_leases_block_media_and_queued_cancel_releases_all_work(string kind)
    {
        var (probe,document,info)=await Audio("mkv",video:true);
        await using(probe)
        {
            if(kind=="frame")
            {
                var clip=((AudioRecipe)document.Recipe).Clips[0];
                document=document with {Recipe=new VideoRecipe([clip with {VideoStream=info.Video[0].Index,AudioStream=null}],null,new(16,8),ImageFit.Contain,true,[],[],null,[])};
            }
            var plan=Compile(probe,document);
            string pcm=Path.Combine(directory,"out.pcm");
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var first=await PreviewWorkScheduler.AcquireAsync(timeout.Token);
            using var second=await PreviewWorkScheduler.AcquireAsync(timeout.Token);
            using var queuedCancel=CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            Task Run(CancellationToken ct)=>kind switch
            {
                "frame"=>new MediaBinaryDecoder(process).DecodeFrameAsync(plan,ct),
                "audio"=>new MediaBinaryDecoder(process).DecodeAudioAsync(plan,pcm,ct),
                // A missing PCM path proves queued waveform work does not even open its file.
                _=>new WaveformService().BuildAsync(new(Path.Combine(directory,"missing.pcm"),48000,1,1),1,ct)
            };
            // Windows stages stay locked; other platforms revalidate changed bytes after acquiring a lease.
            string staged=probe.StagedSources[document.Sources[0].Id];
            byte[] verified=await File.ReadAllBytesAsync(staged,timeout.Token);
            if(kind!="waveform")
            {
                if(OperatingSystem.IsWindows())await Assert.ThrowsAsync<IOException>(()=>File.AppendAllTextAsync(staged,"changed while queued",timeout.Token));
                else await File.AppendAllTextAsync(staged,"changed while queued",timeout.Token);
            }
            var queued=Run(queuedCancel.Token);
            await Task.Delay(150,timeout.Token);
            Assert.False(queued.IsCompleted);
            Assert.False(File.Exists(pcm));
            queuedCancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>queued);
            if(kind!="waveform"&&!OperatingSystem.IsWindows())await File.WriteAllBytesAsync(staged,verified,timeout.Token);
            // Keep one foreign image/file lease occupied: media must require exactly one additional lease.
            second.Dispose();
            if(kind=="waveform") await Assert.ThrowsAsync<FileNotFoundException>(()=>Run(timeout.Token));
            else await Run(timeout.Token);
            first.Dispose();
            using var recovered1=await PreviewWorkScheduler.AcquireAsync(timeout.Token);
            using var recovered2=await PreviewWorkScheduler.AcquireAsync(timeout.Token);
        }
    }
    public Task DisposeAsync() { Directory.Delete(directory,true); return Task.CompletedTask; }
}
