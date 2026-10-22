using System.Collections.Immutable;
using Citrus.Core.Editing;
using Citrus.Conversion.Editing;
using Citrus.Conversion.Editing.Media;

namespace Citrus.Conversion.Tests;

[Collection(SharedPreviewSchedulerCollection.Name)]
public sealed class MediaFrameBufferOwnershipTests
{
    [Fact]
    public async Task Exact_frame_has_one_detached_result_buffer_and_keeps_EOF_source_and_cancel_guards()
    {
        string directory = Path.Combine(Path.GetTempPath(), "citrus-frame-buffer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const int edge = 1024; byte[] pixels = new byte[edge * edge * 4];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 73 + i / 4);
            string path = Path.Combine(directory, "source.bgra"); await File.WriteAllBytesAsync(path, pixels);
            var snapshot = (await new SourceSnapshotService().CaptureAsync([path], default))[0].Snapshot!;
            var process = new MediaProcess(Environment.GetEnvironmentVariable("CITRUS_TEST_FFMPEG") ?? (OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, "media", "ffmpeg.exe") : "/usr/bin/ffmpeg"), Environment.GetEnvironmentVariable("CITRUS_TEST_FFPROBE") ?? (OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, "media", "ffprobe.exe") : "/usr/bin/ffprobe"));
            await using var owner = new MediaProbe(process, new SourceSnapshotService(), Path.Combine(directory, "probe"));
            using var binding = new VerifiedMediaSource(snapshot, path);
            ImmutableArray<string> arguments = ["-f", "rawvideo", "-pixel_format", "bgra", "-video_size", $"{edge}x{edge}", "-framerate", "25", "-i", path, "-frames:v", "1", "-c:v", "rawvideo", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"];
            MediaGraphPlan Plan(SizeI size) => new(2, 7, new(0, 1), new(snapshot.Id, 0, 0), arguments, size, null, null, new(1, 25), [binding], owner);
            var decoder = new MediaBinaryDecoder(process); var plan = Plan(new(edge, edge));
            await decoder.DecodeFrameAsync(plan, default);
            long before = GC.GetTotalAllocatedBytes(precise: true);
            var frame = await decoder.DecodeFrameAsync(plan, default);
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            Assert.True(allocated < pixels.Length * 3L, $"Decoder allocated {allocated:N0} bytes for {pixels.Length:N0} output bytes.");
            Assert.Equal(pixels, frame.Bgra32.ToArray()); Assert.Equal(7, frame.Generation); Assert.Equal(plan.FrameIdentity, frame.FrameIdentity);
            await decoder.DecodeFrameAsync(plan, default);
            await owner.DisposeAsync(); Assert.Equal(pixels, frame.Bgra32.ToArray());
            await using var otherOwner = new MediaProbe(process, new SourceSnapshotService(), Path.Combine(directory, "other-probe"));
            MediaGraphPlan Other(SizeI size) => new(2, 7, new(0, 1), new(snapshot.Id, 0, 0), arguments, size, null, null, new(1, 25), [binding], otherOwner);
            await Assert.ThrowsAsync<MediaProcessException>(() => decoder.DecodeFrameAsync(Other(new(edge + 1, edge)), default));
            await Assert.ThrowsAsync<MediaProcessException>(() => decoder.DecodeFrameAsync(Other(new(edge - 1, edge)), default));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decoder.DecodeFrameAsync(Other(new(edge, edge)), canceled.Token));
            pixels[0] ^= 1; await File.WriteAllBytesAsync(path, pixels);
            await Assert.ThrowsAsync<IOException>(() => decoder.DecodeFrameAsync(Other(new(edge, edge)), default));
            Assert.Equal((byte)0, frame.Bgra32[0]);
        }
        finally { Directory.Delete(directory, true); }
    }
}
