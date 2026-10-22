using System.Reflection;
using System.Collections.Concurrent;
using System.Windows.Threading;
using Citrus.Core;
using Application = System.Windows.Application;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class JobProgressDispatchTests(DesktopFixture desktop)
{
    [Fact]
    public Task Progress_burst_posts_one_ui_update_and_keeps_all_terminal_jobs() => desktop.RunAsync(async () =>
    {
        var app = (App)Application.Current;
        var queue = new JobQueue([]);
        var operation = new OperationDescriptor("fixture.progress", "Progress fixture", OperationMode.Convert, ".txt", false, false);
        var jobs = Enumerable.Range(0, 3).Select(index => queue.EnqueueFailure(new(operation, [$"source-{index}.txt"], new(), null),
            new InvalidOperationException("Expected fixture failure."))).ToArray();
        var changed = typeof(App).GetMethod("JobChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int posted = 0;
        DispatcherHookEventHandler observe = (_, args) =>
        {
            if (args.Operation.Priority == DispatcherPriority.Background) posted++;
        };
        app.Dispatcher.Hooks.OperationPosted += observe;
        try
        {
            // Hold the UI thread during a producer burst, so no operation can drain yet.
            for (int index = 0; index < 10_000; index++) changed.Invoke(app, [jobs[index % jobs.Length]]);
        }
        finally { app.Dispatcher.Hooks.OperationPosted -= observe; }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var notified = (HashSet<ConversionJob>)typeof(App).GetField("notified", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;
        try
        {
            Assert.Equal(1, posted);
            Assert.All(jobs, job => Assert.Contains(job, notified));
        }
        finally { foreach (var job in jobs) notified.Remove(job); }
    });

    [Fact]
    public void Concurrent_producers_schedule_one_refresh_per_distinct_job()
    {
        var callbacks = new ConcurrentQueue<Action>();
        var jobs = CreateJobs(3);
        IReadOnlyCollection<ConversionJob>? received = null;
        var updates = new JobProgressDispatcher(callbacks.Enqueue, changed => received = changed);
        Parallel.For(0, 10_000, index => updates.Enqueue(jobs[index % jobs.Length]));
        Assert.Single(callbacks);
        Assert.True(callbacks.TryDequeue(out var drain)); Assert.IsType<Action>(drain)();
        var batch = Assert.IsAssignableFrom<IReadOnlyCollection<ConversionJob>>(received);
        Assert.Equal(jobs.Length, batch.Count);
        Assert.All(jobs, job => Assert.Contains(job, batch));
        Assert.Empty(callbacks);
    }

    [Fact]
    public void Events_arriving_during_refresh_wait_for_another_dispatch_turn()
    {
        var callbacks = new Queue<Action>();
        var jobs = CreateJobs(2);
        var received = new List<ConversionJob[]>();
        JobProgressDispatcher? updates = null;
        updates = new(callbacks.Enqueue, changed =>
        {
            received.Add(changed.ToArray());
            if (received.Count == 1)
                for (int index = 0; index < 10_000; index++) updates!.Enqueue(jobs[1]);
        });
        updates.Enqueue(jobs[0]);
        callbacks.Dequeue()();
        Assert.Single(received); Assert.Single(callbacks);
        Assert.Same(jobs[0], Assert.Single(received[0]));
        callbacks.Dequeue()();
        Assert.Equal(2, received.Count); Assert.Empty(callbacks);
        Assert.Same(jobs[1], Assert.Single(received[1]));
    }

    [Fact]
    public void Stop_discards_pending_and_future_progress()
    {
        var callbacks = new Queue<Action>();
        var jobs = CreateJobs(2);
        int refreshes = 0;
        var updates = new JobProgressDispatcher(callbacks.Enqueue, _ => refreshes++);
        updates.Enqueue(jobs[0]);
        updates.Stop();
        updates.Enqueue(jobs[1]);
        Assert.Single(callbacks);
        callbacks.Dequeue()();
        Assert.Equal(0, refreshes); Assert.Empty(callbacks);
    }

    [Fact]
    public async Task Completion_before_dispatch_is_presented_as_the_latest_job_state()
    {
        var callbacks = new Queue<Action>();
        var engine = new HeldEngine();
        var queue = new JobQueue([engine]);
        var job = queue.Enqueue(new(engine.Operations[0], ["source.txt"], new(), null));
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ConversionJobState? presented = null;
        var updates = new JobProgressDispatcher(callbacks.Enqueue, changed => presented = Assert.Single(changed).State);
        try
        {
            updates.Enqueue(job);
            Assert.Equal(ConversionJobState.Running, job.State);
            engine.Release.TrySetResult();
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            callbacks.Dequeue()();
            Assert.Equal(ConversionJobState.Succeeded, presented);
        }
        finally { engine.Release.TrySetResult(); await job.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static ConversionJob[] CreateJobs(int count)
    {
        var queue = new JobQueue([]);
        var operation = new OperationDescriptor("fixture.progress", "Progress fixture", OperationMode.Convert, ".txt", false, false);
        return Enumerable.Range(0, count).Select(index => queue.EnqueueFailure(new(operation, [$"source-{index}.txt"], new(), null),
            new InvalidOperationException("Expected fixture failure."))).ToArray();
    }

    private sealed class HeldEngine : IConversionEngine
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<OperationDescriptor> Operations { get; } = [new("fixture.held", "Held fixture", OperationMode.Convert, ".txt", false, false)];
        public bool CanExecute(OperationRequest request) => true;
        public async Task<ConversionResult> ExecuteAsync(OperationRequest request, IProgress<ConversionProgress>? progress, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new([]);
        }
    }
}
