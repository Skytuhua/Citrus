using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Citrus.Conversion;
using Citrus.Conversion.Editing;
using Citrus.Conversion.Editing.Image;
using Citrus.Core;
using Citrus.Core.Editing;
using Citrus.Windows.Editors.Image;
using Citrus.Windows.Presentation;
using ImageMagick;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class ImageMetadataWindowReferenceTests(DesktopFixture desktop)
{
    [Fact]
    public Task Native_Artist_edit_submits_one_real_metadata_job_and_preserves_source_and_pixels() => desktop.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var queue = new JobQueue([fixture.Engine]);
        var submissions = new EditorSubmissionService(queue, fixture.Engine.Routes, fixture.Engine);
        var batch = await Show(fixture, window =>
        {
            var save = Action(window, "Save");
            Assert.False(save.IsEnabled);
            var artist = Field(window, "Artist");
            Assert.False(artist.IsReadOnly);
            Assert.Equal("Before", artist.Text);
            artist.Text = "Native artist";
            Assert.True(save.IsEnabled);
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });

        Assert.NotNull(batch);
        Assert.Single(batch.Items);
        var recipe = Assert.IsType<ImageRecipe>(batch.Items[0].Document.Recipe);
        var edit = Assert.Single(recipe.MetadataEdits);
        Assert.Equal("Artist", edit.Key);
        Assert.Equal(MetadataEditAction.Set, edit.Action);
        Assert.Equal(new TextValue("Native artist"), edit.Value);
        var job = Assert.Single(await submissions.SubmitAsync(batch, default));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(job.State == ConversionJobState.Succeeded, job.Error?.ToString());
        string outputPath = Assert.Single(job.Outputs);
        Assert.NotEqual(fixture.Input, outputPath);

        // Read the encoded EXIF with Magick, independently of the ExifTool writer/verification path.
        using var output = new MagickImage(outputPath);
        Assert.Equal("Native artist", output.GetExifProfile()?.GetValue(ExifTag.Artist)?.Value);
        Assert.Equal("Original copyright", output.GetExifProfile()?.GetValue(ExifTag.Copyright)?.Value);
        Assert.Equal(17u, output.Width);
        Assert.Equal(11u, output.Height);
        using var outputPixels = output.GetPixels();
        Assert.Equal(fixture.Pixels, outputPixels.ToByteArray(PixelMapping.RGBA));
        Assert.Equal(fixture.SourceHash, SHA256.HashData(await File.ReadAllBytesAsync(fixture.Input)));
        Assert.Equal(2, Directory.GetFiles(fixture.Root).Length);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Untouched_Close_or_Escape_returns_no_batch_and_creates_no_job_or_copy(bool escape) => desktop.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var queue = new JobQueue([fixture.Engine]);
        int queueNotifications = 0;
        queue.JobChanged += _ => Interlocked.Increment(ref queueNotifications);
        var batch = await Show(fixture, window =>
        {
            Assert.False(Action(window, "Save").IsEnabled);
            if (escape)
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            else window.Close();
        });

        Assert.Null(batch);
        Assert.Equal(0, queueNotifications);
        Assert.Equal(new[] { fixture.Input }, Directory.GetFiles(fixture.Root));
        Assert.Equal(fixture.SourceHash, SHA256.HashData(await File.ReadAllBytesAsync(fixture.Input)));
    });

    [Fact]
    public Task Search_filters_real_fields_and_technical_values_have_no_editable_text_box() => desktop.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var batch = await Show(fixture, window =>
        {
            var search = Field(window, "Search metadata");
            search.Text = "Image Width";
            var width = Assert.Single(Descendants(window).OfType<TextBlock>(),
                text => AutomationProperties.GetName(text) == "Image Width (read only)");
            Assert.Equal("17", width.Text);
            Assert.Equal("Read only", width.ToolTip);
            Assert.DoesNotContain(Descendants(window).OfType<TextBox>(),
                box => AutomationProperties.GetName(box).StartsWith("Image Width", StringComparison.Ordinal));
            Assert.DoesNotContain(Descendants(window).OfType<TextBox>(),
                box => AutomationProperties.GetName(box) == "Artist");

            search.Text = "no metadata field can match this fixture";
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "No matching metadata.");
            search.Text = "artist";
            Assert.Equal("Before", Field(window, "Artist").Text);
            Assert.False(Action(window, "Save").IsEnabled);
            window.Close();
        });
        Assert.Null(batch);
        Assert.Equal(new[] { fixture.Input }, Directory.GetFiles(fixture.Root));
    });

    [Fact]
    public Task Cancellation_during_loading_awaits_source_work_and_removes_its_private_snapshot() => desktop.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var cancel = new CancellationTokenSource();
        var sources = new CancellableCapture(Path.Combine(fixture.Root, "loading-copy"));
        var window = new ImageMetadataWindow(fixture.Metadata, sources);
        Task? checks = null;
        window.Loaded += (_, _) => checks = CancelLoading();
        var batch = await window.ShowEditorAsync(fixture.Request, cancel.Token);
        await checks!;

        Assert.Null(batch);
        Assert.True(sources.Finished.Task.IsCompletedSuccessfully, "ShowEditorAsync returned before loading cleanup finished.");
        Assert.True(sources.CancellationObserved);
        Assert.False(Directory.Exists(sources.PrivateDirectory));
        Assert.False(window.IsVisible);
        Assert.Equal(new[] { fixture.Input }, Directory.GetFiles(fixture.Root));
        Assert.Equal(fixture.SourceHash, SHA256.HashData(await File.ReadAllBytesAsync(fixture.Input)));

        async Task CancelLoading()
        {
            try
            {
                await sources.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(Action(window, "Save").IsEnabled);
                Assert.True(Directory.Exists(sources.PrivateDirectory));
                Assert.DoesNotContain(Descendants(window).OfType<TextBox>(), box => AutomationProperties.GetName(box) == "Artist");
            }
            finally { if (window.IsVisible) cancel.Cancel(); }
        }
    });

    [Theory]
    [InlineData(ThemeChoice.Light)]
    [InlineData(ThemeChoice.Dark)]
    public Task Real_metadata_window_keeps_white_chrome_and_captures_default_and_minimum_layouts(ThemeChoice theme) => desktop.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var batch = await Show(fixture, window =>
        {
            var surface = Assert.IsType<MaterialSurface>(window.Content);
            Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(surface.EffectiveTint).Color);
            Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(window.FindResource("WindowBackground")).Color);
            var save = Action(window, "Save");
            Assert.Same(Application.Current.FindResource("PrimaryButton"), save.Style);
            Assert.False(save.IsEnabled);
            foreach (string label in new[] { "Remove All", "Save location…" })
                Assert.Same(Application.Current.FindResource("SecondaryButton"), Action(window, label).Style);

            Capture("default");
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            window.UpdateLayout();
            VisualArtifacts.AssertInside(Field(window, "Search metadata"), surface);
            foreach (string label in new[] { "Save", "Remove All", "Save location…" })
                VisualArtifacts.AssertInside(Action(window, label), surface);
            Capture("minimum");
            window.Close();

            void Capture(string size)
            {
                foreach (double scale in new[] { 1d, 1.5, 2d })
                {
                    string name = $"image-metadata-reference-{size}-{theme.ToString().ToLowerInvariant()}-{scale.ToString("0.0", CultureInfo.InvariantCulture)}";
                    var image = VisualArtifacts.Capture(surface, name, scale, theme);
                    Assert.Equal(Colors.White, Pixel(image, (int)(12 * scale), image.PixelHeight / 2));
                }
            }
        }, theme);
        Assert.Null(batch);
    });

    private static async Task<EditorSubmissionBatch?> Show(Fixture fixture, Action<ImageMetadataWindow> exercise, ThemeChoice theme = ThemeChoice.Light)
    {
        var app = Application.Current;
        var savedResources = app.Resources.Keys.Cast<object>().ToDictionary(key => key, key => app.Resources[key]);
        using var themes = new ThemeManager(app);
        themes.ApplyPalette(theme, true, false);
        using var presentation = new WindowPresentationService(app, themes, () => new CitrusSettings { ReduceMotion = true });
        using var cancel = new CancellationTokenSource();
        var window = new ImageMetadataWindow(fixture.Engine);
        presentation.Attach(window, (MaterialSurface)window.Content);
        Task? checks = null;
        window.Loaded += (_, _) => checks = ExerciseWhenReady();
        try
        {
            var batch = await window.ShowEditorAsync(fixture.Request, cancel.Token);
            Assert.NotNull(checks);
            await checks;
            return batch;
        }
        finally
        {
            foreach (object key in app.Resources.Keys.Cast<object>().ToArray()) app.Resources.Remove(key);
            foreach (var pair in savedResources) app.Resources[pair.Key] = pair.Value;
        }

        async Task ExerciseWhenReady()
        {
            try
            {
                for (int attempt = 0; attempt < 400; attempt++)
                {
                    if (Descendants(window).OfType<TextBox>().Any(box => AutomationProperties.GetName(box) == "Artist"))
                    {
                        exercise(window);
                        return;
                    }
                    await Task.Delay(20);
                }
                throw new TimeoutException("The real metadata window did not load the fixture's Artist field.");
            }
            finally { if (window.IsVisible) cancel.Cancel(); }
        }
    }

    private static Button Action(DependencyObject root, string label) =>
        Descendants(root).OfType<Button>().Single(button => Equals(button.Content, label));

    private static TextBox Field(DependencyObject root, string name) =>
        Descendants(root).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == name);

    private static Color Pixel(BitmapSource image, int x, int y)
    {
        byte[] pixel = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private sealed class CancellableCapture(string privateDirectory) : ISourceSnapshotService
    {
        private readonly SourceSnapshotService actual = new();
        public string PrivateDirectory { get; } = privateDirectory;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }

        public async Task<ImmutableArray<SourceCaptureResult>> CaptureAsync(ImmutableArray<string> paths, CancellationToken ct)
        {
            try
            {
                var captured = await actual.CaptureAsync(paths, ct);
                var snapshot = Assert.Single(captured).Snapshot;
                Assert.NotNull(snapshot);
                await actual.StageVerifiedAsync([snapshot], PrivateDirectory, ct);
                Started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return captured;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
            finally
            {
                // Yield once so returning from the modal window cannot stand in for awaiting cleanup.
                await Task.Yield();
                if (Directory.Exists(PrivateDirectory)) Directory.Delete(PrivateDirectory, true);
                Finished.TrySetResult();
            }
        }

        public Task<ImmutableDictionary<Guid, string>> StageVerifiedAsync(ImmutableArray<SourceSnapshot> snapshots, string directory, CancellationToken ct) =>
            actual.StageVerifiedAsync(snapshots, directory, ct);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "citrus-native-metadata-" + Guid.NewGuid().ToString("N"));
        public string Input { get; }
        public byte[] SourceHash { get; }
        public byte[] Pixels { get; }
        public ExifToolMetadataService Metadata { get; }
        public ImageMetadataEngine Engine { get; }
        public EditorOpenRequest Request { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Input = Path.Combine(Root, "original.png");
            var pixels = new byte[17 * 11 * 4];
            for (int y = 0; y < 11; y++)
                for (int x = 0; x < 17; x++)
                {
                    int offset = (y * 17 + x) * 4;
                    pixels[offset] = (byte)(x * 13);
                    pixels[offset + 1] = (byte)(y * 19);
                    pixels[offset + 2] = (byte)((x + y) * 7);
                    pixels[offset + 3] = 255;
                }
            using (var image = new MagickImage(pixels, new PixelReadSettings(17, 11, StorageType.Char, PixelMapping.RGBA)))
            {
                var exif = new ExifProfile();
                exif.SetValue(ExifTag.Artist, "Before");
                exif.SetValue(ExifTag.Copyright, "Original copyright");
                image.SetProfile(exif);
                image.Write(Input, MagickFormat.Png);
            }
            SourceHash = SHA256.HashData(File.ReadAllBytes(Input));
            using (var original = new MagickImage(Input))
            using (var decoded = original.GetPixels())
                Pixels = decoded.ToByteArray(PixelMapping.RGBA)!;
            Metadata = new(Path.Combine(AppContext.BaseDirectory, "image", "exiftool", "exiftool.exe"));
            Assert.True(Metadata.Available, "The copied offline ExifTool feature pack is required for native metadata acceptance.");
            Engine = new(new OutputWriter(), Metadata, new SourceSnapshotService());
            var operation = Assert.Single(Engine.Operations);
            Assert.True(Engine.Routes.TryGet(operation.Id, out var route));
            Request = new(operation, [Input], route.EntryTool, new(), Root, SubmissionOrigin.Home);
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
