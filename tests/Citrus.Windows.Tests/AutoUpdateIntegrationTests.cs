using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using Microsoft.Win32;
using System.Net;
using System.Net.Http;
using Citrus.Core.Updates;
using Citrus.Windows.Updates;

namespace Citrus.Windows.Tests;

public sealed class AutoUpdateIntegrationTests
{
    private static UpdateCandidate Candidate => new("0.3.0-preview.3", 4, 5,
        new Uri("https://github.com/Skytuhua/Citrus/releases/download/v0.3.0-preview.3/Citrus-Setup.exe"), 10, new string('a', 64));

    [Fact]
    public void Isolated_and_development_hosts_never_start_automatic_network_work()
    {
        Assert.True(App.ShouldStartAutomaticUpdates(new()));
        Assert.False(App.ShouldStartAutomaticUpdates(new(SettingsRoot: "test-settings")));
        Assert.False(App.ShouldStartAutomaticUpdates(new(InstanceSuffix: Guid.NewGuid().ToString("N"))));
        Assert.False(App.ShouldStartAutomaticUpdates(new(ObserveGestures: false)));
        Assert.False(App.ShouldStartAutomaticUpdates(App.CreateStartupOptions(["Citrus.Windows.exe", "--development"])));
    }

    [Fact]
    public void Every_busy_boundary_defers_update_application()
    {
        var idle = new UpdateIdleState(false, false, true, false, false, false, false);
        Assert.True(idle.CanApply);
        Assert.False((idle with { Quitting = true }).CanApply);
        Assert.False((idle with { Preparing = true }).CanApply);
        Assert.False((idle with { ActiveActionCompleted = false }).CanApply);
        Assert.False((idle with { PendingJobs = true }).CanApply);
        Assert.False((idle with { WheelActive = true }).CanApply);
        Assert.False((idle with { PointerHeld = true }).CanApply);
        Assert.False((idle with { VisibleWorkspaceOrDialog = true }).CanApply);
    }

    [Fact]
    public void Rapid_consecutive_updates_wait_for_successful_cleanup_and_a_cleared_install_journal()
    {
        var idle = new UpdateIdleState(false, false, true, false, false, false, false);
        var cleanup = new TaskCompletionSource();
        Assert.False(idle.CanApplyAfterCleanup(cleanup.Task.IsCompletedSuccessfully, installTransactionPending: true));
        cleanup.SetCanceled();
        Assert.False(idle.CanApplyAfterCleanup(cleanup.Task.IsCompletedSuccessfully, installTransactionPending: false));
        Assert.False(idle.CanApplyAfterCleanup(Task.CompletedTask.IsCompletedSuccessfully, UpdateInstallation.IsTransactionPending("Committed")));
        Assert.False(idle.CanApplyAfterCleanup(Task.CompletedTask.IsCompletedSuccessfully, UpdateInstallation.IsTransactionPending("Installing")));
        Assert.True(idle.CanApplyAfterCleanup(Task.CompletedTask.IsCompletedSuccessfully, UpdateInstallation.IsTransactionPending(null)));
    }

    [Fact]
    public async Task Turning_automatic_updates_off_during_verification_prevents_the_handoff_but_manual_restart_remains_allowed()
    {
        var verification = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool enabled = true;
        int launches = 0;
        var pending = UpdateApplyGate.VerifyAndApplyAsync(() => UpdateIdleState.HasUpdateConsent(explicitRestart: false, enabled),
            _ => verification.Task, () => launches++, CancellationToken.None);
        enabled = false; verification.SetResult(true);
        Assert.False(await pending);
        Assert.Equal(0, launches);
        Assert.True(await UpdateApplyGate.VerifyAndApplyAsync(() => UpdateIdleState.HasUpdateConsent(explicitRestart: true, enabled),
            _ => Task.FromResult(true), () => launches++, CancellationToken.None));
        Assert.Equal(1, launches);
    }

