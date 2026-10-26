using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Citrus.Conversion.Editing.Pdf;
using Xunit.Abstractions;

namespace Citrus.Conversion.Tests;

public sealed class WindowsOfficeStartupFactAttribute : FactAttribute
{
    public WindowsOfficeStartupFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires the real pinned Windows Python runtime and AppContainer.";
    }
}

/// <summary>Checkpoint diagnostics use the admitted native bundle and production isolation, without changing worker bytes.</summary>
public sealed class OfflineWorkerStartupTests(ITestOutputHelper output)
{
    [WindowsOfficeStartupFact]
    public async Task Bundled_python_starts_and_captures_standard_output_and_error()
    {
        string pack = FeaturePackLocator.Find(), program = Path.Combine(pack, "libreoffice", "program");
        string wrapper = Path.Combine(program, "python.exe");
        string core = Path.Combine(program, "python-core-3.13.15", "bin", "python.exe");
        string root = Path.Combine(Path.GetTempPath(), "citrus-python-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ReportBinary("wrapper", wrapper); ReportBinary("interpreter", core);
            ReportBinary("python313", Path.Combine(program, "python313.dll"));
            foreach (string name in new[] { "vcruntime140.dll", "vcruntime140_1.dll", "vcruntime140_threads.dll", "msvcp140.dll" })
            {
                ReportBinary("pinned-crt/" + name, Path.Combine(pack, "libreoffice", "System64", name));
                ReportBinary("program-crt/" + name, Path.Combine(program, name));
                ReportBinary("system-crt/" + name, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name));
            }
            ReportAcl("source/bundle", pack); ReportAcl("source/program", program);
            ReportAcl("source/python313", Path.Combine(program, "python313.dll"));
            ReportAcl("source/core-bin", Path.GetDirectoryName(core)!); ReportAcl("source/interpreter", core);
            var probes = new List<Probe>();
            // Collect every comparison before asserting, so one startup failure
            // cannot hide the interpreter's stderr or the other checkpoints.
            foreach ((string kind, string executable) in new[] { ("wrapper", wrapper), ("direct", core) })
                foreach (string mode in new[] { "version", "no-site", "normal" })
                    probes.Add(await RunProbeAsync(kind, mode, executable, program, pack, root));
            // These clones vary only DLL placement. Every source/copy byte is
            // checked against the existing runtime catalog before it is executed.
            // Each clone has its own admitted read-only bundle and writable work.
            var pins = ReadClonePins(pack);
            foreach (string layout in new[] { "same-layout", "python-dll-beside-core", "python-and-crt-beside-core" })
            {
                string clone = Path.Combine(root, "clone-" + layout, "bundle");
                CopyPinnedClone(pack, clone, pins, layout);
                string cloneProgram = Path.Combine(clone, "libreoffice", "program");
                string cloneCore = Path.Combine(cloneProgram, "python-core-3.13.15", "bin", "python.exe");
                ReportAcl(layout + "/bundle", clone); ReportAcl(layout + "/program", cloneProgram);
                ReportAcl(layout + "/python313", Path.Combine(cloneProgram, "python313.dll"));
                ReportAcl(layout + "/core-bin", Path.GetDirectoryName(cloneCore)!); ReportAcl(layout + "/interpreter", cloneCore);
                foreach (string mode in new[] { "version", "normal" })
                    probes.Add(await RunProbeAsync("clone-" + layout, mode, cloneCore, cloneProgram, clone, root, CloneCheckpointScript));
            }
            output.WriteLine(JsonSerializer.Serialize(new { Integrity = "source-after-probes", Files = pins.Length, Digest = InventoryDigest(pack, pins) }));
            var normal = probes.Single(probe => probe.Kind == "wrapper" && probe.Mode == "normal");
            Assert.True(normal.ExitCode == 0, "Bundled Python failed: " + JsonSerializer.Serialize(normal));
            Assert.Contains("\"Stage\": \"complete\"", normal.Checkpoints, StringComparison.Ordinal);
            Assert.Contains("CITRUS_PYTHON_STDOUT", normal.Log, StringComparison.Ordinal);
            Assert.Contains("CITRUS_PYTHON_STDERR", normal.Log, StringComparison.Ordinal);
        }
        finally { OfflineStagingFiles.DeletePrivateTree(root); }
    }

    private async Task<Probe> RunProbeAsync(string kind, string mode, string executable, string program, string pack, string root, string? script = null)
    {
        string work = Path.Combine(root, kind + "-" + mode); Directory.CreateDirectory(work);
        string checkpoints = Path.Combine(work, "python-checkpoints.jsonl");
        string role = kind == "wrapper" ? "wrapper" : "direct";
        string[] arguments = mode == "version" ? ["--version"] : mode == "no-site"
            ? ["-S", "-c", script ?? CheckpointScript, program, role] : ["-c", script ?? CheckpointScript, program, role];
        int? code = null; string log = "", failure = "";
        try
        {
            var result = await OfflineToolRunner.RunAsync(executable, arguments, pack, work, TimeSpan.FromSeconds(20), default);
            code = result.ExitCode; log = result.Log;
        }
        catch (Exception error) { failure = error.GetType().Name + ": " + error.Message; }
        // Read before production/private-tree cleanup. Only our own runtime
        // metadata and checkpoints are recorded; no host environment is dumped.
        string stages = File.Exists(checkpoints) ? await File.ReadAllTextAsync(checkpoints) : "";
        var probe = new Probe(kind, mode, code, log, stages, failure);
        output.WriteLine(JsonSerializer.Serialize(probe));
        return probe;
    }

    private void ReportBinary(string label, string path)
    {
        try
        {
            if (!File.Exists(path)) { output.WriteLine(JsonSerializer.Serialize(new { Binary = label, Exists = false })); return; }
            var version = FileVersionInfo.GetVersionInfo(path);
            output.WriteLine(JsonSerializer.Serialize(new { Binary = label, Exists = true, Bytes = new FileInfo(path).Length,
                version.FileVersion, version.ProductVersion, Imports = ReadImports(path) }));
        }
        catch (Exception error) { output.WriteLine(JsonSerializer.Serialize(new { Binary = label, Error = error.GetType().Name + ": " + error.Message })); }
    }

    private void ReportAcl(string label, string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            FileSystemSecurity acl = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            output.WriteLine(JsonSerializer.Serialize(new { Acl = label, Snapshot = "before-lease", acl.AreAccessRulesProtected,
                Descriptor = acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access) }));
        }
        catch (Exception error) { output.WriteLine(JsonSerializer.Serialize(new { Acl = label, Error = error.GetType().Name + ": " + error.Message })); }
    }

    private static FilePin[] ReadClonePins(string pack)
    {
        string catalog = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pack))!, "feature-pack-runtime.sha256.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(catalog));
        return document.RootElement.GetProperty("files").EnumerateArray()
            .Select(file => new FilePin(file.GetProperty("path").GetString()!, file.GetProperty("sha256").GetString()!, file.GetProperty("bytes").GetInt64()))
            .Where(file => file.Relative.StartsWith("libreoffice/program/python-core-3.13.15/", StringComparison.Ordinal)
                || file.Relative is "libreoffice/program/python.exe" or "libreoffice/program/python3.dll" or "libreoffice/program/python313.dll"
                || file.Relative.StartsWith("libreoffice/System64/", StringComparison.Ordinal) && file.Relative.EndsWith(".dll", StringComparison.Ordinal))
            .OrderBy(file => file.Relative, StringComparer.Ordinal).ToArray();
    }

    private void CopyPinnedClone(string source, string clone, FilePin[] pins, string layout)
    {
        string sourceDigest = InventoryDigest(source, pins);
        foreach (var pin in pins)
        {
            string destination = PinnedPath(clone, pin.Relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(PinnedPath(source, pin.Relative), destination);
        }
        string cloneDigest = InventoryDigest(clone, pins);
        Assert.Equal(sourceDigest, cloneDigest);
        string coreBin = Path.Combine(clone, "libreoffice", "program", "python-core-3.13.15", "bin");
        var aliases = new List<object>();
        if (layout != "same-layout")
        {
            var python = pins.Single(pin => pin.Relative == "libreoffice/program/python313.dll");
            Alias(python);
        }
        if (layout == "python-and-crt-beside-core")
            foreach (var crt in pins.Where(pin => pin.Relative.StartsWith("libreoffice/System64/", StringComparison.Ordinal))) Alias(crt);
        output.WriteLine(JsonSerializer.Serialize(new { Clone = layout, Files = pins.Length, Bytes = pins.Sum(pin => pin.Bytes),
            SourceInventorySha256 = sourceDigest, CloneInventorySha256 = cloneDigest, Aliases = aliases }));
        void Alias(FilePin pin)
        {
            string path = Path.Combine(coreBin, Path.GetFileName(pin.Relative));
            File.Copy(PinnedPath(source, pin.Relative), path);
            string digest = FileDigest(path); Assert.Equal(pin.Sha256, digest, ignoreCase: true);
            aliases.Add(new { Source = pin.Relative, Destination = "core-bin/" + Path.GetFileName(path), Sha256 = digest });
        }
    }

    private static string InventoryDigest(string root, FilePin[] pins)
    {
        var records = new StringBuilder();
        foreach (var pin in pins)
        {
            string path = PinnedPath(root, pin.Relative);
            Assert.Equal(pin.Bytes, new FileInfo(path).Length);
            string digest = FileDigest(path); Assert.Equal(pin.Sha256, digest, ignoreCase: true);
            records.Append(pin.Relative).Append(' ').Append(digest).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(records.ToString())));
    }

    private static string PinnedPath(string root, string relative)
    {
        string prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string result = Path.GetFullPath(Path.Combine(root, relative));
        Assert.True(result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase), "Runtime pin escaped its owned bundle");
        return result;
    }

    private static string FileDigest(string path)
    {
        using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed record FilePin(string Relative, string Sha256, long Bytes);

    private static string[] ReadImports(string path)
    {
        using var stream = File.OpenRead(path); using var pe = new PEReader(stream);
        var directory = pe.PEHeaders.PEHeader?.ImportTableDirectory ?? default;
        if (directory.RelativeVirtualAddress == 0) return [];
        var descriptors = pe.GetSectionData(directory.RelativeVirtualAddress).GetContent();
        var names = new List<string>();
        for (int offset = 0; offset + 20 <= descriptors.Length && names.Count < 256; offset += 20)
        {
            int name = BinaryPrimitives.ReadInt32LittleEndian(descriptors.AsSpan(offset + 12, 4));
            if (name == 0) break;
            var bytes = pe.GetSectionData(name).GetContent(); int length = 0;
            while (length < bytes.Length && length < 512 && bytes[length] != 0) length++;
            names.Add(Encoding.ASCII.GetString(bytes.AsSpan(0, length)));
        }
        return names.ToArray();
    }

    private sealed record Probe(string Kind, string Mode, int? ExitCode, string Log, string Checkpoints, string Failure);

    // The first statement needs only builtins. It survives dropped inherited
    // stdio and distinguishes interpreter startup from later imports/DLL loads.
    // The direct-interpreter comparison emulates wrapper setup in this owned
    // diagnostic script only; production and the pinned worker remain unchanged.
    // The minimal clone intentionally lacks LibreOffice/UNO. Its probe stops
    // after normal Python/ctypes/socket imports rather than asserting LO access.
    private static string CloneCheckpointScript => CheckpointScript[..CheckpointScript.IndexOf("library = ctypes.CDLL", StringComparison.Ordinal)]
        + "search.close()\nstage(\"complete\")\n";

    private const string CheckpointScript = """
open("python-checkpoints.jsonl", "a", encoding="utf-8").write('{"Stage":"entered"}\n')
import sys
open("python-checkpoints.jsonl", "a", encoding="utf-8").write('{"Stage":"imported-sys"}\n')
import os
open("python-checkpoints.jsonl", "a", encoding="utf-8").write('{"Stage":"imported-os"}\n')
import json
import pathlib

def stage(name, **details):
    with open("python-checkpoints.jsonl", "a", encoding="utf-8") as sink:
        sink.write(json.dumps(dict(Stage=name, **details)) + "\n")

program = pathlib.Path(sys.argv[1])
stage("interpreter", Executable=sys.executable, Prefix=sys.prefix, Path=sys.path,
      Stdout=sys.stdout is not None, Stderr=sys.stderr is not None,
      UserProfile=os.environ.get("USERPROFILE"), Temp=os.environ.get("TEMP"))
if sys.stdout is not None:
    print("CITRUS_PYTHON_STDOUT", flush=True)
if sys.stderr is not None:
    print("CITRUS_PYTHON_STDERR", file=sys.stderr, flush=True)
if sys.argv[2] == "direct":
    # The LO wrapper passes Python home as argv[0], so the worker sees its
    # parent as program. Emulate that observable path without a production hook.
    sys.executable = str(program / "python.exe")
    os.environ["UNO_PATH"] = str(program)
    os.environ["URE_BOOTSTRAP"] = "vnd.sun.star.pathname:" + str(program / "fundamental.ini")
    for path in (program, program / "python-core-3.13.15" / "lib", program / "python-core-3.13.15" / "lib" / "site-packages"):
        if str(path) not in sys.path:
            sys.path.append(str(path))
    stage("direct-wrapper-environment", Executable=sys.executable)
search = os.add_dll_directory(str(program))
stage("dll-search-added")
import ctypes
stage("imported-ctypes")
import socket
stage("imported-socket")
library = ctypes.CDLL(str(program / "mergedlo.dll"))
stage("loaded-mergedlo")
import uno
stage("imported-uno")
search.close()
stage("complete")
""";
}
