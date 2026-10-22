using System.Runtime.InteropServices;
using static Citrus.Windows.Presentation.StudioD3D11Native;

namespace Citrus.Windows.Presentation;

/// <summary>Resident optical resources without a capture session or retained desktop pixels.</summary>
internal sealed class StudioGlassRuntime
{
    private const long IdleBudget = 64 * 1024 * 1024;
    private const int MaximumOutstandingAllocations = 4;
    internal static StudioGlassRuntime Shared { get; } = new();
    private StudioGlassAllocationCache<Allocation> idle = CreateCache();
    private readonly HashSet<Task> retirements = [];
    // Reservations span scratch preparation, use and retirement. A hung device
    // cannot turn repeated cancel/reopen into an unbounded GPU allocation queue.
    private readonly HashSet<Allocation> outstanding = [];
    private nint device, context;
    private long generation;
    private bool stopped;
    internal int IdleCount { get { lock (DesktopGlassCapturePool.GpuGate) return idle.IdleCount; } }
    internal long IdleBytes { get { lock (DesktopGlassCapturePool.GpuGate) return idle.IdleBytes; } }
    internal int OutstandingAllocationCount { get { lock (DesktopGlassCapturePool.GpuGate) return outstanding.Count; } }
    internal Task RetirementCompletion { get { lock (DesktopGlassCapturePool.GpuGate) return Task.WhenAll(retirements); } }

    private static StudioGlassAllocationCache<Allocation> CreateCache() =>
        new(2, IdleBudget, allocation => allocation.AllocationBytes, allocation => allocation.ClearForReturn());

    internal Task PrewarmAsync(GlassScene scene, CancellationToken token = default) => Task.Run(async () =>
    {
        token.ThrowIfCancellationRequested();
        using var lease = Rent(scene);
        await PrepareExecutionAsync(lease, token);
    }, token);

    internal Task PrewarmWheelAsync(double scale, CancellationToken token = default) => Task.Run(async () =>
    {
        // Return the actual wheel last when both outputs do not fit the idle budget.
        foreach (bool fullPresentation in new[] { false, true })
        {
            token.ThrowIfCancellationRequested();
            var scene = GlassSceneCache.Shared.GetWheel(0, scale, fullPresentation, fullPresentation);
            using var lease = Rent(scene);
            await PrepareExecutionAsync(lease, token);
        }
    }, token);

