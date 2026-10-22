using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Citrus.Windows.Presentation;
using Application = System.Windows.Application;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;

namespace Citrus.Windows.Tests;

/// <summary>Real compositor pixels, not RenderTargetBitmap. Configuration and raw measurements
/// are evidence for frame review; no cross-OS optical pass threshold enables production glass.</summary>
[Collection("Desktop")]
public sealed class GlassCompositorTests(DesktopFixture desktop)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Native_capture_records_live_backdrop_and_fine_pattern_measurements(bool ordinary) => desktop.RunAsync(async () =>
    {
        using var fixture = new Fixture(ordinary, "patterns");
        await fixture.Show();
        fixture.Apply(forceSolid: false);
        using var fine = await fixture.Capture("01-fine-configured");
        fixture.Scene.SetBroadPattern(false);
        using var broad = await fixture.Capture("02-broad-configured");
        fixture.Scene.SetBroadPattern(true);
        using var changed = await fixture.Capture("03-broad-inverted-configured");
        var result = fixture.Result;
        fixture.Apply(forceSolid: true);
        using var solidBroad = await fixture.Capture("04-broad-solid");
        fixture.Scene.SetPattern(false);
        using var solidFine = await fixture.Capture("05-fine-solid");
        var measurements = new
        {
            FineConfigured = Measure(fine, fine, fixture.InRegion),
            BroadConfiguredDifference = Measure(broad, changed, fixture.InRegion),
            FineSolid = Measure(solidFine, solidFine, fixture.InRegion),
            BroadSolid = Measure(solidBroad, solidBroad, fixture.InRegion),
            SamplingRegion = fixture.RegionDescription,
            FinePattern = "16 DIP period, 8 DIP black/white vertical stripes",
            BroadPattern = "256 DIP period, 128 DIP black/white vertical stripes, inverted between frames",
            Comparison = "Raw regional RGB neighbor differences and broad-pattern frame differences. Solid is opaque, not an unblurred optical reference. The separate wheel proof supplies its native-tint-only reference.",
            NativeConfiguration = result,
            OpticalAcceptance = "Pending human comparison of actual frames; configured state and measurements are not optical acceptance",
            UniversalOpticalThresholdApplied = false,
            ProductionCompanionValidated = false
        };
        Write(Path.Combine(fixture.Root, "measurements.json"), measurements);
        Assert.Equal(BackdropState.Solid, fixture.Surface.State);
        Assert.Equal(255, ((System.Windows.Media.SolidColorBrush)fixture.Surface.EffectiveTint).Color.A);
        Assert.Equal(fixture.Scene.ForegroundBefore, BackdropNativeMethods.GetForegroundWindow());
        // Five exhaustive ordinary-window ownership scans/captures take about 30 seconds on CI.
        // Keep every pixel check; only this larger optical workload gets a bounded 90-second budget.
    }, timeout: ordinary ? TimeSpan.FromSeconds(90) : null);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Fallback_capture_is_explicitly_solid(bool ordinary) => desktop.RunAsync(async () =>
    {
        using var fixture = new Fixture(ordinary, "fallback");
        await fixture.Show();
        fixture.Apply(forceSolid: true);
        fixture.Scene.SetBroadPattern(false);
        using var first = await fixture.Capture("01-solid-broad");
        fixture.Scene.SetBroadPattern(true);
        using var second = await fixture.Capture("02-solid-broad-inverted");
        Write(Path.Combine(fixture.Root, "measurements.json"), new
        {
            Result = fixture.Result, SamplingRegion = fixture.RegionDescription,
            SolidBackgroundDifference = Measure(first, second, fixture.InRegion),
            Scope = "Explicit local ReduceTransparency policy, not an OS transparency or high-contrast preference write"
        });
        Assert.Equal(BackdropState.Solid, fixture.Surface.State);
        Assert.Equal(255, ((System.Windows.Media.SolidColorBrush)fixture.Surface.EffectiveTint).Color.A);
        Assert.False(BackdropNativeMethods.IsWindowVisible(fixture.Companion));
        Assert.Equal(fixture.Scene.ForegroundBefore, BackdropNativeMethods.GetForegroundWindow());
    });

    [Fact]
    public Task Visible_reopen_theme_and_policy_frames_keep_hidden_intervals_explicit() => desktop.RunAsync(async () =>
    {
        using var theme = new ThemeManager(Application.Current);
        theme.Apply(ThemeChoice.Light, true);
        using var fixture = new Fixture(ordinary: false, "sequence");
        try
        {
            await fixture.Show();
            fixture.Apply(forceSolid: false);
            using var confirmed = await fixture.Capture("01-confirmed-light");
            fixture.Scene.Wheel.Dismiss();
            Assert.False(fixture.Scene.Wheel.IsVisible);
            Assert.False(fixture.Scene.Wheel.MaterialPresentationPending);
            Assert.False(BackdropNativeMethods.IsWindowVisible(fixture.Companion));
            Write(Path.Combine(fixture.Root, "02-hidden-no-screen-read.json"), new
            {
                fixture.Scene.Wheel.IsVisible, fixture.Scene.Wheel.MaterialPresentationPending,
                HelperVisible = BackdropNativeMethods.IsWindowVisible(fixture.Companion),
                ScreenReadPerformed = false,
                Scope = "Immediate native visibility/callback state only; hidden target cannot pass the unchanged capture guard"
            });
            theme.Apply(ThemeChoice.Dark, true);
            fixture.Reopen();
            await fixture.Scene.WaitForPresentationAsync();
            fixture.Apply(forceSolid: false, dark: true);
            using var reopened = await fixture.Capture("03-reopened-dark");
            fixture.Apply(forceSolid: true, dark: true);
            using var fallback = await fixture.Capture("04-reduce-transparency");
            fixture.Apply(forceSolid: false, dark: true, reducedMotion: true);
            using var reducedMotion = await fixture.Capture("05-reduced-motion-native-request");
            Assert.Equal(fixture.Scene.ForegroundBefore, BackdropNativeMethods.GetForegroundWindow());
        }
        finally { theme.Apply(ThemeChoice.Light, true); }
    });

    internal static void Write(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static object Measure(Bitmap first, Bitmap second, Func<int, int, bool> included)
    {
        Assert.Equal(first.Size, second.Size);
        double edges = 0, difference = 0;
        int pixels = 0, pairs = 0;
        for (int y = 0; y < first.Height; y++)
            for (int x = 0; x < first.Width; x++)
            {
                if (!included(x, y)) continue;
                var a = first.GetPixel(x, y); var b = second.GetPixel(x, y);
                difference += Distance(a, b); pixels++;
                if (x > 0 && included(x - 1, y)) { edges += Distance(a, first.GetPixel(x - 1, y)); pairs++; }
            }
        Assert.True(pixels > 0 && pairs > 0, "The physical sampling region must contain pixels and adjacent pairs.");
        return new { Pixels = pixels, AdjacentPairs = pairs, MeanRgbNeighborDifference = edges / pairs, MeanRgbFrameDifference = difference / pixels };
    }
    private static int Distance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    private sealed class Fixture : IDisposable
    {
        private readonly MainWindow? window;
        private readonly WindowBackdropController? ordinaryController;
        private readonly WheelBackdropController? wheelController;
        private readonly MaterialPreferenceMonitor monitor;
        private Rect region;
        private long generation = 1;
        public GlassOwnedScene Scene { get; }
        public MaterialSurface Surface { get; }
        public string Root { get; }
        public BackdropResult Result => ordinaryController?.Result ?? wheelController!.Result;
        public nint Companion => wheelController?.CompanionHandle ?? 0;
        public object RegionDescription => window is null
            ? new { Shape = "Annulus", Units = "DIP transformed using actual HWND DPI", CenterX = 184, CenterY = 184, InnerRadius = 169, OuterRadius = 173 }
            : (object)new { Shape = "Rectangle", Units = "Physical crop pixels", region.X, region.Y, region.Width, region.Height };

        public Fixture(bool ordinary, string name)
        {
            Root = Path.Combine(Environment.GetEnvironmentVariable("CITRUS_GLASS_ARTIFACTS") ?? Path.Combine(AppContext.BaseDirectory, "TestResults", "glass-native"), ordinary ? "ordinary" : "wheel", name);
            if (ordinary)
            {
                window = new MainWindow((App)Application.Current);
                Assert.Null(PressFeedback.GetAnimator(window)); // Resource-only fixture must not start the resident.
                ordinaryController = new WindowBackdropController();
                ordinaryController.Attach(window);
                ordinaryController.Invalidated += BindState;
            }
            else wheelController = new WheelBackdropController(nativeCompanionValidated: true);
            Scene = new GlassOwnedScene(window);
            Surface = (MaterialSurface)(window is null ? Scene.Wheel.FindName("Shell") : window.FindName("Chrome"));
            if (window is null)
            {
                wheelController!.Invalidated += BindState;
            }
            monitor = new MaterialPreferenceMonitor(Dispatcher.CurrentDispatcher, () => new());
        }
        public async Task Show()
        {
            Scene.Show(activateOwnedBackground: true);
            Write(Path.Combine(Root, "fixture.json"), new
            {
                SourceSha = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "UnavailableOutsideCIRecordExactCommitAlongsideArtifact",
                Scene.CaptureTargetKind, Target = Scene.CaptureTargetHandle.ToInt64(),
                GuardTargetSlot = "Snapshot.Wheel contains this actual owned capture target HWND in both modes",
                Scene.OriginalForeground, Scene.BackgroundActivationAttempted, Scene.BackgroundActivationReturned,
                Scene.BackgroundActivationSucceeded, Scene.BaselineForeground,
                Setup = "One normal owned-background activation; production surface ShowActivated=false; no foreign interaction",
                OperatingSystem = Environment.OSVersion.ToString(), RuntimeInformation.OSArchitecture, RuntimeInformation.ProcessArchitecture,
                ActualDpiScale = Scene.DpiScale, RasterScaling = false, Scene.Bounds,
                Policy = monitor.Decision, AdvancedEffectsEnvironment = monitor.Read(),
                Capture = "Guarded GDI SRCCOPY|CAPTUREBLT desktop compositor crop; no WPF raster substitution"
            });
            if (window is null)
            {
                await Scene.WaitForPresentationAsync();
                wheelController!.Attach(Scene.WheelHandle);
                await Drain();
            }
            else
            {
                // Empty top chrome strip, below native nonclient frame and away from rounded corners/content.
                var start = Surface.PointToScreen(new Point(40, 8));
                var end = Surface.PointToScreen(new Point(Surface.ActualWidth - 40, 16));
                region = new Rect(start.X - Scene.Bounds.Left, start.Y - Scene.Bounds.Top, end.X - start.X, end.Y - start.Y);
                // MaterialSurface chrome uses per-pixel alpha; its native backdrop deliberately falls back to solid.
                Assert.True(window.AllowsTransparency);
                Assert.NotEqual(0, BackdropNativeMethods.GetWindowLongPtr(Scene.CaptureTargetHandle, -20).ToInt64() & BackdropNativeMethods.WsExLayered);
            }
        }
        public bool InRegion(int x, int y)
        {
            if (window is not null) return region.Contains(new Point(x, y));
            double radius = Math.Sqrt(Math.Pow(x / Scene.DpiScale - 184, 2) + Math.Pow(y / Scene.DpiScale - 184, 2));
            return radius >= 169 && radius <= 173;
        }
        public void Reopen() { generation++; Scene.ConfirmWheel(); }
        public void Apply(bool forceSolid, bool dark = false, bool reducedMotion = false)
        {
            var decision = forceSolid ? monitor.Decision with { RequestNativeAcrylic = false, Reason = "UserReduceTransparency" } : monitor.Decision;
            if (reducedMotion)
            {
                decision = decision with { Animate = false };
                Scene.Wheel.ApplyMaterialPolicy(decision, dark, monitor.TextScaleFactor);
            }
            if (window is null) wheelController!.Synchronize(generation, Scene.Bounds, Scene.DpiScale, true, decision, dark);
            else ordinaryController!.Apply(decision, dark);
            BindState();
        }
        private void BindState()
        {
            Surface.State = Result.State;
            if (window is not null)
            {
                if (Result.IsNative) window.Background = Brushes.Transparent;
                else window.SetResourceReference(System.Windows.Window.BackgroundProperty, "WindowBackground");
            }
        }
        private async Task Drain()
        {
            for (int i = 0; i < 100 && Scene.Wheel.MaterialPresentationPending; i++) await Task.Delay(10);
            Assert.False(Scene.Wheel.MaterialPresentationPending);
        }
        public async Task<Bitmap> Capture(string name)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(150);
            if (window is null) await Drain();
            BindState();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(Result.State, Surface.State);
            Write(Path.Combine(Root, name + ".state.json"), new
            {
                Result, SurfaceState = Surface.State.ToString(), Scene.CaptureTargetKind,
                TargetIdentity = GlassNativeDesktopProbe.Describe(Scene.CaptureTargetHandle),
                HelperIdentity = Companion == 0 ? null : GlassNativeDesktopProbe.Describe(Companion),
                CompanionOwner = Companion == 0 ? 0L : BackdropNativeMethods.GetWindow(Companion, 4).ToInt64(),
                ImmediatelyAboveHelper = Companion == 0 ? 0L : BackdropNativeMethods.GetWindow(Companion, 3).ToInt64(),
                NativeConfiguredIsNotOpticalAcceptance = true, ProductionCompanionValidated = false
            });
            return GlassCompositorCapture.Capture(Scene, Companion, Path.Combine(Root, name + ".png"));
        }
        public void Dispose()
        {
            ordinaryController?.Dispose(); wheelController?.Dispose(); monitor.Dispose();
            window?.Dispose(); Scene.Dispose();
        }
    }
}
