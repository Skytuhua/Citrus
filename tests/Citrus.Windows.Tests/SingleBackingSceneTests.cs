using System.Windows.Threading;
using Citrus.Windows.Presentation;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class SingleBackingSceneTests(DesktopFixture desktop)
{
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(8)]
    public void Production_scene_has_only_continuous_backing_and_pagination(int count)
    {
        var scene = GlassScene.WheelPresentation(count, .5, false);
        Assert.Equal(new[] { 1, 32 }, scene.Regions.Select(region => region.Id));
        var demo = GlassScene.Wheel(count, .5);
        Assert.Equal(1, Assert.Single(demo.Regions).Id);
    }

    [Fact]
    public void Backing_cache_does_not_depend_on_action_count_or_pagination_visibility()
    {
        var cache = new GlassSceneCache();
        var first = cache.GetWheel(0, .5, false, true);
        foreach (int count in Enumerable.Range(0, 9))
        {
            Assert.Same(first, cache.GetWheel(count, .5, false, true));
            Assert.Same(first, cache.GetWheel(count, .5, true, true));
        }
    }

    [Fact]
    public Task Count_selection_and_pagination_changes_keep_the_live_backing_session() => desktop.RunAsync(async () =>
    {
        int sessions = 0, frames = 0;
        using var glass = new LiveWheelGlass(Dispatcher.CurrentDispatcher, _ => frames++, () => { },
            exclude: _ => new Lease(), createSession: (scene, _, _) => { sessions++; return new Session(scene); });
        glass.StartWheelPresentation(0, 0, 184, false, false, 1);
        try
        {
            await Until(() => frames > 0);
            int initialSessions = sessions;
            glass.SetPetalCount(7);
            glass.SetAccentRegion(3);
            glass.SetNavigationVisible(true);
            await Until(() => frames > 1);
            Assert.Equal(initialSessions, sessions);
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
    private sealed class Session(GlassScene scene) : ILiveGlassSession
    {
        public GlassScene Scene => scene;
        public bool TryRenderFrame(GlassFrameState state, byte[] destination) => true;
        public void Dispose() { }
    }
}
