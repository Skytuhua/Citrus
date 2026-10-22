using Citrus.Core.Updates;

namespace Citrus.Windows.Updates;

internal sealed record UpdateIdleState(bool Quitting, bool Preparing, bool ActiveActionCompleted,
    bool PendingJobs, bool WheelActive, bool PointerHeld, bool VisibleWorkspaceOrDialog)
{
    internal bool CanApply => !Quitting && !Preparing && ActiveActionCompleted && !PendingJobs &&
        !WheelActive && !PointerHeld && !VisibleWorkspaceOrDialog;
    internal bool CanApplyAfterCleanup(bool cleanupCompleted, bool installTransactionPending) =>
        CanApply && cleanupCompleted && !installTransactionPending;
    internal static bool HasUpdateConsent(bool explicitRestart, bool automaticUpdatesEnabled) => explicitRestart || automaticUpdatesEnabled;
}

internal sealed record ReadyUpdate(UpdateCandidate Candidate, string InstallerPath);
internal sealed record AutomaticUpdateStatus(string Message, bool Checking = false, string? ReadyVersion = null);

internal interface IAutomaticUpdateBackend
{
    Task<UpdateCandidate?> CheckAsync(CancellationToken ct);
    Task<string> DownloadAsync(UpdateCandidate candidate, CancellationToken ct);
    Task<bool> VerifyAsync(ReadyUpdate update, CancellationToken ct);
}

/// <summary>Network/download lifetime independent of the WPF dispatcher and optical renderer.</summary>
internal sealed class AutomaticUpdateService(IAutomaticUpdateBackend backend, Func<bool> enabled, bool canStage, string? failedVersion = null)
{
    private readonly SemaphoreSlim operation = new(1, 1);
    private ReadyUpdate? ready;
    private AutomaticUpdateStatus status = new("Not checked yet.");
    internal ReadyUpdate? Ready => Volatile.Read(ref ready);
    internal AutomaticUpdateStatus Status => Volatile.Read(ref status);
    internal event Action<AutomaticUpdateStatus>? Changed;
    internal void DiscardReady(ReadyUpdate expected) => Interlocked.CompareExchange(ref ready, null, expected);

    internal async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await CheckAsync(manual: false, ct).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await CheckAsync(manual: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    internal async Task CheckAsync(bool manual, CancellationToken ct)
    {
        if (!manual && !enabled()) return;
        bool acquired = false;
        try
        {
            if (!await operation.WaitAsync(0, ct).ConfigureAwait(false)) return;
            acquired = true;
            Publish(new("Checking for updates…", Checking: true));
            var candidate = await backend.CheckAsync(ct).ConfigureAwait(false);
            if (candidate is null)
            {
                Volatile.Write(ref ready, null);
                Publish(new("Up to date."));
                return;
            }
            if (ReleaseVersion.TryParse(failedVersion, out var failed) && ReleaseVersion.TryParse(candidate.Version, out var proposed) && proposed!.CompareTo(failed) == 0)
            {
                Volatile.Write(ref ready, null);
                Publish(new($"Update {candidate.Version} failed. Install it manually from GitHub releases."));
                return;
            }
            if (!canStage)
            {
                Publish(new($"Version {candidate.Version} available. Install it from GitHub releases."));
                return;
            }
            var existing = Ready;
            if (existing?.Candidate == candidate && await backend.VerifyAsync(existing, ct).ConfigureAwait(false))
            {
                Publish(ReadyStatus(candidate));
                return;
            }
            Volatile.Write(ref ready, null);
            Publish(new($"Downloading {candidate.Version}…", Checking: true));
            string installer = await backend.DownloadAsync(candidate, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            Volatile.Write(ref ready, new(candidate, installer));
            Publish(ReadyStatus(candidate));
        }
        catch (OperationCanceledException)
        {
            Publish(new("Update check canceled."));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Publish(new("Updates could not be checked or downloaded. Try again."));
        }
        finally { if (acquired) operation.Release(); }
    }

    internal async Task WaitForIdleAsync()
    {
        await operation.WaitAsync().ConfigureAwait(false);
        operation.Release();
    }

    private static AutomaticUpdateStatus ReadyStatus(UpdateCandidate candidate) =>
        new($"Version {candidate.Version} ready. Installs when idle.", ReadyVersion: candidate.Version);

    private void Publish(AutomaticUpdateStatus value)
    {
        Volatile.Write(ref status, value);
        Changed?.Invoke(value);
    }
}
