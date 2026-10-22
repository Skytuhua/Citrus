using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Interop;
using Citrus.Core;
using Microsoft.Win32;

namespace Citrus.Windows;

public partial class MainWindow : Window, IDisposable
{
    private readonly App app;
    private OperationCatalog? catalog;
    private readonly WorkspaceSelection selection = new();
    private readonly IFilePreviewService previews;
    private CancellationTokenSource? previewCancellation;
    private long previewGeneration;
    private bool disposed;
    private WheelDemoWindow? demo;
    private readonly ObservableCollection<JobViewModel> jobs = [];
    private readonly Dictionary<ConversionJob, JobViewModel> jobModels = [];
    private readonly ObservableCollection<SubmissionFailure> failures = [];
    private bool busy;
    public MainWindow(App app, IFilePreviewService? previews = null, bool startInLauncher = false)
    {
        this.app = app;
        this.previews = previews ?? new FilePreviewService();
        InitializeComponent();
        Citrus.Windows.Presentation.WindowChromeSetup.Apply(this);
        if (startInLauncher) ShowLauncher();
        // The HWND exists here but has not been shown: size/position before first paint.
        SourceInitialized += PlaceInitialWorkspace;
        SelectedFiles.ItemsSource = selection.Items;
        selection.MembershipChanged += MembershipChanged;
        JobList.ItemsSource = jobs;
        SubmissionFailures.ItemsSource = failures;
        Closing += OnClosing;
        IsVisibleChanged += async (_, _) =>
        {
            if (disposed) return;
            if (IsVisible) await RefreshPreviewAsync();
            else StopPreview();
        };
        (Application.Current as App)?.AttachPresentation(this, Chrome);
    }
    internal bool IsLauncher => LauncherRoot.Visibility == Visibility.Visible;
    internal void ShowLauncher()
    {
        if (IsLauncher) return;
        WorkspaceRoot.Visibility = Visibility.Collapsed; LauncherRoot.Visibility = Visibility.Visible;
        double textScale = TryFindResource("TextScaleFactor") is double scale ? scale : 1;
        WindowState = WindowState.Normal; MinWidth = 340; MinHeight = 360 * textScale; Width = 400; Height = 500 * textScale;
        if (IsLoaded) PlaceInitialWorkspace(this, EventArgs.Empty);
    }
    internal void ShowWorkspace()
    {
        if (!IsLauncher) return;
        LauncherRoot.Visibility = Visibility.Collapsed; WorkspaceRoot.Visibility = Visibility.Visible;
        WindowState = WindowState.Normal; MinWidth = 620; MinHeight = 440; Width = 840; Height = 620;
        if (IsLoaded) PlaceInitialWorkspace(this, EventArgs.Empty);
    }
    private void OpenWorkspaceClick(object sender, RoutedEventArgs e) { ShowWorkspace(); if (jobs.Count + failures.Count > 0) ShowJobs(); }
    private void OpenLauncherClick(object sender, RoutedEventArgs e) => ShowLauncher();

