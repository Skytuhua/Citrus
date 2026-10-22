using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Automation;
using System.Windows.Controls;
using Citrus.Core;
using Microsoft.Win32;

namespace Citrus.Windows;

public partial class ToolOptionsWindow : Window
{
    private readonly SubmissionDraft draft;
    private readonly ObservableCollection<string> ordered;
    private readonly Dictionary<string, TextBox> fields = [];
    private CheckBox? aspect;
    private ComboBox? rotation;
    private string? destination;
    public SubmissionChoice? Choice { get; private set; }

    public ToolOptionsWindow(SubmissionDraft draft)
    {
        if(draft.Operation.Mode==OperationMode.Tools&&!draft.Operation.IsGroup&&draft.Operation.Id.StartsWith("video.",StringComparison.Ordinal))
            throw new ArgumentException("This tool requires the visual video editor. Reopen the tool after the media engine is available.",nameof(draft));
        this.draft = draft;
        ordered = new(draft.Inputs);
        destination = draft.DestinationDirectory;
        InitializeComponent();
        Citrus.Windows.Presentation.WindowChromeSetup.Apply(this);
        Heading.Text = draft.Operation.Label;
        InputSummary.Text = $"{draft.Inputs.Count} included file(s) · {draft.Operation.OutputExtension?.TrimStart('.').ToUpperInvariant() ?? "new"} output";
        OperationDescription.Text = OperationCard.Describe(draft.Operation);
        OperationDescription.Visibility = string.IsNullOrEmpty(OperationDescription.Text) ? Visibility.Collapsed : Visibility.Visible;
        Loaded += (_, _) => { if (fields.Values.FirstOrDefault() is { } first) first.Focus(); else CreateButton.Focus(); };
        Title = draft.Operation.Label + " · Citrus";
        CreateButton.Content = draft.Operation.IsGroup ? "Create group copy" : "Create copies";
        if (draft.Operation.IsGroup)
        {
            GroupPanel.Visibility = Visibility.Visible;
            OrderedInputs.ItemsSource = ordered;
            OrderedInputs.SelectedIndex = 0;
        }
        var options = draft.Options;
        string id = draft.Operation.Id;
        if (id == "image.resize")
        {
            Field("Width", "Width in pixels (optional)", options.Width?.ToString(CultureInfo.InvariantCulture) ?? "");
            Field("Height", "Height in pixels (optional)", options.Height?.ToString(CultureInfo.InvariantCulture) ?? "");
            aspect = new CheckBox { Content = "Preserve aspect ratio", IsChecked = options.PreserveAspectRatio, Margin = new(0, 10, 0, 0) };
            Fields.Children.Add(aspect);
        }
        if (id == "image.compress") Field("Quality", "Quality · 1–100", Number(options.Quality));
        if (id == "image.rotate")
        {
            Fields.Children.Add(new TextBlock { Text = "Clockwise rotation", Margin = new(0, 6, 0, 6) });
            rotation = new ComboBox { ItemsSource = new[] { 90, 180, 270 }, SelectedItem = options.RotationDegrees };
            AutomationProperties.SetName(rotation, "Clockwise rotation in degrees");
            Fields.Children.Add(rotation);
        }
        if (id == "audio.trim")
        {
            Field("Start", "Start in seconds", Number(options.StartSeconds));
            Field("End", "End in seconds (blank = input end)", options.EndSeconds is double end ? Number(end) : "");
        }
        if (id == "audio.normalize") Field("Lufs", "Normalization · -70 to -5 LUFS", Number(options.NormalizeLufs));
        if (id is "pdf.split" or "pdf.images" or "pdf.text") Field("Pages", "Pages · blank = all; example 1,3-5", options.PageSelection ?? "");
        InspectionSummary.Text = string.Join("\n", draft.Inspections.Select(i => Path.GetFileName(i.Path) + ": " +
            (i.Error is not null ? "Failed: " + i.Error : i.PageCount is int pages ? $"{pages} pages" : $"{i.DurationSeconds:0.###} seconds")));
        UpdateDestination();
        (Application.Current as App)?.AttachPresentation(this, Chrome);
    }

