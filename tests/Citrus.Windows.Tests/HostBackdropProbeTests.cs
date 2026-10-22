using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Citrus.Windows.Presentation;
using Bitmap = System.Drawing.Bitmap;
using Color = System.Drawing.Color;

namespace Citrus.Windows.Tests;

/// <summary>Real owned compositor pixels. A completed experiment is never an optical pass or product gate.</summary>
[Collection("Desktop")]
public sealed class HostBackdropProbeTests(DesktopFixture desktop)
{
    [Fact]
    public Task Owned_raw_host_backdrop_probe_records_both_hosting_variants_without_enabling_production() => desktop.RunAsync(async () =>
    {
        Assert.Equal(Architecture.X64, RuntimeInformation.ProcessArchitecture);
        string root = Path.Combine(Environment.GetEnvironmentVariable("CITRUS_GLASS_ARTIFACTS") ?? Path.Combine(AppContext.BaseDirectory, "TestResults", "glass-native"), "host-backdrop-probe");
        using var scene = new GlassOwnedScene();
        scene.Show(activateOwnedBackground: true);
        await scene.WaitForPresentationAsync();
        await DrainProductPresentation(scene);
        using var monitor = new MaterialPreferenceMonitor(Dispatcher.CurrentDispatcher, () => new());
        GlassCompositorTests.Write(Path.Combine(root, "fixture.json"), new
        {
            SourceSha = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "UnavailableOutsideCIRecordExactCommitAlongsideArtifact",
            Experiment = "Test-only raw HostBackdropBrush, no effect factory, tint-only real Shell measurement state; not ConfiguredNativeAcrylic proof",
            OperatingSystem = Environment.OSVersion.ToString(), Build = Environment.OSVersion.Version.Build,
            RuntimeInformation.OSArchitecture, RuntimeInformation.ProcessArchitecture, ActualDpiScale = scene.DpiScale,
            scene.Bounds, scene.OriginalForeground, scene.BaselineForeground, scene.BackgroundActivationAttempted,
            scene.BackgroundActivationReturned, scene.BackgroundActivationSucceeded,
            Background = scene.BackgroundHandle.ToInt64(), Wheel = scene.WheelHandle.ToInt64(),
            Policy = monitor.Decision, Environment = monitor.Read(),
            Activation = "One normal activation attempt on owned opaque background, shared by both variants. No force/retry or helper/wheel activation.",
            Capture = "Unchanged GlassCompositorCapture and every desktop/session/full-crop ownership guard",
            NoOsPreferenceWrites = true, ProductionCompanionValidated = false, UniversalOpticalThresholdApplied = false
        });
        var outcomes = new List<object>();
        var queueLease = new HostBackdropProbeQueue();
        string outcome = "IncompleteExperimentNoOpticalAcceptance";
        object? failure = null;
        try
        {
            Assert.Equal(BackdropState.Solid, scene.Wheel.MaterialState);
            Assert.NotEqual(0, BackdropNativeMethods.GetWindowLongPtr(scene.WheelHandle, -20).ToInt64() & BackdropNativeMethods.WsExLayered);
            var presentation = (Canvas)scene.Wheel.FindName("Presentation");
            Assert.Empty(presentation.Children.OfType<System.Windows.Shapes.Ellipse>());
            Assert.Null(((MaterialSurface)scene.Wheel.FindName("Shell")).Clip);
            AssertReadableBacking((System.Windows.Media.Brush)scene.Wheel.FindResource("CapsuleBackground"));
            if (!monitor.Decision.RequestNativeAcrylic)
            {
                // Keep the stricter existing app policy, including build >=22621, despite host API floor22000.
                outcome = "PolicyRefusedNoOpticalEvidence";
                Assert.Equal(scene.ForegroundBefore, BackdropNativeMethods.GetForegroundWindow());
                return;
            }
            foreach (bool noRedirectionBitmap in new[] { false, true })
                outcomes.Add(await RunVariant(scene, Path.Combine(root, noRedirectionBitmap ? "no-redirection-bitmap" : "standard-redirection"), noRedirectionBitmap, queueLease));
            outcome = "RecordedExperimentResultsRequireReviewNotOpticalAcceptance";
        }
        catch (Exception error)
        {
            failure = new { Type = error.GetType().FullName, error.Message, error.HResult };
            throw;
        }
        finally
        {
            try
            {
                try { scene.SetShellState(BackdropState.Solid); }
                finally { await queueLease.DisposeAsync(); }
                if (queueLease.OwnsQueue) Assert.True(queueLease.ShutdownCompleted);
            }
            finally
            {
                GlassCompositorTests.Write(Path.Combine(root, "summary.json"), new
                {
                    Outcome = outcome, Failure = failure, Variants = outcomes,
                    PolicyRefusal = outcome == "PolicyRefusedNoOpticalEvidence" ? monitor.Decision.Reason : null,
                    CaptureSkipped = outcome == "PolicyRefusedNoOpticalEvidence" ? true : (bool?)null,
                    ScreenReadPerformed = outcome == "PolicyRefusedNoOpticalEvidence" ? false : (bool?)null,
                    Queue = new { queueLease.OwnsQueue, queueLease.CreateHResult, queueLease.ShutdownCompleted, SharedAcrossVariants = true },
                    ForegroundBefore = scene.ForegroundBefore.ToInt64(), ForegroundAfter = BackdropNativeMethods.GetForegroundWindow().ToInt64(),
                    FinalShellState = scene.Wheel.MaterialState.ToString(), ProductionCompanionValidated = false,
                    UniversalOpticalThresholdApplied = false,
                    Limitation = "API success, presentation control and live response alone do not prove blur, Liquid Glass refraction or production eligibility. Review frames and each independent metric."
                });
            }
        }
        if (queueLease.OwnsQueue) Assert.True(queueLease.ShutdownCompleted);
        Assert.Equal(BackdropState.Solid, scene.Wheel.MaterialState);
        Assert.Equal(scene.ForegroundBefore, BackdropNativeMethods.GetForegroundWindow());
    }, timeout: TimeSpan.FromSeconds(120)); // Two owned optical variants:12 guarded frames plus6 exhaustive ellipse input scans.

