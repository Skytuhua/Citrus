using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Citrus.Core;
using Citrus.Core.Presentation;
using Citrus.Windows.Presentation;

namespace Citrus.Windows;

public sealed record OperationDrop(OperationDescriptor Operation, IReadOnlyList<string> Paths);

public partial class WheelWindow : Window
{
    private readonly OperationCatalog catalog;
    private static readonly SemaphoreSlim catalogWorkers = new(2);
    private readonly FeedbackAudio audio;
    private readonly bool ownsAudio;
    private CancellationTokenSource? presentationWork;
    private readonly List<WheelPetal> petals = [];
    private bool preparing, reduceMotion, acceptsEntry, presentationReleased;
    // The HWND accent effect is rectangular even with an annular region on this system.
    // A successful API call does not qualify clipping; keep the correctly shaped solid shell.
    private readonly WheelBackdropController backdrop = new();
    private readonly LiveWheelGlass glass;
    private MaterialDecision materialDecision = new(false, false, "NoResidentServices");
    private bool dark;
    private double textScale = 1;
    private bool pendingMaterial;
    private DispatcherOperation? pendingPlacement;
    internal BackdropState MaterialState => Shell.State;
    internal nint CompanionHandle => backdrop.CompanionHandle;
    internal nint DragCaptureHandle => outsideCapture?.Handle ?? 0;
    internal Task GlassCompletion => glass.Completion;
    internal Task GlassRetirementCompletion => glass.RetirementCompletion;
    internal Task DragRetirementCompletion => dragRetirement;
    internal bool MaterialPresentationPending => pendingMaterial;
    internal bool IsInteractionActive => cancelDraining || preparing || session.IsConfirmed || acceptsEntry || outsideCapture is not null || pendingPlacement is not null;
    private long presentationEpoch;
    private long inputEpoch;
    private bool Motion => !reduceMotion && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
    private readonly Dictionary<OperationMode, IReadOnlyList<OperationDescriptor>> availableOperations = [];
    private readonly WheelSession session = new();
    private readonly DispatcherTimer timer;
    private WheelPaging paging = new();
    private IReadOnlyList<OperationDescriptor>[] pages = [];
    private IReadOnlyList<OperationDescriptor> pageOperations = [];
    private IReadOnlyList<OperationDescriptor> operations = [];
    private IReadOnlyList<string> paths => session.Paths;
    private OperationMode mode => session.Mode;
    private long generation;
    private int selected = -1, navigation;
    private bool armed;
    private string payloadSize = "Choose";
    private nint handle;
    private WheelDragCapture? outsideCapture;
    private WheelDragCapture? preparedCapture;
    private DispatcherOperation? pendingCapturePreparation;
    private bool capturePreparationEnabled;
    private readonly WheelDragCapturePlatform dragCapturePlatform;
    private Task dragRetirement = Task.CompletedTask;
    private DispatcherTimer? releaseCleanup;
    public event Action<OperationDrop>? OperationDropped;

    public WheelWindow(OperationCatalog catalog, FeedbackAudio? audio = null, IFilePreviewService? previewService = null)
        : this(catalog, audio, previewService, null) { }

