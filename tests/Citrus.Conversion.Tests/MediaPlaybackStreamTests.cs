using System.Collections.Immutable;
using Citrus.Conversion.Editing;
using Citrus.Conversion.Editing.Media;
using Citrus.Core.Editing;

namespace Citrus.Conversion.Tests;

public sealed class MediaPlaybackStreamTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Citrus-playback-" + Guid.NewGuid().ToString("N"));
    private readonly MediaProcess process = OperatingSystem.IsWindows() ? MediaProcess.Bundled() :
        new(Environment.GetEnvironmentVariable("CITRUS_TEST_FFMPEG") ?? "/usr/bin/ffmpeg", Environment.GetEnvironmentVariable("CITRUS_TEST_FFPROBE") ?? "/usr/bin/ffprobe");
    private MediaProbe probe = null!;
    private MediaGraphCompiler compiler = null!;
    private SourceSnapshot source = null!;
    private MediaSourceInfo info = null!;
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        string raw = Path.Combine(directory, "colors.bgra"), video = Path.Combine(directory, "vfr.mp4");
        using (var data = File.Create(raw))
            foreach (byte color in new byte[] { 20, 60, 100, 160, 220, 240 })
                for (int pixel = 0; pixel < 16 * 8; pixel++)
                { byte shade = pixel % 16 < 8 ? color : (byte)(255 - color); data.Write([shade, shade, shade, 255]); }
        await process.RunAsync(["-f", "rawvideo", "-pixel_format", "bgra", "-video_size", "16x8", "-framerate", "25", "-i", raw,
            "-vf", "settb=1/90000,setpts=900000+if(eq(N\\,0)\\,0\\,if(eq(N\\,1)\\,3600\\,if(eq(N\\,2)\\,9000\\,if(eq(N\\,3)\\,20700\\,if(eq(N\\,4)\\,41400\\,45000)))))",
            "-fps_mode", "vfr", "-enc_time_base", "1:30000", "-c:v", "mpeg4", "-q:v", "1", "-video_track_timescale", "90000", video], null, default);
        source = (await new SourceSnapshotService().CaptureAsync([video], default))[0].Snapshot!;
        probe = new(process, new SourceSnapshotService(), Path.Combine(directory, "probe"));
        info = await probe.InspectAsync(source, default);
        compiler = new(probe, await process.DiscoverCapabilitiesAsync(default));
    }
    private MediaClip Clip(double speed = 1) => new(Guid.NewGuid(), source.Id, info.Video[0].Index, null, new(new(0, 1), new(1, 2)), speed);
    private EditorDocument Document(params MediaClip[] clips) => new(1, 23, [source], [],
        new VideoRecipe([.. clips], null, new(16, 8), ImageFit.Contain, true, [], [], null, []),
        new("mp4", null, "audition", new QualityGoal(80), MetadataPolicy.StripAll, new(0, 0, 0, 255)));
    private Task<MediaPlaybackPlan> Plan(EditorDocument doc, TimeRange range, bool original = false, CancellationToken ct = default) =>
        compiler.CompilePlaybackAsync(doc, range, original, new(16, 8), 45, ct);
    private async Task<List<PreviewFrame>> Decode(MediaPlaybackPlan plan)
    {
        var frames = new List<PreviewFrame>();
        await new MediaPlaybackDecoder(process).StreamVideoAsync(plan, (f, _) => { frames.Add(f); return ValueTask.CompletedTask; }, default);
        return frames;
    }
    [Fact]
    public async Task Stream_preserves_nonzero_vfr_pts_and_independent_pixel_payloads()
    {
        var frames = await Decode(await Plan(Document(Clip()), new(new(0, 1), new(1, 2))));
        Assert.Equal(new long[] { 900000, 903600, 909000, 920700, 941400 }, frames.Select(f => f.FrameIdentity!.RawPts));
        Assert.Equal(new RationalTime[] { new(0, 1), new(1, 25), new(1, 10), new(23, 100), new(23, 50) }, frames.Select(f => f.ActualTime!.Value));
        byte[] expected = [20, 60, 100, 160, 220];
        for (int i = 0; i < expected.Length; i++) Assert.InRange(frames[i].Bgra32[0], expected[i] - 4, expected[i] + 4);
        Assert.All(frames, f => { Assert.Equal(source.Id, f.FrameIdentity!.SourceId); Assert.Equal(45, f.Generation); Assert.Equal(23, f.Revision); Assert.Equal(512, f.Bgra32.Length); });
    }
    [Fact]
    public async Task Range_speed_and_repeated_clips_map_exact_assembled_time()
    {
        var doc = Document(Clip(), Clip(2));
        var frames = await Decode(await Plan(doc, new(new(13, 25), new(3, 4))));
        Assert.Equal(new long[] { 903600, 909000, 920700, 941400 }, frames.Select(f => f.FrameIdentity!.RawPts));
        Assert.Equal(new RationalTime[] { new(13, 25), new(11, 20), new(123, 200), new(73, 100) }, frames.Select(f => f.ActualTime!.Value));
    }
    [Fact]
    public async Task Original_keeps_trim_speed_order_and_bypasses_crop()
    {
        var clip = Clip(2) with { Range = new(new(1, 25), new(1, 2)) };
        var doc = Document(clip);
        doc = doc with { Recipe = ((VideoRecipe)doc.Recipe) with { Crop = new(8, 0, 8, 8) } };
        var edited = await Decode(await Plan(doc, new(new(0, 1), new(23, 100))));
        var original = await Decode(await Plan(doc, new(new(0, 1), new(23, 100)), true));
        Assert.Equal(edited.Select(f => f.FrameIdentity), original.Select(f => f.FrameIdentity));
        Assert.Equal(edited.Select(f => f.ActualTime), original.Select(f => f.ActualTime));
        Assert.Equal(new RationalTime(3, 100), original[1].ActualTime);
        Assert.NotEqual(edited[0].Bgra32[0], original[0].Bgra32[0]);
    }
    [Fact]
    public async Task Limits_and_unknown_effects_fail_instead_of_truncating_or_playing_untreated()
    {
        var doc = Document(Clip());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Plan(doc, new(new(0, 1), new(1, 1))));
        await Assert.ThrowsAsync<NotSupportedException>(() => Plan(Document(Enumerable.Range(0, 65).Select(_ => Clip()).ToArray()), new(new(0, 1), new(1, 1))));
        var mask = new TimedMask(Guid.NewGuid(), new(new(0, 1), new(1, 2)), MaskEffect.Solid, new(0, 0, 0, 255), 0,
            [new(new(0, 1), new(0, 0, 4, 4))], MaskInterpolation.Step);
        var effect = doc with { Recipe = ((VideoRecipe)doc.Recipe) with { Masks = [mask] } };
        var masked=await Decode(await Plan(effect,new(new(0,1),new(1,2))));
        Assert.NotEmpty(masked);
        Assert.InRange(masked[0].Bgra32[0],(byte)0,(byte)5);
        var moving=effect with {Recipe=((VideoRecipe)effect.Recipe) with {Masks=[mask with {Keys=[mask.Keys[0],new(new(1,4),new(4,0,4,4))]}]}};
        await Assert.ThrowsAsync<NotSupportedException>(()=>Plan(moving,new(new(0,1),new(1,2))));
        Assert.NotEmpty(await Decode(await Plan(effect, new(new(0, 1), new(1, 2)), true)));
    }
    [Fact]
    public async Task Cancellation_under_frame_backpressure_releases_probe_and_shared_slot()
    {
        var plan = await Plan(Document(Clip()), new(new(0, 1), new(1, 2)));
        using var cancellation = new CancellationTokenSource();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decoding = new MediaPlaybackDecoder(process).StreamVideoAsync(plan, async (_, ct) =>
        { arrived.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }, cancellation.Token);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decoding.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(5, (await Decode(plan)).Count);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var one = await PreviewWorkScheduler.AcquireAsync(timeout.Token);
        using var two = await PreviewWorkScheduler.AcquireAsync(timeout.Token);
    }
    [Fact]
    public async Task Disposing_probe_cancels_stream_before_removing_owned_sources()
    {
        var plan = await Plan(Document(Clip()), new(new(0, 1), new(1, 2)));
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decoding = new MediaPlaybackDecoder(process).StreamVideoAsync(plan, async (_, ct) =>
        { arrived.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }, default);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await probe.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decoding);
        Assert.False(Directory.Exists(Path.Combine(directory, "probe")));
    }
    [Fact]
    public async Task Streaming_owns_exactly_one_shared_preview_budget_slot()
    {
        var plan = await Plan(Document(Clip()), new(new(0, 1), new(1, 2)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var one = await PreviewWorkScheduler.AcquireAsync(timeout.Token);
        using var two = await PreviewWorkScheduler.AcquireAsync(timeout.Token);
        try
        {
            var decoding = Decode(plan);
            await Task.Delay(100); Assert.False(decoding.IsCompleted);
            one.Dispose(); Assert.Equal(5, (await decoding.WaitAsync(timeout.Token)).Count);
        }
        finally { one.Dispose(); }
    }
    [Fact]
    public async Task Changed_stage_rejected_before_any_pixels_are_published()
    {
        var plan = await Plan(Document(Clip()), new(new(0, 1), new(1, 2)));
        if(OperatingSystem.IsWindows())
        {
            Assert.Throws<IOException>(()=>File.WriteAllBytes(probe.StagedSources[source.Id],new byte[checked((int)source.Length)]));
            Assert.NotEmpty(await Decode(plan));
            return;
        }
        File.WriteAllBytes(probe.StagedSources[source.Id], new byte[checked((int)source.Length)]);
        int calls = 0;
        await Assert.ThrowsAsync<IOException>(() => new MediaPlaybackDecoder(process).StreamVideoAsync(plan, (_, _) => { calls++; return ValueTask.CompletedTask; }, default));
        Assert.Equal(0, calls);
    }
    [Theory]
    [InlineData(2559)]
    [InlineData(2561)]
    public async Task Short_or_excess_stream_payload_is_rejected(int bytes)
    {
        if (OperatingSystem.IsWindows()) return; // shell fixture only; real-media cases above also run with bundled Windows binaries.
        var plan = await Plan(Document(Clip()), new(new(0, 1), new(1, 2)));
        string script = Path.Combine(directory, "binary");
        await File.WriteAllTextAsync(script, "#!/bin/sh\nhead -c " + bytes + " /dev/zero\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await Assert.ThrowsAsync<MediaProcessException>(() => new MediaPlaybackDecoder(new(script, script)).StreamVideoAsync(plan, (_, _) => ValueTask.CompletedTask, default));
    }
    [Fact]
    public async Task Seek_preserves_first_original_clip_display_plane_for_crop()
    {
        string small = Path.Combine(directory, "small.mp4");
        await process.RunAsync(["-i", source.Path, "-vf", "scale=8:8", "-c:v", "mpeg4", "-q:v", "1", small], null, default);
        var other = (await new SourceSnapshotService().CaptureAsync([small], default))[0].Snapshot!;
        var otherInfo = await probe.InspectAsync(other, default);
        var second = new MediaClip(Guid.NewGuid(), other.Id, otherInfo.Video[0].Index, null, new(new(0, 1), new(2, 5)), 1);
        var doc = Document(Clip(), second);
        doc = doc with { Sources = [source, other], Recipe = ((VideoRecipe)doc.Recipe) with { Crop = new(8, 0, 8, 8) } };
        var seek = await Decode(await Plan(doc, new(new(1, 2), new(9, 10))));
        Assert.NotEmpty(seek); Assert.All(seek, f => Assert.Equal(other.Id, f.FrameIdentity!.SourceId));
        var full = await Decode(await Plan(doc, new(new(0, 1), new(9, 10))));
        var matching = full.Where(f => f.FrameIdentity!.SourceId == other.Id).ToArray();
        Assert.Equal(matching.Select(f => f.FrameIdentity), seek.Select(f => f.FrameIdentity));
        for (int i = 0; i < seek.Count; i++) Assert.Equal(matching[i].Bgra32.ToArray(), seek[i].Bgra32.ToArray());
    }
    [Fact]
    public async Task Processed_audio_is_not_original_when_selected()
    {
        // Edited video mute must never accidentally open original sound. A/B uses the same trims/speed.
        string raw = Path.Combine(directory, "audio.s16"), av = Path.Combine(directory, "av.mov");
        await File.WriteAllBytesAsync(raw, Enumerable.Range(0, 24000).SelectMany(_ => BitConverter.GetBytes((short)12000)).ToArray());
        await process.RunAsync(["-i", source.Path, "-f", "s16le", "-ar", "48000", "-ac", "1", "-i", raw,
            "-map", "0:v", "-map", "1:a", "-c:v", "copy", "-c:a", "pcm_s16le", av], null, default);
        var captured = (await new SourceSnapshotService().CaptureAsync([av], default))[0].Snapshot!;
        var avInfo = await probe.InspectAsync(captured, default);
        var clip = new MediaClip(Guid.NewGuid(), captured.Id, avInfo.Video[0].Index, avInfo.Audio[0].Index, new(new(1, 10), new(3, 10)), 1);
        var doc = Document(clip) with { Sources = [captured] };
        var edited = await Plan(doc, new(new(0, 1), new(1, 5)));
        var original = await Plan(doc, new(new(0, 1), new(1, 5)), true);
        Assert.Null(edited.Audio); Assert.NotNull(original.Audio);
        var pcm = await new MediaBinaryDecoder(process).DecodeAudioAsync(original.Audio, Path.Combine(directory, "original.pcm"), default);
        Assert.Equal(9600, pcm.SampleFrames);
        byte[] bytes = await File.ReadAllBytesAsync(pcm.Path);
        Assert.All(Enumerable.Range(0, bytes.Length / 2), i => Assert.Equal(12000, BitConverter.ToInt16(bytes, i * 2)));
        var editedFrames = await Decode(edited); var originalFrames = await Decode(original);
        Assert.Equal(editedFrames.Select(f => f.FrameIdentity), originalFrames.Select(f => f.FrameIdentity));
        Assert.Equal(editedFrames.Select(f => f.ActualTime), originalFrames.Select(f => f.ActualTime));
    }
    [Fact]
    public async Task Seeking_between_vfr_instants_keeps_greatest_predecessor_and_original_actual_time()
    {
        var frames = await Decode(await Plan(Document(Clip()), new(new(7, 100), new(1, 2))));
        Assert.Equal(new RationalTime(1, 25), frames[0].ActualTime);
        Assert.Equal(903600, frames[0].FrameIdentity!.RawPts);
        Assert.InRange(frames[0].Bgra32[0], 56, 64);
        Assert.Equal(909000, frames[1].FrameIdentity!.RawPts);
    }
    [Fact]
    public async Task Long_audition_fails_before_unbounded_preparation()
    {
        var doc = Document(Enumerable.Range(0, 64).Select(_ => Clip(.25)).ToArray());
        await Assert.ThrowsAsync<NotSupportedException>(() => Plan(doc, new(new(0, 1), new(121, 1))));
    }
    public async Task DisposeAsync()
    { if (probe is not null) await probe.DisposeAsync(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