    private static void AssertReadableBacking(System.Windows.Media.Brush brush)
    {
        Assert.NotNull(brush);
        Assert.Equal(1, brush.Opacity);
        if (brush is GradientBrush gradient)
        {
            Assert.NotEmpty(gradient.GradientStops);
            Assert.All(gradient.GradientStops, stop => Assert.InRange(stop.Color.A, 240, 255));
        }
        else Assert.InRange(Assert.IsType<SolidColorBrush>(brush).Color.A, 240, 255);
    }

    private static async Task<object> RunVariant(GlassOwnedScene scene, string root, bool noRedirectionBitmap, HostBackdropProbeQueue queueLease)
    {
        var probe = new HostBackdropProbeWindow(noRedirectionBitmap, queueLease);
        var result = new Dictionary<string, object?>
        {
            ["Variant"] = noRedirectionBitmap ? "HelperOnlyWS_EX_NOREDIRECTIONBITMAP" : "OriginalHelperRedirectionStyles",
            ["Outcome"] = "StartedNoOpticalEvidence", ["ProductionCompanionValidated"] = false,
            ["UniversalOpticalThresholdApplied"] = false,
            ["Brush"] = "Raw Windows.UI.Composition.CompositionBackdropBrush from CreateHostBackdropBrush",
            ["Shell"] = "Actual Shell set to ConfiguredNativeAcrylic enum solely to expose its existing tint; this is a test measurement state, never proof of Acrylic",
            ["Pattern"] = "A:16-DIP period8-DIP black/white stripes; B:16-DIP period8-DIP dark-red/orange stripes. Matching no-helper controls use identical real Shell tint.",
            ["OpticalClaim"] = "None; measurements and PNGs require review; flat/unsupported results are retained."
        };
        nint helper = 0, owner = 0;
        try
        {
            scene.SetPattern(false);
            scene.SetShellState(BackdropState.ConfiguredNativeAcrylic);
            using var baselineA = await Capture(scene, 0, root, "01-baseline-A-no-helper");
            try { probe.Configure(scene.Bounds, scene.DpiScale); }
            catch (Exception error) when (error is COMException or Win32Exception or NotSupportedException or TypeLoadException or EntryPointNotFoundException or DllNotFoundException)
            {
                result["Outcome"] = "ConfigurationUnavailableNoHostOpticalEvidence";
                result["ConfigurationFailure"] = new { probe.Stage, Type = error.GetType().FullName, error.Message, error.HResult };
                probe.Hide();
                using var unavailableFallback = await Capture(scene, probe.Handle, root, "06-real-Solid-fallback-configuration-unavailable", BackdropState.Solid);
                return result;
            }
            helper = probe.Handle; owner = probe.Owner;
            Assert.True(probe.Configured);
            Assert.True(probe.UsesHostBrush);
            probe.ShowBehind(scene.WheelHandle, scene.Bounds);
            result["NativeWindowConfiguration"] = AssertConfiguration(scene, probe, root);
            using var configuredA = await Capture(scene, helper, root, "02-configured-raw-host-A");
            scene.AssertWheelOwnsClip(helper);
            scene.SetPattern(true);
            using var configuredB = await Capture(scene, helper, root, "03-configured-raw-host-owned-B");
            scene.AssertWheelOwnsClip(helper);
            probe.Hide();
            using var baselineB = await Capture(scene, helper, root, "04-baseline-B-no-helper");
            probe.SetOpaquePresentationControl(true);
            Assert.False(probe.UsesHostBrush);
            probe.ShowBehind(scene.WheelHandle, scene.Bounds);
            using var opaque = await Capture(scene, helper, root, "05-opaque-magenta-visual-control-B-NOT-BLUR");
            scene.AssertWheelOwnsClip(helper);
            probe.Hide();
            probe.SetOpaquePresentationControl(false);
            Assert.True(probe.UsesHostBrush);
            scene.SetShellState(BackdropState.Solid);
            Assert.False(BackdropNativeMethods.IsWindowVisible(helper));
            using var fallback = await Capture(scene, helper, root, "06-real-Solid-fallback-B", BackdropState.Solid);
            Assert.Equal(BackdropState.Solid, scene.Wheel.MaterialState);
            Assert.Equal(255, Assert.IsType<SolidColorBrush>(((MaterialSurface)scene.Wheel.FindName("Shell")).EffectiveTint).Color.A);
            double scale = scene.DpiScale;
            result["Outcome"] = "ConfiguredAndMeasuredNotOpticallyAccepted";
            result["LiveResponse"] = new
            {
                Region = "Exposed actual shell annulus169..173DIP about184,184",
                HostMeanRgbChange = GlassCompositorCapture.LiveDifference(configuredA, configuredB, scale),
                MatchingNoHelperMeanRgbChange = GlassCompositorCapture.LiveDifference(baselineA, baselineB, scale),
                Interpretation = "Live change is necessary evidence of sampling but does not prove edge attenuation."
            };
            result["FineEdgeAttenuation"] = new
            {
                BaselineA = GlassCompositorCapture.EdgeEnergy(baselineA, scale), HostA = GlassCompositorCapture.EdgeEnergy(configuredA, scale),
                BaselineB = GlassCompositorCapture.EdgeEnergy(baselineB, scale), HostB = GlassCompositorCapture.EdgeEnergy(configuredB, scale),
                Interpretation = "Compare each host frame with its matching no-helper background. Lower edge energy also occurs for flat opaque output; interpret together with live response."
            };
            result["OutsideEllipseLeakage"] = new
            {
                Region = "Crop pixels outside circle radius178DIP;2DIP guard beyond actual ellipse; same real wheel content in each matching pair",
                HostA = Measure(baselineA, configuredA, (x, y) => Radius(x, y, scale) >= 178),
                HostB = Measure(baselineB, configuredB, (x, y) => Radius(x, y, scale) >= 178),
                OpaqueControlB = Measure(baselineB, opaque, (x, y) => Radius(x, y, scale) >= 178)
            };
            result["OpaquePresentationControlNotBlurEvidence"] = new
            {
                Color = "OpaqueARGBFFFF00FF", Brush = "CompositionColorBrush, same visual/target/native region/visual clip",
                ExposedShellDifference = Measure(baselineB, opaque, (x, y) => Radius(x, y, scale) is >= 169 and <= 173),
                Purpose = "Separate target presentation/ellipse clipping from host sampling. A flat host result plus visible magenta can establish host sampling failure."
            };
            result["ProtectedOpaqueContent"] = new
            {
                HubPatternDifference = Measure(configuredA, configuredB, (x, y) => Radius(x, y, scale) <= 60),
                CaptionPatternDifference = Measure(configuredA, configuredB, (x, y) => x / scale is >= 70 and <= 298 && y / scale is >= 370 and <= 402),
                Presentation = "Actual hub, petals, labels and caption backing unchanged; real layered OLE HWND retained",
                Input = "Unchanged exhaustive owned scene full-crop guard on every capture and AssertWheelOwnsClip on hostA,hostB,opaque control. Synthetic OLE admission only; external Explorer drag is not proven."
            };
            return result;
        }
        catch (Exception error)
        {
            result["Outcome"] = "FailedOrOwnershipRefusedNoOpticalAcceptance";
            result["Failure"] = new { Type = error.GetType().FullName, error.Message, error.HResult };
            throw;
        }
        finally
        {
            result["Configuration"] = new
            {
                probe.Stage, probe.HostAttributeHResult, probe.SystemBackdropHResult, probe.InteropQueryHResult,
                probe.TargetCreateHResult, probe.QueueCreateHResult, probe.OwnsQueue, probe.QueueAcquired, probe.Configured, probe.UsesHostBrush,
                QueueOwnership = !probe.QueueAcquired ? "NotAcquired" : probe.OwnsQueue ? "OwnedCURRENTthreadCOM_STA" : "BorrowedExistingNeverShutdown",
                Helper = probe.Handle.ToInt64(), Owner = probe.Owner.ToInt64(), probe.Width, probe.Height,
                probe.ClipLeft, probe.ClipRight, ClipUnits = "Physical pixels, actual HWND DPI applied once, centered ellipse radius(right-left)/2",
                TargetTopmost = true, SystemBackdropType = "NONE(1)", HostBackdropAttribute17 = "TRUE(1)",
                Visual = "SpriteVisual with CompositionGeometricClip(CompositionEllipseGeometry), separate actual SetWindowRgn retained"
            };
            try
            {
                try
                {
                    probe.Hide(); // Always before any async cleanup or fallback.
                    scene.SetShellState(BackdropState.Solid);
                }
                finally { await probe.DisposeAsync(); }
                Assert.True(probe.TargetDetached);
                Assert.True(probe.CompositionClosed);
                Assert.True(probe.WindowsDestroyed);
                Assert.False(BackdropNativeMethods.IsWindow(helper)); Assert.False(BackdropNativeMethods.IsWindow(owner));
                Assert.Equal(scene.ForegroundBefore, BackdropNativeMethods.GetForegroundWindow());
            }
            catch (Exception cleanupError)
            {
                result["Outcome"] = "CleanupFailureNoOpticalAcceptance";
                result["CleanupFailure"] = cleanupError.ToString();
                throw;
            }
            finally
            {
                result["Cleanup"] = new
                {
                    probe.TargetDetached, probe.CompositionClosed, probe.WindowsDestroyed, probe.OwnsQueue, QueueShutdown = "Shared experiment queue shuts down after both helper/target owners are disposed",
                    ForegroundUnchanged = scene.ForegroundBefore == BackdropNativeMethods.GetForegroundWindow(),
                    ShellState = scene.Wheel.MaterialState.ToString()
                };
                GlassCompositorTests.Write(Path.Combine(root, "probe-result.json"), result);
            }
        }
    }