    private void PlaceInitialWorkspace(object? sender, EventArgs e)
    {
        SourceInitialized -= PlaceInitialWorkspace; // Initial placement only; later user moves/resizes stay untouched.
        if (!Citrus.Windows.Presentation.CompactWindowSizing.TryGetMonitor(this,out var monitor)) return;
        var work=monitor.WorkArea;double scale=monitor.Scale;
        var size=Citrus.Windows.Presentation.CompactWindowSizing.Fit(Width,Height,MinWidth,MinHeight,work.Width/scale,work.Height/scale);
        MinWidth=size.MinWidth;MinHeight=size.MinHeight;
        int width = (int)Math.Floor(size.Width * scale);
        int height = (int)Math.Floor(size.Height * scale);
        int left = (int)Math.Floor(work.Left + (work.Width - width) / 2);
        int top = (int)Math.Floor(work.Top + (work.Height - height) / 2);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = width / scale; Height = height / scale;
        Left = left / scale; Top = top / scale;
        // Work-area origins are physical coordinates, including negative mixed-monitor origins.
        // Native placement avoids assuming the target monitor shares the primary monitor's DPI.
        NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, 0, left, top, width, height, 0x14); // NOACTIVATE | NOZORDER
    }

    public void Configure(OperationCatalog available, IReadOnlyList<string> warnings)
    {
        catalog = available;
        EngineStatus.Text = string.Join("\n", warnings);
        EngineStatus.Visibility = warnings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshOperations();
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!app.IsQuitting && !disposed) { e.Cancel = true; demo?.Close(); Hide(); } }
    private void ChooseFiles(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Choose files", CheckFileExists = true, Multiselect = true, Filter = "All files|*.*" };
        if (picker.ShowDialog(this) == true) AddFiles(picker.FileNames);
    }
    private void ChooseFolder(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose folders", Multiselect = true };
        if (picker.ShowDialog(this) == true) AddFiles(picker.FolderNames);
    }
    internal void AddFiles(IReadOnlyList<string> paths)
    {
        if (!DropValidation.Accepts(paths, OperationMode.Actions)) { Report("Choose downloaded files or folders."); return; }
        if (IsLauncher) ShowWorkspace();
        selection.Add(paths);
        SelectedFiles.SelectedItem = selection.Active;
        if (paths.Any(Directory.Exists)) Modes.SelectedIndex = 2;
    }
    private void FilesDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true; e.Effects = DragDropEffects.None;
        if (e.AllowedEffects.HasFlag(DragDropEffects.Copy) && e.Data.GetDataPresent(DataFormats.FileDrop, false) && e.Data.GetData(DataFormats.FileDrop, false) is string[] paths && DropValidation.Accepts(paths, OperationMode.Actions)) e.Effects = DragDropEffects.Copy;
    }
    private void FilesDropped(object sender, DragEventArgs e)
    {
        FilesDragOver(sender, e);
        if (e.Effects == DragDropEffects.Copy && e.Data.GetData(DataFormats.FileDrop, false) is string[] paths) AddFiles(paths);
    }
    private async void FileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RemoveButton is null || disposed) return;
        selection.Preview(SelectedFiles.SelectedItem as WorkspaceInput);
        RemoveButton.IsEnabled = selection.Active is not null;
        await RefreshPreviewAsync();
    }
    private void MembershipChanged()
    {
        if (disposed) return;
        RefreshOperations();
        UpdatePreviewCaption();
    }
    private void UpdatePreviewCaption() => PreviewCaption.Text = selection.Active is { } active
        ? active.Name + (active.IsIncluded ? " · included" : " · excluded from batch") : "File preview";
    private async Task RefreshPreviewAsync()
    {
        previewCancellation?.Cancel(); previewCancellation?.Dispose();
        previewCancellation = new();
        var token = previewCancellation.Token;
        long generation = ++previewGeneration;
        PreviewImage.Source = null;
        PreviewFallback.Visibility = Visibility.Visible;
        UpdatePreviewCaption();
        if (selection.Active is not { } active) { PreviewMessage.Text = "Drop files here"; return; }
        PreviewMessage.Text = "Loading preview…";
        try
        {
            // Preview-only navigation does not rebuild the operation catalog or change batch inclusion.
            var image = await previews.LoadAsync(active.Path, 640, token);
            if (disposed || token.IsCancellationRequested || generation != previewGeneration) return;
            PreviewImage.Source = image;
            PreviewFallback.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
            PreviewMessage.Text = "Preview unavailable";
            PreviewCaption.ToolTip = active.Path;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            if (!disposed && generation == previewGeneration) PreviewMessage.Text = "Preview unavailable";
        }
    }
    private void RemoveFiles(object sender, RoutedEventArgs e)
    {
        if (selection.Active is { } active) selection.Remove(active);
        SelectedFiles.SelectedItem = selection.Active;
    }
    private void ClearFiles(object sender, RoutedEventArgs e) { selection.Clear(); SelectedFiles.SelectedItem = null; }
    private void IncludeAll(object sender, RoutedEventArgs e) => selection.SetAllIncluded(true);
    private void ExcludeAll(object sender, RoutedEventArgs e) => selection.SetAllIncluded(false);
    private void ModeChanged(object sender, SelectionChangedEventArgs e) { if (Operations is not null) RefreshOperations(); }
    private void OperationChanged(object sender, SelectionChangedEventArgs e) => UpdateInspector();
    private void UpdateInspector()
    {
        if (RunButton is null) return;
        var operation = (Operations.SelectedItem as OperationCard)?.Operation;
        int includedCount = selection.IncludedPaths.Count;
        RunButton.IsEnabled = !busy && operation is not null;
        RunButton.Content = operation is null ? "Choose an operation" : operation.Mode == OperationMode.Actions ? operation.Label : operation.IsGroup ? "Review group…" : operation.NeedsOptions ? "Set options…" : includedCount == 1 ? "Create copy" : $"Create {includedCount} copies";
        InspectorTitle.Text = operation?.Label ?? "Choose files and an operation";
        OperationHint.Text = operation is null ? "" : OperationCard.Describe(operation);
        BatchSummary.Text = $"{includedCount} included file(s)";
        OutputSummary.Text = operation?.Id == "action.move" ? "Moves originals · choose a destination next"
            : operation?.Mode == OperationMode.Actions ? $"{includedCount} included file(s)"
            : operation?.IsGroup == true ? "One combined output"
            : $"{operation?.OutputExtension?.TrimStart('.').ToUpperInvariant() ?? "New"} copies · beside each original";
        SafetySummary.Text = operation?.Id == "action.move" ? "Moves originals to a folder you choose. Existing items are never replaced."
            : "";
    }
    private void RefreshOperations()
    {
        string? previous = (Operations.SelectedItem as OperationCard)?.Operation.Id;
        var paths = selection.IncludedPaths;
        var available = catalog?.GetOperations(paths, Modes.SelectedIndex == 2 ? OperationMode.Actions : Modes.SelectedIndex == 1 ? OperationMode.Tools : OperationMode.Convert) ?? [];
        var cards = available.Select(operation => new OperationCard(operation)).ToArray();
        Operations.ItemsSource = cards;
        Operations.SelectedItem = cards.FirstOrDefault(card => card.Operation.Id == previous) ?? cards.FirstOrDefault();
        ClearButton.IsEnabled = selection.Items.Count > 0;
        SelectionStatus.Text = $"{paths.Count} of {selection.Items.Count} included";
        bool hasFiles = selection.Items.Count > 0;
        EmptyState.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
        FilesPanel.Visibility = CenterPanel.Visibility = Inspector.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        OperationsEmpty.Text = catalog is null ? "Loading tools…" : selection.Items.Count == 0 ? "Add files to begin."
            : paths.Count == 0 ? "Include at least one file."
            : available.Count == 0 ? "No compatible operations. Exclude a file or switch mode." : "";
        UpdateInspector();
    }

    private async void RunOperation(object sender, RoutedEventArgs e)
    {
        if (Operations.SelectedItem is OperationCard card) await app.SubmitAsync(new(card.Operation, selection.IncludedPaths), origin: SubmissionOrigin.Home);
    }
    public void SetBusy(bool value) { busy = value; RunButton.IsEnabled = !busy && Operations.SelectedItem is OperationCard; }
    public void AddJobs(IReadOnlyList<ConversionJob> submitted)
    {
        foreach (var job in submitted) { var model = new JobViewModel(job); jobModels[job] = model; jobs.Insert(0, model); }
        RefreshJobs();
        if (IsVisible && submitted.Count > 0) JobsPanel.IsExpanded = true;
    }
    internal void RemoveJobs(IReadOnlyCollection<ConversionJob> retired)
    {
        foreach (var job in retired)
            if (jobModels.Remove(job, out var model)) jobs.Remove(model);
    }
    public void RefreshJobs(IReadOnlyCollection<ConversionJob>? changedJobs = null)
    {
        JobsPanel.Visibility = jobs.Count + failures.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (changedJobs is null) { foreach (var job in jobs) job.Refresh(); }
        else { foreach (var job in changedJobs) if (jobModels.TryGetValue(job, out var model)) model.Refresh(); }
        LauncherRecent.Visibility = JobsPanel.Visibility;
        LauncherJobSummary.Text = jobs.FirstOrDefault() is { } recent ? recent.Source + " · " + recent.Status : failures.FirstOrDefault()?.Message ?? "";
        int succeeded = 0, failed = failures.Count, canceled = 0, pending = 0;
        foreach (var model in jobs)
        {
            switch (model.Job.State)
            {
                case ConversionJobState.Succeeded: succeeded++; break;
                case ConversionJobState.Failed: failed++; break;
                case ConversionJobState.Canceled: canceled++; break;
                default: pending++; break;
            }
        }
        JobSummary.Text = $"Recent jobs · {succeeded} succeeded · {failed} failed · {canceled} canceled · {pending} pending";
    }
    private void CancelJob(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is JobViewModel model) model.Job.Cancel(); }
    private async void RetryJob(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is JobViewModel model && model.CanRetry) await app.RetryAsync(model.Job, model.Job.Request.DestinationDirectory);
    }
    private async void ChangeDestination(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not JobViewModel model || !model.CanChooseFolder) return;
        var picker = new OpenFolderDialog { Title = "Choose another folder and retry" };
        if (picker.ShowDialog(this) == true) await app.RetryAsync(model.Job, picker.FolderName);
    }
    private async void RevealOutput(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is string path) await app.RevealAsync([path]);
    }
    private void OpenSettings(object sender, RoutedEventArgs e) => app.OpenSettings();
    private void ToggleGestures(object sender, RoutedEventArgs e) { if (IsLoaded) app.SetGestures(GesturesEnabled.IsChecked == true); }
    public void RefreshSelection()
    {
        foreach (var input in selection.Items.ToArray()) if (!File.Exists(input.Path) && !Directory.Exists(input.Path)) selection.Remove(input);
        SelectedFiles.SelectedItem = selection.Active;
        RefreshOperations();
    }
    public void UpdateGestureGuide(CitrusSettings settings)
    {
        ShiftModeLabel.Text = ModeName(settings.ShiftMode);
        AltModeLabel.Text = ModeName(settings.AltMode);
        ControlModeLabel.Text = ModeName(settings.ControlMode);
        LauncherShiftIcon.SetResourceReference(System.Windows.Shapes.Path.DataProperty, ModeIconKey(settings.ShiftMode));
        LauncherAltIcon.SetResourceReference(System.Windows.Shapes.Path.DataProperty, ModeIconKey(settings.AltMode));
        LauncherControlIcon.SetResourceReference(System.Windows.Shapes.Path.DataProperty, ModeIconKey(settings.ControlMode));
        System.Windows.Automation.AutomationProperties.SetName(GestureGuide,
            $"Drag a file, then tap Shift for {ShiftModeLabel.Text}, Alt for {AltModeLabel.Text}, or Control for {ControlModeLabel.Text}.");
    }
    private static string ModeName(OperationMode mode) => mode == OperationMode.Actions ? "Files" : mode.ToString();
    private static string ModeIconKey(OperationMode mode) => mode switch
    { OperationMode.Convert => "Icon.Convert", OperationMode.Tools => "Icon.Tools", _ => "Icon.File" };
    public void SetGestureCheck(bool enabled) => GesturesEnabled.IsChecked = enabled;
    public void AddSubmissionFailure(string message, Func<Task>? retry)
    {
        failures.Insert(0, new(message, retry));
        if (failures.Count > App.CompletedJobHistoryLimit) failures.RemoveAt(failures.Count - 1);
        if (IsVisible) JobsPanel.IsExpanded = true;
        RefreshJobs();
    }
    private async void RetrySubmission(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is SubmissionFailure { Retry: { } retry })
        {
            var button = (Button)sender;
            button.IsEnabled = false;
            try { await retry(); }
            finally { button.IsEnabled = true; }
            // Preserve the failed attempt, just as failed queue jobs remain after a retry.
            RefreshJobs();
        }
    }
    public void Report(string message) { Status.Text = message; Status.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible; }
    internal void ShowJobs() { ShowWorkspace(); JobsPanel.Visibility = Visibility.Visible; JobsPanel.IsExpanded = true; JobsPanel.BringIntoView(); }
    private void WorkspaceResized(object sender, SizeChangedEventArgs e)
    {
        if (OperationColumn is null) return;
        ApplyTextScale(TryFindResource("TextScaleFactor") is double scale ? scale : 1);
    }
    internal void ApplyTextScale(double scale)
    {
        if (OperationColumn is null) return;
        bool stackedLauncher = scale > 1.25;
        Grid.SetRow(LauncherSettingsButton, stackedLauncher ? 1 : 0);
        Grid.SetColumn(LauncherSettingsButton, stackedLauncher ? 0 : 1);
        Grid.SetColumnSpan(LauncherWorkspaceButton, stackedLauncher ? 2 : 1);
        Grid.SetColumnSpan(LauncherSettingsButton, stackedLauncher ? 2 : 1);
        LauncherWorkspaceButton.HorizontalAlignment = stackedLauncher ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        bool large = scale > 1.25 || Width < 780;
        OperationColumn.Width = large ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(CenterPanel, large ? 0 : 1); Grid.SetRow(CenterPanel, large ? 1 : 0);
        FilesPanel.Margin = large ? new Thickness(0, 0, 0, 20) : new Thickness(0, 0, 24, 0);
        FilesPanel.Height = large ? 420 * scale / 2 : double.NaN;
        CenterPanel.Height = large ? 360 * scale / 2 : double.NaN;
        EmptyState.MinHeight = large ? 360 : 0;
        // Secondary content shares the body scroll; it cannot spend the footer's client area.
        WorkspaceGrid.Height = large ? double.NaN : Math.Max(300, WorkspaceScroll.ActualHeight);
        // At the supported minimum width these pairs still fit side by side at 200% text.
        // Avoid adding extra Auto rows above and below the useful body viewport.
        Grid.SetRow(HeaderActions, 0); Grid.SetColumn(HeaderActions, 1);
        Grid.SetColumnSpan(HeaderActions, 1); HeaderActions.Margin = new Thickness(0);
        Grid.SetRow(RunButton, 0); Grid.SetColumn(RunButton, 1); Grid.SetColumnSpan(RunButton, 1);
        RunButton.HorizontalAlignment = HorizontalAlignment.Right; RunButton.Margin = new Thickness(0);
        Grid.SetColumnSpan(DestinationSummary, 1);
    }

    private void ShowDemo(object sender, RoutedEventArgs e)
    {
        if (demo is not null) { demo.Activate(); return; }
        demo = new() { Owner = this };
        app.AttachPresentation(demo, demo.Content as Citrus.Windows.Presentation.MaterialSurface);
        demo.Closed += (_, _) => demo = null;
        demo.Show();
    }
    private void StopPreview()
    {
        ++previewGeneration;
        previewCancellation?.Cancel(); previewCancellation?.Dispose(); previewCancellation = null;
        PreviewImage.Source = null;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopPreview();
        selection.MembershipChanged -= MembershipChanged;
        demo?.Close(); demo = null;
    }
}

public sealed record SubmissionFailure(string Message, Func<Task>? Retry) { public bool CanRetry => Retry is not null; }
