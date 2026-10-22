using System.Windows.Media;
using Citrus.Core;

namespace Citrus.Windows;

public sealed record OperationCard(OperationDescriptor Operation)
{
    public string Label => Operation.Label;
    public string Summary => Operation.Mode == OperationMode.Actions ? Describe(Operation) : Operation.IsGroup ? "Combine files" : "";
    public Geometry Icon => (Geometry)Application.Current.FindResource("Icon." + Category);
    private string Category => Operation.Id switch
    {
        var id when id.Contains("resize", StringComparison.Ordinal) => "Resize",
        var id when id.Contains("rotate", StringComparison.Ordinal) => "Rotate",
        var id when id.StartsWith("image", StringComparison.Ordinal) => "Image",
        var id when id.StartsWith("audio", StringComparison.Ordinal) => "Audio",
        var id when id.StartsWith("video", StringComparison.Ordinal) || id.StartsWith("media", StringComparison.Ordinal) => "Video",
        var id when id.StartsWith("archive", StringComparison.Ordinal) => "Archive",
        var id when id.StartsWith("pdf", StringComparison.Ordinal) || id.StartsWith("subtitle", StringComparison.Ordinal) => "Text",
        _ => "Folder"
    };
    public static string Describe(OperationDescriptor operation) => operation.Id switch
    {
        "image.resize" or "video.resize" or "image.compress" or "image.rotate" or "audio.trim" or "video.trim" or "video.speed" or "audio.normalize" => "",
        "archive.extract" => "Extract into a new folder. Stops at 100,000 entries or 20 GiB; nested archives stay packed.",
        "action.move" => "Move originals to a folder you choose. This changes their location.",
        "action.reveal" => "Show the included items in Explorer.",
        "action.copy" => "Copy included items to the Windows clipboard.",
        "action.float" => "Keep included items in a floating shelf for another drag.",
        var id when id.StartsWith("image.", StringComparison.Ordinal) || id == "images.pdf" => "Animated and multipage images are not supported.",
        _ => ""
    };
}