    private static object AssertConfiguration(GlassOwnedScene scene, HostBackdropProbeWindow probe, string root)
    {
        long style = BackdropNativeMethods.GetWindowLongPtr(probe.Handle, -16).ToInt64();
        long extended = BackdropNativeMethods.GetWindowLongPtr(probe.Handle, -20).ToInt64();
        Assert.NotEqual(0, style & BackdropNativeMethods.WsDisabled);
        Assert.Equal(0, extended & BackdropNativeMethods.WsExLayered);
        foreach (int required in new[] { BackdropNativeMethods.WsExNoActivate, BackdropNativeMethods.WsExToolWindow, BackdropNativeMethods.WsExTransparent })
            Assert.NotEqual(0, extended & required);
        Assert.Equal(probe.NoRedirectionBitmap, (extended & HostBackdropProbeWindow.WsExNoRedirectionBitmap) != 0);
        Assert.Equal(probe.Owner, BackdropNativeMethods.GetWindow(probe.Handle, 4));
        Assert.False(BackdropNativeMethods.IsWindowVisible(probe.Owner));
        Assert.Equal(scene.WheelHandle, BackdropNativeMethods.GetWindow(probe.Handle, 3));
        Assert.Equal(scene.ForegroundBefore, BackdropNativeMethods.GetForegroundWindow());
        // Requested geometric endpoints and the rasterized native bounding box are different
        // contracts. Compare the entire actual region against an independent native ellipse,
        // never an assumed endpoint-inclusive bounding box or a +/-1 tolerance.
        int requestedLeft = checked((int)Math.Ceiling(8 * scene.DpiScale));
        int requestedRight = checked((int)Math.Floor(360 * scene.DpiScale));
        nint region = BackdropNativeMethods.CreateRectRgn(0, 0, 0, 0), reference = 0;
        try
        {
            Assert.NotEqual(0, region);
            reference = BackdropNativeMethods.CreateEllipticRgn(requestedLeft, requestedLeft, requestedRight, requestedRight);
            Assert.NotEqual(0, reference);
            int regionKind = BackdropNativeMethods.GetWindowRgn(probe.Handle, region);
            int boundsKind = BackdropNativeMethods.GetRgnBox(region, out var bounds);
            int referenceKind = BackdropNativeMethods.GetRgnBox(reference, out var referenceBounds);
            bool matchesReference = EqualRgn(region, reference);
            bool centerIncluded = BackdropNativeMethods.PtInRegion(region, (int)(184 * scene.DpiScale), (int)(184 * scene.DpiScale));
            bool cornerIncluded = BackdropNativeMethods.PtInRegion(region, 0, 0);
            var visual = probe.ReadVisualGeometry();
            var metadata = new
            {
                Style = style, ExtendedStyle = extended, RegionKind = regionKind, NativeBoundsKind = boundsKind,
                RegionBounds = bounds.ToScreenRect(), ReferenceRegionKind = referenceKind, ReferenceRegionBounds = referenceBounds.ToScreenRect(),
                RegionMatchesIndependentNativeEllipse = matchesReference,
                RequestedPhysicalEllipse = new { Left = requestedLeft, Top = requestedLeft, Right = requestedRight, Bottom = requestedRight },
                ActualVisualClip = new { Width = visual.Size.X, Height = visual.Size.Y, CenterX = visual.Center.X, CenterY = visual.Center.Y, RadiusX = visual.Radius.X, RadiusY = visual.Radius.Y },
                RegionCenterIncluded = centerIncluded, RegionCornerIncluded = cornerIncluded, Helper = probe.Handle.ToInt64(), Owner = probe.Owner.ToInt64(),
                ImmediatelyAboveHelper = BackdropNativeMethods.GetWindow(probe.Handle, 3).ToInt64(),
                OwnerHidden = !BackdropNativeMethods.IsWindowVisible(probe.Owner), InputExclusion = "WS_DISABLED|WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW|WS_EX_TRANSPARENT; WM_MOUSEACTIVATE MA_NOACTIVATE; WM_NCHITTEST HTTRANSPARENT"
            };
            GlassCompositorTests.Write(Path.Combine(root, "native-window-configuration.json"), metadata);
            Assert.Equal(3, referenceKind); // The independently rasterized ellipse is complex.
            Assert.Equal(referenceKind, regionKind);
            Assert.Equal(referenceKind, boundsKind);
            Assert.True(matchesReference, "The HWND must retain exactly the independently rasterized inward ellipse.");
            Assert.Equal(referenceBounds.ToScreenRect(), bounds.ToScreenRect());
            Assert.Equal(requestedLeft, probe.ClipLeft); Assert.Equal(requestedRight, probe.ClipRight);
            float radius = (requestedRight - requestedLeft) / 2f;
            Assert.Equal((float)probe.Width, visual.Size.X); Assert.Equal((float)probe.Height, visual.Size.Y);
            Assert.Equal(requestedLeft + radius, visual.Center.X); Assert.Equal(requestedLeft + radius, visual.Center.Y);
            Assert.Equal(radius, visual.Radius.X); Assert.Equal(radius, visual.Radius.Y);
            Assert.True(centerIncluded); Assert.False(cornerIncluded);
            return metadata;
        }
        finally
        {
            if (reference != 0) BackdropNativeMethods.DeleteObject(reference);
            if (region != 0) BackdropNativeMethods.DeleteObject(region);
        }
    }

