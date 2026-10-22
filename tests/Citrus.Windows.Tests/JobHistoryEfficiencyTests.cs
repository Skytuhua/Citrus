using System.IO;
using Citrus.Core;

namespace Citrus.Windows.Tests;

public sealed class JobHistoryEfficiencyTests
{
    [Fact]
    public async Task Completed_output_rows_keep_their_identity_across_refreshes()
    {
        var engine = new OutputEngine();
        var queue = new JobQueue([engine]);
        var job = queue.Enqueue(new(engine.Operations[0], ["source.txt"], new(), null));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var model = new JobViewModel(job);
        var outputs = model.Outputs;
        Assert.Equal("result.txt", Assert.Single(outputs).Name);
        model.Refresh();
        Assert.Same(outputs, model.Outputs);
        Assert.Same(outputs[0], model.Outputs[0]);
    }

    [Fact]
    public async Task Committed_outputs_replace_the_cached_running_job_rows()
    {
        var engine = new OutputEngine(hold: true);
        var queue = new JobQueue([engine]);
        var job = queue.Enqueue(new(engine.Operations[0], ["source.txt"], new(), null));
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var model = new JobViewModel(job);
        var initial = model.Outputs;
        try
        {
            Assert.Empty(initial);
            engine.Release.TrySetResult();
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            model.Refresh();
            Assert.Equal("result.txt", Assert.Single(model.Outputs).Name);
            Assert.NotSame(initial, model.Outputs);
        }
        finally { engine.Release.TrySetResult(); await job.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private sealed class OutputEngine(bool hold = false) : IConversionEngine
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<OperationDescriptor> Operations { get; } = [new("fixture.outputs", "Output fixture", OperationMode.Convert, ".txt", false, false)];
        public bool CanExecute(OperationRequest request) => true;
        public async Task<ConversionResult> ExecuteAsync(OperationRequest request, IProgress<ConversionProgress>? progress, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            if (hold) await Release.Task.WaitAsync(cancellationToken);
            return new(["result.txt"]);
        }
    }
}

[Collection("Desktop")]
public sealed class JobHistoryPresentationTests(DesktopFixture desktop)
{
    [Fact]
    public Task Ten_thousand_completed_jobs_retire_every_owner_and_virtualize_recent_history() => desktop.RunAsync(async () =>
    {
        var app=(App)System.Windows.Application.Current;
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var tracked=(List<ConversionJob>)typeof(App).GetField("jobs",flags)!.GetValue(app)!;
        var notified=(HashSet<ConversionJob>)typeof(App).GetField("notified",flags)!.GetValue(app)!;
        var quiet=(HashSet<ConversionJob>)typeof(App).GetField("quietJobs",flags)!.GetValue(app)!;
        var succeededField=typeof(App).GetField("retiredQuietSucceeded",flags)!;var failedField=typeof(App).GetField("retiredQuietFailed",flags)!;
        object? previousSucceeded=succeededField.GetValue(app),previousFailed=failedField.GetValue(app);
        var homeField=typeof(App).GetField("home",flags)!;object? previous=homeField.GetValue(app);
        var toastField=typeof(App).GetField("toast",flags)!;object? previousToast=toastField.GetValue(app);
        using var toast=new JobToastWindow(()=>{},_=>Task.CompletedTask,()=>false);toastField.SetValue(app,toast);
        using var home=new MainWindow(app);homeField.SetValue(app,home);
        var engine=new HeldEngine();var queue=new JobQueue([engine],maxConcurrency:1);var operation=engine.Operations[0];
        var running=queue.Enqueue(new(operation,["running.txt"],new(),null));
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued=queue.Enqueue(new(operation,["queued.txt"],new(),null));
        try
        {
            var history=Enumerable.Range(0,10_000).Select(i=>queue.EnqueueFailure(new(operation,[$"source-{i}.txt"],new(),null),new IOException("Fixture failure"))).ToArray();
            notified.UnionWith(history);
            ConversionJob[] submitted=[running,queued,..history];
            typeof(App).GetMethod("TrackSubmitted",flags)!.Invoke(app,[submitted,SubmissionOrigin.Wheel]);
            Assert.Equal(102,tracked.Count);Assert.Equal(100,notified.Count);Assert.Equal(102,quiet.Count);
            Assert.Equal(9_900,failedField.GetValue(app));
            Assert.Equal(new[]{running,queued}.Concat(history.TakeLast(100)),tracked);
            Assert.Equal(ConversionJobState.Running,running.State);Assert.Equal(ConversionJobState.Queued,queued.State);
            var list=(System.Windows.Controls.ItemsControl)home.FindName("JobList");Assert.Equal(102,list.Items.Count);
            Assert.Equal(102,((System.Collections.IDictionary)typeof(MainWindow).GetField("jobModels",flags)!.GetValue(home)!).Count);
            for(int i=0;i<10_000;i++)home.AddSubmissionFailure("Preflight fixture",null);
            Assert.Equal(100,((System.Windows.Controls.ItemsControl)home.FindName("SubmissionFailures")).Items.Count);
            home.Show();home.ShowJobs();home.UpdateLayout();await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var virtualized=Assert.IsType<System.Windows.Controls.ListBox>(list);
            Assert.True(System.Windows.Controls.VirtualizingPanel.GetIsVirtualizing(virtualized));
            Assert.InRange(Enumerable.Range(0,list.Items.Count).Count(i=>list.ItemContainerGenerator.ContainerFromIndex(i) is not null),1,15);
            virtualized.ScrollIntoView(virtualized.Items[^1]);home.UpdateLayout();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.NotNull(virtualized.ItemContainerGenerator.ContainerFromIndex(list.Items.Count-1));
            engine.Release.TrySetResult();await Task.WhenAll(running.Completion,queued.Completion).WaitAsync(TimeSpan.FromSeconds(5));
            typeof(App).GetMethod("RefreshChangedJobs",flags)!.Invoke(app,[new[]{running,queued,history[0]}]);
            Assert.Equal(100,tracked.Count);Assert.Equal(100,notified.Count);Assert.Equal(100,quiet.Count);Assert.Equal(100,list.Items.Count);
            Assert.DoesNotContain(history[0],notified);Assert.DoesNotContain(running,tracked);Assert.DoesNotContain(queued,tracked);
            Assert.Equal(2,succeededField.GetValue(app));Assert.Equal(9_900,failedField.GetValue(app));
            Assert.Equal("2 ready · 10000 failed",((System.Windows.Controls.TextBlock)toast.FindName("Message")).Text);
        }
        finally {engine.Release.TrySetResult();await Task.WhenAll(running.Completion,queued.Completion);homeField.SetValue(app,previous);toastField.SetValue(app,previousToast);toast.Close();tracked.Clear();notified.Clear();quiet.Clear();succeededField.SetValue(app,previousSucceeded);failedField.SetValue(app,previousFailed);home.Dispose();home.Close();}
    });

