using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Windows.Threading;
using Microsoft.Win32;
using Citrus.Core;
using Citrus.Windows.Updates;
using Forms = System.Windows.Forms;

namespace Citrus.Windows;

public partial class App
{
    private AutomaticUpdateService? automaticUpdates;
    private UpdateInstallation? updateInstallation;
    private HttpClient? updateHttp;
    private CancellationTokenSource? updateLifetime;
    private Task updateBackground = Task.CompletedTask;
    private Task updateBackupCleanup = Task.CompletedTask;
    private Task updateStagingCleanup = Task.CompletedTask;
    private DispatcherTimer? updateIdleTimer;
    private Forms.ToolStripMenuItem? updateMenuItem;
    private bool updateAdmissionClosed, applyingUpdate;
    private AutomaticUpdateStatus updateStatus = new("Install Citrus to enable updates.");

    internal static bool ShouldStartAutomaticUpdates(ResidentOptions value) =>
        value.SettingsRoot is null && value.InstanceSuffix is null && value.ObserveGestures;

    private void AddUpdateMenu(Forms.ContextMenuStrip menu)
    {
        updateMenuItem = new("Check for updates…");
        updateMenuItem.Click += async (_, _) => { if (automaticUpdates?.Ready is not null) OpenSettings(); else await CheckForUpdatesAsync(); };
        menu.Items.Add(updateMenuItem);
    }

