using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Citrus.Windows.Presentation;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class LiveWheelGlassTests(DesktopFixture desktop)
{
    [Fact]
    public Task Movement_rejects_an_obsolete_queued_crop_without_stopping_the_live_session() => desktop.RunAsync(async () =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var scene = new GlassScene(80, 60, 1, [GlassRegion.RoundedRect(1, 1, 1, 78, 58, 10)]);
        var session = new MovingRecordingSession(scene);
        using var posted = new ManualResetEventSlim();
        int shown = 0, publishedX = -1, created = 0;
        using var glass = new LiveWheelGlass(dispatcher, body =>
        {
            shown++;
            var pixel = new byte[4]; body.CopyPixels(new Int32Rect(40, 30, 1, 1), pixel, 4, 0);
            publishedX = pixel[0];
        }, () => { }, exclude: _ => new Lease(), createSession: (_, _, _) => { created++; return session; });
        void Posted(object? sender, DispatcherHookEventArgs args)
        {
            if (session.Reading && args.Operation.Priority == DispatcherPriority.Render) posted.Set();
        }
        dispatcher.Hooks.OperationPosted += Posted;
        try
        {
            glass.StartSurface(() => scene, 10, 20, false, false, 1);
            await WaitUntil(() => session.Reading);
            using (dispatcher.DisableProcessing())
            {
                session.FirstGate.SetResult();
                Assert.True(posted.Wait(TimeSpan.FromSeconds(5)));
                var clock = System.Diagnostics.Stopwatch.StartNew();
                glass.SetPosition(30, 40);
                Assert.True(clock.ElapsedMilliseconds < 50);
            }
            await WaitUntil(() => session.Reads >= 2);
            Assert.Equal(0, shown);
            Assert.Equal(30, session.X); Assert.Equal(40, session.Y);
            session.SecondGate.SetResult();
            await WaitUntil(() => shown > 0);
            Assert.Equal(30, publishedX); Assert.Equal(1, created);
            Assert.False(session.Disposed); Assert.True(glass.Running);
        }
        finally
        {
            dispatcher.Hooks.OperationPosted -= Posted;
            session.FirstGate.TrySetResult(); session.SecondGate.TrySetResult();
            glass.Stop(); await glass.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    });

    [Fact]
    public Task Scene_build_and_render_do_not_block_pointer_selection_or_the_dispatcher() => desktop.RunAsync(async () =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var factoryEntered = new ManualResetEventSlim();
        using var factoryContinue = new ManualResetEventSlim();
        var lease = new Lease();
        RecordingSession? session = null;
        GlassFrameState published = default;
        int shown = 0, sceneThread = 0;
        using var glass = new LiveWheelGlass(dispatcher, _ => shown++, () => { }, exclude: _ => lease,
            createSession: (scene, _, _) => session = new RecordingSession(scene));
        glass.FramePresented = (_, _) =>
        {
            Assert.True(dispatcher.CheckAccess());
            published = session!.LastState;
        };
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            glass.StartSurface(() =>
            {
                sceneThread = Environment.CurrentManagedThreadId;
                factoryEntered.Set();
                factoryContinue.Wait(TimeSpan.FromSeconds(5));
                return new GlassScene(100, 60, 1, [GlassRegion.RoundedRect(1, 2, 2, 96, 56, 12)]);
            }, 10, 20, true, false, 1);
            Assert.True(clock.ElapsedMilliseconds < 100);
            await WaitUntil(() => factoryEntered.IsSet);
            Assert.NotEqual(Environment.CurrentManagedThreadId, sceneThread);
            clock.Restart();
            glass.SetPointer(new System.Windows.Point(30, 40)); glass.SetAccentRegion(1);
            Assert.True(clock.ElapsedMilliseconds < 50);
            factoryContinue.Set();
            await WaitUntil(() => shown > 0);
            Assert.Equal(1, published.AccentRegion);
            Assert.Equal(30, published.PointerXDip); Assert.Equal(40, published.PointerYDip);
            Assert.True(published.Dark); Assert.Equal(1, published.Materialize); Assert.Equal(0, published.Illumination);
        }
        finally
        {
            factoryContinue.Set(); glass.Stop(); await glass.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(session!.Disposed); Assert.True(lease.Disposed);
    });

    [Fact]
    public Task Selection_change_rejects_a_queued_previous_accent_without_waiting_for_render() => desktop.RunAsync(async () =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var scene = new GlassScene(80, 60, 1,
            [GlassRegion.RoundedRect(1, 1, 1, 78, 58, 10), GlassRegion.Circle(2, 40, 30, 14)]);
        var session = new RecordingSession(scene) { FirstGate = new(), SecondGate = new() };
        using var posted = new ManualResetEventSlim();
        int shown = 0, publishedAccent = 0;
        using var glass = new LiveWheelGlass(dispatcher, body =>
        {
            shown++;
            var pixel = new byte[4]; body.CopyPixels(new Int32Rect(40, 30, 1, 1), pixel, 4, 0);
            publishedAccent = pixel[0];
        }, () => { }, exclude: _ => new Lease(), createSession: (_, _, _) => session);
        void Posted(object? sender, DispatcherHookEventArgs args)
        {
            if (session.Reading && args.Operation.Priority == DispatcherPriority.Render) posted.Set();
        }
        dispatcher.Hooks.OperationPosted += Posted;
        try
        {
            glass.SetAccentRegion(1); glass.StartSurface(() => scene, 0, 0, false, false, 1);
            await WaitUntil(() => session.Reading);
            using (dispatcher.DisableProcessing())
            {
                session.FirstGate.SetResult();
                Assert.True(posted.Wait(TimeSpan.FromSeconds(5)));
                var clock = System.Diagnostics.Stopwatch.StartNew();
                glass.SetAccentRegion(2);
                Assert.True(clock.ElapsedMilliseconds < 50);
            }
            await WaitUntil(() => session.Reads >= 2);
            Assert.Equal(0, shown);
            session.SecondGate.SetResult();
            await WaitUntil(() => shown > 0);
            Assert.Equal(2, publishedAccent);
        }
        finally
        {
            dispatcher.Hooks.OperationPosted -= Posted;
            session.FirstGate.TrySetResult(); session.SecondGate.TrySetResult();
            glass.Stop(); await glass.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    });

    [Fact]
    public Task No_capture_starts_when_window_exclusion_fails() => desktop.RunAsync(async () =>
    {
        int created = 0, displayed = 0;
        using var glass = new LiveWheelGlass(Dispatcher.CurrentDispatcher, _ => displayed++, () => { },
            (_, _, _) => { created++; return new FakeSource(); }, _ => null);
        glass.Start(0, 0, 128, false, true, 1);
        await glass.Completion;
        Assert.Equal(0, created); Assert.Equal(0, displayed); Assert.False(glass.Running);
    });

    [Fact]
    public Task Fresh_frames_render_curved_transparent_contours_and_stop_releases_every_resource() => desktop.RunAsync(async () =>
    {
        var source = new FakeSource(); var lease = new Lease();
        int displayed = 0, cleared = 0;
        WriteableBitmap? backing = null;
        using var glass = new LiveWheelGlass(Dispatcher.CurrentDispatcher, body =>
        {
            displayed++;
            backing ??= body;
            Assert.Same(backing, body);
            Assert.Equal(128, body.PixelWidth);
            var pixel = new byte[4]; body.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            Assert.Equal(0, pixel[3]);
        }, () => cleared++, (_, _, _) => source, _ => lease);
        glass.SetPetalCount(7); glass.SetPointer(new System.Windows.Point(200, 50));
        glass.Start(0, 0, 128, false, true, 1, 2);
        await WaitUntil(() => displayed > 1);
        glass.Stop(); int stoppedAt = displayed;
        await glass.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(stoppedAt, displayed); Assert.True(source.Disposed); Assert.True(lease.Disposed);
        Assert.True(cleared > 0); Assert.False(glass.Running);
    });

    [Fact]
    public Task Old_session_cannot_publish_or_clear_new_session() => desktop.RunAsync(async () =>
    {
        var first = new FakeSource { Gate = new TaskCompletionSource() }; var second = new FakeSource();
        var leases = new List<Lease>(); int creates = 0, displayed = 0;
        using var glass = new LiveWheelGlass(Dispatcher.CurrentDispatcher, _ => displayed++, () => { },
            (_, _, _) => Interlocked.Increment(ref creates) == 1 ? first : second,
            _ => { var lease = new Lease(); leases.Add(lease); return lease; });
        glass.Start(0, 0, 128, false, false, 1);
        await WaitUntil(() => first.Reading);
        var old = glass.Completion;
        glass.Start(0, 0, 128, true, false, 1);
        await Task.Delay(50);
        Assert.Equal(1, creates); // The old affinity lease must finish before the new source starts.
        first.Gate.SetResult(); await old.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntil(() => displayed > 0);
        Assert.True(glass.Running); Assert.True(first.Disposed); Assert.False(second.Disposed);
        glass.Stop(); await glass.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(second.Disposed); Assert.All(leases, item => Assert.True(item.Disposed));
    });

    [Fact]
    public Task Device_failure_returns_to_fallback_and_disposes_source() => desktop.RunAsync(async () =>
    {
        var source = new FakeSource { Fail = true }; var lease = new Lease(); int shown = 0, cleared = 0;
        using var glass = new LiveWheelGlass(Dispatcher.CurrentDispatcher, _ => shown++, () => cleared++,
            (_, _, _) => source, _ => lease);
        glass.Start(0, 0, 128, false, false, 1);
        await glass.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, shown); Assert.True(cleared >= 2); Assert.True(source.Disposed); Assert.True(lease.Disposed);
        Assert.False(glass.Running);
    });

    [Fact]
    public Task No_initial_frame_times_out_but_static_desktop_keeps_its_last_valid_frame() => desktop.RunAsync(async () =>
    {
        var empty = new FakeSource { Frames = 0 }; int shown = 0;
        using var glass = new LiveWheelGlass(Dispatcher.CurrentDispatcher, _ => shown++, () => { },
            (_, _, _) => empty, _ => new Lease());
        glass.Start(0, 0, 128, false, false, 1);
        await glass.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, shown); Assert.True(empty.Disposed); Assert.False(glass.Running);
        var still = new FakeSource { Frames = 1 };
        using var staticGlass = new LiveWheelGlass(Dispatcher.CurrentDispatcher, _ => shown++, () => { },
            (_, _, _) => still, _ => new Lease());
        staticGlass.Start(0, 0, 128, false, false, 1);
        await WaitUntil(() => shown > 0); await Task.Delay(1700);
        Assert.True(staticGlass.Running); Assert.False(still.Disposed);
        staticGlass.Stop(); await staticGlass.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    });

    [Fact]
    public Task Foreground_count_change_preserves_the_existing_backing_bitmap() => desktop.RunAsync(async () =>
    {
        int shown = 0, cleared = 0;
        using var glass = new LiveWheelGlass(Dispatcher.CurrentDispatcher, _ => shown++, () => cleared++,
            (_, _, _) => new FakeSource(), _ => new Lease());
        glass.SetPetalCount(2); glass.Start(0, 0, 128, false, true, 1);
        await WaitUntil(() => shown > 0);
        int before = cleared;
        glass.SetPetalCount(7);
        Assert.Equal(before, cleared);
        glass.Stop(); await glass.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    });

    [Fact]
    public Task Queued_backing_frame_remains_valid_when_only_foreground_count_changes() => desktop.RunAsync(async () =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var source = new FakeSource { Gate = new TaskCompletionSource(), SecondGate = new TaskCompletionSource() };
        using var posted = new ManualResetEventSlim();
        int shown = 0;
        using var glass = new LiveWheelGlass(dispatcher, _ => shown++, () => { }, (_, _, _) => source, _ => new Lease());
        void Posted(object? sender, DispatcherHookEventArgs args)
        {
            if (source.Reading && args.Operation.Priority == DispatcherPriority.Render) posted.Set();
        }
        dispatcher.Hooks.OperationPosted += Posted;
        try
        {
            glass.SetPetalCount(2); glass.Start(0, 0, 128, false, false, 1);
            await WaitUntil(() => source.Reading);
            // Hold dispatcher delivery while the worker queues the shared backing.
            // No UI work is needed between the released read and that queued publication.
            using (dispatcher.DisableProcessing())
            {
                source.Gate.SetResult();
                Assert.True(posted.Wait(TimeSpan.FromSeconds(5)));
                glass.SetPetalCount(7);
            }
            await WaitUntil(() => source.Reads >= 2);
            Assert.Equal(1, shown);
            source.SecondGate.SetResult();
            await WaitUntil(() => shown > 0);
        }
        finally
        {
            dispatcher.Hooks.OperationPosted -= Posted;
            source.Gate.TrySetResult(); source.SecondGate.TrySetResult();
            glass.Stop(); await glass.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    });

    private static async Task WaitUntil(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < 5000) await Task.Delay(10);
        Assert.True(condition());
    }
    private sealed class Lease : IDisposable { public bool Disposed; public void Dispose() => Disposed = true; }
    private sealed class MovingRecordingSession(GlassScene scene) : ILiveGlassSession, IRelocatableGlassSession
    {
        public GlassScene Scene => scene;
        public volatile bool Reading, Disposed;
        public volatile int Reads, X = 10, Y = 20;
        public TaskCompletionSource FirstGate = new(), SecondGate = new();
        public bool TryMoveTo(int screenLeft, int screenTop) { X = screenLeft; Y = screenTop; return true; }
        public bool TryRenderFrame(GlassFrameState state, byte[] destination)
        {
            int read = Interlocked.Increment(ref Reads), renderedX = X;
            Reading = true;
            (read == 1 ? FirstGate : SecondGate).Task.GetAwaiter().GetResult();
            for (int i = 0; i < destination.Length; i += 4) { destination[i] = (byte)renderedX; destination[i + 3] = 255; }
            return true;
        }
        public void Dispose() => Disposed = true;
    }
    private sealed class RecordingSession(GlassScene scene) : ILiveGlassSession
    {
        public GlassScene Scene { get; } = scene;
        public volatile bool Reading, Disposed;
        public volatile int Reads;
        public GlassFrameState LastState;
        public TaskCompletionSource? FirstGate, SecondGate;
        public bool TryRenderFrame(GlassFrameState state, byte[] destination)
        {
            int read = Interlocked.Increment(ref Reads);
            Reading = true; LastState = state;
            FirstGate?.Task.GetAwaiter().GetResult();
            if (read >= 2) SecondGate?.Task.GetAwaiter().GetResult();
            for (int i = 0; i < destination.Length; i += 4)
            { destination[i] = (byte)state.AccentRegion; destination[i + 3] = 255; }
            return true;
        }
        public void Dispose() => Disposed = true;
    }
    private sealed class FakeSource : IDesktopGlassSource
    {
        public volatile bool Reading, Disposed;
        public bool Fail;
        public int Frames = int.MaxValue;
        public volatile int Reads;
        public TaskCompletionSource? Gate;
        public TaskCompletionSource? SecondGate;
        public bool TryReadFrame(byte[] pixels)
        {
            int read = Interlocked.Increment(ref Reads);
            Reading = true; Gate?.Task.GetAwaiter().GetResult();
            if (read >= 2) SecondGate?.Task.GetAwaiter().GetResult();
            if (Fail) throw new InvalidOperationException("Owned device loss");
            if (Frames-- <= 0) return false;
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 80; pixels[i + 1] = 100; pixels[i + 2] = 200; pixels[i + 3] = 255; }
            return true;
        }
        public void Dispose() => Disposed = true;
    }
}