    internal WheelWindow(OperationCatalog catalog, FeedbackAudio? audio, IFilePreviewService? previewService,
        Func<int, int, int, IDesktopGlassSource?>? glassSource,
        Func<GlassScene, int, int, ILiveGlassSession?>? glassSession = null,
        Func<IDragSourceLifetime>? dragSource = null, Func<bool>? physicalLeftButtonDown = null,
        Func<GlassScene, int, int, CancellationToken, byte[]?>? bootstrap = null,
        WheelDragCapturePlatform? dragCapturePlatform = null)
    {
        this.catalog = catalog;
        this.dragCapturePlatform = dragCapturePlatform ?? WheelDragCapturePlatform.Default;
        captureDragSource = dragSource ?? NativeDragSourceLifetime.Capture;
        this.physicalLeftButtonDown = physicalLeftButtonDown ?? (() =>
            (NativeMethods.GetAsyncKeyState(NativeMethods.GetSystemMetrics(23) != 0 ? 0x02 : 0x01) & 0x8000) != 0);
        this.audio = audio ?? new FeedbackAudio();
        ownsAudio = audio is null;
        InitializeComponent();
        WheelForegroundStyle.Apply(Resources, SystemParameters.HighContrast);
        Width = WheelPresentationMetrics.Width;
        Height = WheelPresentationMetrics.Height;
        glass = new LiveWheelGlass(Dispatcher, body =>
        {
            GlassBody.Source = body;
            GlassBody.Visibility = Visibility.Visible;
        }, () =>
        {
            GlassBody.Source = null;
            GlassBody.Visibility = Visibility.Collapsed;
            SetMaterialState(BackdropState.Solid);
        }, glassSource, createSession: glassSession, bootstrap: bootstrap);
        glass.StageChanged = stage =>
        {
            pendingMaterial = stage == GlassPresentationStage.Pending;
            // The first visible material is already a freshly blurred desktop.
            // OLE admission and input remain active while its worker prepares it.
            if (stage == GlassPresentationStage.Pending && glass.UsesBootstrap) Presentation.Opacity = 0;
            else if (session.IsConfirmed && IsVisible && stage is GlassPresentationStage.Bootstrap or GlassPresentationStage.Live or GlassPresentationStage.Fallback)
                Presentation.Opacity = 1;
            SetMaterialState(stage switch
            {
                GlassPresentationStage.Pending => BackdropState.PendingFrosted,
                GlassPresentationStage.Bootstrap or GlassPresentationStage.Live => BackdropState.LiveRefractiveGlass,
                _ => BackdropState.Solid
            });
        };
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(handle, -20);
            NativeMethods.SetWindowLongPtr(handle, -20, style | 0x08000000 | 0x00000080); // NOACTIVATE | TOOLWINDOW; preserve existing styles.
            HwndSource.FromHwnd(handle)?.AddHook(WindowMessage);
            backdrop.Attach(handle);
        };
        Surface.PreviewDragEnter += OnPreviewDragEnter;
        Surface.PreviewDragOver += OnPreviewDragOver;
        Surface.PreviewDrop += OnPreviewDrop;
        Surface.PreviewDragLeave += (_, e) =>
            DispatchWpf(e, WheelDragEventKind.Leave);
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(25), DispatcherPriority.Input, Tick, Dispatcher);
        timer.Stop();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible || presentationReleased || cancelDraining) return;
            acceptsEntry = false; session.Cancel(generation); timer.Stop(); CancelWork();
            CloseOutsideCapture();
            CancelMaterialPresentation(); pendingPlacement?.Abort(); pendingPlacement = null;
            glass.Stop(); backdrop.Hide(generation); SetMaterialState(BackdropState.Solid); CancelEmphasis();
            ScheduleDragCapturePreparation();
        };
        backdrop.Invalidated += MaterialInvalidated;
        Closing += (_, _) => ReleasePresentation();
        Closed += (_, _) => { ReleasePresentation(); CancelWork(); if (ownsAudio) this.audio.Dispose(); };
        (Application.Current as App)?.AttachPresentation(this);
    }

    public void ApplySettings(CitrusSettings settings)
    {
        reduceMotion = settings.ReduceMotion;
        audio.Enabled = settings.SoundEnabled;
        if (!Motion) CancelEmphasis();
    }

    internal void ApplyMaterialPolicy(MaterialDecision decision, bool dark, double textScale)
    {
        if (presentationReleased) return;
        materialDecision = decision; this.dark = dark; this.textScale = Math.Clamp(textScale, 1, 2.25);
        // Refreshes do not produce audio or reset payload, selection, anchor or arming.
        CancelEmphasis();
        ApplyTextScale();
        MaterialInvalidated();
    }

    private void ApplyTextScale()
    {
        Badge.Visibility = ModeLabel.Visibility = Visibility.Collapsed;
        Hint.Visibility = NavigationPlate.Visibility = pages.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var petal in petals) petal.ApplyTextScale(textScale);
        UpdateHubPresentation();
        System.Windows.Automation.AutomationProperties.SetName(this, $"Citrus {mode}: {paths.Count} files. {Badge.Text}. {Caption.Text}");
    }

    private void SetMaterialState(BackdropState state)
    {
        Shell.State = state;
        bool live = state == BackdropState.LiveRefractiveGlass;
        bool pending = state == BackdropState.PendingFrosted;
        bool highContrast = SystemParameters.HighContrast;
        WheelForegroundStyle.Apply(Resources, highContrast);
        foreach (var petal in petals) petal.ApplyForegroundPalette(highContrast);
        NavigationPlate.Background = live ? Brushes.Transparent : pending ? PendingTint() : (Brush)FindResource("CapsuleBackground");
        NavigationPlate.BorderBrush = highContrast ? SystemColors.WindowTextBrush : Brushes.Transparent;
        if (pending) Resources["MaterialShellTint"] = PendingTint();
        else Resources.Remove("MaterialShellTint");
        UpdateHubPresentation();
    }

    private SolidColorBrush PendingTint()
    {
        if (SystemParameters.HighContrast) return SystemColors.WindowBrush;
        var material = GlassMaterial.Wheel;
        byte Channel(double value) => (byte)Math.Round(value * 255);
        var tint = new SolidColorBrush(Color.FromArgb(Channel(material.TintAlpha), Channel(material.TintRed), Channel(material.TintGreen), Channel(material.TintBlue)));
        tint.Freeze(); return tint;
    }

    private void UpdateHubPresentation()
    {
        bool target = selected >= 0;
        Canvas.SetTop(CaptionPlate, 160);
        Canvas.SetLeft(CaptionPlate, (WheelGeometry.Width-WheelPresentationMetrics.HubCapsuleWidth)/2);
        CaptionPlate.Width = WheelPresentationMetrics.HubCapsuleWidth;
        CaptionPlate.Height = 48;
        CaptionPlate.CornerRadius = new CornerRadius(24);
        CaptionPlate.SetResourceReference(Border.BackgroundProperty, "CapsuleBackground");
        if (target && !SystemParameters.HighContrast) CaptionPlate.SetResourceReference(EffectProperty, "WheelCaptionShadow");
        else CaptionPlate.Effect = null;
        WheelTitleTypography.ApplyHub(Caption, textScale);
        if (!target)
        {
            Caption.FontSize = Math.Min(16.5 * textScale, 20) * WheelPresentationMetrics.LabelCompensation;
            Caption.FontWeight = FontWeights.Normal;
            Caption.TextWrapping = TextWrapping.NoWrap;
            Caption.LineHeight = double.NaN;
        }
    }

    private void CancelEmphasis()
    {
        foreach (var petal in petals) petal.CancelEmphasis();
    }

    private void CancelMaterialPresentation()
    {
        pendingMaterial = false;
    }

    private void MaterialInvalidated()
    {
        if (!materialDecision.RequestNativeAcrylic || !session.IsConfirmed || !IsVisible)
        {
            glass.Stop();
            CancelMaterialPresentation();
            SetMaterialState(BackdropState.Solid);
        }
        if (presentationReleased || handle == 0) return;
        // Unlike Hide, unconfirmed synchronization does not cancel this generation's future eligibility.
        backdrop.Synchronize(generation, default, 1, false, materialDecision, dark);
        QueueMaterialPresentation();
    }

    private void QueueMaterialPresentation()
    {
        if (presentationReleased || !session.IsConfirmed || !IsVisible) return;
        if (!materialDecision.RequestNativeAcrylic || outsideCapture is null)
        { glass.Stop(); CancelMaterialPresentation(); SetMaterialState(BackdropState.Solid); return; }
        if (PresentationSource.FromVisual(Surface) is null) return;
        Point origin = Surface.PointToScreen(new Point());
        Point right = Surface.PointToScreen(new Point(WheelGeometry.Width, 0));
        glass.SetPetalCount(petals.Count);
        glass.SetNavigationVisible(pages.Length > 1);
        glass.StartWheelPresentation((int)Math.Round(origin.X), (int)Math.Round(origin.Y), (int)Math.Round(right.X - origin.X), dark,
            materialDecision.Animate && Motion, handle, outsideCapture.Handle);
    }

    internal void ReleasePresentation()
    {
        if (presentationReleased) return;
        presentationReleased = true; acceptsEntry = false;
        StopDragCapturePreparation();
        StopCancelDrain();
        session.Cancel(generation); timer.Stop(); CancelWork(); audio.Stop(); ++presentationEpoch;
        CloseOutsideCapture();
        CancelMaterialPresentation(); pendingPlacement?.Abort(); pendingPlacement = null;
        CancelEmphasis();
        backdrop.Invalidated -= MaterialInvalidated;
        glass.Dispose();
        backdrop.Dispose();
        SetMaterialState(BackdropState.Solid);
        if (handle != 0) HwndSource.FromHwnd(handle)?.RemoveHook(WindowMessage);
    }

    public void ShowAtPointer(OperationMode requestedMode)
    {
        Dispatcher.VerifyAccess();
        if (presentationReleased || cancelDraining) return;
        ++inputEpoch;
        acceptsEntry = true;
        NativeMethods.GetCursorPos(out var pointer);
        bool newSession = session.RequestMode(Environment.TickCount64, requestedMode, pointer.X, pointer.Y);
        generation = session.Generation;
        paging = new();
        selected = -1;
        navigation = 0;
        armed = false;
        if (newSession)
        {
            glass.Stop();
            glass.SetActivationTiming(System.Diagnostics.Stopwatch.GetTimestamp(), 0);
            CancelMaterialPresentation();
            pendingPlacement?.Abort(); pendingPlacement = null;
            CancelWork();
            presentationEpoch++;
            preparing = false;
            availableOperations.Clear();
            payloadSize = "Choose";
            Caption.Text = payloadSize;
            SetPresentationVisible(false);
        }
        // Prepare content before showing a real capture HWND. Threshold/marquee candidates have
        // only Surface's nonzero-alpha background: OLE can target them, but no menu is displayed.
        operations = availableOperations.GetValueOrDefault(mode) ?? [];
        ModeLabel.Text = mode.ToString().ToUpperInvariant();
        UpdatePayloadLabel();
        RenderSectors();
        if (!session.IsConfirmed) SetPresentationVisible(false);
        if (newSession)
        {
            new WindowInteropHelper(this).EnsureHandle();
            Place(NativeMethods.MonitorScale(pointer));
            Show(); // ShowActivated=false; no Focus, Activate or mouse capture.
            Place(VisualTreeHelper.GetDpi(this).DpiScaleX);
        }
        // A mode change keeps native placement and OLE payload ownership; only selection is rearmed.
        var initial = Surface.PointFromScreen(new Point(pointer.X, pointer.Y));
        session.ResetPointer(initial.X, initial.Y);
        timer.Start();
    }

    /// <summary>Prepare native/layout first use without a visible window or desktop capture.</summary>
    internal void PrepareHiddenPresentation()
    {
        Dispatcher.VerifyAccess();
        if (presentationReleased || IsVisible) return;
        new WindowInteropHelper(this).EnsureHandle();
        Measure(new Size(Width, Height));
        Arrange(new Rect(0, 0, Width, Height));
        UpdateLayout();
    }

    /// <summary>Prepare one hidden OLE target on its owning STA during resident idle time.</summary>
    internal void QueueDragCapturePreparation()
    {
        Dispatcher.VerifyAccess();
        if (presentationReleased) return;
        capturePreparationEnabled = true;
        ScheduleDragCapturePreparation();
    }

    private void ScheduleDragCapturePreparation()
    {
        if (!CanPrepareDragCapture || pendingCapturePreparation is not null) return;
        pendingCapturePreparation = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            pendingCapturePreparation = null;
            if (!CanPrepareDragCapture) return;
            WheelDragCapture? candidate = null;
            try
            {
                candidate = new WheelDragCapture(this, dragCapturePlatform);
                candidate.PrepareHiddenBounds();
                preparedCapture = candidate;
            }
            catch (Exception error) when (error is ExternalException or InvalidOperationException)
            {
                if (candidate is not null) RetireDragCapture(candidate, Task.CompletedTask);
                // Admission can still create a fresh target. Retry only after another hide.
                System.Diagnostics.Trace.WriteLine($"Drag target preparation unavailable: {error.GetType().Name}");
            }
        }));
    }

    private bool CanPrepareDragCapture => capturePreparationEnabled && !presentationReleased &&
        !Dispatcher.HasShutdownStarted && !IsVisible && !cancelDraining && outsideCapture is null && preparedCapture is null;

    internal void StopDragCapturePreparation()
    {
        Dispatcher.VerifyAccess();
        capturePreparationEnabled = false;
        pendingCapturePreparation?.Abort(); pendingCapturePreparation = null;
        var capture = preparedCapture; preparedCapture = null;
        if (capture is not null) RetireDragCapture(capture, Task.CompletedTask);
    }

    private void UpdatePayloadLabel()
    {
        Badge.Text = paths.Count == 0 ? "Drag a file here" : preparing ? "Checking available tools" : operations.Count == 0 ? "No supported operations" :
            paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} files";
        ApplyTextScale();
        Hint.Text = preparing ? "Preparing…" : paths.Count > 0 && operations.Count == 0 ? "Release to cancel" : "Hold · glide · release";
    }

    private void Place(double scale)
    {
        var anchor = new NativeMethods.Point { X = (int)session.Anchor.X, Y = (int)session.Anchor.Y };
        var bounds = WheelGeometry.Place(anchor.X, anchor.Y, NativeMethods.WorkArea(anchor), scale * WheelPresentationMetrics.Scale);
        NativeMethods.SetWindowPos(handle, new nint(-1), (int)bounds.Left, (int)bounds.Top,
            (int)bounds.Width, (int)bounds.Height, 0x10); // SWP_NOACTIVATE
    }

    private nint WindowMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x21) { handled = true; return 3; } // MA_NOACTIVATE
        if (message == 0x02E0) // WM_DPICHANGED: WPF updates its transforms; clamp native placement afterward.
        {
            long placedGeneration = generation;
            pendingPlacement?.Abort();
            pendingPlacement = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                pendingPlacement = null;
                if (!presentationReleased && IsVisible && generation == placedGeneration)
                { Place(VisualTreeHelper.GetDpi(this).DpiScaleX); MaterialInvalidated(); }
            }));
        }
        return 0;
    }

    /// <summary>Called by the mouse observer: capture identity now, then schedule only unconfirmed cleanup.</summary>
    public void ObserveGlobalRelease()
    {
        if (cancelDraining)
        {
            // This hook event is authoritative; GetAsyncKeyState can still reflect
            // the preceding button state before the hook returns.
            ObserveCancelledRelease();
            return;
        }
        long releasedGeneration = generation;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (cancelDraining || presentationReleased) return;
            if (session.GlobalRelease(releasedGeneration)) Dismiss();
            else if (generation == releasedGeneration && session.IsConfirmed && outsideCapture is not null)
            {
                // Give OLE its Drop callback first. An aborted drag still releases the desktop target.
                releaseCleanup?.Stop();
                var cleanup = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                releaseCleanup = cleanup;
                cleanup.Tick += (_, _) =>
                {
                    cleanup.Stop();
                    if (ReferenceEquals(releaseCleanup, cleanup)) releaseCleanup = null;
                    if (generation == releasedGeneration && session.IsConfirmed) Dismiss();
                };
                cleanup.Start();
            }
        }));
    }

    public void Dismiss()
    {
        // Pausing and ordinary dismiss requests cannot expose a still-active external drag.
        if (cancelDraining) return;
        ++inputEpoch;
        acceptsEntry = false;
        session.Cancel(generation);
        CloseOutsideCapture();
        timer.Stop();
        CancelWork(); CancelMaterialPresentation();
        pendingPlacement?.Abort(); pendingPlacement = null;
        audio.Stop(); CancelEmphasis();
        preparing = false; operations = []; availableOperations.Clear();
        armed = false; selected = -1; navigation = 0;
        ++presentationEpoch;
        glass.Stop();
        backdrop.Hide(generation);
        SetMaterialState(BackdropState.Solid);
        SetPresentationVisible(false);
        Sectors.Children.Clear(); petals.Clear();
        Hide();
        ScheduleDragCapturePreparation();
    }

    private void CloseOutsideCapture()
    {
        glass.Stop();
        Task exclusionReleased = glass.ExclusionReleased;
        releaseCleanup?.Stop(); releaseCleanup = null;
        var capture = outsideCapture; outsideCapture = null;
        if (capture is not null)
            RetireDragCapture(capture, exclusionReleased);
        ReleaseDragSource();
    }

    private void RetireDragCapture(WheelDragCapture capture, Task exclusionReleased)
    {
        capture.Release(exclusionReleased);
        dragRetirement = dragRetirement.IsCompleted ? capture.Completion : Task.WhenAll(dragRetirement, capture.Completion);
    }

    /// <summary>Snapshot in the low-level observer; page only this admitted menu on the dispatcher.</summary>
    public void ObserveScroll(int delta)
    {
        if (delta == 0 || presentationReleased || !IsVisible || !session.IsConfirmed || preparing || pages.Length <= 1) return;
        long observedEpoch = inputEpoch;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (observedEpoch != inputEpoch || presentationReleased || !IsVisible || !session.IsConfirmed || preparing) return;
            int oldPage = paging.Page;
            paging.Scroll(delta,pages.Length);
            if (oldPage == paging.Page) return;
            selected = -1; navigation = 0;
            RenderSectors();
        }));
    }

    /// <summary>OLE may reenter Drop before dispatcher work: cancel admission now, hide afterward.</summary>
    public void ObserveCancellation()
    {
        if (TryObserveCancellation() || cancelDraining || presentationReleased || !IsVisible) return;
        long observedEpoch = ++inputEpoch;
        acceptsEntry = false;
        session.Cancel(generation);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (observedEpoch == inputEpoch && !presentationReleased) Dismiss();
        }));
    }

    private void CaptureOutside()
    {
        if (outsideCapture is not null) return;
        long capturedGeneration = generation;
        Action<WheelDragInput, bool> hover = (e, entering) =>
        {
            if (capturedGeneration != generation || presentationReleased)
            { e.Handled = true; e.Effects = DragDropEffects.None; return; }
            if (RejectCancelledDrag(e)) return;
            if (entering && (!ReadPhysicalPayload(e, out var payload, out _) || !payload.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase)))
            { e.Handled = true; e.Effects = DragDropEffects.None; Dismiss(); return; }
            UpdateDragOver(e, LocalPoint(e));
        };
        Action<WheelDragInput> drop = e =>
        {
            if (capturedGeneration != generation || presentationReleased)
            { e.Handled = true; e.Effects = DragDropEffects.None; return; }
            if (RejectCancelledDrag(e, terminalDrop: true)) return;
            CompleteDrop(e, LocalPoint(e));
        };
        Action<WheelDragInput> leave = e =>
        {
            if (capturedGeneration != generation || presentationReleased) return;
            if (!ObserveCancelledLeave(e)) Dismiss();
        };
        var capture = preparedCapture; preparedCapture = null;
        if (capture is not null && !capture.TryBind(hover, drop, leave))
        {
            // Never reuse an invalidated HWND, even if Windows recycled its numeric value.
            RetireDragCapture(capture, Task.CompletedTask);
            capture = null;
        }
        capture ??= new WheelDragCapture(this, hover, drop, leave, dragCapturePlatform);
        outsideCapture = capture;
        capture.PlacementFailed += () =>
        {
            if (capturedGeneration != generation || presentationReleased || !ReferenceEquals(outsideCapture, capture)) return;
            acceptsEntry = false;
            session.Cancel(generation);
            // A failed native target cannot continue the held-drag drain. Reject
            // admission first, then retire it against this activation's glass lease.
            StopCancelDrain();
            Dismiss();
        };
        // The candidate becomes a capture exclusion only after this one-use binding.
    }

    private void CancelWork()
    {
        presentationWork?.Cancel(); presentationWork?.Dispose(); presentationWork = null;
    }

    private void SetPresentationVisible(bool visible)
    {
        // Full geometry and opaque structural content are immediate; no target transforms or hide tail.
        Presentation.Opacity = visible ? 1 : 0;
        if (visible) QueueMaterialPresentation();
        else { glass.Stop(); CancelMaterialPresentation(); SetMaterialState(BackdropState.Solid); }
    }

    private bool ReadPhysicalPayload(WheelDragInput e, out string[] payload, out FileAttributes[] attributes)
    {
        Dispatcher.VerifyAccess(); // Never allow COM IDataObject or DragEventArgs to leave this callback/STA.
        payload = [];
        attributes = [];
        if ((e.AllowedEffects & DragDropEffects.Copy) == 0) return false;
        try
        {
            if (e.ReadFiles() is not { Length: > 0 } raw) return false;
            payload = raw.ToArray();
            if (payload.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))) return false;
            attributes = payload.Select(File.GetAttributes).ToArray();
            // Actions is the physical availability policy, including directories. Mode eligibility
            // is separate, so switching a folder to Convert/Tools cannot destroy OLE ownership.
            return attributes.All(value => DropValidation.AttributesAllowed(value, OperationMode.Actions));
        }
        catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException) { return false; }
    }

    private void OnPreviewDragEnter(object sender, DragEventArgs e)
        => DispatchWpf(e, WheelDragEventKind.Enter);

    private void HandleDragEnter(WheelDragInput e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (RejectCancelledDrag(e)) return;
        if (!IsVisible || !acceptsEntry) return;
        if (!session.IsConfirmed)
        {
            if (!ReadPhysicalPayload(e, out var payload, out var attributes)) { Dismiss(); return; }
            try
            {
                session.ConfirmPayload(payload); // First admission alone owns the immutable ordered snapshot.
                long admittedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                glass.SetAdmittedTimestamp(admittedAt);
                ReleaseDragSource();
                dragSourceLifetime = captureDragSource();
                long sourceReadyAt = System.Diagnostics.Stopwatch.GetTimestamp();
                preparing = true;
                presentationWork = new CancellationTokenSource();
                operations = [];
                paging = new();
                selected = -1;
                navigation = 0;
                var entry = LocalPoint(e);
                session.ResetPointer(entry.X, entry.Y);
                UpdatePayloadLabel();
                RenderSectors();
                long contentReadyAt = System.Diagnostics.Stopwatch.GetTimestamp();
                CaptureOutside();
                long targetReadyAt = System.Diagnostics.Stopwatch.GetTimestamp();
                SetPresentationVisible(true);
                long opticsRequestedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                outsideCapture?.Start();
                GlassStartupLog.RecordAdmissionIfEnabled(admittedAt, sourceReadyAt, contentReadyAt, targetReadyAt,
                    opticsRequestedAt, System.Diagnostics.Stopwatch.GetTimestamp());
                _ = PrepareAsync(payload, attributes, generation, presentationWork.Token);
                _ = UpdatePayloadSizeAsync(payload[0], generation, presentationWork.Token);
            }
            catch (Exception error) when (error is ExternalException or InvalidOperationException)
            {
                // Rejection is synchronous; Dismiss stops this activation's glass
                // before retiring its target against the matching exclusion task.
                e.Effects = DragDropEffects.None;
                Dismiss();
                return;
            }
        }
        UpdateDragOver(e, LocalPoint(e));
    }

    private async Task PrepareAsync(string[] payload, FileAttributes[] attributes, long expected, CancellationToken token)
    {
        try
        {
            await catalogWorkers.WaitAsync(token);
            Dictionary<OperationMode,IReadOnlyList<OperationDescriptor>> prepared;
            try
            {
                prepared = await Task.Run(() =>
                {
                    var result = new Dictionary<OperationMode,IReadOnlyList<OperationDescriptor>>();
                    foreach (var candidateMode in Enum.GetValues<OperationMode>())
                    {
                        token.ThrowIfCancellationRequested();
                        result[candidateMode] = attributes.All(value => DropValidation.AttributesAllowed(value,candidateMode))
                            ? catalog.GetOperations(payload,candidateMode).OrderBy(operation => candidateMode == OperationMode.Convert && operation.Id.StartsWith("archive.", StringComparison.Ordinal) ? 1 : 0).ToArray() : [];
                    }
                    return result;
                },token);
            }
            finally { catalogWorkers.Release(); }
            if (token.IsCancellationRequested || !session.IsConfirmed || expected != generation) return;
            foreach (var pair in prepared) availableOperations[pair.Key] = pair.Value;
            preparing = false;
            operations = availableOperations[mode]; // Mode may have changed while the worker was busy.
            selected = -1; armed = false;
            NativeMethods.GetCursorPos(out var pointer);
            var local = Surface.PointFromScreen(new Point(pointer.X,pointer.Y));
            session.ResetPointer(local.X,local.Y);
            UpdatePayloadLabel(); RenderSectors();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (!token.IsCancellationRequested && session.IsConfirmed && expected == generation)
            { preparing = false; operations = []; UpdatePayloadLabel(); Hint.Text = "Unavailable · release to cancel"; }
        }
    }

    private async Task UpdatePayloadSizeAsync(string path, long expected, CancellationToken token)
    {
        try
        {
            var size = await Task.Run(() =>
            {
                try { return File.Exists(path) ? new FileInfo(path).Length : (long?)null; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
            }, token);
            if (token.IsCancellationRequested || !session.IsConfirmed || expected != generation) return;
            payloadSize = paths.Count > 1 ? $"{paths.Count} files" : size is { } bytes ?
                bytes >= 1_000_000_000 ? $"{bytes / 1_000_000_000d:0.#} GB" :
                bytes >= 1_000_000 ? $"{bytes / 1_000_000d:0.#} MB" :
                bytes >= 1000 ? $"{bytes / 1000d:0.#} KB" : $"{bytes} B" : "Choose";
            if (selected < 0) Caption.Text = payloadSize;
            UpdateHubPresentation();
        }
        catch (OperationCanceledException) { }
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
        => DispatchWpf(e, WheelDragEventKind.Over);

    private void UpdateDragOver(WheelDragInput e, Point point)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (RejectCancelledDrag(e)) return;
        // The steady hover path uses only cached admission, operations and local coordinates.
        // Neither COM extraction nor filesystem/catalog work belongs here.
        if (!session.IsConfirmed) return;
        glass.SetPointer(Surface.TranslatePoint(point, Presentation));
        if ((e.AllowedEffects & DragDropEffects.Copy) == 0) { Dismiss(); return; }
        if (preparing) return;
        armed = session.ObservePointer(point.X, point.Y);
        int nextNavigation = armed ? NavigationAt(point) : 0;
        if (navigation != nextNavigation) { navigation = nextNavigation; UpdateNavigationPresentation(); }
        int next = armed && navigation == 0 ? SelectionAt(point) : -1;
        if (next != selected) Select(next);
        if (selected >= 0 || navigation != 0) e.Effects = DragDropEffects.Copy;
    }

    private int HitTarget(Point point)
    {
        // Use the retained renderer's exact fill convention, including rounded edges. No rebuilt paths.
        for (int i = 0; i < petals.Count; i++) if (petals[i].ContainsPoint(point)) return i;
        return -1;
    }

    private int NavigationAt(Point point)
    {
        if (pages.Length <= 1) return 0;
        bool Inside(FrameworkElement element) => element.IsVisible && element.TransformToAncestor(Surface).TransformBounds(new Rect(element.RenderSize)).Contains(point);
        if (paging.Page > 0 && Inside(Previous)) return -1;
        if (paging.Page + 1 < pages.Length && Inside(Next)) return 1;
        return 0;
    }

    private int SelectionAt(Point point)
    {
        if (NavigationPlate.IsVisible && NavigationPlate.TransformToAncestor(Surface).TransformBounds(new Rect(NavigationPlate.RenderSize)).Contains(point)) return -1;
        point = Surface.TranslatePoint(point, Presentation);
        double x = point.X - WheelGeometry.CenterX, y = point.Y - WheelGeometry.CenterY;
        int hit = HitTarget(point);
        if (hit >= 0) return hit;
        // Only the neutral center cancels. Narrow gutters and the outer rim retain the target.
        return x * x + y * y >= WheelGeometry.InnerRadius * WheelGeometry.InnerRadius ? selected : -1;
    }

    private void OnPreviewDrop(object sender, DragEventArgs e)
        => DispatchWpf(e, WheelDragEventKind.Drop);

    private void CompleteDrop(WheelDragInput e, Point point)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (RejectCancelledDrag(e, terminalDrop: true)) return;
        OperationDrop? accepted = null;
        if (Presentation.Opacity > 0 && session.IsConfirmed && ReadPhysicalPayload(e, out var payload, out var attributes) &&
            payload.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase) &&
            attributes.All(value => DropValidation.AttributesAllowed(value, mode)))
        {
            int index = armed && NavigationAt(point) == 0 ? SelectionAt(point) : -1;
            if (index >= 0 && session.TryCompleteDrop(generation))
            {
                var operation = pageOperations[index];
                accepted = new(operation, Array.AsReadOnly(payload));
                e.Effects = DragDropEffects.Copy;
            }
        }
        Dismiss();
        // OLE returns before any option dialog, jobs, clipboard work or other application callbacks.
        if (accepted is { } drop) Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => OperationDropped?.Invoke(drop)));
    }

    private Point LocalPoint(WheelDragInput input) => input.SurfacePosition ?? Surface.PointFromScreen(input.ScreenPosition);

    private void DispatchWpf(DragEventArgs args, WheelDragEventKind kind)
    {
        args.Handled = true;
        var input = WheelDragInput.FromWpf(args, Surface, kind != WheelDragEventKind.Leave);
        try
        {
            switch (kind)
            {
                case WheelDragEventKind.Enter: HandleDragEnter(input); break;
                case WheelDragEventKind.Over: UpdateDragOver(input, LocalPoint(input)); break;
                case WheelDragEventKind.Drop: CompleteDrop(input, LocalPoint(input)); break;
                case WheelDragEventKind.Leave:
                    if (!ObserveCancelledLeave(input) && outsideCapture is null) Dismiss();
                    break;
            }
        }
        finally { input.Complete(); }
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (session.ShouldExpire(generation, Environment.TickCount64)) { Dismiss(); return; }
        int old = paging.Page;
        paging.UpdateHover(navigation, Environment.TickCount64, pages.Length * WheelGeometry.MaxTargetsPerPage);
        if (old != paging.Page) { selected = -1; RenderSectors(); }
    }

    private void Select(int next)
    {
        if (selected >= 0 && selected < petals.Count) petals[selected].SetSelected(false,Motion);
        selected = next;
        if (selected >= 0)
        {
            petals[selected].SetSelected(true,Motion);
            var operation = pageOperations[selected];
            Caption.Text = WheelDisplayLabels.Petal(operation);
            audio.Play(FeedbackCue.Selection);
        }
        else
        {
            Caption.Text = payloadSize;
        }
        UpdateHubPresentation();
    }

    private void RenderSectors()
    {
        foreach (var petal in petals) petal.CancelEmphasis();
        Sectors.Children.Clear(); petals.Clear();
        pages = WheelMenuLayout.Build(operations, mode, paths);
        if (paging.Page >= pages.Length) paging = new();
        var items = pageOperations = pages.ElementAtOrDefault(paging.Page) ?? [];
        glass.SetPetalCount(items.Count);

        for (int i=0;i<items.Count;i++)
        {
            var petal = new WheelPetal(items[i],i,items.Count);
            petals.Add(petal); Sectors.Children.Add(petal);
            petal.ApplyTextScale(textScale);
            petal.ApplyForegroundPalette(SystemParameters.HighContrast);
        }
        Caption.Text = payloadSize;
        ApplyTextScale();
        UpdateNavigationPresentation();
    }

    private void UpdateNavigationPresentation()
    {
        NavigationPlate.Visibility = pages.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        // Bootstrap renders the reusable capsule once. Mask it until paging is
        // available; visibility changes never require another desktop snapshot.
        glass.SetNavigationVisible(pages.Length > 1);
        if (NavigationPlate.IsVisible) NavigationPlate.UpdateLayout();
        Previous.Child.Opacity = paging.Page == 0 ? .35 : 1;
        Next.Child.Opacity = paging.Page + 1 >= pages.Length ? .35 : 1;
        Previous.Background = Next.Background = Brushes.Transparent;
        if (navigation < 0 && paging.Page > 0) Previous.SetResourceReference(Border.BackgroundProperty, "InsetBackground");
        if (navigation > 0 && paging.Page + 1 < pages.Length) Next.SetResourceReference(Border.BackgroundProperty, "InsetBackground");
        if (pages.Length > 1) Hint.Text = $"{paging.Page+1} / {pages.Length}";
    }
}