    private void StartAutomaticUpdates()
    {
        if (!ShouldStartAutomaticUpdates(options)) return;
        updateLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var ct = updateLifetime.Token;
        updateBackground = Task.Run(async () =>
        {
            try
            {
                updateInstallation = UpdateInstallation.ReadCurrent();
                updateHttp = new(new HttpClientHandler { UseCookies = false, CheckCertificateRevocationList = true })
                { Timeout = TimeSpan.FromHours(2) };
                string version = (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];
                string staging = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Citrus", "Updates");
                updateStagingCleanup = UpdateStagingReceipts.CleanupAsync(staging, version, null, ct);
                string? failedVersion = ReadFailedUpdateVersion();
                // Publish the cleanup lifetime before manual UI checks can see the service.
                if (updateInstallation is { } installed)
                    updateBackupCleanup = UpdateBackupCleanup.ConfirmSuccessfulBootAsync(installed, ct);
                automaticUpdates = new(new HttpAutomaticUpdateBackend(updateHttp, version, staging), () => Volatile.Read(ref settings).AutomaticUpdates,
                    canStage: updateInstallation?.CanUpdateSilently == true, failedVersion: failedVersion);
                automaticUpdates.Changed += QueueUpdateStatus;
                if (updateInstallation?.CanUpdateSilently == true)
                {
                    await updateBackupCleanup.ConfigureAwait(false);
                    await automaticUpdates.RunAsync(ct).ConfigureAwait(false);
                }
                else QueueUpdateStatus(updateStatus);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            { QueueUpdateStatus(new("Updates unavailable. Check GitHub releases.")); }
        }, ct);
    }

    internal async Task CheckForUpdatesAsync()
    {
        if (IsQuitting || updateAdmissionClosed) return;
        if (automaticUpdates is null || updateLifetime is null)
        {
            SetUpdateStatus(new("Install Citrus to enable updates."));
            return;
        }
        await automaticUpdates.CheckAsync(manual: true, updateLifetime.Token);
    }

    private void QueueUpdateStatus(AutomaticUpdateStatus status)
    {
        if (Dispatcher.HasShutdownStarted || IsQuitting) return;
        try { Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => SetUpdateStatus(status))); }
        catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted) { }
    }

    private static string? ReadFailedUpdateVersion()
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var registration = hive.OpenSubKey(UpdateInstallation.RegistryPath);
        return registration?.GetValue("UpdateFailedVersion") as string;
    }

    private void UpdatePreferenceChanged()
    {
        if (settings.AutomaticUpdates && automaticUpdates?.Ready is not null) updateIdleTimer?.Start();
        else updateIdleTimer?.Stop();
    }
    private void SetUpdateStatus(AutomaticUpdateStatus status)
    {
        if (IsQuitting) return;
        updateStatus = status;
        settingsWindow?.SetUpdateStatus(status);
        if (updateMenuItem is not null)
        {
            updateMenuItem.Enabled = !status.Checking;
            updateMenuItem.Text = status.ReadyVersion is null ? "Check for updates…" : "Update ready · open settings…";
        }
        if (status.ReadyVersion is not null)
        {
            updateIdleTimer ??= new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher)
            { Interval = TimeSpan.FromSeconds(2) };
            updateIdleTimer.Tick -= UpdateIdleTick;
            updateIdleTimer.Tick += UpdateIdleTick;
            updateIdleTimer.Start();
        }
        else updateIdleTimer?.Stop();
    }
    private async void UpdateIdleTick(object? sender, EventArgs e)
    {
        if (settings.AutomaticUpdates) await TryApplyReadyUpdateAsync(explicitRestart: false);
    }

    private UpdateIdleState CaptureUpdateIdleState(bool explicitRestart) => new(IsQuitting || updateAdmissionClosed,
        preparing, activeAction.IsCompleted,
        jobs.Any(job => job.State is ConversionJobState.Queued or ConversionJobState.Running),
        wheel?.IsInteractionActive == true,
        (NativeMethods.GetAsyncKeyState(NativeMethods.GetSystemMetrics(23) != 0 ? 0x02 : 0x01) & 0x8000) != 0,
        Windows.Cast<Window>().Any(window => window.IsVisible && window.WindowState != WindowState.Minimized &&
            !(explicitRestart && (ReferenceEquals(window, home) || ReferenceEquals(window, settingsWindow)))));

    internal Task RestartForUpdateAsync() => TryApplyReadyUpdateAsync(explicitRestart: true);

    private async Task TryApplyReadyUpdateAsync(bool explicitRestart)
    {
        if (applyingUpdate || automaticUpdates?.Ready is not { } ready || updateLifetime is null ||
            !CaptureUpdateIdleState(explicitRestart).CanApplyAfterCleanup(updateBackupCleanup.IsCompletedSuccessfully, installTransactionPending: false)) return;
        applyingUpdate = true;
        try
        {
            UpdateInstallation? installed = null;
            bool installTransactionPending = false;
            bool CanApply() => CaptureUpdateIdleState(explicitRestart).CanApplyAfterCleanup(updateBackupCleanup.IsCompletedSuccessfully, installTransactionPending) &&
                UpdateIdleState.HasUpdateConsent(explicitRestart, settings.AutomaticUpdates) && automaticUpdates.Ready == ready;
            async Task<bool> Verify(CancellationToken ct)
            {
                // Disk/registry work never blocks the dispatcher. The gate checks admission and consent again afterward.
                installed = await Task.Run(UpdateInstallation.ReadCurrent, ct);
                if (installed?.CanUpdateSilently != true || !await Citrus.Core.Updates.VerifiedUpdateDownloader.VerifyAsync(ready.Candidate, ready.InstallerPath, ct))
                { automaticUpdates.DiscardReady(ready); SetUpdateStatus(new("Update verification failed. Check for updates to try again.")); return false; }
                installTransactionPending = await Task.Run(UpdateInstallation.HasPendingTransaction, ct);
                return true;
            }
            void Launch()
            {
                // Installed synchronously before process launch or nested dispatcher work can admit an edit/drag.
                updateAdmissionClosed = true;
                monitor?.SetEnabled(false);
                using var current = Process.GetCurrentProcess();
                var start = new ProcessStartInfo(ready.InstallerPath)
                { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(ready.InstallerPath)! };
                foreach (string argument in new[] { "/S", "/ACCEPTSDKTERMS=1", "/AUTOUPDATE=1", "/UPDATEPID=" + current.Id,
                    "/UPDATESTART=" + current.StartTime.ToUniversalTime().ToFileTimeUtc(), "/PREVIOUSINVENTORY=" + installed!.InventoryHash }) start.ArgumentList.Add(argument);
                using var installer = Process.Start(start) ?? throw new InvalidOperationException("Update installer could not start.");
            }
            if (await UpdateApplyGate.VerifyAndApplyAsync(CanApply, Verify, Launch, updateLifetime.Token)) await QuitAsync();
        }
        catch (OperationCanceledException) when (updateLifetime?.IsCancellationRequested == true) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            updateAdmissionClosed = false;
            if (!IsQuitting) { SetGestures(settings.GesturesEnabled, persist: false); SetUpdateStatus(new("Update could not start. Try again.")); }
        }
        finally { applyingUpdate = false; }
    }

    internal void OpenUpdateReleases()
    {
        if (IsQuitting || updateAdmissionClosed) return;
        try { Process.Start(new ProcessStartInfo("https://github.com/Skytuhua/Citrus/releases") { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        { SetUpdateStatus(new("Open github.com/Skytuhua/Citrus/releases in your browser to get the latest release.")); }
    }

    private async Task StopAutomaticUpdatesAsync()
    {
        updateIdleTimer?.Stop();
        updateLifetime?.Cancel();
        try
        {
            await updateBackground; // Initialization may still be assigning its cleanup tasks.
        }
        catch (OperationCanceledException) { }
        try { await Task.WhenAll(updateBackupCleanup, updateStagingCleanup); }
        catch (OperationCanceledException) { }
        if (automaticUpdates is not null) await automaticUpdates.WaitForIdleAsync();
        if (automaticUpdates is not null) automaticUpdates.Changed -= QueueUpdateStatus;
        updateHttp?.Dispose(); updateHttp = null;
        updateLifetime?.Dispose(); updateLifetime = null;
    }
}