    [DllImport("gdi32.dll")] private static extern bool EqualRgn(nint first, nint second);

    private static async Task<Bitmap> Capture(GlassOwnedScene scene, nint helper, string root, string name, BackdropState shellState = BackdropState.ConfiguredNativeAcrylic)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(150); // Bounded compositor settle; no animation/input loop or global input.
        await DrainProductPresentation(scene);
        scene.SetShellState(shellState);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(shellState, scene.Wheel.MaterialState);
        GlassCompositorTests.Write(Path.Combine(root, name + ".state.json"), new
        {
            ShellState = scene.Wheel.MaterialState.ToString(), Foreground = BackdropNativeMethods.GetForegroundWindow().ToInt64(),
            Wheel = GlassNativeDesktopProbe.Describe(scene.WheelHandle),
            Helper = helper == 0 ? null : GlassNativeDesktopProbe.Describe(helper),
            HelperVisible = helper != 0 && BackdropNativeMethods.IsWindowVisible(helper),
            VisibleProbeBrush = helper == 0 || !BackdropNativeMethods.IsWindowVisible(helper) ? "NoVisibleHelper"
                : name.Contains("opaque-magenta", StringComparison.Ordinal) ? "OpaqueMagentaCompositionColorBrushPresentationControlNotBlur" : "RawHostBackdropBrushNotOpticallyValidated",
            Owner = helper == 0 ? 0 : BackdropNativeMethods.GetWindow(helper, 4).ToInt64(),
            ImmediatelyAboveHelper = helper == 0 ? 0 : BackdropNativeMethods.GetWindow(helper, 3).ToInt64(),
            ScreenReadStillRequiresUnchangedOwnershipPreflight = true, ProductionCompanionValidated = false
        });
        return GlassCompositorCapture.Capture(scene, helper, Path.Combine(root, name + ".png"));
    }

    private static async Task DrainProductPresentation(GlassOwnedScene scene)
    {
        for (int i = 0; i < 100 && scene.Wheel.MaterialPresentationPending; i++) await Task.Delay(10);
        Assert.False(scene.Wheel.MaterialPresentationPending);
    }
    private static double Radius(int x, int y, double scale) => Math.Sqrt(Math.Pow(x / scale - 184, 2) + Math.Pow(y / scale - 184, 2));
    private static object Measure(Bitmap first, Bitmap second, Func<int, int, bool> included)
    {
        Assert.Equal(first.Size, second.Size);
        long total = 0; int count = 0, maximum = 0;
        for (int y = 0; y < first.Height; y++)
            for (int x = 0; x < first.Width; x++)
            {
                if (!included(x, y)) continue;
                Color a = first.GetPixel(x, y), b = second.GetPixel(x, y);
                int distance = Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                total += distance; maximum = Math.Max(maximum, distance); count++;
            }
        Assert.True(count > 0, "Physical sampling region must contain pixels.");
        return new { Pixels = count, MeanRgbFrameDifference = (double)total / count, MaximumRgbFrameDifference = maximum };
    }
}
