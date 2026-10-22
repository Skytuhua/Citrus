using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class MenuRefinementTests(DesktopFixture desktop)
{
    [Fact]
    public Task Settings_scrolls_to_the_last_control_without_a_visible_scrollbar() => desktop.RunAsync(async () =>
    {
        var window = new SettingsWindow(new(), _ => Task.CompletedTask) { ShowActivated = false, Height = 440 };
        try
        {
            window.Show(); window.UpdateLayout();
            var scroll = Descendants(window).OfType<ScrollViewer>().First();
            Assert.Equal(Visibility.Collapsed, scroll.ComputedVerticalScrollBarVisibility);
            Assert.True(scroll.ScrollableHeight > 0);
            scroll.ScrollToEnd(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(scroll.VerticalOffset > 0);
            var last = (FrameworkElement)window.FindName("Control");
            var bounds = last.TransformToAncestor(scroll).TransformBounds(new Rect(last.RenderSize));
            Assert.InRange(bounds.Bottom, 0, scroll.ActualHeight + 1);
            Capture(window, "settings-scrolled-end");
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Large_text_launcher_keeps_footer_labels_inside_their_buttons() => desktop.RunAsync(async () =>
    {
        using var theme = new ThemeManager(Application.Current);
        theme.ApplyTypography(2);
        using var home = new MainWindow((App)Application.Current, startInLauncher: true) { ShowActivated = false };
        try
        {
            home.Show(); home.Width = home.MinWidth; home.Height = home.MinHeight;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); home.UpdateLayout();
            var root = (FrameworkElement)home.FindName("LauncherRoot");
            var scroll = (ScrollViewer)home.FindName("LauncherScroll");
            var addFiles = Descendants(root).OfType<Button>().Single(button => Equals(button.Content, "Add files…"));
            var primaryBounds = addFiles.TransformToAncestor(scroll).TransformBounds(new Rect(addFiles.RenderSize));
            Assert.True(primaryBounds.Bottom <= scroll.ActualHeight + 1, "The primary file action should fit the initial large-text viewport.");
            var buttons = Descendants(root).OfType<Button>().Where(button => Equals(button.Content, "Open workspace") || Equals(button.Content, "Settings")).ToArray();
            Assert.Equal(2, buttons.Length);
            foreach (var button in buttons)
            {
                var label = Descendants(button).OfType<TextBlock>().Single();
                var bounds = label.TransformToAncestor(button).TransformBounds(new Rect(label.RenderSize));
                Assert.True(bounds.Left >= 0 && bounds.Right <= button.ActualWidth + 1, $"{button.Content} label clips at large text: {bounds} in {button.ActualWidth}.");
                var placement = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                Assert.True(placement.Left >= 0 && placement.Right <= root.ActualWidth + 1);
            }
            Capture(home, "launcher-minimum-text200");
        }
        finally { home.Dispose(); home.Close(); theme.ApplyTypography(1); }
    });

    [Fact]
    public Task Pending_save_accepts_one_request_and_restores_the_action_after_failure() => desktop.RunAsync(async () =>
    {
        int requests = 0;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new SettingsWindow(new(), _ => { requests++; return pending.Task; }) { ShowActivated = false };
        try
        {
            window.Show(); window.UpdateLayout();
            var save = Descendants(window).OfType<Button>().Single(button => Equals(button.Content, "Save"));
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, requests);
            Assert.False(save.IsEnabled);
            pending.TrySetException(new InvalidOperationException("Expected save failure."));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(save.IsEnabled);
            Assert.Equal("Save", save.Content);
            Assert.Equal("Expected save failure.", ((TextBlock)window.FindName("Error")).Text);
        }
        finally { pending.TrySetResult(); window.Close(); }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var item in Descendants(child)) yield return item;
        }
    }

    private static void Capture(Window window, string name)
    {
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        string directory = System.IO.Path.Combine(AppContext.BaseDirectory, "TestResults", "menu-polish");
        System.IO.Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(System.IO.Path.Combine(directory, name + ".png")); encoder.Save(file);
    }
}
