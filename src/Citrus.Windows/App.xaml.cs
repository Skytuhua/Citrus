using System.ComponentModel;
using System.Collections.Immutable;
using Citrus.Core.Editing;
using Citrus.Windows.Editors;
using Citrus.Windows.Editors.Media;
using Citrus.Windows.Editors.Image;
using Citrus.Windows.Editors.Pdf;
using Citrus.Conversion.Editing.Media;
using System.Windows.Threading;
using Citrus.Conversion;
using Citrus.Core;
using Citrus.Windows.Presentation;
using Forms = System.Windows.Forms;

namespace Citrus.Windows;

// Isolated child-process tests exercise the resident without installing global input hooks or changing user settings.
internal sealed record ResidentOptions(string? SettingsRoot = null, string? InstanceSuffix = null, bool ObserveGestures = true, bool Background = false, FileActions? NativeActions = null);

public partial class App : Application
{
    private readonly ResidentOptions options;
    private SingleInstance? instance;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private Forms.ToolStripMenuItem? pauseItem;
    private GestureMonitor? monitor;
    private WheelWindow? wheel;
    private MainWindow? home;
    private ShelfWindow? shelf;
    private JobToastWindow? toast;
    private string quietTitle = "Citrus", quietSource = "";
    private bool quietPreparing;
    private int retiredQuietSucceeded, retiredQuietFailed;
    private readonly HashSet<ConversionJob> quietJobs = [];
    private SettingsWindow? settingsWindow;
    private ThemeManager? themes;
    private WindowPresentationService? presentation;
    private Task demoRetirement = Task.CompletedTask;
    internal void AttachPresentation(Window window, MaterialSurface? surface = null)
    {
        presentation?.Attach(window, surface);
        if (window is WheelDemoWindow demo)
            demo.Closed += (_, _) =>
            {
                // Closed windows leave Application.Windows before their asynchronous
                // capture worker retires. Keep that lifetime available to Quit.
                Task pending = demo.GlassRetirementCompletion;
                demoRetirement = demoRetirement.IsCompleted ? pending : Task.WhenAll(demoRetirement, pending);
            };
    }
    private FeedbackAudio? feedbackAudio;
    private readonly SettingsStore settingsStore;
    private CitrusSettings settings = new();
    private Task<EngineRegistry>? discovery;
    private JobQueue? queue;
    private SubmissionService? submissions;
    private ImageEditorServices? imageEditors;
    private EditorWorkflowRegistry? editorWorkflows;
    private VideoCropEngine? videoCrop;
    private FileActions? actions;
    private Task activeAction = Task.CompletedTask;
    private Task glassPreparation = Task.CompletedTask;
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<ConversionJob> jobs = [];
    internal const int CompletedJobHistoryLimit = 100;
    private readonly HashSet<ConversionJob> notified = [];
    private readonly JobProgressDispatcher jobUpdates;
    private bool preparing, updatingGestures;
    public bool IsQuitting { get; private set; }
    internal event Action? ResidentReady;
    internal bool HomeVisible => home?.IsVisible == true;
    internal bool WheelVisible => wheel?.IsVisible == true;
    internal bool ResourcesDisposed { get; private set; }
    internal CitrusSettings Settings => settings;
    public App() : this(CreateStartupOptions(Environment.GetCommandLineArgs())) { }
    internal static ResidentOptions CreateStartupOptions(IReadOnlyList<string> arguments) => arguments.Contains("--development", StringComparer.Ordinal)
        ? new ResidentOptions(SettingsRoot: Path.Combine(AppContext.BaseDirectory, ".development"), InstanceSuffix: "e788249bd7134c4ba771b7ae038bda83")
        : new ResidentOptions();
    internal App(ResidentOptions options)
    {
        this.options = options; settingsStore = new(options.SettingsRoot);
        jobUpdates = new(action => Dispatcher.BeginInvoke(DispatcherPriority.Background, action), RefreshChangedJobs);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (await TryCleanupUninstallBackupAsync(e.Args)) return;
        try
        {
            instance = new(options.InstanceSuffix);
            if (!instance.IsOwner)
            {
                // Sign-in activation stays quiet. A normal second launch opens the existing Home.
                if (!options.Background && !e.Args.Contains("--background", StringComparer.Ordinal)) await instance.ActivateAsync();
                await instance.DisposeAsync(); instance = null;
                Shutdown(); return;
            }
            instance.Listen(() => Dispatcher.BeginInvoke(new Action(OpenHome)));
            settings = settingsStore.Load();
            themes = new(this);
            themes.Apply(settings.Theme, settings.ReduceMotion);
            presentation = new(this, themes, () => settings);
            // Execute GPU first-use work while engine discovery and launcher setup
            // are still running, rather than after gestures are already available.
            glassPreparation = PrewarmGlassAsync();
            feedbackAudio = new() { Enabled = settings.SoundEnabled };
            home = new(this, startInLauncher: true);
            MainWindow = home;
            actions = options.NativeActions ?? new(floatPaths: FloatPaths, cancellationToken: lifetime.Token);
            using var iconStream = GetResourceStream(new Uri("pack://application:,,,/Citrus.Windows;component/Resources/Citrus.ico")).Stream;
            trayIcon = new System.Drawing.Icon(iconStream);
            tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "Citrus", Visible = true };
            var menu = new Forms.ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true, Padding = new Forms.Padding(5) };
            menu.Items.Add("Open Citrus", null, (_, _) => OpenHome());
            menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
            AddUpdateMenu(menu);
            menu.Items.Add(new Forms.ToolStripSeparator());
            pauseItem = new Forms.ToolStripMenuItem("Pause drag gestures") { CheckOnClick = true };
            pauseItem.CheckedChanged += (_, _) => { if (!updatingGestures) SetGestures(!pauseItem.Checked); };
            menu.Items.Add(pauseItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Quit Citrus", null, async (_, _) => await QuitAsync());
            foreach (Forms.ToolStripItem item in menu.Items)
                if (item is Forms.ToolStripMenuItem) item.Padding = new Forms.Padding(8, 4, 8, 4);
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (_, _) => OpenHome();
            if (!options.Background && !e.Args.Contains("--background", StringComparer.Ordinal)) home.Show();
            SetGestures(settings.GesturesEnabled, persist: false);
            discovery = EngineRegistry.Create(new OutputWriter(), Path.Combine(AppContext.BaseDirectory, "media"), lifetime.Token);
            var registry = await discovery;
            if (IsQuitting) { registry.ImageEditors?.Dispose(); return; }
            queue = new(registry.Engines);
            queue.JobChanged += JobChanged;
            submissions = new(queue, registry.Inspector.InspectAsync);
            imageEditors = registry.ImageEditors;
            videoCrop = registry.VideoCrop;
            editorWorkflows = new(queue);
            if (imageEditors is not null)
                editorWorkflows.Register(imageEditors.Engine.Operations, imageEditors.Routes, imageEditors.Inspector, (request, owner, ct) =>
                {
                    var editor = new EditorHostWindow(imageEditors);
                    PlaceEditor(editor, owner);
                    return editor.ShowEditorAsync(request, ct);
                });
            if (videoCrop is not null)
                editorWorkflows.Register(videoCrop.Operations, videoCrop.Routes, videoCrop, OpenVideoCropBatchAsync);
            if (registry.MediaEditors is { } mediaEditors)
                editorWorkflows.Register(mediaEditors.Operations, mediaEditors.Routes, mediaEditors, (request, owner, ct) =>
                {
                    var editor = new MediaEditorWindow(mediaEditors);
                    PlaceEditor(editor, owner);
                    return editor.ShowEditorAsync(request, ct);
                });
            if (registry.PdfEditors is { } pdfEditors)
                editorWorkflows.Register(pdfEditors.Operations, pdfEditors.Routes, pdfEditors, (request, owner, ct) =>
                {
                    var editor = new PdfEditorWindow(pdfEditors);
                    PlaceEditor(editor, owner);
                    return editor.ShowEditorAsync(request, ct);
                });
            if (registry.ImageMetadata is { } imageMetadata)
                editorWorkflows.Register(imageMetadata.Operations, imageMetadata.Routes, imageMetadata, (request, owner, ct) =>
                {
                    var editor = new ImageMetadataWindow(imageMetadata);
                    PlaceEditor(editor, owner);
                    return editor.ShowEditorAsync(request, ct);
                });
            // Native actions participate in discovery only; the worker queue owns conversion engines exclusively.
            var catalog = new OperationCatalog(registry.Engines.Concat([new NativeActionCatalog()]));
            wheel = new(catalog, feedbackAudio);
            wheel.ApplySettings(settings);
            wheel.PrepareHiddenPresentation();
            wheel.QueueDragCapturePreparation();
            wheel.OperationDropped += SubmitDrop;
            home.Configure(catalog, registry.Warnings);
            try
            {
                if (options.ObserveGestures)
                {
                    monitor = new(Dispatcher);
                    monitor.ModeRequested += mode => { if (IsQuitting || updateAdmissionClosed) return; toast?.Hide(); wheel.ShowAtPointer(settings.Remap(mode)); };
                    monitor.CancellationObserved += wheel.ObserveCancellation;
                    monitor.RightButtonCancellationRequested += wheel.TryObserveCancellation;
                    monitor.DragReleased += wheel.ObserveGlobalRelease;
                    monitor.WheelScrolled += wheel.ObserveScroll;
                }
                SetGestures(settings.GesturesEnabled, persist: false);
            }
            catch (Win32Exception)
            {
                home.Report("Gesture observation could not start. You can still add files here. Restart Citrus to retry.");
                SetGestures(false, persist: false);
            }
            if (settingsStore.LoadWarning is { } warning) home.Report(warning);
            ResidentReady?.Invoke();
            StartAutomaticUpdates();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (home is not null) home.Report("Citrus could not initialize. Restart or reinstall Citrus. " + error.Message);
            else { MessageBox.Show("Citrus could not start: " + error.Message, "Citrus"); await QuitAsync(); }
        }
    }

    private async Task PrewarmGlassAsync()
    {
        Task<bool>? captureCapabilities = null;
        try
        {
            captureCapabilities = Task.Run(DesktopGlassSource.PrewarmCapabilities, lifetime.Token);
            if (!NativeMethods.GetCursorPos(out var pointer)) return;
            double rasterScale = WheelPresentationMetrics.RasterScale(NativeMethods.MonitorScale(pointer));
            await GlassSceneCache.Shared.PrewarmAsync(rasterScale, lifetime.Token);
            await StudioGlassRuntime.Shared.PrewarmWheelAsync(rasterScale, lifetime.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or
            ArgumentException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            System.Diagnostics.Trace.WriteLine($"Glass prewarm unavailable: {error.GetType().Name}");
        }
        finally
        {
            if (captureCapabilities is not null)
            {
                try { await captureCapabilities; }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }
        }
    }

    internal void OpenHome()
    {
        if (IsQuitting || updateAdmissionClosed) return;
        home?.ShowLauncher();
        BringHomeToFront();
    }
    private void BringHomeToFront()
    {
        home?.Show();
        if (home?.WindowState == WindowState.Minimized) home.WindowState = WindowState.Normal;
        home?.Activate();
    }
    internal void CloseHome() => home?.Close();
    internal void ShowWheelForLifecycleTest() => wheel?.ShowAtPointer(OperationMode.Actions);
    internal void OpenSettings()
    {
        if (IsQuitting || updateAdmissionClosed) return;
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        if (home?.IsVisible != true) OpenHome();
        settingsWindow = new(settings, ApplySettingsAsync, CheckForUpdatesAsync, RestartForUpdateAsync, OpenUpdateReleases) { Owner = home };
        settingsWindow.SetUpdateStatus(updateStatus);
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
    }
    private void FloatPaths(IReadOnlyList<string> paths)
    {
        if (IsQuitting || updateAdmissionClosed) return;
        if (shelf is null)
        {
            shelf = new();
            shelf.Closed += (_, _) => shelf = null;
        }
        shelf.AddPaths(paths); shelf.Show(); shelf.Activate();
    }
    internal Task ApplySettingsAsync(CitrusSettings updated)
    {
        if (IsQuitting || updateAdmissionClosed) throw new InvalidOperationException("Citrus is quitting.");
        updated.Validate();
        // Only an explicit preference change writes HKCU Run. Test hosts never use the real startup key.
        var startup = new StartupRegistration();
        bool startupChanged = updated.LaunchAtSignIn != settings.LaunchAtSignIn;
        if (startupChanged && options.SettingsRoot is null) startup.Apply(updated.LaunchAtSignIn);
        try { settingsStore.Save(updated); }
        catch
        {
            if (startupChanged && options.SettingsRoot is null) startup.Apply(settings.LaunchAtSignIn);
            throw;
        }
        settings = updated;
        themes?.Apply(settings.Theme, settings.ReduceMotion);
        presentation?.Refresh();
        wheel?.ApplySettings(settings);
        if (feedbackAudio is not null) feedbackAudio.Enabled = settings.SoundEnabled;
        SetGestures(settings.GesturesEnabled, persist: false);
        home?.UpdateGestureGuide(settings);
        UpdatePreferenceChanged();
        return Task.CompletedTask;
    }
    internal void SetGestures(bool enabled, bool persist = true)
    {
        if (updatingGestures || IsQuitting || updateAdmissionClosed) return;
        updatingGestures = true;
        try
        {
            if (persist && settings.GesturesEnabled != enabled)
            {
                var updated = settings with { GesturesEnabled = enabled };
                try { settingsStore.Save(updated); settings = updated; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { home?.Report("Gesture setting could not be saved: " + error.Message); }
            }
            if (monitor?.Enabled != enabled) monitor?.SetEnabled(enabled);
            if (!enabled) { wheel?.Dismiss(); toast?.Close(); feedbackAudio?.Stop(); }
            if (pauseItem is not null) pauseItem.Checked = !enabled;
            home?.SetGestureCheck(enabled);
            home?.UpdateGestureGuide(settings);
        }
        finally { updatingGestures = false; }
    }
    private async void SubmitDrop(OperationDrop drop) => await SubmitAsync(drop, origin: SubmissionOrigin.Wheel);
    internal async Task RetryAsync(ConversionJob job, string? destination)
    {
        if (job.Request.Editor is null) { await SubmitAsync(new(job.Request.Operation, job.Request.Inputs), job.Request.Options, destination, SubmissionOrigin.Retry); return; }
        if (IsQuitting || updateAdmissionClosed || home is null) return;
        if (preparing) { home.Report("Finish the current editor before retrying."); return; }
        if (editorWorkflows?.Contains(job.Request.Operation.Id) != true) { home.Report("Editor unavailable. Restore the required tools and retry."); return; }
        preparing = true; home.SetBusy(true);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); activeAction = completed.Task;
        try
        {
            var retried = await editorWorkflows.RetryAsync(job.Request, destination, lifetime.Token);
            TrackSubmitted([retried], SubmissionOrigin.Retry);
        }
        catch (OperationCanceledException) { home.Report("Retry canceled."); }
        catch (Exception error) { home.AddSubmissionFailure("Could not retry: " + error.Message, () => RetryAsync(job, destination)); }
        finally { preparing = false; home.SetBusy(false); completed.TrySetResult(); }
    }
    internal async Task SubmitAsync(OperationDrop drop, OperationOptions? options = null, string? destination = null, SubmissionOrigin origin = SubmissionOrigin.Home)
    {
        if (IsQuitting || updateAdmissionClosed || submissions is null || home is null) return;
        if (preparing) { ReportSubmission("Finish the current task first.", origin); return; }
        drop = new(drop.Operation, Array.AsReadOnly(drop.Paths.ToArray())); // Freeze the actual incoming payload before any await/modal pump.
        preparing = true;
        home.SetBusy(true);
        // Origin affects feedback only. All routes still use one authoritative submission service.
        if (origin == SubmissionOrigin.Home) BringHomeToFront();
        if (origin == SubmissionOrigin.Wheel) BeginQuietSubmission(drop);
        ReportSubmission("Checking inputs…", origin, pending: true);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        activeAction = completed.Task; // Installed before a destination dialog can pump nested Quit messages.
        try
        {
            if (drop.Operation.Mode == OperationMode.Actions)
            {
                await actions!.ExecuteAsync(drop.Operation.Id, drop.Paths);
                home.RefreshSelection();
                ReportSubmission(drop.Operation.Id == "action.move" ? "Moved to: " + string.Join("; ", actions.LastMoveDestinations) :
                    $"{drop.Operation.Label.TrimEnd('…')} completed for {drop.Paths.Count} item(s).", origin);
            }
            else
            {
                IReadOnlyList<ConversionJob> submitted;
                if (editorWorkflows?.Contains(drop.Operation.Id) == true)
                    submitted = await editorWorkflows.OpenAndSubmitAsync(drop, options ?? new(), destination, origin, home.IsVisible ? home : null, lifetime.Token);
                else submitted = await submissions.SubmitAsync(drop.Operation, drop.Paths, ConfirmAsync, options, destination, lifetime.Token);
                TrackSubmitted(submitted, origin);
            }
        }
        catch (OperationCanceledException) { home.Report("Canceled."); if (origin == SubmissionOrigin.Wheel) toast?.Close(); }
        catch (Exception error)
        {
            string message = "Could not complete: " + error.Message;
            home.AddSubmissionFailure(message, drop.Operation.Mode == OperationMode.Actions ? null : () => SubmitAsync(drop, options, destination, SubmissionOrigin.Retry));
            ReportSubmission(message, origin);
        }
        finally { quietPreparing = false; preparing = false; home.SetBusy(false); toast?.RefreshVisibility(); completed.TrySetResult(); }
    }
    private void TrackSubmitted(IReadOnlyList<ConversionJob> submitted, SubmissionOrigin origin)
    {
        jobs.AddRange(submitted);
        if (origin == SubmissionOrigin.Wheel)
        {
            foreach (var job in submitted) quietJobs.Add(job);
            quietPreparing = false;
        }
        var retired = TrimJobHistory();
        var retained = submitted.Where(job => !retired.Contains(job)).ToArray();
        home?.AddJobs(retained);
        if (submitted.Count == 0) { home?.Report("Canceled."); if (origin == SubmissionOrigin.Wheel) toast?.Close(); }
        else if (origin == SubmissionOrigin.Wheel) RefreshQuietJobs();
        else ReportSubmission($"Submitted {submitted.Count} job(s).", origin, pending: true);
        RefreshChangedJobs(retained);
    }
    private HashSet<ConversionJob> TrimJobHistory()
    {
        HashSet<ConversionJob> retired = [];
        int completed = 0;
        for (int i = jobs.Count - 1; i >= 0; i--)
            if (jobs[i].State is not (ConversionJobState.Queued or ConversionJobState.Running) && ++completed > CompletedJobHistoryLimit)
                retired.Add(jobs[i]);
        if (retired.Count == 0) return retired;
        foreach (var job in retired.Where(quietJobs.Contains))
        {
            if (job.State == ConversionJobState.Succeeded) retiredQuietSucceeded++;
            else if (job.State == ConversionJobState.Failed) retiredQuietFailed++;
        }
        jobs.RemoveAll(retired.Contains);
        notified.ExceptWith(retired);
        quietJobs.ExceptWith(retired);
        home?.RemoveJobs(retired);
        return retired;
    }
    private static void PlaceEditor(Window editor, Window? owner)
    {
        if (owner is not null) editor.Owner = owner;
        else editor.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }
    private async Task<EditorSubmissionBatch?> OpenVideoCropBatchAsync(EditorOpenRequest request, Window? owner, CancellationToken ct)
    {
        var sources = ImmutableArray.CreateBuilder<SourceSnapshot>();
        var items = ImmutableArray.CreateBuilder<EditorSubmissionItem>();
        foreach (string path in request.InitialPaths)
        {
            var editor = new VideoCropWindow(videoCrop!);
            PlaceEditor(editor, owner);
            var batch = await editor.ShowEditorAsync(request with { InitialPaths = [path] }, ct);
            if (batch is null) return null; // No partial batch is enqueued if any editor is canceled.
            sources.AddRange(batch.InitialSources); items.AddRange(batch.Items);
        }
        return new EditorSubmissionBatch(request, sources.ToImmutable(), [], [],
            sources.Select(source => source.Id).ToImmutableArray(), items.ToImmutable());
    }
    private static string ProgressTitle(OperationDescriptor operation) => operation.Id switch
    {
        "video.crop" => "Cropping video", "video.trim" => "Trimming video", "video.resize" => "Resizing video",
        _ => operation.Mode == OperationMode.Convert ? "Converting to " + WheelDisplayLabels.Petal(operation) : operation.Label
    };
    private void BeginQuietSubmission(OperationDrop drop)
    {
        quietPreparing = true;
        quietTitle = ProgressTitle(drop.Operation);
        quietSource = drop.Paths.Count == 1 ? Path.GetFileName(drop.Paths[0]) : $"{drop.Paths.Count} files";
        quietJobs.RemoveWhere(job => job.State is not (ConversionJobState.Queued or ConversionJobState.Running));
        retiredQuietSucceeded = retiredQuietFailed = 0;
        // Only a new accepted submission may acquire presentation. Dismissal or pause
        // during inspection/confirmation remains effective for its later status and jobs.
        if (toast is not null || IsQuitting) return;
        toast = new(() => { OpenHome(); home?.ShowJobs(); }, RevealAsync, () => !IsQuitting && !preparing && wheel?.IsVisible != true);
        toast.Closed += (_, _) => toast = null;
    }
    private void ReportSubmission(string message, SubmissionOrigin origin, bool pending = false)
    {
        home?.Report(message);
        if (origin != SubmissionOrigin.Wheel || IsQuitting) return;
        // Status/progress/completion can update a live notification, never reacquire one.
        toast?.Update(pending ? quietSource : message, pending, operation: quietTitle);
    }
    private void RefreshQuietJobs()
    {
        // Earlier jobs cannot replace or expire the card belonging to a new options/editor flow.
        if (toast is null || quietPreparing) return; // Dismissal stays dismissed during later progress updates.
        int pending = quietJobs.Count(job => job.State is ConversionJobState.Running or ConversionJobState.Queued);
        int failed = retiredQuietFailed + quietJobs.Count(job => job.State == ConversionJobState.Failed);
        int complete = retiredQuietSucceeded + quietJobs.Count(job => job.State == ConversionJobState.Succeeded);
        if (pending == 0 && failed == 0 && complete == 0) { toast.Close(); return; }
        string message = pending > 0 ? $"{pending} job(s) in progress · {complete} ready · {failed} failed"
            : failed > 0 ? $"{complete} ready · {failed} failed" : $"{complete} complete";
        var single = quietJobs.Count == 1 ? quietJobs.First() : null;
        if (pending > 0 && single is not null) message = string.Join(", ", single.Request.Inputs.Select(Path.GetFileName));
        toast.Update(message, pending > 0, quietJobs.Where(job => job.State == ConversionJobState.Succeeded).SelectMany(job => job.Outputs).ToArray(),
            single is not null ? ProgressTitle(single.Request.Operation) : "Processing files", autoDismiss: pending == 0 && failed == 0,
            fraction: single?.Progress?.Fraction);
    }
    internal async Task RevealAsync(IReadOnlyList<string> paths)
    {
        try { if (!IsQuitting) await actions!.ExecuteAsync("action.reveal", paths); }
        catch (Exception error) { home?.Report("Could not reveal output: " + error.Message); }
    }
    private Task<SubmissionChoice?> ConfirmAsync(SubmissionDraft draft, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var dialog = new ToolOptionsWindow(draft);
        // A never-shown background Home is not a valid modal owner.
        if (home?.IsVisible == true) dialog.Owner = home;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        using var registration = token.Register(() => Dispatcher.BeginInvoke(new Action(() => dialog.Close())));
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.Choice : null);
    }
    private void JobChanged(ConversionJob job) => jobUpdates.Enqueue(job);
    private void RefreshChangedJobs(IReadOnlyCollection<ConversionJob> changedJobs)
    {
        if (IsQuitting) return;
        bool refreshQuiet = changedJobs.Any(quietJobs.Contains);
        TrimJobHistory();
        var retained = jobs.ToHashSet();
        changedJobs = changedJobs.Where(retained.Contains).ToArray();
        home?.RefreshJobs(changedJobs);
        if (refreshQuiet) RefreshQuietJobs();
        foreach (var job in changedJobs)
        {
            if (job.State is ConversionJobState.Succeeded or ConversionJobState.Failed && notified.Add(job))
            {
                feedbackAudio?.Play(job.State == ConversionJobState.Succeeded ? FeedbackCue.Success : FeedbackCue.Error);
                if (settings.RevealCompletedOutputs && job.State == ConversionJobState.Succeeded && job.Outputs.Count > 0)
                    _ = RevealAsync(job.Outputs);
            }
        }
    }
    internal async Task QuitAsync()
    {
        if (IsQuitting) return;
        IsQuitting = true;
        jobUpdates.Stop();
        // Home.Dispose closes its owned demonstration. Retain those workers now,
        // before closing their windows removes them from Application.Windows.
        var opticalDemos = Windows.OfType<WheelDemoWindow>().ToArray();
        lifetime.Cancel();
        monitor?.Dispose();
        wheel?.StopDragCapturePreparation();
        wheel?.Dismiss();
        toast?.Close();
        home?.Dispose();
        settingsWindow?.Close();
        foreach (Window dialog in Windows.Cast<Window>().Where(window => window is ToolOptionsWindow or EditorHostWindow or VideoCropWindow or MediaEditorWindow or PdfEditorWindow or ImageMetadataWindow).ToArray()) dialog.Close();
        if (tray is not null) tray.Visible = false;
        foreach (var job in jobs) job.Cancel();
        await StopAutomaticUpdatesAsync();
        await activeAction; // A native Move is never abandoned midway through its filesystem work.
        await Task.WhenAll(jobs.Select(job => job.Completion));
        await glassPreparation;
        // Stop every optical worker before retiring the app-owned prepared device.
        wheel?.ReleasePresentation();
        foreach (WheelDemoWindow demo in opticalDemos) demo.Close();
        if (wheel is not null)
        {
            await Task.WhenAll(wheel.GlassCompletion, wheel.GlassRetirementCompletion);
            await wheel.DragRetirementCompletion;
        }
        await Task.WhenAll(opticalDemos.Select(demo => demo.GlassRetirementCompletion));
        await demoRetirement;
        await DesktopGlassCapturePool.RetirementCompletion;
        await StudioGlassRuntime.Shared.ShutdownAsync();
        if (discovery is not null)
        {
            try { await discovery; }
            catch (Exception) { /* Initialization has its own status path; shutdown still releases resident resources. */ }
        }
        if (instance is not null) { await instance.DisposeAsync(); instance = null; }
        Shutdown();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        jobUpdates.Stop();
        lifetime.Cancel();
        monitor?.Dispose();
        if (queue is not null) queue.JobChanged -= JobChanged;
        shelf?.Dispose();
        toast?.Dispose();
        home?.Dispose();
        imageEditors?.Dispose();
        presentation?.Dispose();
        themes?.Dispose();
        feedbackAudio?.Dispose();
        tray?.ContextMenuStrip?.Dispose();
        tray?.Dispose();
        trayIcon?.Dispose();
        instance?.Dispose(); instance = null;
        lifetime.Dispose();
        ResourcesDisposed = true;
        base.OnExit(e);
    }
}
