using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using Citrus.Conversion.Editing;
using Citrus.Conversion.Editing.Media;
using Citrus.Core.Editing;

namespace Citrus.Conversion.Tests;

public sealed class MediaProbeFrameTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Citrus-probe-" + Guid.NewGuid().ToString("N"));
    private readonly MediaProcess process = new(Environment.GetEnvironmentVariable("CITRUS_TEST_FFMPEG") ?? "/usr/bin/ffmpeg", Environment.GetEnvironmentVariable("CITRUS_TEST_FFPROBE") ?? "/usr/bin/ffprobe");
    private MediaProbe probe = null!;
    private SourceSnapshot source = null!;
    private MediaSourceInfo info = null!;
    private MediaCapabilities capabilities = null!;
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        string raw = Path.Combine(directory, "colors.bgra"), video = Path.Combine(directory,"vfr.mp4");
        using (var data = File.Create(raw))
            foreach (byte color in new byte[] { 20, 60, 100, 160, 220, 240 })
                for (int pixel = 0; pixel < 16*8; pixel++) data.Write(new byte[] { color, color, color, 255 });
        await process.RunAsync(["-f","rawvideo","-pixel_format","bgra","-video_size","16x8","-framerate","25","-i",raw,
            "-vf","settb=1/90000,setpts=900000+if(eq(N\\,0)\\,0\\,if(eq(N\\,1)\\,3600\\,if(eq(N\\,2)\\,9000\\,if(eq(N\\,3)\\,20700\\,if(eq(N\\,4)\\,41400\\,45000)))))",
            "-fps_mode","vfr","-enc_time_base","1:30000","-c:v","mpeg4","-q:v","1","-video_track_timescale","90000",video],null,default);
        source = (await new SourceSnapshotService().CaptureAsync([video],default))[0].Snapshot!;
        probe = new(process,new SourceSnapshotService(),Path.Combine(directory,"probe"));
        info = await probe.InspectAsync(source,default);
        capabilities = await process.DiscoverCapabilitiesAsync(default);
    }
    private EditorDocument Document(params MediaClip[] clips) => new(1,7,[source],[],new VideoRecipe([..clips],null,new(16,8),ImageFit.Contain,true,[],[],null,[]),new("mp4",null,"preview",new QualityGoal(80),MetadataPolicy.StripAll,new(0,0,0,255)));
    private MediaClip Clip(RationalTime start,RationalTime end,double speed=1) => new(Guid.NewGuid(),source.Id,info.Video[0].Index,null,new(start,end),speed);
    [Fact]
    public async Task Covered_timestamp_windows_are_reused_without_another_source_scan()
    {
        long inspected = probe.TimestampScanCount;
        var first = await probe.ReadFrameTimesAsync(source.Id, info.Video[0].Index, new(new(0, 1), new(1, 2)), 4096, default);
        var second = await probe.ReadFrameTimesAsync(source.Id, info.Video[0].Index, new(new(1, 25), new(1, 2)), 2, default);
        Assert.Equal(new long[] { 900000, 903600, 909000, 920700, 941400 }, first.Select(f=>f.RawPts));
        Assert.Equal(new long[] { 903600, 909000 }, second.Select(f=>f.RawPts));
        Assert.Equal(inspected, probe.TimestampScanCount);
    }
    [Fact]
    public async Task Cached_seeks_reuse_verified_locked_source_without_hashing_its_bytes_again()
    {
        if(!OperatingSystem.IsWindows())return;
        long verified=probe.SourceVerificationBytes;
        Assert.Equal(source.Length*2,verified);
        var doc=Document(Clip(new(0,1),new(1,2)));
        for(int generation=1;generation<=2;generation++)
        {
            var poster=await new MediaGraphCompiler(probe,capabilities).CompilePosterAsync(doc,new(new(11,50),new(1,2)),false,new(16,8),generation,default);
            var frame=await new MediaBinaryDecoder(process).DecodeFrameAsync(poster.Graph,default);
            Assert.Equal(909000,frame.FrameIdentity!.RawPts);
        }
        Assert.Equal(verified,probe.SourceVerificationBytes);
        string staged=probe.StagedSources[source.Id];
        Assert.Throws<IOException>(()=>File.Delete(staged));
        Assert.Throws<IOException>(()=>File.WriteAllBytes(staged,[0]));
    }
    [Fact]
    public async Task Paused_poster_uses_exact_vfr_predecessor_without_compiling_an_audio_stream()
    {
        long inspected = probe.TimestampScanCount;
        var doc = Document(Clip(new(0,1),new(1,2)));
        doc=doc with {Recipe=((VideoRecipe)doc.Recipe) with {Mute=false}};
        var plan = await new MediaGraphCompiler(probe, capabilities).CompilePosterAsync(doc, new(new(11,50),new(1,2)), false, new(16,8), 17, default);
        Assert.Equal(new RationalTime(1,10), plan.Graph.FrameTime);
        Assert.Equal(909000, plan.Graph.FrameIdentity!.RawPts);
        Assert.Contains(new RationalTime(23,100), plan.FrameTimes);
        Assert.Null(plan.Graph.Channels);
        Assert.Equal(inspected, probe.TimestampScanCount);
        var frame = await new MediaBinaryDecoder(process).DecodeFrameAsync(plan.Graph, default);
        Assert.Equal(17, frame.Generation);
        Assert.InRange(frame.Bgra32[0], (byte)96, (byte)104);
    }
    [Fact]
    public async Task Cached_poster_still_rejects_changed_private_source_bytes()
    {
        if(OperatingSystem.IsWindows())
        {
            Assert.Throws<IOException>(()=>File.WriteAllBytes(probe.StagedSources[source.Id],new byte[checked((int)source.Length)]));
            Assert.NotEmpty(await probe.PrepareFrameAtAsync(source.Id,info.Video[0].Index,new(new(0,1),new(1,2)),new(11,50),default));
            return;
        }
        File.WriteAllBytes(probe.StagedSources[source.Id],new byte[checked((int)source.Length)]);
        await Assert.ThrowsAsync<IOException>(()=>probe.PrepareFrameAtAsync(source.Id,info.Video[0].Index,new(new(0,1),new(1,2)),new(11,50),default));
    }
    [Fact]
    public void Compiler_rejects_a_copy_outside_the_verified_session_binding()
    {
        string copy=Path.Combine(directory,"alternate.mp4");
        File.Copy(probe.StagedSources[source.Id],copy);
        var alternate=probe.StagedSources.SetItem(source.Id,copy);
        Assert.Throws<ArgumentException>(()=>new MediaGraphCompiler(probe,capabilities).Compile(Document(Clip(new(0,1),new(1,2))),RenderPurpose.Preview,new(1,new(16,8),new(0,1),null,null),alternate));
    }
    [Fact]
    public async Task Adjacent_frame_navigation_uses_actual_vfr_neighbors_and_clip_edges()
    {
        var range=new TimeRange(new(0,1),new(1,2));
        var previous=await probe.GetAdjacentFrameAsync(source.Id,info.Video[0].Index,range,new(11,50),-1,default);
        var next=await probe.GetAdjacentFrameAsync(source.Id,info.Video[0].Index,range,new(11,50),1,default);
        Assert.Equal(903600,previous!.RawPts);
        Assert.Equal(920700,next!.RawPts);
        Assert.Null(await probe.GetAdjacentFrameAsync(source.Id,info.Video[0].Index,range,new(0,1),-1,default));
        Assert.Null(await probe.GetAdjacentFrameAsync(source.Id,info.Video[0].Index,range,new(23,50),1,default));
        var trimmed=new TimeRange(new(1,20),new(1,2));
        Assert.Null(await probe.GetAdjacentFrameAsync(source.Id,info.Video[0].Index,trimmed,new(11,50),-1,default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>probe.GetAdjacentFrameAsync(source.Id,info.Video[0].Index,range,new(0,1),0,default));
    }
    [Fact]
    public async Task Vfr_poster_refills_successors_through_initial_and_later_cache_boundaries()
    {
        string raw=Path.Combine(directory,"long-vfr.bgra"),path=Path.Combine(directory,"long-vfr.mp4");
        using(var file=File.Create(raw))for(int frame=0;frame<4800;frame++)for(int pixel=0;pixel<16*8;pixel++)file.Write([(byte)(frame%255),40,180,255]);
        await process.RunAsync(["-f","rawvideo","-pixel_format","bgra","-video_size","16x8","-framerate","50","-i",raw,
            "-vf","settb=1/90000,setpts=900000+N*1800+floor(N/3)*900","-fps_mode","vfr","-enc_time_base","1:30000","-video_track_timescale","90000","-c:v","mpeg4",path],null,default);
        var captured=(await new SourceSnapshotService().CaptureAsync([path],default))[0].Snapshot!;
        var inspected=await probe.InspectAsync(captured,default);var stream=Assert.Single(inspected.Video);
        using var independent=await process.ProbeJsonAsync(["-show_frames","-show_entries","frame=pts","-select_streams","v:0","-of","json","-i",path],1024*1024,default);
        var pts=independent.RootElement.GetProperty("frames").EnumerateArray().Select(f=>f.GetProperty("pts").GetInt64()).ToArray();
        Assert.True(pts.Length>4450);
        var all=pts.Select(p=>new RationalTime((p-pts[0])*stream.TimeBase.Numerator,stream.TimeBase.Denominator)).ToArray();
        var range=new TimeRange(new(0,1),inspected.Duration);
        long scans=probe.TimestampScanCount;
        for(int frame=4094;frame<4430;frame++)
        {
            var prepared=await probe.PrepareFrameAtAsync(captured.Id,stream.Index,range,all[frame],default);
            Assert.Contains(prepared,f=>f.RawPts==pts[frame]);
            Assert.Contains(prepared,f=>f.RawPts==pts[frame+1]);
        }
        Assert.InRange(probe.TimestampScanCount-scans,0,2);
    }
    [Fact]
    public async Task Vfr_nonzero_start_reports_actual_display_pts()
    {
        Assert.Equal(new RationalTime(10,1), info.PresentationOrigin);
        Assert.Equal(900000, info.Video[0].RawStartPts);
        // FFmpeg versions differ in whether the terminal MP4 packet at .50 is discarded.
        // The editor range remains end-exclusive and contains exactly these five identities.
        var included=await probe.ReadFrameTimesAsync(source.Id,info.Video[0].Index,new(new(0,1),new(1,2)),4096,default);
        Assert.Equal(new long[] {900000,903600,909000,920700,941400},included.Select(f=>f.RawPts));
        Assert.Equal(new RationalTime(1,10), info.Video[0].Frames[2].SourceOffset);
        // Independently verify the complete decoded identity and extent, including any terminal frame.
        using var raw=await process.ProbeJsonAsync(["-show_frames","-select_streams","v:0","-of","json","-i",source.Path],1024*1024,default);
        var frames=raw.RootElement.GetProperty("frames").EnumerateArray().ToArray();
        Assert.Equal(frames.Select(f=>f.GetProperty("pts").GetInt64()),info.Video[0].Frames.Select(f=>f.RawPts));
        var last=frames[^1];
        long end=last.GetProperty("pts").GetInt64()+(last.TryGetProperty("duration",out var duration)?duration:last.GetProperty("pkt_duration")).GetInt64();
        Assert.Equal(new RationalTime(end-900000,90000),info.Duration);
        Assert.Empty(await probe.ReadFrameTimesAsync(source.Id,info.Video[0].Index,new(info.Duration,new(1,1)),4096,default));
    }
    [Fact]
    public async Task Origin_10_range_point04_speed2_offset3_maps_pts909000_to_actual3point03()
    {
        var clips = Enumerable.Range(0,6).Select(_=>Clip(new(0,1),new(1,2))).Append(Clip(new(1,25),new(1,2),2)).ToArray();
        var plan = new MediaGraphCompiler(probe,capabilities).Compile(Document(clips),RenderPurpose.Preview,new(9,new(16,8),new(76,25),null,null),probe.StagedSources);
        var frame = await new MediaBinaryDecoder(process).DecodeFrameAsync(plan,default);
        Assert.Equal(new RationalTime(303,100),frame.ActualTime);
        Assert.Equal(909000,frame.FrameIdentity!.RawPts);
        Assert.Equal(source.Id,frame.FrameIdentity.SourceId);
        Assert.Equal(7,frame.Revision); Assert.Equal(9,frame.Generation);
        Assert.Equal(16*8*4,frame.Bgra32.Length);
        Assert.InRange(frame.Bgra32[0],(byte)96,(byte)104);
        Assert.Equal(255,frame.Bgra32[3]);
        Assert.Equal(source.Sha256,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source.Path))));
    }
    [Fact]
    public async Task Windows_are_bounded_and_unknown_identity_or_mutated_stage_fails()
    {
        var stamps = await probe.ReadFrameTimesAsync(source.Id,info.Video[0].Index,new(new(1,25),new(1,2)),2,default);
        Assert.Equal(new long[]{903600,909000},stamps.Select(f=>f.RawPts));
        await Assert.ThrowsAsync<ArgumentException>(()=>probe.ReadFrameTimesAsync(Guid.NewGuid(),0,new(new(0,1),new(1,1)),2,default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>probe.ReadFrameTimesAsync(source.Id,0,new(new(0,1),new(1,1)),4097,default));
        var plan = new MediaGraphCompiler(probe,capabilities).Compile(Document(Clip(new(0,1),new(1,2))),RenderPurpose.Preview,new(1,new(16,8),new(0,1),null,null),probe.StagedSources);
        if(OperatingSystem.IsWindows())
        {
            Assert.Throws<IOException>(()=>File.WriteAllBytes(probe.StagedSources[source.Id],new byte[checked((int)source.Length)]));
            await new MediaBinaryDecoder(process).DecodeFrameAsync(plan,default);
            return;
        }
        File.WriteAllBytes(probe.StagedSources[source.Id],new byte[checked((int)source.Length)]);
        await Assert.ThrowsAsync<IOException>(()=>new MediaBinaryDecoder(process).DecodeFrameAsync(plan,default));
    }
    [Fact]
    public async Task Original_mutation_requires_reload_and_probe_disposal_removes_private_copies()
    {
        await File.AppendAllTextAsync(source.Path,"changed");
        await Assert.ThrowsAsync<IOException>(()=>probe.InspectAsync(source,default));
        string staged = probe.StagedSources[source.Id];
        await probe.DisposeAsync();
        Assert.False(File.Exists(staged));
    }
    [Fact]
    public void Unsupported_effects_and_missing_filter_fail_explicitly()
    {
        var doc = Document(Clip(new(0,1),new(1,2)));
        Assert.Throws<NotSupportedException>(()=>new MediaGraphCompiler(probe,capabilities).Compile(doc with {Export=doc.Export with {Compression=new ByteGoal(1000)}},RenderPurpose.Export,null,probe.StagedSources));
        var missing = capabilities with {Filters=new HashSet<string>()};
        Assert.Throws<NotSupportedException>(()=>new MediaGraphCompiler(probe,missing).Compile(doc,RenderPurpose.Preview,new(1,new(16,8),new(0,1),null,null),probe.StagedSources));
    }
    [Fact]
    public async Task Short_frame_eof_and_overlong_frame_fail_without_publishing()
    {
        if(OperatingSystem.IsWindows()) return;
        var plan=new MediaGraphCompiler(probe,capabilities).Compile(Document(Clip(new(0,1),new(1,2))),RenderPurpose.Preview,new(1,new(16,8),new(0,1),null,null),probe.StagedSources);
        foreach(int bytes in new[]{511,513})
        {
            string script=Path.Combine(directory,"binary-"+bytes);
            File.WriteAllText(script,"#!/bin/sh\nhead -c "+bytes+" /dev/zero\n");
            File.SetUnixFileMode(script,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            await Assert.ThrowsAsync<MediaProcessException>(()=>new MediaBinaryDecoder(new(script,script)).DecodeFrameAsync(plan,default));
        }
    }
    [Fact]
    public async Task Sar_and_rotation_apply_to_real_pixels_before_canvas_fit()
    {
        string raw=Path.Combine(directory,"quarters.bgra"),encoded=Path.Combine(directory,"quarters.mp4"),rotated=Path.Combine(directory,"rotated.mp4");
        using(var output=File.Create(raw))
            for(int frame=0;frame<3;frame++) for(int y=0;y<8;y++) for(int x=0;x<16;x++)
            { byte c=(byte)(y<4?(x<8?20:70):(x<8?130:220)); output.Write(new byte[]{c,c,c,255}); }
        await process.RunAsync(["-f","rawvideo","-pixel_format","bgra","-video_size","16x8","-framerate","25","-i",raw,"-vf","setsar=2","-c:v","mpeg4","-q:v","1",encoded],null,default);
        await process.RunAsync(["-display_rotation","90","-i",encoded,"-c","copy",rotated],null,default);
        var captured=(await new SourceSnapshotService().CaptureAsync([rotated],default))[0].Snapshot!;
        var inspected=await probe.InspectAsync(captured,default); var stream=Assert.Single(inspected.Video);
        Assert.Equal(new SizeI(8,32),stream.DisplaySize); Assert.Equal(new RationalTime(2,1),stream.SampleAspect);
        var clip=new MediaClip(Guid.NewGuid(),captured.Id,stream.Index,null,new(new(0,1),new(1,25)),1);
        var doc=Document() with {Sources=[captured],Recipe=new VideoRecipe([clip],null,stream.DisplaySize,ImageFit.Contain,true,[],[],null,[])};
        var plan=new MediaGraphCompiler(probe,capabilities).Compile(doc,RenderPurpose.Preview,new(1,new(8,32),new(0,1),null,null),probe.StagedSources);
        var frameResult=await new MediaBinaryDecoder(process).DecodeFrameAsync(plan,default);
        Assert.InRange(frameResult.Bgra32[(2*8+2)*4],(byte)66,(byte)74);
        Assert.InRange(frameResult.Bgra32[(2*8+6)*4],(byte)216,(byte)224);
        Assert.InRange(frameResult.Bgra32[(28*8+2)*4],(byte)16,(byte)24);
        Assert.InRange(frameResult.Bgra32[(28*8+6)*4],(byte)126,(byte)134);
    }
    [Fact]
    public void Original_path_cannot_be_substituted_for_a_private_stage()
    {
        var doc=Document(Clip(new(0,1),new(1,2)));
        Assert.Throws<ArgumentException>(()=>new MediaGraphCompiler(probe,capabilities).Compile(doc,RenderPurpose.Preview,new(1,new(16,8),new(0,1),null,null),ImmutableDictionary<Guid,string>.Empty.Add(source.Id,source.Path)));
    }
    [Fact]
    public void Missing_png_and_lavfi_do_not_block_raw_preview_manifest()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null&&!File.Exists(Path.Combine(root.FullName,"packaging","media","source","citrus-media-source.tar.gz"))) root=root.Parent;
        Assert.NotNull(root);
        using var file=File.OpenRead(Path.Combine(root.FullName,"packaging","media","source","citrus-media-source.tar.gz"));
        using var gzip=new System.IO.Compression.GZipStream(file,System.IO.Compression.CompressionMode.Decompress);
        using var tar=new System.Formats.Tar.TarReader(gzip);
        string? manifest=null;
        while(tar.GetNextEntry() is {} entry) if(entry.Name=="provenance/config_components.h") { using var reader=new StreamReader(entry.DataStream!); manifest=reader.ReadToEnd(); break; }
        Assert.NotNull(manifest);
        foreach(string absent in new[]{"PNG_ENCODER","PNG_DECODER","LAVFI_INDEV","DRAWTEXT_FILTER","BOXBLUR_FILTER","EQ_FILTER"}) Assert.Contains("#define CONFIG_"+absent+" 0",manifest,StringComparison.Ordinal);
        foreach(string required in new[]{"RAWVIDEO_ENCODER","RAWVIDEO_MUXER","PCM_S16LE_ENCODER","PCM_S16LE_MUXER","TRIM_FILTER","SETPTS_FILTER","SCALE_FILTER","SETSAR_FILTER","FORMAT_FILTER","TRANSPOSE_FILTER","PAD_FILTER","CROP_FILTER","ATRIM_FILTER","ASETPTS_FILTER","ARESAMPLE_FILTER","ATEMPO_FILTER","APAD_FILTER","CONCAT_FILTER","COLOR_FILTER"}) Assert.Contains("#define CONFIG_"+required+" 1",manifest,StringComparison.Ordinal);
    }
    [Fact]
    public async Task Disposing_probe_cancels_live_decode_before_removing_staged_sources()
    {
        if(OperatingSystem.IsWindows()) return;
        var plan=new MediaGraphCompiler(probe,capabilities).Compile(Document(Clip(new(0,1),new(1,2))),RenderPurpose.Preview,new(1,new(16,8),new(0,1),null,null),probe.StagedSources);
        string script=Path.Combine(directory,"slow.sh"),started=Path.Combine(directory,"started");
        File.WriteAllText(script,"#!/bin/sh\ntouch '"+started+"'\nsleep 60 &\nwait\n");
        File.SetUnixFileMode(script,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        string staged=probe.StagedSources[source.Id];
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var decode=new MediaBinaryDecoder(new(script,script)).DecodeFrameAsync(plan,cancel.Token);
        while(!File.Exists(started)) await Task.Delay(10,cancel.Token);
        var timer=System.Diagnostics.Stopwatch.StartNew();
        await probe.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>decode);
        Assert.True(timer.Elapsed<TimeSpan.FromSeconds(2)); Assert.False(File.Exists(staged));
    }
    [Fact]
    public async Task Real_negative_pts_decode_preserves_source_identity_and_normalized_time()
    {
        string path=Path.Combine(directory,"negative.mkv");
        await process.RunAsync(["-copyts","-f","rawvideo","-pixel_format","bgra","-video_size","16x8","-framerate","25","-i",Path.Combine(directory,"colors.bgra"),"-vf","setpts=PTS-1","-fps_mode","passthrough","-c:v","mpeg4","-q:v","1","-avoid_negative_ts","disabled",path],null,default);
        var captured=(await new SourceSnapshotService().CaptureAsync([path],default))[0].Snapshot!;
        var inspected=await probe.InspectAsync(captured,default);
        Assert.Equal(new RationalTime(-1,25),inspected.PresentationOrigin);
        Assert.Equal(-40,inspected.Video[0].Frames[0].RawPts);
        var clip=new MediaClip(Guid.NewGuid(),captured.Id,0,null,new(new(0,1),new(1,5)),1);
        var doc=Document() with {Sources=[captured],Recipe=new VideoRecipe([clip],null,new(16,8),ImageFit.Contain,true,[],[],null,[])};
        var compiler=new MediaGraphCompiler(probe,capabilities);
        var first=await new MediaBinaryDecoder(process).DecodeFrameAsync(compiler.Compile(doc,RenderPurpose.Preview,new(1,new(16,8),new(0,1),null,null),probe.StagedSources),default);
        var second=await new MediaBinaryDecoder(process).DecodeFrameAsync(compiler.Compile(doc,RenderPurpose.Preview,new(1,new(16,8),new(1,25),null,null),probe.StagedSources),default);
        Assert.Equal(-40,first.FrameIdentity!.RawPts); Assert.Equal(new RationalTime(0,1),first.ActualTime);
        Assert.Equal(0,second.FrameIdentity!.RawPts); Assert.Equal(new RationalTime(1,25),second.ActualTime);
        Assert.InRange(first.Bgra32[0],(byte)16,(byte)24); Assert.InRange(second.Bgra32[0],(byte)56,(byte)64);
    }
    [Fact]
    public void Preview_rejects_unimplemented_compression_targets()
    {
        var doc=Document(Clip(new(0,1),new(1,2)));
        Assert.Throws<NotSupportedException>(()=>new MediaGraphCompiler(probe,capabilities).Compile(doc with {Export=doc.Export with {Compression=new ByteGoal(1000)}},RenderPurpose.Preview,new(1,new(16,8),new(0,1),null,null),probe.StagedSources));
    }
    [Fact]
    public async Task Crop_is_in_first_display_canvas_before_final_output_scaling()
    {
        string raw=Path.Combine(directory,"halves.bgra"),path=Path.Combine(directory,"halves.mp4");
        using(var output=File.Create(raw))
            for(int frame=0;frame<3;frame++) for(int y=0;y<8;y++) for(int x=0;x<16;x++)
            { byte c=x<8?(byte)20:(byte)220; output.Write(new byte[]{c,c,c,255}); }
        await process.RunAsync(["-f","rawvideo","-pixel_format","bgra","-video_size","16x8","-framerate","25","-i",raw,"-c:v","mpeg4","-q:v","1",path],null,default);
        var captured=(await new SourceSnapshotService().CaptureAsync([path],default))[0].Snapshot!;
        await probe.InspectAsync(captured,default);
        var clip=new MediaClip(Guid.NewGuid(),captured.Id,0,null,new(new(0,1),new(1,25)),1);
        var recipe=new VideoRecipe([clip],new(8,0,8,8),new(4,2),ImageFit.Contain,true,[],[],null,[]);
        var doc=Document() with {Sources=[captured],Recipe=recipe};
        var compiler=new MediaGraphCompiler(probe,capabilities);
        var request=new PreviewRequest(1,new(32,32),new(0,1),null,null);
        var frameResult=await new MediaBinaryDecoder(process).DecodeFrameAsync(compiler.Compile(doc,RenderPurpose.Preview,request,probe.StagedSources),default);
        Assert.Equal(new SizeI(4,2),frameResult.Size);
        Assert.All(Enumerable.Range(0,8),i=>Assert.InRange(frameResult.Bgra32[i*4],(byte)216,(byte)224));
        Assert.Throws<ArgumentException>(()=>compiler.Compile(doc with {Recipe=recipe with {Crop=new(15,0,2,8),Canvas=new(32,32)}},RenderPurpose.Preview,request,probe.StagedSources));
    }
    public async Task DisposeAsync() { if(probe is not null) await probe.DisposeAsync(); Directory.Delete(directory,true); }
}

