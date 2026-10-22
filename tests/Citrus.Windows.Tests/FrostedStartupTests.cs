using System.Collections.Concurrent;
using System.Windows.Threading;
using Citrus.Windows.Presentation;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class FrostedStartupTests(DesktopFixture desktop)
{
    [Fact]
    public Task Start_publishes_pending_without_waiting_for_capture_and_stop_rejects_late_frame() => desktop.RunAsync(async () =>
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stages = new List<GlassPresentationStage>();
        int shown = 0;
        using var glass = new LiveWheelGlass(Dispatcher.CurrentDispatcher, _ => shown++, () => { },
            exclude: _ => new Lease(), createSession: (scene, _, _) => new Session(scene, gate, entered));
        glass.StageChanged = stages.Add;
        glass.Start(0, 0, 128, false, true, 1);
        try
        {
            Assert.Equal(GlassPresentationStage.Pending, Assert.Single(stages));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            glass.Stop();
        }
        finally { glass.Stop(); gate.TrySetResult(); await glass.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(0, shown);
        Assert.Equal(GlassPresentationStage.Fallback, stages[^1]);
    });

    [Fact]
    public Task First_animated_native_frame_has_full_material_opacity() => desktop.RunAsync(async () =>
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); gate.SetResult();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new ConcurrentQueue<GlassFrameState>();
        int shown = 0;
        using var glass = new LiveWheelGlass(Dispatcher.CurrentDispatcher, _ => shown++, () => { },
            exclude: _ => new Lease(), createSession: (scene, _, _) => new Session(scene, gate, entered, states));
        glass.Start(0, 0, 128, false, true, 1);
        try
        {
            await Until(() => shown > 0);
            Assert.True(states.TryPeek(out var first));
            Assert.Equal(1, first.Materialize);
        }
        finally { glass.Stop(); await glass.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
    });

    [Fact]
    public Task Geometry_changes_during_pending_reapply_pending_material_after_clearing_old_geometry() => desktop.RunAsync(async () =>
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var material = GlassPresentationStage.Fallback;
        using var glass = new LiveWheelGlass(Dispatcher.CurrentDispatcher, _ => { },
            () => material = GlassPresentationStage.Fallback,
            exclude: _ => new Lease(), createSession: (scene, _, _) => new Session(scene, gate, entered));
        glass.StageChanged = value => material = value;
        glass.StartWheelPresentation(0, 0, 128, false, true, 1);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            glass.SetPetalCount(7);
            Assert.Equal(GlassPresentationStage.Pending, material);
            glass.SetNavigationVisible(true);
            Assert.Equal(GlassPresentationStage.Pending, material);
            glass.StartWheelPresentation(0, 0, 128, false, true, 1);
            Assert.Equal(GlassPresentationStage.Pending, material);
        }
        finally { glass.Stop(); gate.TrySetResult(); await glass.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
    });

    [Fact]
    public Task Foreground_replacement_keeps_the_backing_live_without_a_pending_material_swap() => desktop.RunAsync(async () =>
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); gate.SetResult();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stages = new List<GlassPresentationStage>();
        using var glass = new LiveWheelGlass(Dispatcher.CurrentDispatcher, _ => { }, () => { },
            exclude: _ => new Lease(), createSession: (scene, _, _) => new Session(scene, gate, entered));
        glass.StageChanged = stages.Add;
        glass.SetPetalCount(2); glass.Start(0, 0, 128, false, false, 1);
        try
        {
            await Until(() => stages.Contains(GlassPresentationStage.Live));
            stages.Clear(); glass.SetPetalCount(7);
            await Task.Delay(50);
            Assert.Empty(stages);
        }
        finally { glass.Stop(); await glass.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
    });

    private static async Task Until(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < 5000) await Task.Delay(10);
        Assert.True(condition());
    }
    private sealed class Lease : IDisposable { public void Dispose() { } }
    private sealed class Session(GlassScene scene, TaskCompletionSource gate, TaskCompletionSource entered,
        ConcurrentQueue<GlassFrameState>? states = null) : ILiveGlassSession
    {
        public GlassScene Scene => scene;
        public bool TryRenderFrame(GlassFrameState state, byte[] destination)
        {
            states?.Enqueue(state); entered.TrySetResult(); gate.Task.GetAwaiter().GetResult(); return true;
        }
        public void Dispose() { }
    }
}
