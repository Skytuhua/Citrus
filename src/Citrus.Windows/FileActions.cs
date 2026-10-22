using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Citrus.Core;
using Microsoft.Win32;

namespace Citrus.Windows;

/// <summary>Native operations run once for the complete selection, outside the conversion queue.</summary>
public sealed class FileActions
{
    private readonly Func<string?> pickDestination;
    private readonly Action<ProcessStartInfo> launch;
    private readonly Action<IReadOnlyList<string>> floatPaths;
    private readonly Func<string?> terminalPath;
    private readonly CancellationToken stopping;
    internal Action? BeforeMovePerform { get; init; }
    public IReadOnlyList<string> LastMoveDestinations { get; private set; } = [];
    public static IReadOnlyList<OperationDescriptor> Operations { get; } = Array.AsReadOnly(new[]
    {
        new OperationDescriptor("action.copy", "Copy files", OperationMode.Actions, null, true, false),
        new OperationDescriptor("action.move", "Move originals…", OperationMode.Actions, null, true, false),
        new OperationDescriptor("action.paths", "Copy paths", OperationMode.Actions, null, true, false),
        new OperationDescriptor("action.reveal", "Reveal", OperationMode.Actions, null, true, false),
        new OperationDescriptor("action.terminal", "Open Terminal", OperationMode.Actions, null, true, false),
        new OperationDescriptor("action.float", "Float on shelf", OperationMode.Actions, null, true, false)
    });

    public FileActions(Func<string?>? pickDestination = null, Action<ProcessStartInfo>? launch = null,
        Action<IReadOnlyList<string>>? floatPaths = null, Func<string?>? terminalPath = null, CancellationToken cancellationToken = default)
    {
        stopping = cancellationToken;
        this.pickDestination = pickDestination ?? (() =>
        {
            var picker = new OpenFolderDialog { Title = "Move originals to" };
            return picker.ShowDialog() == true ? picker.FolderName : null;
        });
        this.launch = launch ?? (start => Process.Start(start)?.Dispose());
        this.floatPaths = floatPaths ?? (_ => throw new InvalidOperationException("The floating shelf is unavailable."));
        this.terminalPath = terminalPath ?? FindWindowsTerminal;
    }

    public async Task ExecuteAsync(string actionId, IReadOnlyList<string> paths)
    {
        stopping.ThrowIfCancellationRequested();
        string[] selection = Validate(paths);
        switch (actionId)
        {
            case "action.copy":
                await WithClipboardRetryAsync(() => Clipboard.SetDataObject(CreateCopyData(selection), true));
                break;
            case "action.paths":
                await WithClipboardRetryAsync(() => Clipboard.SetText(string.Join("\r\n", selection)));
                break;
            case "action.move":
                string? destination = pickDestination();
                if (destination is null) throw new OperationCanceledException("Move canceled.");
                stopping.ThrowIfCancellationRequested();
                LastMoveDestinations = await ShellFileMover.MoveAsync(selection, destination, BeforeMovePerform);
                break;
            case "action.reveal":
                foreach (string path in selection) launch(BuildRevealStart(path));
                break;
            case "action.terminal":
                foreach (string folder in selection.Select(ContainingDirectory).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string? terminal = terminalPath();
                    try { launch(BuildTerminalStart(folder, terminal)); }
                    catch (Win32Exception) when (terminal is not null) { launch(BuildTerminalStart(folder, null)); }
                }
                break;
            case "action.float": floatPaths(selection); break;
            default: throw new ArgumentException("Unknown native action.", nameof(actionId));
        }
    }

    public static string[] Validate(IReadOnlyList<string> paths)
    {
        var selection = paths.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!DropValidation.Accepts(paths, OperationMode.Actions) || selection.Any(path => !File.Exists(path) && !Directory.Exists(path)))
            throw new IOException("Choose existing files or folders. Download cloud files first.");
        return selection;
    }

    public static DataObject CreateCopyData(IReadOnlyList<string> paths)
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, Validate(paths));
        data.SetData("Preferred DropEffect", new MemoryStream([1, 0, 0, 0]));
        return data;
    }

    private static async Task WithClipboardRetryAsync(Action write)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { write(); return; }
            catch (COMException error) when ((uint)error.HResult == 0x800401D0 && attempt < 4) { await Task.Delay(50); }
        }
    }

    internal static void ValidateMove(string[] paths, string destination)
    {
        Validate(paths);
        string folder = Path.GetFullPath(destination);
        Validate([folder]);
        if (!Directory.Exists(folder)) throw new IOException("Choose an existing destination folder.");
        var targets = paths.Select(path => Path.Combine(folder, Path.GetFileName(Path.TrimEndingDirectorySeparator(path)))).ToArray();
        if (targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Length)
            throw new IOException("Two selected items have the same name. Move them separately to different folders.");
        for (int i = 0; i < paths.Length; i++)
        {
            if (Path.GetPathRoot(paths[i]) == Path.TrimEndingDirectorySeparator(paths[i]) || string.IsNullOrEmpty(Path.GetFileName(paths[i])))
                throw new IOException("A drive root cannot be moved.");
            if (File.Exists(targets[i]) || Directory.Exists(targets[i])) throw new IOException($"Move stopped before changing files: '{targets[i]}' already exists.");
            if (Directory.Exists(paths[i]) && (IsWithin(folder, paths[i]) || paths.Any(other => !string.Equals(other, paths[i], StringComparison.OrdinalIgnoreCase) && IsWithin(other, paths[i]))))
                throw new IOException("Do not move a folder into itself or select both a folder and an item inside it.");
        }
    }

    private static bool IsWithin(string path, string folder) =>
        string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static ProcessStartInfo BuildRevealStart(string path)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
        start.ArgumentList.Add("/select,");
        start.ArgumentList.Add(path);
        return start;
    }

    // A folder selection opens a terminal inside it; a file selection uses its parent.
    private static string ContainingDirectory(string path) => Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
    private static string? FindWindowsTerminal()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        return File.Exists(path) ? path : null;
    }
    public static ProcessStartInfo BuildTerminalStart(string directory, string? windowsTerminal)
    {
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(windowsTerminal ?? (File.Exists(powershell) ? powershell : Path.Combine(Environment.SystemDirectory, "cmd.exe")))
        { UseShellExecute = false, WorkingDirectory = directory };
        if (windowsTerminal is not null) { start.ArgumentList.Add("-d"); start.ArgumentList.Add(directory); }
        else if (string.Equals(Path.GetFileName(start.FileName), "powershell.exe", StringComparison.OrdinalIgnoreCase))
        { start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoExit"); }
        else start.ArgumentList.Add("/D");
        return start;
    }
}

/// <summary>Discovery only: deliberately excluded from the conversion worker engines.</summary>
internal sealed class NativeActionCatalog : IConversionEngine
{
    public IReadOnlyList<OperationDescriptor> Operations => FileActions.Operations;
    public bool CanExecute(OperationRequest request) => request.Operation.Mode == OperationMode.Actions &&
        Operations.Any(operation => operation.Id == request.Operation.Id) && DropValidation.Accepts(request.Inputs, OperationMode.Actions);
    public Task<ConversionResult> ExecuteAsync(OperationRequest request, IProgress<ConversionProgress>? progress, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Native actions must be submitted as a complete selection through FileActions.");
}
