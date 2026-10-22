using Citrus.Core;
using Citrus.Windows.Updates;

namespace Citrus.Windows;

public partial class SettingsWindow : Window
{
    private readonly Func<CitrusSettings, Task> apply;
    private readonly Func<Task>? checkUpdates, restartUpdate;
    private readonly Action? openReleases;
    private bool saving;
    public SettingsWindow(CitrusSettings settings, Func<CitrusSettings, Task> apply,
        Func<Task>? checkUpdates = null, Func<Task>? restartUpdate = null, Action? openReleases = null)
    {
        this.apply = apply;
        this.checkUpdates = checkUpdates; this.restartUpdate = restartUpdate; this.openReleases = openReleases;
        InitializeComponent();
        Citrus.Windows.Presentation.WindowChromeSetup.Apply(this);
        Theme.ItemsSource = Enum.GetValues<ThemeChoice>();
        Shift.ItemsSource = Alt.ItemsSource = Control.ItemsSource = Enum.GetValues<OperationMode>();
        Sound.IsChecked = settings.SoundEnabled;
        Motion.IsChecked = settings.ReduceMotion;
        Transparency.IsChecked = settings.ReduceTransparency;
        Startup.IsChecked = settings.LaunchAtSignIn;
        AutomaticUpdates.IsChecked = settings.AutomaticUpdates;
        CheckUpdates.IsEnabled = checkUpdates is not null;
        OpenReleases.IsEnabled = openReleases is not null;
        UpdateStatus.Text = "Install Citrus to check for updates.";
        Reveal.IsChecked = settings.RevealCompletedOutputs;
        Gestures.IsChecked = settings.GesturesEnabled;
        Theme.SelectedItem = settings.Theme;
        Shift.SelectedItem = settings.ShiftMode;
        Alt.SelectedItem = settings.AltMode;
        Control.SelectedItem = settings.ControlMode;
        (Application.Current as App)?.AttachPresentation(this, Chrome);
    }
    private void Cancel(object sender, RoutedEventArgs e) => Close();
    internal void SetUpdateStatus(AutomaticUpdateStatus status)
    {
        UpdateStatus.Text = status.Message;
        CheckUpdates.IsEnabled = checkUpdates is not null && !status.Checking;
        RestartUpdate.Visibility = status.ReadyVersion is not null && restartUpdate is not null ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void CheckForUpdates(object sender, RoutedEventArgs e) { if (checkUpdates is not null) await checkUpdates(); }
    private async void RestartForUpdate(object sender, RoutedEventArgs e) { if (restartUpdate is not null) await restartUpdate(); }
    private void OpenGitHubReleases(object sender, RoutedEventArgs e) => openReleases?.Invoke();
    private async void Save(object sender, RoutedEventArgs e)
    {
        if (saving) return;
        saving = true;
        SaveSettingsButton.IsEnabled = false;
        SaveSettingsButton.Content = "Saving…";
        Error.Text = "";
        try
        {
            var settings = new CitrusSettings { SoundEnabled = Sound.IsChecked == true, ReduceMotion = Motion.IsChecked == true, ReduceTransparency = Transparency.IsChecked == true, LaunchAtSignIn = Startup.IsChecked == true, RevealCompletedOutputs = Reveal.IsChecked == true,
                AutomaticUpdates = AutomaticUpdates.IsChecked == true,
                GesturesEnabled = Gestures.IsChecked == true, Theme = (ThemeChoice)Theme.SelectedItem,
                ShiftMode = (OperationMode)Shift.SelectedItem, AltMode = (OperationMode)Alt.SelectedItem, ControlMode = (OperationMode)Control.SelectedItem };
            settings.Validate();
            await apply(settings);
            Close();
        }
        catch (Exception error) { Error.Text = error.Message; }
        finally
        {
            saving = false;
            SaveSettingsButton.IsEnabled = true;
            SaveSettingsButton.Content = "Save";
        }
    }
}
