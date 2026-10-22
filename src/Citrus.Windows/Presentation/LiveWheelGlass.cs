using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Citrus.Windows.Presentation;

internal interface IDesktopGlassSource : IDisposable
{
    bool TryReadFrame(byte[] destination);
}

/// <summary>One bounded native optical loop. Input only publishes cheap state; capture and field construction stay off the dispatcher.</summary>
internal sealed class LiveWheelGlass : IDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly Action<WriteableBitmap> present;
    private readonly Action fallback;
    private readonly Func<int, int, int, IDesktopGlassSource?>? createSource;
    private readonly Func<GlassScene, int, int, ILiveGlassSession?>? createSession;
    private readonly Func<GlassScene, int, int, CancellationToken, byte[]?>? bootstrap;
    private readonly Func<nint[], IDisposable?> exclude;
    private CancellationTokenSource? cancellation;
    private long epoch, accentChanged;
    private int petalCount, navigationVisible, accentRegion, accentVersion;
    private long activationRequested, activationAdmitted;
    private bool activationContext;
    private GlassPresentationStage stage = GlassPresentationStage.Fallback;
    private double pointerX = double.NaN, pointerY = double.NaN;
    private CapturePosition position = new(0, 0);
    private bool disposed;
    private EventHandler? renderBoundary;
    private long boundaryEpoch = -1;
    private readonly SemaphoreSlim captureSlot = new(1, 1);
    private readonly SemaphoreSlim bootstrapSlot = new(1, 1);
    private WheelPresentationStart? wheelPresentation;
    internal Task Completion { get; private set; } = Task.CompletedTask;
    internal Task RetirementCompletion { get; private set; } = Task.CompletedTask;
    internal Task ExclusionReleased { get; private set; } = Task.CompletedTask;
    internal bool Running => cancellation is not null;
    internal bool UsesBootstrap => bootstrap is not null || createSource is null && createSession is null;
    internal string? LastFailure { get; private set; }

    /// <summary>Runs synchronously on the dispatcher. The reusable pixel buffer must not be retained.</summary>
    internal Action<GlassScene, byte[]>? FramePresented { get; set; }
    internal Action<double, double>? PresentationMeasured { get; set; }
    internal Action<GlassPresentationStage>? StageChanged { get; set; }
    internal Action<GlassStartupTiming>? StartupMeasured { get; set; }

    internal void SetActivationTiming(long requested, long admitted)
    {
        dispatcher.VerifyAccess();
        activationRequested = requested; activationAdmitted = admitted; activationContext = true;
    }

    internal void SetAdmittedTimestamp(long admitted)
    {
        dispatcher.VerifyAccess();
        activationAdmitted = admitted;
    }

    internal LiveWheelGlass(Dispatcher dispatcher, Action<WriteableBitmap> present, Action fallback,
        Func<int, int, int, IDesktopGlassSource?>? createSource = null, Func<nint[], IDisposable?>? exclude = null,
        Func<GlassScene, int, int, ILiveGlassSession?>? createSession = null,
        Func<GlassScene, int, int, CancellationToken, byte[]?>? bootstrap = null)
    {
        this.dispatcher = dispatcher; this.present = present; this.fallback = fallback;
        this.createSource = createSource; this.createSession = createSession;
        this.bootstrap = bootstrap;
        this.exclude = exclude ?? CaptureExclusion.TryAcquire;
    }

    internal void SetPetalCount(int count)
    {
        dispatcher.VerifyAccess();
        count = Math.Clamp(count, 0, Citrus.Core.WheelGeometry.MaxTargetsPerPage);
        if (count == petalCount) return;
        Volatile.Write(ref petalCount, count);
    }

    internal void SetNavigationVisible(bool visible)
    {
        dispatcher.VerifyAccess();
        int next = visible ? 1 : 0;
        if (navigationVisible == next) return;
        Volatile.Write(ref navigationVisible, next);
    }

    private void PublishStage(GlassPresentationStage value, bool reapply = false)
    {
        if (stage == value && !reapply) return;
        stage = value; StageChanged?.Invoke(value);
    }

    internal void SetAccentRegion(int region)
    {
        dispatcher.VerifyAccess();
        region = Math.Clamp(region, 0, 64);
        if (region == accentRegion) return;
        Volatile.Write(ref accentRegion, region);
        Volatile.Write(ref accentChanged, Environment.TickCount64);
        Interlocked.Increment(ref accentVersion);
    }

    internal void SetPointer(Point point)
    {
        Volatile.Write(ref pointerX, double.IsFinite(point.X) ? point.X : double.NaN);
        Volatile.Write(ref pointerY, double.IsFinite(point.Y) ? point.Y : double.NaN);
    }

    /// <summary>Publish only; native crop relocation and rendering stay on the worker.</summary>
    internal void SetPosition(int screenX, int screenY)
    {
        dispatcher.VerifyAccess();
        var current = Volatile.Read(ref position);
        if (!Running || current.X == screenX && current.Y == screenY) return;
        Volatile.Write(ref position, new(screenX, screenY));
    }

    internal void Start(int x, int y, int size, bool dark, bool animate, params nint[] windows)
    {
        dispatcher.VerifyAccess();
        if (size is < 128 or > 1536) { Stop(); return; }
        StartCore(count => GlassScene.Wheel(count, size / 368d), true, false, x, y, size, dark, animate, windows);
    }

    internal void StartWheelPresentation(int x, int y, int widthPixels, bool dark, bool animate, params nint[] windows)
    {
        dispatcher.VerifyAccess();
        if (widthPixels is < 128 or > 1536) { Stop(); return; }
        // Layout and policy notifications can repeat while the first WGC frame is
        // pending. A matching pale backing needs only a crop update, not a cold restart.
        if (Running && wheelPresentation is { } existing && existing.Width == widthPixels &&
            existing.Windows.AsSpan().SequenceEqual(windows))
        {
            SetPosition(x, y);
            return;
        }
        StartCore(_ => throw new InvalidOperationException("Wheel fields use the shared cache."),
            true, true, x, y, widthPixels, dark, animate, windows);
        if (Running) wheelPresentation = new(widthPixels, windows.ToArray());
    }

    internal void StartSurface(Func<GlassScene> sceneFactory, int screenX, int screenY, bool dark, bool animate, params nint[] windows)
    {
        dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(sceneFactory);
        StartCore(_ => sceneFactory(), false, false, screenX, screenY, 0, dark, animate, windows);
    }

    private void StartCore(Func<int, GlassScene> sceneFactory, bool wheel, bool fullPresentation, int x, int y, int sourceSize,
        bool dark, bool animate, nint[] windows)
    {
        StopCore(notify: false);
        if (disposed || windows.Length == 0 || windows.Any(window => window == 0)) return;
        var lifetime = new CancellationTokenSource();
        cancellation = lifetime;
        Volatile.Write(ref position, new(x, y));
        LastFailure = null;
        // StopCore cleared the previous bitmap and its brushes, even when the
        // previous session was still Pending. Restore the pending material too.
        PublishStage(GlassPresentationStage.Pending, reapply: true);
        long pendingAt = System.Diagnostics.Stopwatch.GetTimestamp();
        long requestedAt = activationContext && activationRequested > 0 ? activationRequested : pendingAt;
        long admittedAt = activationContext && activationAdmitted > 0 ? activationAdmitted : requestedAt;
        activationContext = false;
        long expected = epoch;
        Task previous = Completion;
        nint[] ownedWindows = windows.ToArray();
        // Reference-counted affinity allows the new backing to start while an
        // older native capture retires. Each proxy keeps its HWND until its own
        // affinity lease is released, so HWND reuse cannot restore the wrong one.
        bool independentBootstrap = wheel && UsesBootstrap;
        bool exclusionPrepared = independentBootstrap;
        IDisposable? preparedExclusion = exclusionPrepared ? exclude(ownedWindows) : null;
        var exclusionFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // A scale restart can share the proxy HWND with a retiring generation.
        // Keep the HWND alive until every outstanding affinity lease has returned.
        ExclusionReleased = ExclusionReleased.IsCompleted ? exclusionFinished.Task :
            Task.WhenAll(ExclusionReleased, exclusionFinished.Task);
        Completion = Task.Run(() => RenderAsync(previous, sceneFactory, wheel, fullPresentation, x, y, sourceSize,
            dark, animate, expected, lifetime, ownedWindows, requestedAt, admittedAt, pendingAt,
            exclusionPrepared, preparedExclusion, independentBootstrap, exclusionFinished));
        RetirementCompletion = RetirementCompletion.IsCompleted ? Completion : Task.WhenAll(RetirementCompletion, Completion);
    }

    private async Task RenderAsync(Task previous, Func<int, GlassScene> sceneFactory, bool wheel, bool fullPresentation, int x, int y,
        int sourceSize, bool dark, bool animate, long expected, CancellationTokenSource lifetime, nint[] windows,
        long requestedAt, long admittedAt, long pendingAt, bool exclusionPrepared, IDisposable? preparedExclusion,
        bool independentBootstrap, TaskCompletionSource exclusionFinished)
    {
        var token = lifetime.Token;
        using var renderDelay = new GlassRenderDelay();
        IDisposable? exclusion = preparedExclusion;
        ILiveGlassSession? active = null;
        RawSource? legacy = null;
        byte[]? body = null;
        var statistics = GlassStartupLog.Enabled ? new GlassFrameStatistics() : null;
        bool ownsCaptureSlot = false;
        try
        {
            // Legacy/surface fixtures preserve their ordering. Production wheel
            // bootstrap is independent of slow WGC teardown; native capture below
            // still has exactly one creating/active/retiring owner per controller.
            if (!independentBootstrap) await previous.WaitAsync(token);
            long previousStoppedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            token.ThrowIfCancellationRequested();
            if (!exclusionPrepared)
                await dispatcher.InvokeAsync(() =>
                {
                    if (expected == epoch && !disposed && !token.IsCancellationRequested) exclusion = exclude(windows);
                }, DispatcherPriority.Send, token);
            long excludedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            if (exclusion is null) { Fail("CaptureExclusionUnavailable"); return; }
            token.ThrowIfCancellationRequested();
            if (createSession is null && createSource is not null)
            {
                var source = createSource(x, y, sourceSize);
                if (source is null) return;
                legacy = new RawSource(source, sourceSize);
            }

            CapturePosition? activePosition = null;
            WriteableBitmap? bodyImage = null;
            var stale = System.Diagnostics.Stopwatch.StartNew();
            bool haveFrame = false;
            bool startupReported = false;
            bool bootstrapPresented = false;
            long sceneReadyAt = 0;
            long captureStartedAt = 0;
            long stampSequence = 0, nextSubmissionAt = 0;
            long frameInterval = (long)Math.Ceiling(System.Diagnostics.Stopwatch.Frequency / 60d);
            var submittedFrames = new Dictionary<long, SubmittedFrame>(2);
            while (!token.IsCancellationRequested)
            {
                long started = Environment.TickCount64;
                CapturePosition expectedPosition = Volatile.Read(ref position);
                int key = wheel ? Volatile.Read(ref petalCount) : -1;
                if (active is null)
                {
                    var nextScene = wheel
                        ? GlassSceneCache.Shared.GetWheel(key, sourceSize / 368d, Volatile.Read(ref navigationVisible) != 0, fullPresentation)
                        : sceneFactory(key);
                    sceneReadyAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    token.ThrowIfCancellationRequested();
                    if (wheel && UsesBootstrap && !bootstrapPresented)
                    {
                        long bootstrapStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                        long bootstrapCaptureRevision = StudioCaptureExclusion.ContentRevision;
                        bool bootstrapNavigation = Volatile.Read(ref navigationVisible) != 0;
                        GlassBootstrapWork work = default;
                        byte[]? pixels;
                        await bootstrapSlot.WaitAsync(token);
                        try
                        {
                            if (bootstrap is not null) pixels = bootstrap(nextScene, expectedPosition.X, expectedPosition.Y, token);
                            else
                            {
                                var result = await GlassBootstrapRenderer.TryRenderAsync(nextScene, expectedPosition.X, expectedPosition.Y, token, bootstrapNavigation);
                                pixels = result?.Pixels; work = result?.Work ?? default;
                            }
                        }
                        finally { bootstrapSlot.Release(); }
                        if (pixels is not null)
                        {
                            try
                            {
                                token.ThrowIfCancellationRequested();
                                if (pixels.Length != checked(nextScene.PixelWidth * nextScene.PixelHeight * 4))
                                    throw new ArgumentException("Expected a body-sized bootstrap frame.");
                                long queued = System.Diagnostics.Stopwatch.GetTimestamp();
                                await dispatcher.InvokeAsync(() =>
                                {
                                    if (expected != epoch || disposed || token.IsCancellationRequested ||
                                        !ReferenceEquals(expectedPosition, Volatile.Read(ref position)) ||
                                        bootstrapNavigation != (Volatile.Read(ref navigationVisible) != 0) ||
                                        bootstrapCaptureRevision != StudioCaptureExclusion.ContentRevision) return;
                                    bodyImage = new WriteableBitmap(nextScene.PixelWidth, nextScene.PixelHeight, 96, 96, PixelFormats.Pbgra32, null);
                                    bodyImage.WritePixels(new Int32Rect(0, 0, nextScene.PixelWidth, nextScene.PixelHeight), pixels, nextScene.PixelWidth * 4, 0);
                                    present(bodyImage);
                                    PublishStage(GlassPresentationStage.Bootstrap);
                                    ObserveFirstRenderBoundary(expected, requestedAt, admittedAt);
                                    bootstrapPresented = true;
                                    GlassStartupLog.RecordBootstrapIfEnabled(requestedAt, admittedAt, bootstrapStarted,
                                        queued, System.Diagnostics.Stopwatch.GetTimestamp(), work);
                                }, DispatcherPriority.Render, token);
                            }
                            finally { Array.Clear(pixels); }
                            // Placement/exclusion changes cannot reveal a stale snapshot.
                            if (!bootstrapPresented) continue;
                        }
                    }
                    token.ThrowIfCancellationRequested();
                    if (!ownsCaptureSlot)
                    {
                        await captureSlot.WaitAsync(token);
                        ownsCaptureSlot = true;
                        previousStoppedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    }
                    captureStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    // Acquire the replacement's shared monitor lease before dropping
                    // the previous lease so paging does not restart desktop capture.
                    ILiveGlassSession? replacement = createSession is not null ? createSession(nextScene, expectedPosition.X, expectedPosition.Y)
                        : legacy is not null ? new RawSourceSession(nextScene, legacy)
                        : StudioGlassSession.TryCreate(nextScene, expectedPosition.X, expectedPosition.Y);
                    if (replacement is null) { Fail("CaptureSessionUnavailable"); return; }
                    try
                    {
                        if (active is IStagedLiveGlassSession retiring) await retiring.RetireAsync();
                        active?.Dispose();
                    }
                    catch { replacement.Dispose(); throw; }
                    active = replacement; activePosition = expectedPosition;
                    haveFrame = false; stale.Restart();
                    int length = checked(nextScene.PixelWidth * nextScene.PixelHeight * 4);
                    if (body is null || body.Length != length)
                    {
                        body = new byte[length];
                        // Reuse the installed bootstrap bitmap for live publication.
                        if (!bootstrapPresented) bodyImage = null;
                    }
                }
                else if (!ReferenceEquals(activePosition, expectedPosition))
                {
                    if (active is IRelocatableGlassSession movable)
                    {
                        if (!movable.TryMoveTo(expectedPosition.X, expectedPosition.Y))
                        { Fail("CaptureRelocationUnavailable"); return; }
                    }
                    else
                    {
                        var replacement = createSession is not null
                            ? createSession(active.Scene, expectedPosition.X, expectedPosition.Y)
                            : legacy is not null ? null : StudioGlassSession.TryCreate(active.Scene, expectedPosition.X, expectedPosition.Y);
                        if (replacement is null) { Fail("CaptureRelocationUnavailable"); return; }
                        try
                        {
                            if (active is IStagedLiveGlassSession retiring) await retiring.RetireAsync();
                            active.Dispose();
                        }
                        catch { replacement.Dispose(); throw; }
                        active = replacement;
                    }
                    activePosition = expectedPosition;
                    haveFrame = false; stale.Restart();
                }

                int expectedAccent = Volatile.Read(ref accentVersion);
                int accent = Volatile.Read(ref accentRegion);
                long now = Environment.TickCount64;
                double illumination = !animate || accent == 0 ? 0
                    : Math.Pow(1 - Math.Clamp((now - Volatile.Read(ref accentChanged)) / 160d, 0, 1), 2);
                bool navigation = Volatile.Read(ref navigationVisible) != 0;
                var state = new GlassFrameState(wheel ? 0 : accent, wheel ? double.NaN : Volatile.Read(ref pointerX),
                    wheel ? double.NaN : Volatile.Read(ref pointerY), wheel ? 0 : illumination, 1, wheel ? false : dark,
                    navigation);
                long expectedCapture = StudioCaptureExclusion.ContentRevision;
                bool rendered;
                var frame = new SubmittedFrame(expectedPosition, expectedAccent, navigation, expectedCapture,
                    System.Diagnostics.Stopwatch.GetTimestamp());
                if (active is IStagedLiveGlassSession staged)
                {
                    rendered = staged.TryCompleteFrame(body!, out long completedStamp);
                    if (rendered)
                    {
                        rendered = submittedFrames.TryGetValue(completedStamp, out frame);
                        foreach (long old in submittedFrames.Keys.Where(value => value <= completedStamp).ToArray()) submittedFrames.Remove(old);
                    }
                    long due = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (due >= nextSubmissionAt)
                    {
                        nextSubmissionAt = nextSubmissionAt == 0 || due - nextSubmissionAt > frameInterval
                            ? due + frameInterval : nextSubmissionAt + frameInterval;
                        long stamp = ++stampSequence;
                        if (staged.TrySubmitFrame(state, stamp))
                        {
                            statistics?.Submitted();
                            submittedFrames.Add(stamp, new(expectedPosition, expectedAccent, navigation, expectedCapture, due));
                        }
                    }
                }
                else rendered = active.TryRenderFrame(state, body!);
                if (rendered)
                {
                    var priority = !startupReported || !haveFrame ? DispatcherPriority.Render : DispatcherPriority.Background;
                    var scene = active.Scene;
                    long queuedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    // Await consumption before touching the reusable pixel buffer.
                    // Geometry and selection revisions reject obsolete queued frames.
                    await dispatcher.InvokeAsync(() =>
                    {
                        if (expected != epoch || (!wheel && frame.AccentVersion != accentVersion) ||
                            (wheel && frame.Navigation != (Volatile.Read(ref navigationVisible) != 0)) ||
                            !ReferenceEquals(frame.Position, Volatile.Read(ref position)) ||
                            frame.ExclusionRevision != StudioCaptureExclusion.ContentRevision ||
                            token.IsCancellationRequested || disposed) return;
                        double queueMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds;
                        long uiStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                        bodyImage ??= new WriteableBitmap(scene.PixelWidth, scene.PixelHeight, 96, 96, PixelFormats.Pbgra32, null);
                        var bounds = new Int32Rect(0, 0, scene.PixelWidth, scene.PixelHeight);
                        bodyImage.WritePixels(bounds, body!, scene.PixelWidth * 4, 0);
                        present(bodyImage);
                        haveFrame = true;
                        PublishStage(GlassPresentationStage.Live);
                        ObserveFirstRenderBoundary(expected, requestedAt, admittedAt);
                        FramePresented?.Invoke(scene, body!);
                        if (!startupReported)
                        {
                            startupReported = true;
                            var nativeTiming = (active as IGlassSessionTiming)?.StartupTiming ?? default;
                            var timing = new GlassStartupTiming(requestedAt, admittedAt, pendingAt, previousStoppedAt,
                                excludedAt, sceneReadyAt, nativeTiming.CaptureInitialized, nativeTiming.RendererReady,
                                nativeTiming.FirstFrame, nativeTiming.RenderCompleted,
                                System.Diagnostics.Stopwatch.GetTimestamp(), nativeTiming.RenderMilliseconds,
                                nativeTiming.ReadbackMilliseconds, nativeTiming.PreparedResources)
                            { CaptureStarted = captureStartedAt };
                            StartupMeasured?.Invoke(timing);
                            GlassStartupLog.RecordIfEnabled(timing);
                        }
                        double uiMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(uiStarted).TotalMilliseconds;
                        PresentationMeasured?.Invoke(queueMilliseconds, uiMilliseconds);
                        statistics?.Presented(frame.SubmittedAt, queueMilliseconds, uiMilliseconds);
                    }, priority, token);
                }
                // False before the first texture has a bounded fallback. Production
                // sessions continue rendering cached desktop textures after that.
                if (!haveFrame && stale.ElapsedMilliseconds > 1500) { Fail("InitialCaptureFrameTimeout"); return; }
                int delay = active is IStagedLiveGlassSession waiting
                    ? waiting.HasPendingFrames ? 2 : (int)Math.Clamp(
                        (nextSubmissionAt - System.Diagnostics.Stopwatch.GetTimestamp()) * 1000d / System.Diagnostics.Stopwatch.Frequency, 1, 17)
                    : (int)Math.Max(1, 16 - (Environment.TickCount64 - started));
                await renderDelay.WaitAsync(delay, token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or COMException or Win32Exception or
            InvalidOperationException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        { if (error is not OperationCanceledException) Fail(error.GetType().Name + ": " + error.Message); }
        finally
        {
            if (body is not null) Array.Clear(body);
            try
            {
                try
                {
                    try
                    {
                        if (active is IStagedLiveGlassSession retiring) await retiring.RetireAsync();
                        active?.Dispose();
                    }
                    finally
                    {
                        try { legacy?.Dispose(); }
                        finally
                        {
                            try { exclusion?.Dispose(); }
                            finally { exclusionFinished.TrySetResult(); }
                        }
                    }
                }
                finally
                {
                    if (!dispatcher.HasShutdownStarted)
                        await dispatcher.InvokeAsync(() =>
                        {
                            if (expected != epoch) return;
                            cancellation = null;
                            fallback();
                            PublishStage(GlassPresentationStage.Fallback);
                        }, DispatcherPriority.Background);
                }
            }
            catch (TaskCanceledException) when (dispatcher.HasShutdownStarted) { }
            finally
            {
                if (ownsCaptureSlot) captureSlot.Release();
                exclusionFinished.TrySetResult();
                if (statistics is not null) GlassStartupLog.RecordFramesIfEnabled(admittedAt, statistics);
                lifetime.Dispose();
            }
        }
        void Fail(string reason) { if (expected == Volatile.Read(ref epoch)) LastFailure = reason; }
    }

    internal void Stop()
        => StopCore(notify: true);

    private void StopCore(bool notify)
    {
        dispatcher.VerifyAccess();
        if (renderBoundary is not null) { CompositionTarget.Rendering -= renderBoundary; renderBoundary = null; }
        wheelPresentation = null;
        ++epoch;
        var previous = cancellation; cancellation = null;
        previous?.Cancel();
        fallback();
        if (notify) PublishStage(GlassPresentationStage.Fallback);
    }

    public void Dispose() { if (disposed) return; Stop(); disposed = true; }

    private sealed record CapturePosition(int X, int Y);
    private readonly record struct SubmittedFrame(CapturePosition Position, int AccentVersion, bool Navigation,
        long ExclusionRevision, long SubmittedAt);

    private void ObserveFirstRenderBoundary(long expected, long requested, long admitted)
    {
        if (!GlassStartupLog.Enabled || boundaryEpoch == expected) return;
        boundaryEpoch = expected;
        renderBoundary = (_, _) =>
        {
            if (renderBoundary is not null) CompositionTarget.Rendering -= renderBoundary;
            renderBoundary = null;
            if (expected == epoch && !disposed)
                GlassStartupLog.RecordBoundaryIfEnabled(requested, admitted, System.Diagnostics.Stopwatch.GetTimestamp(), stage.ToString());
        };
        CompositionTarget.Rendering += renderBoundary;
    }
    private sealed record WheelPresentationStart(int Width, nint[] Windows);

    /// <summary>Compatibility seam for generated raw-source tests; live production capture stays on the GPU.</summary>
    private sealed class RawSource : IDisposable
    {
        private readonly IDesktopGlassSource source;
        internal int Size { get; }
        internal byte[] Pixels { get; }
        internal bool HaveFrame { get; private set; }
        internal RawSource(IDesktopGlassSource source, int size)
        { this.source = source; Size = size; Pixels = new byte[checked(size * size * 4)]; }
        internal bool Read() { if (source.TryReadFrame(Pixels)) HaveFrame = true; return HaveFrame; }
        public void Dispose() => source.Dispose();
    }

    private sealed class RawSourceSession(GlassScene scene, RawSource source) : ILiveGlassSession
    {
        private D3D11StudioRenderer? renderer;
        public GlassScene Scene { get; } = scene;
        public bool TryRenderFrame(GlassFrameState state, byte[] destination)
        {
            if (!source.Read()) return false;
            renderer ??= D3D11StudioRenderer.CreateForTesting(Scene, software: true);
            renderer.RenderPixels(source.Pixels, source.Size, source.Size, -Scene.PaddingPixels, -Scene.PaddingPixels, state, destination);
            return true;
        }
        public void Dispose() { renderer?.Dispose(); renderer = null; }
    }
}

/// <summary>Compatibility entry point; all surfaces share reference-counted capture-affinity leases.</summary>
internal static class CaptureExclusion
{
    internal static IDisposable? TryAcquire(nint[] windows) => StudioCaptureExclusion.TryAcquire(windows);
}