    private sealed class HeldEngine:IConversionEngine
    {
        internal TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<OperationDescriptor> Operations {get;}=[new("fixture.history","History",OperationMode.Convert,".txt",false,false)];
        public bool CanExecute(OperationRequest request)=>true;
        public async Task<ConversionResult> ExecuteAsync(OperationRequest request,IProgress<ConversionProgress>? progress,CancellationToken cancellationToken)
        {Started.TrySetResult();await Release.Task.WaitAsync(cancellationToken);return new([]);}
    }

    [Fact]
    public Task Progress_only_invalidates_the_changed_job_in_history() => desktop.RunAsync(() =>
    {
        var queue = new JobQueue([]);
        var operation = new OperationDescriptor("fixture.history", "History fixture", OperationMode.Convert, ".txt", false, false);
        var first = queue.EnqueueFailure(new(operation, ["first.txt"], new(), null), new InvalidOperationException("First fixture."));
        var second = queue.EnqueueFailure(new(operation, ["second.txt"], new(), null), new InvalidOperationException("Second fixture."));
        using var home = new MainWindow((App)System.Windows.Application.Current);
        try
        {
            home.AddJobs([first, second]);
            var models = ((System.Windows.Controls.ItemsControl)home.FindName("JobList")).Items.Cast<JobViewModel>().ToDictionary(model => model.Job);
            int firstChanges = 0, secondChanges = 0;
            models[first].PropertyChanged += (_, _) => firstChanges++;
            models[second].PropertyChanged += (_, _) => secondChanges++;
            home.RefreshJobs([first]);
            Assert.Equal(1, firstChanges);
            Assert.Equal(0, secondChanges);
            Assert.Contains("2 failed", ((System.Windows.Controls.TextBlock)home.FindName("JobSummary")).Text);
        }
        finally { home.Dispose(); home.Close(); }
        return Task.CompletedTask;
    });
}
