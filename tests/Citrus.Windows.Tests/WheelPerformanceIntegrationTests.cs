using System.Reflection;
using System.Windows.Threading;
using Citrus.Windows.Presentation;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class WheelPerformanceIntegrationTests(DesktopFixture desktop)
{
    [Fact]
    public void Controller_publishes_only_the_body_bitmap()
    {
        var constructor = Assert.Single(typeof(LiveWheelGlass).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));
        var bitmapType = Assert.Single(constructor.GetParameters()[1].ParameterType.GenericTypeArguments);
        Assert.Equal(typeof(System.Windows.Media.Imaging.WriteableBitmap), bitmapType);
    }

    [Fact]
    public Task Fresh_bootstrap_does_not_wait_for_retiring_native_capture() => desktop.RunAsync(async () =>
    {
        using var retiring = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int presentations = 0, creations = 0;
        using var glass = new LiveWheelGlass(Dispatcher.CurrentDispatcher, _ => presentations++, () => { },
            exclude: _ => new Lease(),
            createSession: (scene, _, _) => new HeldSession(scene, Interlocked.Increment(ref creations) == 1 ? retiring : null, release),
            bootstrap: (scene, _, _, _) => Enumerable.Repeat((byte)135, scene.PixelWidth * scene.PixelHeight * 4).ToArray());
        Task first = Task.CompletedTask;
        try
        {
            glass.StartWheelPresentation(0, 0, 160, false, false, new nint(1));
            await Until(() => presentations == 1 && creations == 1);
            first = glass.Completion;
            glass.Stop();
            await Until(() => retiring.IsSet);
            glass.StartWheelPresentation(0, 0, 160, false, false, new nint(1));
            await Task.Delay(250);
            Assert.Equal(2, presentations);
            Assert.Equal(1, creations); // Native capture still has one retiring owner.
        }
        finally
        {
            release.Set(); glass.Stop();
            await Task.WhenAll(first, glass.Completion).WaitAsync(TimeSpan.FromSeconds(5));
        }
    });

    [Fact]
    public Task Exclusion_release_waits_for_all_generations_using_the_same_proxy() => desktop.RunAsync(async () =>
    {
        using var retiring = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int presentations = 0, creations = 0, exclusions = 0;
        using var glass = new LiveWheelGlass(Dispatcher.CurrentDispatcher, _ => presentations++, () => { },
            exclude: _ => { Interlocked.Increment(ref exclusions); return new CallbackLease(() => Interlocked.Decrement(ref exclusions)); },
            createSession: (scene, _, _) => new HeldSession(scene, Interlocked.Increment(ref creations) == 1 ? retiring : null, release),
            bootstrap: (scene, _, _, _) => Enumerable.Repeat((byte)135, scene.PixelWidth * scene.PixelHeight * 4).ToArray());
        try
        {
            glass.StartWheelPresentation(0, 0, 160, false, false, new nint(1));
            await Until(() => presentations == 1 && creations == 1);
            // A DPI restart retains the same HWND while the previous native session retires.
            glass.StartWheelPresentation(0, 0, 192, false, false, new nint(1));
            await Until(() => retiring.IsSet && presentations == 2);
            glass.Stop();
            await glass.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Volatile.Read(ref exclusions));
            Assert.False(glass.ExclusionReleased.IsCompleted, "The proxy HWND must outlive the older exclusion lease too.");
        }
        finally
        {
            release.Set(); glass.Stop();
            await glass.RetirementCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await glass.ExclusionReleased.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, Volatile.Read(ref exclusions));
    });

    [Fact]
    public Task Controller_uses_staged_completion_instead_of_the_blocking_compatibility_reader() => desktop.RunAsync(async () =>
    {
        int presentations = 0;
        var session = new StagedSession(GlassScene.WheelPresentation(0, 160 / 368d, true));
        using var glass = new LiveWheelGlass(Dispatcher.CurrentDispatcher, _ => presentations++, () => { },
            exclude: _ => new Lease(), createSession: (_, _, _) => session);
        try
        {
            glass.StartWheelPresentation(0, 0, 160, false, false, new nint(1));
            await Until(() => presentations > 0 || !glass.Running);
            Assert.True(session.Submissions > 0, "The controller must submit without using the blocking reader.");
            Assert.True(presentations > 0);
        }
        finally { glass.Stop(); await glass.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
    });

    [Fact]
    public void Controller_exposes_exclusion_and_retirement_lifetimes_separately()
    {
        foreach (string property in new[] { "ExclusionReleased", "RetirementCompletion" })
        {
            var member = typeof(LiveWheelGlass).GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(member); Assert.Equal(typeof(Task), member.PropertyType);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && started.ElapsedMilliseconds < 5000) await Task.Delay(10);
        Assert.True(condition());
    }
    private sealed class Lease : IDisposable { public void Dispose() { } }
    private sealed class CallbackLease(Action release) : IDisposable { public void Dispose() => release(); }
    private sealed class HeldSession(GlassScene scene, ManualResetEventSlim? retiring, ManualResetEventSlim release) : ILiveGlassSession
    {
        public GlassScene Scene => scene;
        public bool TryRenderFrame(GlassFrameState state, byte[] destination) => false;
        public void Dispose() { if (retiring is not null) { retiring.Set(); release.Wait(TimeSpan.FromSeconds(5)); } }
    }
    private sealed class StagedSession(GlassScene scene) : IStagedLiveGlassSession
    {
        private long pending;
        public int Submissions { get; private set; }
        public GlassScene Scene => scene;
        public bool HasPendingFrames => pending != 0;
        public bool TrySubmitFrame(GlassFrameState state, long stamp) { pending = stamp; Submissions++; return true; }
        public bool TryCompleteFrame(byte[] destination, out long stamp)
        {
            stamp = pending; if (stamp == 0) return false;
            pending = 0; Array.Fill(destination, (byte)135); return true;
        }
        public bool TryRenderFrame(GlassFrameState state, byte[] destination) => throw new NotSupportedException("Use staged submission.");
        public Task RetireAsync() { pending = 0; return Task.CompletedTask; }
        public void Dispose() { }
    }
}
