using System.Text.Json;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace Citrus.Windows.Updates;

internal static class UpdateBackupCleanup
{
    internal static readonly string[] JournalKeys = ["UpdateState", "UpdateBackupPath", "UpdatePreviousInventory", "UpdateInstalledInventory", "UpdatePreviousSdkLicense", "UpdatePreviousVersion"];

    internal static async Task<int?> RunUninstallCleanupAsync(IReadOnlyList<string> arguments)
    {
        const string command = "--cleanup-update-backup", fixture = command + "-fixture=";
        if (arguments.Count == 0 || !arguments[0].StartsWith(command, StringComparison.Ordinal)) return null;
        if (arguments.Count != 1) return 23;
        string name = "Citrus", suffix = "";
        if (arguments[0].StartsWith(fixture, StringComparison.Ordinal))
        {
            string identity = arguments[0][fixture.Length..];
            if (identity.Length != 32 || identity.Any(character => !"0123456789abcdef".Contains(character))) return 23;
            name += "-UpdateFixture-" + identity;
            suffix = "-fixture-" + identity;
        }
        else if (arguments[0] != command) return 23;
        try
        {
            using var user = WindowsIdentity.GetCurrent();
            using var process = Process.GetCurrentProcess();
            string? sid = user.User?.Value;
            if (sid is null || !GateHeld(@"Global\Citrus-Install-" + sid + suffix) ||
                !GateHeld($@"Local\Citrus-{sid}-{process.SessionId}{suffix}")) return 23;
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = hive.OpenSubKey(@"Software\" + name, writable: true);
            if (key is null) return 23;
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", name);
            var installation = UpdateInstallation.Validate(AppContext.BaseDirectory, directory,
                key.GetValue("InstallLocation") as string, key.GetValue("PackageInventory") as string,
                key.GetValue("RecoveryIdentity") as string, acceptedLicense: null);
            if (installation is null) return 23;
            string? state = key.GetValue("UpdateState") as string;
            if (string.IsNullOrEmpty(state)) return 0;
            if (state != "Committed") return 23;
            string inventory = installation.InventoryHash;
            if (!StringComparer.OrdinalIgnoreCase.Equals(key.GetValue("UpdateCleanupCompleteInventory") as string, inventory))
            {
                string? backup = key.GetValue("UpdateBackupPath") as string;
                if (backup is null || !await CleanupAsync(directory, inventory, backup,
                    key.GetValue("UpdatePreviousInventory") as string, key.GetValue("UpdateInstalledInventory") as string,
                    CancellationToken.None, () => { key.SetValue("UpdateCleanupCompleteInventory", inventory); key.Flush(); }).ConfigureAwait(false)) return 23;
            }
            ClearCommittedJournal(key, inventory);
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { return 23; }
    }

    private static bool GateHeld(string name)
    {
        Mutex gate;
        try { gate = Mutex.OpenExisting(name); }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        using var ownedGate = gate;
        bool acquired;
        try { acquired = gate.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) return true;
        gate.ReleaseMutex();
        return false;
    }
    internal static async Task ConfirmSuccessfulBootAsync(UpdateInstallation installation, CancellationToken ct)
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = hive.OpenSubKey(UpdateInstallation.RegistryPath, writable: true);
            if (key is null || !StringComparer.OrdinalIgnoreCase.Equals(key.GetValue("PackageInventory") as string, installation.InventoryHash)) return;
            if (!StringComparer.Ordinal.Equals(key.GetValue("UpdateState") as string, "Committed")) return;
            key.SetValue("LastSuccessfulInventory", installation.InventoryHash, RegistryValueKind.String);
            if (StringComparer.OrdinalIgnoreCase.Equals(key.GetValue("UpdateCleanupCompleteInventory") as string, installation.InventoryHash))
            { ClearCommittedJournal(key, installation.InventoryHash); return; }
            string? backup = key.GetValue("UpdateBackupPath") as string;
            string? previous = key.GetValue("UpdatePreviousInventory") as string;
            string? installed = key.GetValue("UpdateInstalledInventory") as string;
            if (backup is null) return;
            void CommitCleanup()
            {
                key.SetValue("UpdateCleanupCompleteInventory", installation.InventoryHash, RegistryValueKind.String);
                key.Flush();
            }
            if (!await CleanupAsync(installation.Directory, installation.InventoryHash, backup, previous, installed, ct, CommitCleanup).ConfigureAwait(false)) return;
            // A later installer cannot have acquired the resident's install gate while this process runs.
            ClearCommittedJournal(key, installation.InventoryHash);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { /* Keep the journal and remaining backup for a later successful startup. */ }
    }

    internal static async Task<bool> CleanupAsync(string installDirectory, string currentInventory, string backupPath,
        string? previousInventory, string? installedInventory, CancellationToken ct, Action? commitCleanup = null)
    {
        if (!UpdateInstallation.IsHash(previousInventory) || !UpdateInstallation.IsHash(currentInventory) ||
            !StringComparer.OrdinalIgnoreCase.Equals(currentInventory, installedInventory)) return false;
        string expected = Path.GetFullPath(installDirectory) + ".previous-" + previousInventory;
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(backupPath), expected)) return false;
        if (!Directory.Exists(backupPath)) return true;
        try
        {
            if (UpdateInstallation.HasLinkedAncestor(backupPath) || UpdateInstallation.HasLinkedAncestor(installDirectory)) return false;
            string manifest = Path.Combine(backupPath, "install-inventory.json");
            string currentManifest = Path.Combine(installDirectory, "install-inventory.json");
            if (UpdateInstallation.HasLinkedAncestor(manifest) || !UpdateInstallation.HashMatches(manifest, previousInventory!) ||
                UpdateInstallation.HasLinkedAncestor(currentManifest) || !UpdateInstallation.HashMatches(currentManifest, currentInventory)) return false;
            if (new FileInfo(manifest).Length > 16 * 1024 * 1024) return false;
            var files = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(manifest, ct).ConfigureAwait(false));
            if (files is null || files.Count > 100_000) return false;
            var paths = new List<(string Path, string Hash)>();
            foreach (var (relative, hash) in files)
            {
                if (!UpdateInstallation.IsHash(hash) || relative.Contains('\\') || relative.Contains(':') ||
                    Path.IsPathFullyQualified(relative) || relative.Split('/').Any(part => part is "" or "." or "..")) return false;
                string path = Path.GetFullPath(Path.Combine(backupPath, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(Path.GetFullPath(backupPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
                paths.Add((path, hash));
            }
            bool retry = false;
            foreach (var (path, hash) in paths)
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(path)) continue;
                try
                {
                    // Changed, linked and unrelated files retain ownership with the user.
                    if (!UpdateInstallation.HasLinkedAncestor(path) && UpdateInstallation.HashMatches(path, hash)) File.Delete(path);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { retry = true; }
            }
            if (retry) return false;
            var directories = paths.SelectMany(item => Ancestors(Path.GetDirectoryName(item.Path), backupPath))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(path => path.Length);
            foreach (string directory in directories)
            {
                ct.ThrowIfCancellationRequested();
                try { if (Directory.Exists(directory) && !UpdateInstallation.HasLinkedAncestor(directory)) Directory.Delete(directory, recursive: false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            // Retain the receipt until all cancelable cleanup work is complete, so interrupted cleanup is resumable.
            // Persist completion before removing that receipt; a crash after this point can finish clearing the journal without it.
            commitCleanup?.Invoke();
            File.Delete(manifest);
            try { Directory.Delete(backupPath, recursive: false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return false; }
    }

    internal static void ClearCommittedJournal(RegistryKey key, string installedInventory)
    {
        if (!StringComparer.Ordinal.Equals(key.GetValue("UpdateState") as string, "Committed")) return;
        // One durable receipt makes an interrupted multi-value registry cleanup resumable.
        // It is written only after owned backup cleanup has completed and matches this exact installed inventory.
        key.SetValue("UpdateCleanupCompleteInventory", installedInventory, RegistryValueKind.String);
        key.Flush();
        // Delete state last. A crash beforehand remains visibly committed and can be retried, never silently admitted as a new update.
        foreach (string name in JournalKeys.Where(name => name != "UpdateState")) key.DeleteValue(name, false);
        key.DeleteValue("UpdateState", false);
        key.Flush();
        key.DeleteValue("UpdateCleanupCompleteInventory", false);
    }

    private static IEnumerable<string> Ancestors(string? directory, string root)
    {
        while (directory is not null && (StringComparer.OrdinalIgnoreCase.Equals(directory, root) ||
               directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        { yield return directory; directory = Path.GetDirectoryName(directory); }
    }
}