    private static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);
    private void Field(string key, string label, string value)
    {
        var box = new TextBox { Text = value, Margin = new(0, 0, 0, 8), Padding = new(8) };
        AutomationProperties.SetName(box, label);
        RegisterName(key + "Input", box);
        Fields.Children.Add(new Label { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, Target = box, Padding = new(0, 6, 0, 6) });
        Fields.Children.Add(box);
        fields.Add(key, box);
        box.TextChanged += (_, _) => ErrorText.Text = "";
    }

    private void OrderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MoveUpButton is null) return;
        MoveUpButton.IsEnabled = OrderedInputs.SelectedIndex > 0;
        MoveDownButton.IsEnabled = OrderedInputs.SelectedIndex >= 0 && OrderedInputs.SelectedIndex < ordered.Count - 1;
    }
    private void MoveUp(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown(object sender, RoutedEventArgs e) => Move(1);
    private void Move(int direction)
    {
        int index = OrderedInputs.SelectedIndex;
        if (index < 0 || index + direction < 0 || index + direction >= ordered.Count) return;
        ordered.Move(index, index + direction);
        OrderedInputs.SelectedIndex = index + direction;
        UpdateDestination();
    }
    private void UpdateDestination() => DestinationText.Text = destination ??
        (draft.Operation.IsGroup ? BatchRequestBuilder.RequiresDestination(ordered)
            ? "Files are in different folders. Choose a destination."
            : Path.GetDirectoryName(ordered[0]) : "Beside each original");

    private void ChooseFolder(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose destination for new copies", InitialDirectory = destination ?? Path.GetDirectoryName(ordered[0])! };
        if (picker.ShowDialog(this) == true) { destination = picker.FolderName; UpdateDestination(); }
    }
    private void Cancel(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Create(object sender, RoutedEventArgs e)
    {
        try
        {
            string? Text(string key) => fields.TryGetValue(key, out var box) ? box.Text : null;
            int? Integer(string key)
            {
                if (string.IsNullOrWhiteSpace(Text(key))) return null;
                if (int.TryParse(Text(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) return value;
                fields[key].Focus(); fields[key].SelectAll();
                throw new ArgumentException(key + ": enter a whole number within the displayed bounds.");
            }
            double Real(string key, double fallback)
            {
                if (Text(key) is not string text) return fallback;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)) return value;
                fields[key].Focus(); fields[key].SelectAll();
                throw new ArgumentException(key + ": enter a finite number using a decimal point.");
            }
            var options = draft.Options with
            {
                Width = fields.ContainsKey("Width") ? Integer("Width") : draft.Options.Width, Height = fields.ContainsKey("Height") ? Integer("Height") : draft.Options.Height,
                Quality = Integer("Quality") ?? draft.Options.Quality, PreserveAspectRatio = aspect?.IsChecked ?? draft.Options.PreserveAspectRatio,
                RotationDegrees = (int?)rotation?.SelectedItem ?? draft.Options.RotationDegrees,
                StartSeconds = Real("Start", draft.Options.StartSeconds), EndSeconds = fields.ContainsKey("End") ? string.IsNullOrWhiteSpace(Text("End")) ? null : Real("End", 0) : draft.Options.EndSeconds,
                Speed = Real("Speed", draft.Options.Speed), NormalizeLufs = Real("Lufs", draft.Options.NormalizeLufs), PageSelection = Text("Pages") ?? draft.Options.PageSelection
            };
            string? error = OperationOptionsValidator.GetError(draft.Operation, options, draft.Inspections);
            if (error is not null) throw new ArgumentException(error);
            // Same builder as submission: mixed-folder groups cannot bypass destination selection.
            BatchRequestBuilder.Build(draft.Operation, ordered, options, destination);
            Choice = new(Array.AsReadOnly(ordered.ToArray()), options, destination);
            DialogResult = true;
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
        {
            ErrorText.Text = error is FormatException or OverflowException ? "Enter valid numbers using a decimal point; values must fit the displayed bounds." : error.Message;
        }
    }
}
