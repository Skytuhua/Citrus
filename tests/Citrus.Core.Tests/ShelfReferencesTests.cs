using System.Collections.Concurrent;
using Citrus.Core;

namespace Citrus.Core.Tests;

public sealed class ShelfReferencesTests
{
    [Fact]
    public async Task Slow_metadata_does_not_block_the_refresh_caller()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shelf = new ShelfReferences(_ =>
        {
            entered.TrySetResult();
            release.Wait();
            return true;
        });
        shelf.Add("one");
        Task? refresh = null;
        Task caller = Task.Run(() => { refresh = shelf.RefreshAsync(); returned.SetResult(); });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await CompletesWithin(returned.Task), "The refresh caller remained inside the metadata query.");
        }
        finally
        {
            release.Set();
            await caller.WaitAsync(TimeSpan.FromSeconds(5));
            if (refresh is not null) await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Repeated_refresh_requests_do_not_overlap_metadata_scans()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var shelf = new ShelfReferences(_ =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            release.Wait();
            return true;
        });
        shelf.Add("one");
        Task first = shelf.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task second = shelf.RefreshAsync();
            Assert.True(second.IsCompletedSuccessfully);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally { release.Set(); await first.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task A_stale_missing_result_cannot_remove_readded_or_new_paths()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var shelf = new ShelfReferences(_ =>
        {
            if (Interlocked.Increment(ref calls) != 1) return true;
            entered.TrySetResult();
            release.Wait();
            return false;
        });
        shelf.Add("one");
        Task refresh = shelf.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(shelf.Remove("ONE"));
            Assert.True(shelf.Add("ONE"));
            Assert.True(shelf.Add("new"));
        }
        finally { release.Set(); await refresh.WaitAsync(TimeSpan.FromSeconds(5)); }

        Assert.Equal(new[] { "ONE", "new" }, shelf.Items);
        await shelf.RefreshAsync();
        Assert.Equal(new[] { "ONE", "new" }, shelf.Items);
    }

    [Fact]
    public async Task Readmitting_an_existing_path_protects_it_from_an_older_missing_result()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shelf = new ShelfReferences(_ =>
        {
            entered.TrySetResult();
            release.Wait();
            return false;
        });
        shelf.Add("One");
        Task refresh = shelf.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // AddPaths has freshly validated this path, even though it is already listed.
            Assert.False(shelf.Add("ONE"));
        }
        finally { release.Set(); await refresh.WaitAsync(TimeSpan.FromSeconds(5)); }

        Assert.Equal(new[] { "One" }, shelf.Items);
    }

    [Fact]
    public void Membership_is_case_insensitive_and_supports_removing_and_readding()
    {
        using var shelf = new ShelfReferences(_ => true);
        Assert.True(shelf.Add("One"));
        Assert.False(shelf.Add("ONE"));
        Assert.Equal(new[] { "One" }, shelf.Items);
        Assert.True(shelf.Remove("oNe"));
        Assert.False(shelf.Remove("one"));
        Assert.True(shelf.Add("ONE"));
        Assert.Equal(new[] { "ONE" }, shelf.Items);
    }

    [Fact]
    public void Final_selection_validation_only_queries_and_prunes_the_selected_paths()
    {
        var queried = new List<string>();
        using var shelf = new ShelfReferences(path => { queried.Add(path); return path != "missing"; });
        shelf.Add("missing");
        shelf.Add("other");

        Assert.Empty(shelf.ValidateSelection(["missing"]));
        Assert.Equal(new[] { "missing" }, queried);
        Assert.Equal(new[] { "other" }, shelf.Items);
        Assert.Equal(new[] { "other" }, shelf.ValidateSelection(["other"]));
    }

    [Fact]
    public async Task Disposal_cancels_a_running_scan_without_pruning_or_starting_another()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var shelf = new ShelfReferences(_ =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            release.Wait();
            return false;
        });
        shelf.Add("one");
        shelf.Add("two");
        Task refresh = shelf.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            shelf.Dispose();
        }
        finally { release.Set(); }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        await shelf.RefreshAsync();
        Assert.Equal(1, calls);
        Assert.Equal(new[] { "one", "two" }, shelf.Items);
    }

    [Fact]
    public async Task Default_metadata_probe_keeps_local_files_and_directories_and_prunes_missing_paths()
    {
        string directory = Path.Combine(Path.GetTempPath(), "citrus-shelf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "one.txt"), missing = Path.Combine(directory, "missing.txt");
        File.WriteAllText(file, "content");
        try
        {
            using var shelf = new ShelfReferences();
            shelf.Add(file);
            shelf.Add(directory);
            shelf.Add(missing);
            await shelf.RefreshAsync();
            Assert.Equal(new[] { file, directory }, shelf.Items);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Missing_results_are_applied_on_the_callers_synchronization_context()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        using var context = new PumpContext();
        var previous = SynchronizationContext.Current;
        int owner = Environment.CurrentManagedThreadId, changedOn = -1;
        using var shelf = new ShelfReferences(_ => { entered.Set(); release.Wait(); return false; });
        shelf.Add("missing");
        ((System.Collections.Specialized.INotifyCollectionChanged)shelf.Items).CollectionChanged += (_, _) => changedOn = Environment.CurrentManagedThreadId;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            Task refresh = shelf.RefreshAsync();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            release.Set();
            context.RunUntil(refresh);
        }
        finally { release.Set(); SynchronizationContext.SetSynchronizationContext(previous); }

        Assert.Empty(shelf.Items);
        Assert.Equal(owner, changedOn);
    }

    private static async Task<bool> CompletesWithin(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(1)); return true; }
        catch (TimeoutException) { return false; }
    }

    private sealed class PumpContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> pending = [];
        public override void Post(SendOrPostCallback callback, object? state) => pending.Add((callback, state));
        public void RunUntil(Task task)
        {
            while (!task.IsCompleted)
            {
                if (!pending.TryTake(out var work, TimeSpan.FromSeconds(5))) throw new TimeoutException("Refresh did not return to its owner context.");
                work.Callback(work.State);
            }
            task.GetAwaiter().GetResult();
        }
        public void Dispose() => pending.Dispose();
    }
}
