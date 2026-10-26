using System.Diagnostics;
using System.Text.Json;
using Citrus.Conversion.Editing.Pdf;

namespace Citrus.Conversion.Tests;

public sealed class WindowsOfficeStartupFactAttribute : FactAttribute
{
    public WindowsOfficeStartupFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires the pinned Windows Python runtime and AppContainer.";
    }
}

[CollectionDefinition("Offline worker startup", DisableParallelization = true)]
public sealed class OfflineWorkerStartupCollection;

[Collection("Offline worker startup")]
public sealed class OfflineWorkerStartupTests
{
    [WindowsOfficeStartupFact]
    public async Task Concurrent_unrelated_process_does_not_retain_the_worker_log()
    {
        string pack = FeaturePackLocator.Find();
        string work = Path.Combine(Path.GetTempPath(), "citrus-python-handles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        Task<OfflineToolResult>? worker = null;
        Process? unrelated = null;
        try
        {
            worker = OfflineToolRunner.RunAsync(Path.Combine(pack, "libreoffice", "program", "python.exe"),
                ["-c", "import pathlib,time; pathlib.Path('ready').write_text('ready'); exec(\"while not pathlib.Path('release').exists(): time.sleep(0.01)\")"],
                pack, work, TimeSpan.FromSeconds(30), default);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(Path.Combine(work, "ready")))
            {
                if (worker.IsCompleted) Assert.Fail("Worker exited before the handle-inheritance checkpoint: " + (await worker).Log);
                await Task.Delay(10, wait.Token);
            }
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string argument in new[] { "/d", "/q", "/c", "set /p citrusHold=" }) start.ArgumentList.Add(argument);
            unrelated = Process.Start(start)!;
            File.WriteAllText(Path.Combine(work, "release"), "release");
            Assert.Equal(0, (await worker).ExitCode);
            Assert.False(unrelated.HasExited, "The unrelated process must stay alive while log ownership is checked.");
            // An inherited duplicate would keep the log open even after its
            // actual worker and the runner have retired. Check exclusivity
            // while the independently started process is still alive.
            using var exclusive = File.Open(Directory.GetFiles(work, "worker-*.log").Single(),
                FileMode.Open, FileAccess.Read, FileShare.None);
        }
        finally
        {
            File.WriteAllText(Path.Combine(work, "release"), "release");
            if (unrelated is not null)
            {
                if (!unrelated.HasExited) unrelated.Kill(entireProcessTree: true);
                await unrelated.WaitForExitAsync(); unrelated.Dispose();
            }
            try { if (worker is not null) await worker; }
            finally { OfflineStagingFiles.DeletePrivateTree(work); }
        }
    }

    [WindowsOfficeStartupFact]
    public async Task Bundled_python_starts_and_captures_standard_output_and_error()
    {
        string pack = FeaturePackLocator.Find();
        string program = Path.Combine(pack, "libreoffice", "program");
        string work = Path.Combine(Path.GetTempPath(), "citrus-python-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // Exercise the public entry point and real native imports. Preserve
            // the vendor's sys.executable convention and admitted stdio handles.
            var result = await OfflineToolRunner.RunAsync(Path.Combine(program, "python.exe"),
                ["-c", CheckpointScript, program], pack, work, TimeSpan.FromSeconds(30), default);
            Assert.True(result.ExitCode == 0, "Bundled Python failed: " + result.Log);
            Assert.Contains("CITRUS_PYTHON_STDOUT", result.Log, StringComparison.Ordinal);
            Assert.Contains("CITRUS_PYTHON_STDERR", result.Log, StringComparison.Ordinal);
            using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(work, "startup.json")));
            Assert.Equal("complete", receipt.RootElement.GetProperty("Stage").GetString());
            Assert.Equal(Path.GetFullPath(Path.Combine(program, "python-core-3.13.15")),
                receipt.RootElement.GetProperty("Executable").GetString(), ignoreCase: true);
        }
        finally { OfflineStagingFiles.DeletePrivateTree(work); }
    }

    private const string CheckpointScript = """
import sys
import os
import json
import pathlib
import ctypes
import socket

program = pathlib.Path(sys.argv[1]).absolute()
assert pathlib.Path(sys.executable).parent == program, sys.executable
assert os.environ["UNO_PATH"] == str(program)
assert os.environ["URE_BOOTSTRAP"] == "vnd.sun.star.pathname:" + str(program / "fundamental.ini")
assert sys.stdout is not None and sys.stderr is not None
print("CITRUS_PYTHON_STDOUT", flush=True)
print("CITRUS_PYTHON_STDERR", file=sys.stderr, flush=True)
search = os.add_dll_directory(str(program))
library = ctypes.CDLL(str(program / "mergedlo.dll"))
import uno
with open("startup.json", "w", encoding="utf-8") as sink:
    json.dump(dict(Stage="complete", Executable=sys.executable), sink)
search.close()
""";
}