    [Theory]
    [InlineData("cleanup")]
    [InlineData("journal")]
    [InlineData("editing")]
    [InlineData("cancel")]
    public async Task A_boundary_that_changes_during_verification_prevents_the_real_handoff_sequence(string boundary)
    {
        var verification = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cleanup = true, journal = false, editing = false; int launches = 0;
        using var cancellation = new CancellationTokenSource();
        bool CanApply() => new UpdateIdleState(false, editing, true, false, false, false, false).CanApplyAfterCleanup(cleanup, journal);
        var pending = UpdateApplyGate.VerifyAndApplyAsync(CanApply, _ => verification.Task, () => launches++, cancellation.Token);
        if (boundary == "cleanup") cleanup = false;
        if (boundary == "journal") journal = true;
        if (boundary == "editing") editing = true;
        if (boundary == "cancel") cancellation.Cancel();
        verification.SetResult(true);
        if (boundary == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else Assert.False(await pending);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task Concurrent_checks_share_one_download_and_leave_the_ready_update_available()
    {
        var backend = new FakeBackend { Candidate = Candidate };
        var service = new AutomaticUpdateService(backend, () => true, canStage: true);
        var first = service.CheckAsync(manual: true, CancellationToken.None);
        await backend.Entered.Task;
        await service.CheckAsync(manual: true, CancellationToken.None);
        Assert.Equal(1, backend.Checks);
        backend.Release.TrySetResult();
        await first;
        Assert.Equal(1, backend.Downloads);
        Assert.NotNull(service.Ready);
        await service.CheckAsync(manual: true, CancellationToken.None);
        Assert.Equal(1, backend.Downloads);
        Assert.Equal(Candidate, service.Ready!.Candidate);
    }

    [Fact]
    public async Task Opt_out_skips_automatic_checks_but_a_manual_check_remains_available()
    {
        var backend = new FakeBackend { Candidate = Candidate }; backend.Release.SetResult();
        var service = new AutomaticUpdateService(backend, () => false, canStage: true);
        await service.CheckAsync(manual: false, CancellationToken.None);
        Assert.Equal(0, backend.Checks);
        await service.CheckAsync(manual: true, CancellationToken.None);
        Assert.Equal(1, backend.Downloads);
    }

    [Fact]
    public async Task A_portable_or_legacy_install_never_stages_an_unusable_automatic_installer()
    {
        var backend = new FakeBackend { Candidate = Candidate }; backend.Release.SetResult();
        var service = new AutomaticUpdateService(backend, () => true, canStage: false);
        await service.CheckAsync(manual: true, CancellationToken.None);
        Assert.Equal(0, backend.Downloads);
        Assert.Null(service.Ready);
        Assert.Contains("Install", service.Status.Message);
    }

    [Fact]
    public async Task Failure_and_cancellation_never_publish_an_unverified_ready_installer()
    {
        var backend = new FakeBackend { Candidate = Candidate, DownloadFailure = new IOException("offline") }; backend.Release.SetResult();
        var service = new AutomaticUpdateService(backend, () => true, canStage: true);
        await service.CheckAsync(manual: true, CancellationToken.None);
        Assert.Null(service.Ready);
        Assert.Contains("could not", service.Status.Message);
        backend.DownloadFailure = new OperationCanceledException();
        await service.CheckAsync(manual: true, CancellationToken.None);
        Assert.Null(service.Ready);
        Assert.False(service.Status.Checking);
    }

    [Fact]
    public async Task An_already_canceled_manual_check_never_escapes_to_a_UI_event_handler()
    {
        var backend = new FakeBackend { Candidate = Candidate };
        var service = new AutomaticUpdateService(backend, () => true, canStage: true);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await service.CheckAsync(manual: true, canceled.Token);
        Assert.Equal(0, backend.Checks); Assert.False(service.Status.Checking); Assert.Null(service.Ready);
    }

    [Fact]
    public async Task A_failed_version_is_not_automatically_downloaded_and_restarted_again()
    {
        var backend = new FakeBackend { Candidate = Candidate }; backend.Release.SetResult();
        var service = new AutomaticUpdateService(backend, () => true, canStage: true, failedVersion: Candidate.Version);
        await service.CheckAsync(manual: false, CancellationToken.None);
        await service.CheckAsync(manual: true, CancellationToken.None);
        Assert.Equal(0, backend.Downloads); Assert.Null(service.Ready);
        Assert.Contains("manually", service.Status.Message);
        backend.Candidate = Candidate with { Version = "0.3.0-preview.4" };
        await service.CheckAsync(manual: false, CancellationToken.None);
        Assert.Equal(1, backend.Downloads); Assert.NotNull(service.Ready);
    }

    [Fact]
    public void Ownership_and_matching_explicit_license_acceptance_are_required_for_silent_updates()
    {
        using var files = new NativeFiles();
        string manifest = files.File("install-inventory.json", "{}");
        string license = Write(files, "notices/presentation/Windows-SDK-License.rtf", "fixture component license");
        string inventory = Hash(manifest), accepted = Hash(license);
        string path = files.Root;
        Assert.True(UpdateInstallation.Validate(path, path, path, inventory, path + "|" + inventory, accepted)!.CanUpdateSilently);
        Assert.False(UpdateInstallation.Validate(path, path, path, inventory, path + "|" + inventory, null)!.CanUpdateSilently);
        Assert.False(UpdateInstallation.Validate(path, path, path, inventory, path + "|" + inventory, new string('0', 64))!.CanUpdateSilently);
        Assert.Null(UpdateInstallation.Validate(Path.Combine(path, "portable"), path, path, inventory, path + "|" + inventory, accepted));
        Assert.Null(UpdateInstallation.Validate(path, path, path, new string('0', 64), path + "|" + inventory, accepted));
        Assert.Null(UpdateInstallation.Validate(path, path, path, inventory, path + "|wrong", accepted));
    }

    [Fact]
    public async Task Uninstall_cleanup_requires_the_installer_gates_and_never_treats_other_startup_arguments_as_cleanup()
    {
        Assert.Null(await UpdateBackupCleanup.RunUninstallCleanupAsync(["--development"]));
        Assert.Equal(23, await UpdateBackupCleanup.RunUninstallCleanupAsync(["--cleanup-update-backup-fixture=../outside"]));
        string identity = Guid.NewGuid().ToString("N");
        Assert.Equal(23, await UpdateBackupCleanup.RunUninstallCleanupAsync(["--cleanup-update-backup-fixture=" + identity]));
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        Assert.Null(hive.OpenSubKey(@"Software\Citrus-UpdateFixture-" + identity));
    }

    [Fact]
    public async Task Successful_boot_cleanup_deletes_only_hash_matched_owned_backup_files()
    {
        using var files = new NativeFiles();
        string install = Path.Combine(files.Root, "Citrus"); Directory.CreateDirectory(install);
        string currentManifest = Path.Combine(install, "install-inventory.json"); await File.WriteAllTextAsync(currentManifest, "{}");
        string oldOwned = Write(files, "payload/old.exe", "owned old payload");
        var inventory = new Dictionary<string, string> { ["old.exe"] = Hash(oldOwned), ["changed.txt"] = Hash(oldOwned) };
        string sourceManifest = Write(files, "payload/install-inventory.json", JsonSerializer.Serialize(inventory));
        string previous = Hash(sourceManifest), current = Hash(currentManifest);
        string backup = install + ".previous-" + previous; Directory.Move(Path.Combine(files.Root, "payload"), backup);
        string changed = Path.Combine(backup, "changed.txt"), unrelated = Path.Combine(backup, "user-note.txt"), uninstaller = Path.Combine(backup, "Uninstall.exe");
        await File.WriteAllTextAsync(changed, "user changed this"); await File.WriteAllTextAsync(unrelated, "keep unrelated"); await File.WriteAllTextAsync(uninstaller, "uninventoried uninstaller");
        Assert.True(await UpdateBackupCleanup.CleanupAsync(install, current, backup, previous, current, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(backup, "old.exe")));
        Assert.Equal("user changed this", await File.ReadAllTextAsync(changed));
        Assert.Equal("keep unrelated", await File.ReadAllTextAsync(unrelated));
        Assert.True(File.Exists(uninstaller));
    }

    [Fact]
    public async Task Invalid_backup_journals_and_path_traversal_never_delete_content()
    {
        using var files = new NativeFiles();
        string install = Path.Combine(files.Root, "Citrus"); Directory.CreateDirectory(install);
        string currentManifest = Path.Combine(install, "install-inventory.json"); await File.WriteAllTextAsync(currentManifest, "{}");
        string unrelated = files.File("user.txt", "keep this");
        string content = JsonSerializer.Serialize(new Dictionary<string, string> { ["../user.txt"] = Hash(unrelated) });
        string source = Write(files, "old/install-inventory.json", content), previous = Hash(source), current = Hash(currentManifest);
        string backup = install + ".previous-" + previous; Directory.Move(Path.Combine(files.Root, "old"), backup);
        Assert.False(await UpdateBackupCleanup.CleanupAsync(install, current, backup, previous, current, CancellationToken.None));
        Assert.False(await UpdateBackupCleanup.CleanupAsync(install, current, files.Root, previous, current, CancellationToken.None));
        Assert.False(await UpdateBackupCleanup.CleanupAsync(install, current, backup, previous, new string('0', 64), CancellationToken.None));
        Assert.Equal("keep this", await File.ReadAllTextAsync(unrelated));
    }

    [Fact]
    public void Successful_cleanup_clears_the_complete_journal_for_two_consecutive_upgrades()
    {
        string path = @"Software\Citrus-Update-Test-" + Guid.NewGuid().ToString("N");
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        try
        {
            using var key = hive.CreateSubKey(path);
            for (int update = 0; update < 2; update++)
            {
                foreach (string name in UpdateBackupCleanup.JournalKeys) key.SetValue(name, name == "UpdateState" ? "Committed" : "fixture");
                key.SetValue("PackageInventory", new string('a', 64));
                UpdateBackupCleanup.ClearCommittedJournal(key, new string('a', 64));
                Assert.All(UpdateBackupCleanup.JournalKeys, name => Assert.Null(key.GetValue(name)));
                Assert.Null(key.GetValue("UpdateCleanupCompleteInventory"));
                Assert.Equal(new string('a', 64), key.GetValue("PackageInventory"));
                Assert.False(UpdateInstallation.IsTransactionPending(key.GetValue("UpdateState") as string));
            }
        }
        finally { hive.DeleteSubKeyTree(path, false); }
    }

    [Fact]
    public async Task Backup_cleanup_commits_its_recovery_receipt_before_removing_the_original_manifest()
    {
        using var files = new NativeFiles();
        string install = Path.Combine(files.Root, "Citrus"); Directory.CreateDirectory(install);
        string currentManifest = Write(files, "Citrus/install-inventory.json", "{}");
        string source = Write(files, "old/install-inventory.json", "{}"), previous = Hash(source), current = Hash(currentManifest);
        string backup = install + ".previous-" + previous; Directory.Move(Path.Combine(files.Root, "old"), backup);
        using var canceled = new CancellationTokenSource(); bool committed = false;
        Assert.True(await UpdateBackupCleanup.CleanupAsync(install, current, backup, previous, current, canceled.Token, () =>
        {
            Assert.True(File.Exists(Path.Combine(backup, "install-inventory.json")));
            committed = true; canceled.Cancel(); // Cancellation at this boundary cannot strand the now-committed transaction.
        }));
        Assert.True(committed);
    }

    [Fact]
    public async Task Staging_cleanup_removes_only_verified_receipted_old_installers_and_preserves_the_pending_one()
    {
        using var files = new NativeFiles();
        string directory = files.Root;
        var old = CandidateFor("0.3.0-preview.2", "old bytes");
        var pending = CandidateFor("0.3.0-preview.3", "new bytes");
        string oldFile = Write(files, Name(old), "old bytes"), nextFile = Write(files, Name(pending), "new bytes");
        await UpdateStagingReceipts.RecordAsync(old, oldFile, directory, CancellationToken.None);
        await UpdateStagingReceipts.RecordAsync(pending, nextFile, directory, CancellationToken.None);
        string unrelated = files.File("unrelated.exe", "keep user file");
        await UpdateStagingReceipts.CleanupAsync(directory, "0.3.0-preview.2", new(pending, nextFile), CancellationToken.None);
        Assert.False(File.Exists(oldFile)); Assert.False(File.Exists(oldFile + ".update.json"));
        Assert.True(File.Exists(nextFile)); Assert.True(File.Exists(unrelated));
        await File.WriteAllTextAsync(nextFile, "changed by user");
        await UpdateStagingReceipts.CleanupAsync(directory, "0.3.0-preview.3", null, CancellationToken.None);
        Assert.Equal("changed by user", await File.ReadAllTextAsync(nextFile));
    }

    [Fact]
    public async Task A_hash_correct_executable_with_the_wrong_embedded_version_is_rejected_before_ready()
    {
        using var files = new NativeFiles();
        byte[] oldExecutable = await File.ReadAllBytesAsync(typeof(App).Assembly.Location);
        var candidate = Candidate with { Bytes = oldExecutable.Length, Sha256 = Convert.ToHexString(SHA256.HashData(oldExecutable)).ToLowerInvariant() };
        using var client = new HttpClient(new BytesHandler(oldExecutable));
        var backend = new HttpAutomaticUpdateBackend(client, "0.3.0-preview.2", files.Root);
        await Assert.ThrowsAsync<InvalidDataException>(() => backend.DownloadAsync(candidate, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(files.Root, "*.exe"));
        Assert.Empty(Directory.EnumerateFiles(files.Root, "*.update.json"));
    }

    private static UpdateCandidate CandidateFor(string version, string contents)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(contents);
        return Candidate with { Version = version, Bytes = bytes.Length,
            DownloadUrl = new Uri($"https://github.com/Skytuhua/Citrus/releases/download/v{version}/Citrus-Setup.exe"),
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
    }
    private static string Name(UpdateCandidate candidate) => $"Citrus-{candidate.Version}-{candidate.Sha256[..12]}.exe";

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Write(NativeFiles files, string relative, string content)
    {
        string path = Path.Combine(files.Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content); return path;
    }

    private sealed class BytesHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) });
    }

    private sealed class FakeBackend : IAutomaticUpdateBackend
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal UpdateCandidate? Candidate;
        internal Exception? DownloadFailure;
        internal int Checks, Downloads;
        public async Task<UpdateCandidate?> CheckAsync(CancellationToken ct) { Interlocked.Increment(ref Checks); Entered.TrySetResult(); await Release.Task.WaitAsync(ct); return Candidate; }
        public Task<string> DownloadAsync(UpdateCandidate candidate, CancellationToken ct) { Interlocked.Increment(ref Downloads); return DownloadFailure is null ? Task.FromResult("verified-installer.exe") : Task.FromException<string>(DownloadFailure); }
        public Task<bool> VerifyAsync(ReadyUpdate update, CancellationToken ct) => Task.FromResult(true);
    }
}
