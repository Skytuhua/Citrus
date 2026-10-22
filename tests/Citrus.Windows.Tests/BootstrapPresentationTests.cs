using System.Reflection;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Citrus.Windows.Presentation;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class BootstrapPresentationTests(DesktopFixture desktop)
{
    [Fact]
    public Task First_blurred_backing_is_published_before_live_capture_initialization() => desktop.RunAsync(async () =>
    {
        int frames = 0, created = 0;
        bool ready = false;
        byte[]? bootstrap = null;
        WriteableBitmap? backing = null;
        var stages = new List<string>();
        using var glass = Create(body =>
        {
            backing ??= body;
            Assert.Same(backing, body);
            var pixels = new byte[body.PixelWidth * body.PixelHeight * 4];
            body.CopyPixels(pixels, body.PixelWidth * 4, 0);
            byte expected = frames++ == 0 ? (byte)135 : (byte)255;
            Assert.All(pixels, channel => Assert.Equal(expected, channel));
        }, (scene, _, _) =>
        {
            Assert.Equal(1, frames);
            created++;
            return new Session(scene, () => ready);
        }, (scene, _, _, _) => bootstrap = Enumerable.Repeat((byte)135, scene.PixelWidth * scene.PixelHeight * 4).ToArray());
        glass.StageChanged = stage => stages.Add(stage.ToString());
        glass.StartWheelPresentation(0, 0, 184, false, false, 1);
        try
        {
            await Until(() => created == 1);
            Assert.Equal(1, frames);
            Assert.Contains("Bootstrap", stages);
            Assert.DoesNotContain("Live", stages);
            Assert.NotNull(bootstrap);
            Assert.All(bootstrap, channel => Assert.Equal(0, channel));
            ready = true;
            await Until(() => frames > 1);
            Assert.Contains("Live", stages);
        }
        finally { glass.Stop(); await glass.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
    });

    [Fact]
    public Task Cancelled_bootstrap_cannot_reveal_or_initialize_a_replacement_capture() => desktop.RunAsync(async () =>
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int frames = 0, created = 0;
        byte[]? pixels = null;
        using var glass = Create(_ => frames++, (scene, _, _) => { created++; return new Session(scene, () => false); },
            (scene, _, _, _) =>
            {
                entered.Set(); release.Wait(TimeSpan.FromSeconds(5));
                return pixels = Enumerable.Repeat((byte)135, scene.PixelWidth * scene.PixelHeight * 4).ToArray();
            });
        glass.StartWheelPresentation(0, 0, 184, false, false, 1);
        try
        {
            await Until(() => entered.IsSet);
            glass.Stop();
            release.Set();
            await glass.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, frames); Assert.Equal(0, created);
            Assert.NotNull(pixels); Assert.All(pixels, channel => Assert.Equal(0, channel));
        }
        finally { release.Set(); glass.Stop(); await glass.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
    });

    [Fact]
    public Task Generic_content_surfaces_do_not_take_an_admission_snapshot() => desktop.RunAsync(async () =>
    {
        int snapshots = 0, frames = 0;
        using var glass = Create(_ => frames++, (scene, _, _) => new Session(scene, () => true),
            (_, _, _, _) => { snapshots++; throw new InvalidOperationException("Only wheel admission may capture a bootstrap."); });
        glass.StartSurface(() => new GlassScene(32, 32, 1, [GlassRegion.Circle(1, 16, 16, 14)]), 0, 0, false, false, 1);
        try { await Until(() => frames > 0); Assert.Equal(0, snapshots); }
        finally { glass.Stop(); await glass.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
    });

    private static LiveWheelGlass Create(Action<WriteableBitmap> present,
        Func<GlassScene, int, int, ILiveGlassSession?> session,
        Func<GlassScene, int, int, CancellationToken, byte[]?> bootstrap)
    {
        var constructor = typeof(LiveWheelGlass).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.GetParameters().Any(parameter => parameter.Name == "bootstrap"));
        Assert.NotNull(constructor);
        return (LiveWheelGlass)constructor.Invoke([Dispatcher.CurrentDispatcher, present, (Action)(() => { }), null,
            (Func<nint[], IDisposable?>)(_ => new Lease()), session, bootstrap]);
    }
    private static async Task Until(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < 5000) await Task.Delay(10);
        Assert.True(condition());
    }
    private sealed class Lease : IDisposable { public void Dispose() { } }
    private sealed class Session(GlassScene scene, Func<bool> ready) : ILiveGlassSession
    {
        public GlassScene Scene => scene;
        public bool TryRenderFrame(GlassFrameState state, byte[] destination)
        { if (!ready()) return false; Array.Fill(destination, (byte)255); return true; }
        public void Dispose() { }
    }
}
