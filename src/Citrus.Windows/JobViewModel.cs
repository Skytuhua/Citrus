using System.ComponentModel;
using Citrus.Core;

namespace Citrus.Windows;

/// <summary>UI-thread projection of one queue job; output names always come from committed engine results.</summary>
public sealed class JobViewModel(ConversionJob job) : INotifyPropertyChanged
{
    private IReadOnlyList<string>? outputPaths;
    private IReadOnlyList<OutputViewModel> outputRows = [];
    public ConversionJob Job { get; } = job;
    public string Source => string.Join(", ", Job.Request.Inputs.Select(Path.GetFileName));
    public string SourcePaths => string.Join(Environment.NewLine, Job.Request.Inputs);
    public string Operation => Job.Request.Operation.Label + (Job.Request.Operation.IsGroup ? " · group" : "");
    public string Status => Job.State switch
    {
        ConversionJobState.Succeeded => $"Succeeded · {Job.Outputs.Count} output(s)",
        ConversionJobState.Failed => "Failed · " + Job.Error?.Message,
        ConversionJobState.Canceled => "Canceled",
        ConversionJobState.Queued => "Queued",
        _ => "Running · " + (Job.Progress?.Message ?? "Preparing")
    };
    public bool CanCancel => Job.State is ConversionJobState.Queued or ConversionJobState.Running;
    public bool CanRetry => Job.State is ConversionJobState.Failed or ConversionJobState.Canceled;
    public bool CanChooseFolder => Job.State == ConversionJobState.Failed;
    public bool IsIndeterminate => CanCancel && (Job.State == ConversionJobState.Queued || Job.Progress?.Fraction is not double fraction || !double.IsFinite(fraction));
    public double Percent => Job.State == ConversionJobState.Succeeded ? 100 : Job.Progress?.Fraction is double value && double.IsFinite(value) ? Math.Clamp(value * 100, 0, 100) : 0;
    public IReadOnlyList<OutputViewModel> Outputs
    {
        get
        {
            var paths = Job.Outputs;
            if (!ReferenceEquals(paths, outputPaths))
            {
                outputRows = Array.AsReadOnly(paths.Select(path => new OutputViewModel(path)).ToArray());
                outputPaths = paths;
            }
            return outputRows;
        }
    }
    public IReadOnlyList<string> Warnings => Job.Warnings;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

public sealed record OutputViewModel(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);
}