// Controlled ffprobe records exercise timestamps that some container muxers normalize away.
// Pixel and PCM behavior is separately covered with real FFmpeg fixtures above.
public sealed class MediaProbeTimestampTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"Citrus-timestamps-"+Guid.NewGuid().ToString("N"));
    public MediaProbeTimestampTests()=>Directory.CreateDirectory(directory);
    private async Task<(MediaProbe Probe,SourceSnapshot Source)> Prepare(string streams,string frames)
    {
        string path=Path.Combine(directory,"source.dat"),exe=Path.Combine(directory,"probe.sh");
        File.WriteAllBytes(path,[1,2,3]);
        File.WriteAllText(Path.Combine(directory,"streams.json"),"{\"streams\":"+streams+"}");
        File.WriteAllText(Path.Combine(directory,"frames.txt"),frames);
        File.WriteAllText(exe,"#!/bin/sh\ncase \"$*\" in *-show_streams*) cat '"+Path.Combine(directory,"streams.json")+"';; *) cat '"+Path.Combine(directory,"frames.txt")+"';; esac\n");
        if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        var source=(await new SourceSnapshotService().CaptureAsync([path],default))[0].Snapshot!;
        return(new(new(exe,exe),new SourceSnapshotService(),Path.Combine(directory,"stage")),source);
    }
    private const string Video="{\"index\":0,\"codec_type\":\"video\",\"time_base\":\"1/90000\",\"width\":16,\"height\":8,\"sample_aspect_ratio\":\"1:1\"}";
    [Fact]
    public async Task Negative_origin_preserves_positive_normalized_offset_and_audio_start_offset()
    {
        if(OperatingSystem.IsWindows()) return;
        var (probe,source)=await Prepare("["+Video+",{\"index\":1,\"codec_type\":\"audio\",\"time_base\":\"1/48000\",\"sample_rate\":\"48000\",\"channels\":1}]",
            "stream_index=0|pts=-4500|duration=4500\nstream_index=1|pts=0|nb_samples=2400\nstream_index=0|pts=0|duration=4500\n");
        await using(probe)
        {
            var info=await probe.InspectAsync(source,default);
            Assert.Equal(new RationalTime(-1,20),info.PresentationOrigin);
            Assert.Equal(new RationalTime(1,20),info.Video[0].Frames[1].SourceOffset);
            Assert.Equal(0,info.Audio[0].RawStartPts);
            Assert.Equal(new RationalTime(1,10),info.Duration);
            Assert.Equal(new RationalTime(-1,20),(await probe.InspectAsync(source,default)).PresentationOrigin);
        }
    }
    [Fact]
    public async Task Frame_window_limit_requires_preparation_and_can_reach_actual_last_frame()
    {
        if(OperatingSystem.IsWindows()) return;
        string frames=string.Concat(Enumerable.Range(0,4100).Select(i=>$"stream_index=0|pts={i*90}|duration=90\n"));
        var (probe,source)=await Prepare("["+Video+"]",frames);
        await using(probe)
        {
            var info=await probe.InspectAsync(source,default); Assert.Equal(4096,info.Video[0].Frames.Length);
            var doc=new EditorDocument(1,1,[source],[],new VideoRecipe([new(Guid.NewGuid(),source.Id,0,null,new(new(0,1),new(41,10)),1)],null,new(16,8),ImageFit.Contain,true,[],[],null,[]),new("mp4",null,"frame",new QualityGoal(80),MetadataPolicy.StripAll,new()));
            var caps=new MediaCapabilities(new HashSet<string>{"rawvideo"},new HashSet<string>{"rawvideo"},new HashSet<string>(),new HashSet<string>{"trim","setpts","scale","setsar","format","pad"},false);
            var compiler=new MediaGraphCompiler(probe,caps); var request=new PreviewRequest(1,new(16,8),new(40995,10000),null,null);
            Assert.Throws<InvalidOperationException>(()=>compiler.Compile(doc,RenderPurpose.Preview,request,probe.StagedSources));
            var last=await probe.ReadFrameTimesAsync(source.Id,0,new(new(4095,1000),info.Duration),4096,default);
            Assert.Equal(5,last.Length);
            var plan=compiler.Compile(doc,RenderPurpose.Preview,request,probe.StagedSources);
            Assert.Equal(368910,plan.FrameIdentity!.RawPts); Assert.Equal(new RationalTime(4099,1000),plan.FrameTime);
            Assert.Empty(await probe.ReadFrameTimesAsync(source.Id,0,new(info.Duration,new(5,1)),4096,default));
        }
    }
    [Fact]
    public async Task Attached_picture_does_not_move_origin_and_missing_timestamps_are_rejected()
    {
        if(OperatingSystem.IsWindows()) return;
        var (probe,source)=await Prepare("["+Video+",{\"index\":1,\"codec_type\":\"video\",\"disposition\":{\"attached_pic\":1}}]","stream_index=1|pts=-900000\nstream_index=0|pts=900000|duration=9000\n");
        await using(probe)
        {
            var info=await probe.InspectAsync(source,default); Assert.Single(info.Video); Assert.Equal(new RationalTime(10,1),info.PresentationOrigin);
        }
    }
    [Theory]
    [InlineData("stream_index=0|duration=90\n")]
    [InlineData("stream_index=0|pts=90|duration=90\nstream_index=0|pts=90|duration=90\n")]
    [InlineData("stream_index=0|pts=90|duration=90\nstream_index=0|pts=0|duration=90\n")]
    public async Task Ambiguous_or_missing_presentation_pts_fail(string frames)
    {
        if(OperatingSystem.IsWindows()) return;
        var (probe,source)=await Prepare("["+Video+"]",frames);
        await using(probe) await Assert.ThrowsAsync<MediaProcessException>(()=>probe.InspectAsync(source,default));
    }
    [Fact]
    public async Task Declared_dimensions_are_bounded_before_decoding()
    {
        if(OperatingSystem.IsWindows()) return;
        var (probe,source)=await Prepare("["+Video.Replace("16","2147483647")+"]","stream_index=0|pts=0|duration=90\n");
        await using(probe) await Assert.ThrowsAsync<NotSupportedException>(()=>probe.InspectAsync(source,default));
    }
    public void Dispose()=>Directory.Delete(directory,true);
}
