using System.Windows.Media.Imaging;
using Citrus.Core.Editing;

namespace Citrus.Windows.Editors.Pdf;

/// <summary>Decode on the shared preview workers; only frozen pixels cross back to the dispatcher.</summary>
internal static class PdfPreviewBitmap
{
    internal static async Task<BitmapImage> DecodeAsync(Stream source, Rotation rotation,
        int decodePixelWidth, int decodePixelHeight, CancellationToken ct)
    {
        using var lease = await PreviewWorkScheduler.AcquireAsync(ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = source;
            image.Rotation = rotation;
            if (decodePixelWidth > 0) image.DecodePixelWidth = decodePixelWidth;
            if (decodePixelHeight > 0) image.DecodePixelHeight = decodePixelHeight;
            image.EndInit();
            image.Freeze();
            // WIC decoding itself is synchronous; cancellation must also reject a result
            // whose selection changed while the native codec was running.
            ct.ThrowIfCancellationRequested();
            return image;
        }, ct).ConfigureAwait(false);
    }
}