    private static async Task PrepareExecutionAsync(Lease lease, CancellationToken token)
    {
        byte[]? output = null;
        long started = Environment.TickCount64;
        using var completionDelay = new GlassRenderDelay();
        try
        {
            lock (DesktopGlassCapturePool.GpuGate)
            {
                token.ThrowIfCancellationRequested();
                if (lease.Renderer.ExecutionPrepared) return;
                if (!lease.Renderer.TrySubmitClearedInput(new(NavigationVisible: true), 1))
                    throw new InvalidOperationException("A prepared allocation cannot already have pending presentation work.");
            }
            output = new byte[checked(lease.Scene.PixelWidth * lease.Scene.PixelHeight * 4)];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                lock (DesktopGlassCapturePool.GpuGate)
                {
                    if (lease.Renderer.TryComplete(output, out _))
                    {
                        lease.Renderer.MarkExecutionPrepared();
                        return;
                    }
                }
                if (Environment.TickCount64 - started >= 2000)
                    throw new InvalidOperationException("Prepared GPU execution timed out.");
                await completionDelay.WaitAsync(1, token).ConfigureAwait(false);
            }
        }
        finally { if (output is not null) Array.Clear(output); }
    }

    internal Lease Rent(GlassScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        Allocation allocation;
        Key key;
        bool prepared;
        long expectedGeneration;
        nint borrowedDevice, borrowedContext;
        lock (DesktopGlassCapturePool.GpuGate)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            if (outstanding.Count >= MaximumOutstandingAllocations)
                throw new InvalidOperationException("The bounded glass renderer allocations are still in use or retiring.");
            EnsureDevice();
            key = new(scene.Scale, scene.PixelWidth, scene.PixelHeight, scene.Material, scene.IsWheelBacking ? null : scene);
            allocation = idle.Rent(key, () => new Allocation(new D3D11StudioRenderer(device, context, scene)), out prepared);
            outstanding.Add(allocation);
            expectedGeneration = generation; borrowedDevice = device; borrowedContext = context;
        }
        try
        {
            // DC/DIB preparation can touch the graphics driver. It reads no pixels
            // and must not prevent unrelated immediate-context commands.
            allocation.EnsureScratch(scene);
            return new(this, key, expectedGeneration, allocation, scene, borrowedDevice, borrowedContext, prepared);
        }
        catch
        {
            lock (DesktopGlassCapturePool.GpuGate) DiscardAllocation(allocation);
            throw;
        }
    }

    /// <summary>Retains COM ownership while slow WinRT capture setup runs outside the GPU gate.</summary>
    internal DeviceLease AcquireDevice()
    {
        lock (DesktopGlassCapturePool.GpuGate)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            EnsureDevice();
            return new(device, context);
        }
    }

    private void EnsureDevice()
    {
        if (device != 0 && Method<DeviceStatus>(device, 39)(device) < 0) ResetCore();
        if (device != 0) return;
        try { Check(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out device, out _, out context)); }
        catch { Release(ref context); Release(ref device); throw; }
    }

    internal async Task ShutdownAsync()
    {
        Task pending;
        lock (DesktopGlassCapturePool.GpuGate) { stopped = true; pending = Task.WhenAll(retirements); }
        await pending;
        lock (DesktopGlassCapturePool.GpuGate) ResetCore();
    }

    /// <summary>Owned measurements only; production shutdown remains terminal.</summary>
    internal void ResetForTesting()
    {
        lock (DesktopGlassCapturePool.GpuGate) { ResetCore(); stopped = false; }
    }

    private void ResetCore()
    {
        // Outstanding allocations retain their own COM ownership and capacity
        // reservations until returned, even after device generation changes.
        generation++;
        try { idle.Dispose(); }
        finally { idle = CreateCache(); Release(ref context); Release(ref device); }
    }

    private void Return(Key key, long expectedGeneration, Allocation allocation, bool discard)
    {
        lock (DesktopGlassCapturePool.GpuGate)
        {
            if (discard) { DiscardAllocation(allocation); return; }
            if (allocation.Renderer.PendingReadbackCount == 0)
            { ReturnReady(key, expectedGeneration, allocation); return; }
            allocation.Renderer.InvalidateReadbacks();
            var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            retirements.Add(complete.Task);
            _ = Task.Run(() => RetireAsync(key, expectedGeneration, allocation, complete));
        }
    }

    private async Task RetireAsync(Key key, long expectedGeneration, Allocation allocation, TaskCompletionSource complete)
    {
        bool reusable = false;
        using var completionDelay = new GlassRenderDelay();
        try
        {
            long deadline = Environment.TickCount64 + 2000;
            while (Environment.TickCount64 < deadline)
            {
                lock (DesktopGlassCapturePool.GpuGate)
                {
                    if (allocation.Renderer.TryDiscardCompletedReadbacks()) { reusable = true; break; }
                }
                await completionDelay.WaitAsync(1).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is ExternalException or InvalidOperationException) { }
        finally
        {
            lock (DesktopGlassCapturePool.GpuGate)
            {
                try
                {
                    if (reusable) ReturnReady(key, expectedGeneration, allocation);
                    else DiscardAllocation(allocation); // Never clear/pool unfinished or failed resources.
                }
                // Both terminal helpers attempt disposal and release their
                // reservation even when clearing a lost device fails. Do not
                // fault an unobserved cleanup worker for that expected failure.
                catch (Exception error) when (error is ExternalException or InvalidOperationException) { }
                finally { retirements.Remove(complete.Task); complete.TrySetResult(); }
            }
        }
    }

    private void ReturnReady(Key key, long expectedGeneration, Allocation allocation)
    {
        try
        {
            if (stopped || generation != expectedGeneration) allocation.Dispose();
            else idle.Return(key, allocation);
        }
        catch
        {
            // Lease ownership is already consumed. A later Discard cannot
            // reclaim it, so terminal cleanup must happen here before rethrow.
            allocation.Dispose();
            throw;
        }
        finally { outstanding.Remove(allocation); }
    }

    private void DiscardAllocation(Allocation allocation)
    { try { allocation.Dispose(); } finally { outstanding.Remove(allocation); } }

    internal sealed record Key(double Scale, int Width, int Height, GlassMaterial Material, GlassScene? OtherGeometry);

    internal sealed class Allocation(D3D11StudioRenderer renderer) : IDisposable
    {
        internal D3D11StudioRenderer Renderer { get; } = renderer;
        internal BootstrapCaptureScratch? Scratch { get; private set; }
        internal long AllocationBytes => Renderer.AllocationBytes + (Scratch?.AllocationBytes ?? 0);
        internal void EnsureScratch(GlassScene scene) => Scratch ??= new(scene.CaptureWidth, scene.CaptureHeight);
        internal void ClearForReturn()
        {
            Renderer.ClearCapturedContent(); Scratch?.Clear();
            // At large raster scales keep expensive warmed GPU resources when a
            // cleared DIB would make their combined idle allocation exceed budget.
            if (AllocationBytes > IdleBudget && Renderer.AllocationBytes <= IdleBudget)
            { Scratch?.Dispose(); Scratch = null; }
        }
        public void Dispose() { try { Scratch?.Dispose(); Scratch = null; } finally { Renderer.Dispose(); } }
    }

    internal sealed class Lease : IDisposable
    {
        private StudioGlassRuntime? owner;
        private readonly Key key;
        private readonly long generation;
        private readonly Allocation allocation;
        internal D3D11StudioRenderer Renderer => allocation.Renderer;
        internal BootstrapCaptureScratch Scratch => allocation.Scratch!;
        internal GlassScene Scene { get; }
        internal nint Device { get; }
        internal nint Context { get; }
        internal bool Prepared { get; }
        internal Lease(StudioGlassRuntime owner, Key key, long generation, Allocation allocation, GlassScene scene,
            nint device, nint context, bool prepared)
        {
            this.owner = owner; this.key = key; this.generation = generation; this.allocation = allocation;
            Scene = scene; Device = device; Context = context; Prepared = prepared;
        }
        internal void Discard() => Interlocked.Exchange(ref owner, null)?.Return(key, generation, allocation, discard: true);
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Return(key, generation, allocation, discard: false);
    }

    internal sealed class DeviceLease : IDisposable
    {
        private nint device, context;
        internal nint Device => device;
        internal nint Context => context;
        internal DeviceLease(nint device, nint context)
        {
            Marshal.AddRef(device); this.device = device;
            try { Marshal.AddRef(context); this.context = context; }
            catch { Release(ref this.device); throw; }
        }
        public void Dispose()
        {
            nint capturedContext = Interlocked.Exchange(ref context, 0), capturedDevice = Interlocked.Exchange(ref device, 0);
            if (capturedContext != 0) Marshal.Release(capturedContext);
            if (capturedDevice != 0) Marshal.Release(capturedDevice);
        }
    }
}
