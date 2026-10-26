using System.Windows.Input;
using System.Windows.Threading;
using Citrus.Core;

namespace Citrus.Windows;

public partial class ShelfWindow : Window, IDisposable
{
    private readonly ShelfReferences references = new();
    private readonly DispatcherTimer timer;
    private Point dragStart;
    private bool disposed, closed;
    public IReadOnlyList<string> Paths => references.Items.ToArray();
    public ShelfWindow()
    {
        InitializeComponent();
        Citrus.Windows.Presentation.WindowChromeSetup.Apply(this);
        References.ItemsSource = references.Items;
        timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, RefreshOnTimer, Dispatcher);
        Closed += (_, _) => { closed = true; Dispose(); };
        (Application.Current as App)?.AttachPresentation(this, Chrome);
    }
    public void AddPaths(IReadOnlyList<string> paths)
    {
        foreach (var path in FileActions.Validate(paths))
            references.Add(path);
    }
    public void RefreshReferences()
    {
        references.ValidateSelection(references.Items.ToArray());
    }
    private async void RefreshOnTimer(object? sender, EventArgs e)
    {
        try { await references.RefreshAsync(); }
        catch (OperationCanceledException) when (disposed) { }
    }
    private string[] Selected() => References.SelectedItems.Cast<string>().ToArray();
    private void DragStarted(object sender, MouseButtonEventArgs e) => dragStart = e.GetPosition(this);
    private void DragMoved(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(current.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var paths = references.ValidateSelection(Selected());
        if (paths.Length == 0) return;
        try { DragDrop.DoDragDrop(References, FileActions.CreateCopyData(paths), DragDropEffects.Copy); }
        catch (IOException error) { MessageBox.Show(this, error.Message, "Citrus shelf"); }
    }
    private async void CopySelected(object sender, RoutedEventArgs e)
    {
        if (references.ValidateSelection(Selected()) is not { Length: > 0 } paths) return;
        try { await new FileActions().ExecuteAsync("action.copy", paths); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Citrus shelf"); }
    }
    private void RemoveSelected(object sender, RoutedEventArgs e) { foreach (string path in Selected()) references.Remove(path); }
    private void ShelfKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { RemoveSelected(sender, e); e.Handled = true; }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { CopySelected(sender, e); e.Handled = true; }
    }
    public void Dispose() { if (disposed) return; disposed = true; timer.Stop(); references.Dispose(); if (!closed) Close(); }
}
