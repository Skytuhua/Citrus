using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Citrus.Core.Editing;
using Citrus.Windows.Editors.Pdf;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class PdfPreviewBitmapTests(DesktopFixture desktop)
{
    [Theory]
    [InlineData(0, 8, 4, "RGBY")]
    [InlineData(1, 4, 8, "BRYG")]
    [InlineData(2, 8, 4, "YBGR")]
    [InlineData(3, 4, 8, "GYRB")]
    public Task Decode_preserves_full_preview_pixels_rotation_and_stream_independence(int turns, int width, int height, string corners) => desktop.RunAsync(async () =>
    {
        BitmapImage bitmap;
        using (var source = new MemoryStream(Png()))
            bitmap = await PdfPreviewBitmap.DecodeAsync(source, (Rotation)turns, 0, 0, default);

        Assert.True(bitmap.IsFrozen);
        Assert.Equal(width, bitmap.PixelWidth);
        Assert.Equal(height, bitmap.PixelHeight);
        AssertCorners(bitmap, corners);
    });

    [Theory]
    [InlineData(0, 4, 0, 4, 2)]
    [InlineData(1, 4, 0, 2, 4)]
    [InlineData(0, 0, 2, 4, 2)]
    [InlineData(3, 0, 2, 2, 4)]
    public Task Thumbnail_decode_preserves_the_requested_decode_edge(int turns, int decodeWidth, int decodeHeight, int width, int height) => desktop.RunAsync(async () =>
    {
        using var source = new MemoryStream(Png());
        var bitmap = await PdfPreviewBitmap.DecodeAsync(source, (Rotation)turns, decodeWidth, decodeHeight, default);
        Assert.True(bitmap.IsFrozen);
        Assert.Equal(width, bitmap.PixelWidth);
        Assert.Equal(height, bitmap.PixelHeight);
    });

    [Fact]
    public Task Decode_reads_on_a_worker_while_the_dispatcher_processes_input() => desktop.RunAsync(async () =>
    {
        int dispatcherThread = Environment.CurrentManagedThreadId;
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var source = new GatedReadStream(Png());
        var work = PdfPreviewBitmap.DecodeAsync(source, Rotation.Rotate0, 0, 0, default);
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(dispatcherThread, source.ReadThread);
            bool heartbeat = false;
            await dispatcher.InvokeAsync(() => heartbeat = true, DispatcherPriority.Input);
            Assert.True(heartbeat);
            Assert.False(work.IsCompleted);
        }
        finally
        {
            source.Release.Set();
            await work;
        }
    });

    [Fact]
    public Task Canceled_queued_selection_does_not_start_decoding() => desktop.RunAsync(async () =>
    {
        using var first = await PreviewWorkScheduler.AcquireAsync(default);
        using var second = await PreviewWorkScheduler.AcquireAsync(default);
        using var cancellation = new CancellationTokenSource();
        using var source = new GatedReadStream(Png());
        var work = PdfPreviewBitmap.DecodeAsync(source, Rotation.Rotate0, 0, 0, cancellation.Token);
        try
        {
            Assert.False(work.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(source.Entered.Task.IsCompleted);
        }
        finally
        {
            source.Release.Set();
            try { await work; }
            catch (OperationCanceledException) { }
        }
    });

    [Fact]
    public Task Selection_canceled_during_native_decode_does_not_return_a_stale_bitmap() => desktop.RunAsync(async () =>
    {
        using var cancellation = new CancellationTokenSource();
        using var source = new GatedReadStream(Png());
        var work = PdfPreviewBitmap.DecodeAsync(source, Rotation.Rotate0, 0, 0, cancellation.Token);
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            source.Release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        }
        finally
        {
            cancellation.Cancel();
            source.Release.Set();
            try { await work; }
            catch (OperationCanceledException) { }
        }
    });

    [Fact]
    public Task Invalid_png_surfaces_the_decode_error_and_releases_the_worker() => desktop.RunAsync(async () =>
    {
        using var source = new MemoryStream("not an image"u8.ToArray());
        var error = await Record.ExceptionAsync(() => PdfPreviewBitmap.DecodeAsync(source, Rotation.Rotate0, 0, 0, default));
        Assert.True(error is NotSupportedException or FileFormatException, error?.ToString());
        using var first = await PreviewWorkScheduler.AcquireAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        using var second = await PreviewWorkScheduler.AcquireAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    });

    private static byte[] Png()
    {
        byte[] pixels = new byte[8 * 4 * 4];
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 8; x++)
        {
            var color = Color(y < 2 ? (x < 4 ? 'R' : 'G') : (x < 4 ? 'B' : 'Y'));
            int at = (y * 8 + x) * 4;
            pixels[at] = color.B; pixels[at + 1] = color.G; pixels[at + 2] = color.R; pixels[at + 3] = 255;
        }
        var bitmap = BitmapSource.Create(8, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 8 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }

    private static void AssertCorners(BitmapSource bitmap, string corners)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        converted.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        (int X, int Y)[] points = [(0, 0), (bitmap.PixelWidth - 1, 0), (0, bitmap.PixelHeight - 1), (bitmap.PixelWidth - 1, bitmap.PixelHeight - 1)];
        for (int i = 0; i < points.Length; i++)
        {
            int at = (points[i].Y * bitmap.PixelWidth + points[i].X) * 4;
            var expected = Color(corners[i]);
            Assert.Equal(expected.B, pixels[at]); Assert.Equal(expected.G, pixels[at + 1]); Assert.Equal(expected.R, pixels[at + 2]);
        }
    }

    private static (byte R, byte G, byte B) Color(char color) => color switch
    { 'R' => (255, 0, 0), 'G' => (0, 255, 0), 'B' => (0, 0, 255), _ => (255, 255, 0) };

    private sealed class GatedReadStream(byte[] png) : Stream
    {
        private readonly MemoryStream inner = new(png);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public int ReadThread { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadThread = Environment.CurrentManagedThreadId;
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test did not release the PNG decoder.");
            return inner.Read(buffer, offset, count);
        }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => inner.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Release.Set(); inner.Dispose(); Release.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
