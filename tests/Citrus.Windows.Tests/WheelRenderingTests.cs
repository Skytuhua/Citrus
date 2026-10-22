using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Citrus.Conversion;
using Citrus.Core;
using Citrus.Windows.Presentation;
using Application = System.Windows.Application;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using ShapePath = System.Windows.Shapes.Path;
using Size = System.Windows.Size;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class WheelRenderingTests(DesktopFixture desktop)
{
    [Theory]
    [InlineData(368, 400)]
    [InlineData(300, 454)]
    public Task Short_client_keeps_paging_visible_and_hoverable(double width, double height) => desktop.RunAsync(async () =>
    {
        using var fixture = new RenderFixture();
        await fixture.Enter([fixture.Sample]);
        fixture.Wheel.Width = width; fixture.Wheel.Height = height;
        fixture.Wheel.UpdateLayout();
        var presentation = (FrameworkElement)fixture.Wheel.FindName("Presentation");
        Point Local(Point point) => presentation.TransformToAncestor(fixture.Surface).Transform(point);
        fixture.Hover(Local(new Point(184, 184)));
        fixture.Hover(Local(new Point(184, 62)));
        fixture.Wheel.UpdateLayout();
        Assert.Equal("WEBP", fixture.Text("Caption"));
        Assert.Null(fixture.Wheel.FindName("ActionPlate"));
        Assert.Null(fixture.Wheel.FindName("ActionDescription"));
        foreach (string name in new[] { "Previous", "Next", "CaptionPlate" })
            VisualArtifacts.AssertInside((FrameworkElement)fixture.Wheel.FindName(name), fixture.Surface);
        var next = (FrameworkElement)fixture.Wheel.FindName("Next");
        fixture.Hover(next.TransformToAncestor(fixture.Surface).Transform(new Point(next.ActualWidth / 2, next.ActualHeight / 2)));
        await fixture.WaitFor(() => fixture.Text("Hint").StartsWith("2 /"), "fitted next-page hover");
    });

    [Theory]
    [InlineData(ThemeChoice.Light)]
    [InlineData(ThemeChoice.Dark)]
    public Task Center_caption_preserves_contrast_and_layout_on_matching_wallpaper(ThemeChoice choice) => desktop.RunAsync(async () =>
    {
        using var theme = new ThemeManager(Application.Current);
        try
        {
            theme.Apply(choice, true);
            using var fixture = new RenderFixture();
            await fixture.Enter([fixture.Sample], OperationMode.Actions);
            fixture.Hover(new Point(184,184)); fixture.Hover(new Point(184,62));
            fixture.Wheel.UpdateLayout();
            Assert.Null(fixture.Wheel.FindName("DescriptionBacking"));
            Assert.Null(fixture.Wheel.FindName("ActionDescription"));
            Assert.Null(fixture.Wheel.FindName("ActionPlate"));
            var caption = (TextBlock)fixture.Wheel.FindName("Caption");
            var plate = (Border)fixture.Wheel.FindName("CaptionPlate");
            Assert.Equal("COPY", caption.Text);
            Assert.Equal(WheelPresentationMetrics.HubCapsuleWidth, plate.ActualWidth);
            Assert.Equal(48, plate.ActualHeight);
            VisualArtifacts.AssertInside(plate, fixture.Surface);
            caption.Measure(new Size(plate.ActualWidth, double.PositiveInfinity));
            Assert.True(caption.DesiredSize.Height <= plate.ActualHeight + 1, "Selected caption must remain inside the hub.");
            var foreground = Assert.IsType<SolidColorBrush>(caption.Foreground).Color;
            var wallpaper = Assert.IsType<SolidColorBrush>(fixture.Wheel.FindResource("SecondaryText"));
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(wallpaper, null, new Rect(0,0,368,454));
                drawing.DrawRectangle(new VisualBrush(fixture.Surface), null, new Rect(0,0,368,454));
            }
            var bitmap = new RenderTargetBitmap(368,454,96,96,PixelFormats.Pbgra32); bitmap.Render(visual);
            var sample = plate.TransformToAncestor(fixture.Surface).Transform(new Point(5, plate.ActualHeight / 2));
            var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect((int)sample.X,(int)sample.Y,1,1),pixel,4,0);
            Assert.Equal(255, pixel[3]);
            double foregroundLuminance = Luminance(foreground.R, foreground.G, foreground.B);
            double backgroundLuminance = Luminance(pixel[2], pixel[1], pixel[0]);
            double contrast = (Math.Max(foregroundLuminance, backgroundLuminance) + .05) /
                (Math.Min(foregroundLuminance, backgroundLuminance) + .05);
            Assert.True(contrast >= 4.5, $"{choice}: selected center caption contrast is {contrast:F2}:1.");

            static double Luminance(byte red, byte green, byte blue)
            {
                static double Linear(byte channel)
                {
                    double value = channel / 255d;
                    return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
                }
                return .2126 * Linear(red) + .7152 * Linear(green) + .0722 * Linear(blue);
            }
        }
        finally { theme.Apply(ThemeChoice.Light,true); }
    });

    [Fact]
    public Task Reference_pdf_and_archive_families_have_actual_count_geometry_and_retained_center_captions() => desktop.RunAsync(async () =>
    {
        using var fixture = new RenderFixture();
        string pdf = Path.Combine(fixture.Root, "reference.pdf");
        using (var document = new PdfSharp.Pdf.PdfDocument()) { document.AddPage(); document.Save(pdf); }
        await fixture.Enter([pdf]);
        Assert.Equal(8, fixture.Petals.Length);
        Assert.Equal(new[] { "pdf.docx", "pdf.pptx", "pdf.xlsx", "pdf.jpg", "pdf.images", "pdf.epub", "pdf.text", "pdf.markdown" }, fixture.OperationIds);
        fixture.Hover(new Point(184, 184)); fixture.Hover(new Point(184, 62));
        fixture.Wheel.UpdateLayout();
        Assert.Equal("DOCX", fixture.Text("Caption"));
        Assert.Null(fixture.Wheel.FindName("ActionDescription"));
        Assert.Equal(WheelPresentationMetrics.HubCapsuleWidth, ((FrameworkElement)fixture.Wheel.FindName("CaptionPlate")).ActualWidth);
        Assert.Equal(48, ((FrameworkElement)fixture.Wheel.FindName("CaptionPlate")).ActualHeight);
        Assert.Null(fixture.Wheel.FindName("ActionPlate"));
        Assert.Equal(372, Canvas.GetTop((UIElement)fixture.Wheel.FindName("NavigationPlate")));
        fixture.Capture("wheel-reference-eight-pdf-formats", 1, ThemeChoice.Light);
        fixture.Wheel.Dismiss();
        string archive = Path.Combine(fixture.Root, "reference.zip");
        using (var zip = new System.IO.Compression.ZipArchive(File.Create(archive), System.IO.Compression.ZipArchiveMode.Create)) { }
        await fixture.Enter([archive], OperationMode.Tools);
        Assert.Single(fixture.Petals);
        Assert.Equal(new[] { "archive.extract" }, fixture.OperationIds);
        fixture.Hover(new Point(184, 184)); fixture.Hover(new Point(184, 62));
        Assert.Equal("EXTRACT", fixture.Text("Caption"));
        Assert.Null(fixture.Wheel.FindName("ActionDescription"));
        Assert.Null(fixture.Wheel.FindName("ActionPlate"));
        Assert.Equal(Visibility.Collapsed, ((UIElement)fixture.Wheel.FindName("NavigationPlate")).Visibility);
        fixture.Capture("wheel-reference-one-tool", 1, ThemeChoice.Light);
    });

    [Theory]
    [InlineData(1d)]
    [InlineData(2.25d)]
    public Task Full_tool_and_file_title_ink_fits_every_eight_choice_position(double scale) => desktop.RunAsync(() =>
    {
        var extra = new[] { ("image.editor", "Adjust"), ("image.crop", "Crop"), ("image.annotate", "Annotate"), ("image.redact", "Manual masks"),
            ("audio.trim", "Trim audio"), ("audio.normalize", "Normalize audio"), ("video.trim", "Trim video"), ("video.resize", "Resize video"),
            ("video.speed", "Change speed"), ("video.audio", "Extract MP3 audio") }
            .Select(item => new OperationDescriptor(item.Item1, item.Item2, OperationMode.Tools, null, false, false));
        var operations = FileActions.Operations.Concat(new ImageEngine().Operations).Concat(new PdfEngine().Operations)
            .Concat(new ArchiveEngine().Operations).Concat(extra).ToArray();
        foreach (var operation in operations)
        for (int index = 0; index < 8; index++)
        {
            var petal = new WheelPetal(operation, index, 8);
            var label = petal.Children.OfType<TextBlock>().Single();
            label.FontFamily = (FontFamily)Application.Current.FindResource("AppFont");
            petal.ApplyTextScale(scale);
            var text = new FormattedText(label.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch), label.FontSize,
                Brushes.Black, VisualTreeHelper.GetDpi(label).PixelsPerDip)
                { MaxTextWidth = label.Width, TextAlignment = TextAlignment.Center, LineHeight = label.LineHeight };
            var outline = ((ShapePath)petal.Children[0]).Data;
            var painted = text.BuildGeometry(new Point(Canvas.GetLeft(label), Canvas.GetTop(label)));
            Assert.True(outline.FillContainsWithDetail(painted) == IntersectionDetail.FullyContains,
                $"{label.Text}, slot {index}, scale {scale}: title ink must remain inside the petal.");
            var icon = petal.Children.OfType<ShapePath>().Last();
            if (icon.Visibility == Visibility.Visible)
            {
                var iconBounds = new RectangleGeometry(new Rect(Canvas.GetLeft(icon), Canvas.GetTop(icon), icon.Width, icon.Height));
                Assert.True(outline.FillContainsWithDetail(iconBounds) == IntersectionDetail.FullyContains,
                    $"{label.Text}, slot {index}, scale {scale}: icon must stay in its petal.");
                Assert.True(text.BuildGeometry(new Point(Canvas.GetLeft(label), Canvas.GetTop(label))).FillContainsWithDetail(iconBounds) == IntersectionDetail.Empty,
                    $"{label.Text}, slot {index}, scale {scale}: icon overlaps title.");
            }
            Assert.Equal((operation.Mode == OperationMode.Convert && !string.IsNullOrEmpty(operation.OutputExtension) ? Math.Min(20.5 * scale, 22) : Math.Min(11.5 * scale, 13)) * WheelPresentationMetrics.LabelCompensation, label.FontSize);
        }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(ThemeChoice.Light, 1d)]
    [InlineData(ThemeChoice.Dark, 2.25d)]
    public Task Every_action_uses_the_same_title_size_and_shows_its_full_name(ThemeChoice theme, double scale) => desktop.RunAsync(async () =>
    {
        using var themes = new ThemeManager(Application.Current);
        themes.Apply(theme, true);
        using var fixture = new RenderFixture();
        try
        {
            await fixture.Enter([fixture.Sample], OperationMode.Actions);
            fixture.Wheel.ApplyMaterialPolicy(new(false, false, "TitleFixture"), theme == ThemeChoice.Dark, scale);
            await fixture.WaitFor(() => !fixture.Wheel.MaterialPresentationPending, "title presentation");
            double expected = Math.Min(11.5 * scale, 13) * WheelPresentationMetrics.LabelCompensation;
            for (int index = 0; index < FileActions.Operations.Count; index++)
            {
                var operation = FileActions.Operations[index];
                var label = fixture.Petals[index].Children.OfType<TextBlock>().Single();
                Assert.Equal(WheelDisplayLabels.Petal(operation), label.Text);
                Assert.Equal(expected, label.FontSize);
                Assert.Equal(TextTrimming.None, label.TextTrimming);
                Assert.Equal(TextWrapping.Wrap, label.TextWrapping);
                label.Measure(new Size(label.Width, double.PositiveInfinity));
                Assert.InRange(label.DesiredSize.Height, 1, 56);
                var ink = new FormattedText(label.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch), label.FontSize,
                    Brushes.Black, VisualTreeHelper.GetDpi(label).PixelsPerDip)
                    { MaxTextWidth = label.Width, TextAlignment = TextAlignment.Center, LineHeight = label.LineHeight };
                var outline = ((ShapePath)fixture.Petals[index].Children[0]).Data;
                Assert.Equal(IntersectionDetail.FullyContains, outline.FillContainsWithDetail(ink.BuildGeometry(new Point(Canvas.GetLeft(label), Canvas.GetTop(label)))));
                fixture.Hover(new Point(184, 184));
                var position = WheelGeometry.At(118, index * 360d / fixture.Petals.Length);
                fixture.Hover(new Point(position.X, position.Y));
                fixture.Wheel.UpdateLayout();
                var caption = (TextBlock)fixture.Wheel.FindName("Caption");
                var plate = (FrameworkElement)fixture.Wheel.FindName("CaptionPlate");
                Assert.Equal(WheelDisplayLabels.Petal(operation), caption.Text);
                Assert.Equal(Math.Min(16 * scale, 18) * WheelPresentationMetrics.LabelCompensation, caption.FontSize);
                Assert.Null(fixture.Wheel.FindName("ActionDescription"));
                Assert.Null(fixture.Wheel.FindName("ActionPlate"));
                Assert.Null(fixture.Wheel.FindName("DescriptionBacking"));
                Assert.Equal(operation.Label, System.Windows.Automation.AutomationProperties.GetName(fixture.Petals[index]));
                Assert.Equal(TextTrimming.None, caption.TextTrimming);
                caption.Measure(new Size(plate.ActualWidth, double.PositiveInfinity));
                Assert.True(caption.DesiredSize.Height <= plate.ActualHeight + 1, $"{operation.Label} exceeds the selected hub.");
                if (operation.Id == "action.move") fixture.Capture($"wheel-full-title-{theme}-text{scale * 100:0}", 1, theme);
            }
            foreach (var operation in new ImageEngine().Operations.Concat(new PdfEngine().Operations).Concat(new ArchiveEngine().Operations))
            {
                var petal = new WheelPetal(operation, 0, 8);
                petal.ApplyTextScale(scale);
                Assert.Equal(operation.Mode == OperationMode.Convert && !string.IsNullOrEmpty(operation.OutputExtension) ? Math.Min(20.5 * scale, 22) * WheelPresentationMetrics.LabelCompensation : expected, petal.Children.OfType<TextBlock>().Single().FontSize);
            }
        }
        finally { themes.Apply(ThemeChoice.Light, true); }
    });

    [Theory]
    [InlineData(ThemeChoice.Light)]
    [InlineData(ThemeChoice.Dark)]
    public Task Hub_keeps_only_the_capsule_and_paging_is_one_connected_control(ThemeChoice choice) => desktop.RunAsync(async () =>
    {
        using var themes = new ThemeManager(Application.Current); themes.Apply(choice, true);
        using var fixture = new RenderFixture();
        try
        {
            await fixture.Enter([fixture.Sample]);
            await fixture.WaitFor(() => fixture.Text("Caption").EndsWith("B"), "payload size loaded");
            var caption = (Border)fixture.Wheel.FindName("CaptionPlate");
            fixture.AssertCapsuleOnly();
            Assert.Equal(160, Canvas.GetTop(caption));
            Assert.Equal(48, caption.Height);
            var navigation = Assert.IsType<Border>(fixture.Wheel.FindName("NavigationPlate"));
            Assert.InRange(navigation.ActualWidth, 160, 220);
            VisualArtifacts.AssertInside((FrameworkElement)fixture.Wheel.FindName("Previous"), navigation);
            VisualArtifacts.AssertInside((FrameworkElement)fixture.Wheel.FindName("Next"), navigation);
            VisualArtifacts.AssertInside((FrameworkElement)fixture.Wheel.FindName("Hint"), navigation);
            fixture.Capture($"wheel-hub-{choice}-neutral", 1, choice);
            fixture.Hover(new Point(184, 184)); fixture.Hover(new Point(184, 62));
            Assert.Equal("WEBP", fixture.Text("Caption"));
            fixture.AssertCapsuleOnly();
            fixture.Capture($"wheel-hub-{choice}-target", 1, choice);
            var next = (FrameworkElement)fixture.Wheel.FindName("Next");
            fixture.Hover(next.TransformToAncestor(fixture.Surface).Transform(new Point(next.ActualWidth / 2, next.ActualHeight / 2)));
            await fixture.WaitFor(() => fixture.Text("Hint").StartsWith("2 /"), "connected next-page target");
            fixture.Capture($"wheel-hub-{choice}-page2", 1, choice);
        }
        finally { themes.Apply(ThemeChoice.Light, true); }
    });

    [Fact]
    public Task Production_wheel_keeps_unclipped_native_companion_disabled() => desktop.RunAsync(async () =>
    {
        using var fixture = new RenderFixture();
        fixture.Wheel.ApplyMaterialPolicy(BackdropControllerTests.Eligible, false, 1);
        await fixture.Enter([fixture.Sample]);
        await fixture.WaitFor(() => !fixture.Wheel.MaterialPresentationPending, "complete wheel presentation");
        Assert.Contains(fixture.Wheel.MaterialState, new[] { BackdropState.Solid, BackdropState.LiveRefractiveGlass });
        Assert.Equal(nint.Zero, fixture.Wheel.CompanionHandle);
    });

    [Theory]
    [InlineData(ThemeChoice.Light, 1d)] [InlineData(ThemeChoice.Light, 1.5d)] [InlineData(ThemeChoice.Light, 2d)]
    [InlineData(ThemeChoice.Dark, 1d)] [InlineData(ThemeChoice.Dark, 1.5d)] [InlineData(ThemeChoice.Dark, 2d)]
    public Task Real_wheel_states_render_with_fixed_bounds_and_clean_reuse(ThemeChoice theme, double scale) => desktop.RunAsync(async () =>
    {
        using var themes = new ThemeManager(Application.Current);
        themes.Apply(theme, reduceMotion: true);
        using var fixture = new RenderFixture();
        string prefix = $"wheel-{theme}-{scale * 100:0}pct-raster";
        try
        {
            await fixture.Enter([fixture.Root]); // Real admitted directory, empty Convert catalog.
            fixture.Capture(prefix + "-empty", scale, theme);
            Assert.Empty(fixture.Petals);
            fixture.AssertCapsuleOnly();

            fixture.Wheel.Dismiss();
            await fixture.Enter([fixture.Sample]);
            await fixture.WaitFor(() => fixture.Text("Caption").EndsWith("B"), "real file size metadata");
            Assert.Equal(5, fixture.Petals.Length);
            Assert.Equal(new[] { "image.convert.webp", "image.convert.tiff", "image.convert.avif", "image.convert.bmp", "images.pdf" }, fixture.OperationIds);
            fixture.Capture(prefix + "-page1", scale, theme);
            var original = fixture.Petals;
            var geometry = original.Select(petal => ((ShapePath)petal.Children[0]).Data).ToArray();
            var bounds = original.Select(VisualTreeHelper.GetDescendantBounds).ToArray();
            fixture.Hover(new Point(184, 184));
            Assert.Equal(DragDropEffects.Copy, fixture.Hover(new Point(184, 62)).Effects);
            fixture.Capture(prefix + "-hover", scale, theme);
            Assert.Equal("WEBP", fixture.Text("Caption"));
            for (int i = 0; i < original.Length; i++)
            {
                Assert.Same(original[i], fixture.Petals[i]);
                Assert.Same(geometry[i], ((ShapePath)fixture.Petals[i].Children[0]).Data);
                Assert.Equal(bounds[i], VisualTreeHelper.GetDescendantBounds(fixture.Petals[i]));
            }
            Assert.True(original[0].ContainsPoint(new Point(184, 62)));
            Assert.DoesNotContain(original, petal => petal.ContainsPoint(new Point(184, 184)));
            Assert.DoesNotContain(original, petal => petal.ContainsPoint(new Point(184, 17)));

            // Use the actual hover-navigation timer, rather than injecting paging state.
            fixture.Hover(new Point(262, 390));
            await fixture.WaitFor(() => fixture.Text("Hint").StartsWith("2 /"), "400 ms page hover");
            fixture.Capture(prefix + "-page2", scale, theme);
            Assert.Equal(2, fixture.Petals.Length);
            Assert.Equal(new[] { "image.convert.jpeg", "image.convert.gif" }, fixture.OperationIds);
            Assert.DoesNotContain(fixture.Petals, petal => original.Contains(petal));
            fixture.Hover(new Point(184, 184));
            fixture.Hover(new Point(184, 62));
            Assert.Equal("JPG", fixture.Text("Caption"));

            fixture.Hover(new Point(262, 390));
            await fixture.WaitFor(() => fixture.Text("Hint").StartsWith("3 /"), "archive-family page hover");
            fixture.Capture(prefix + "-page3", scale, theme);
            Assert.Equal(3, fixture.Petals.Length);
            Assert.Equal(new[] { "archive.zip", "archive.tar", "archive.gzip" }, fixture.OperationIds);
            Assert.DoesNotContain(fixture.Petals, petal => original.Contains(petal));
            fixture.Hover(new Point(184, 184));
            fixture.Hover(new Point(184, 62));
            Assert.Equal("ZIP", fixture.Text("Caption"));

            fixture.Wheel.Dismiss();
            fixture.Wheel.ShowAtPointer(OperationMode.Convert);
            Assert.Equal(0, ((FrameworkElement)fixture.Wheel.FindName("Presentation")).Opacity);
            Assert.Empty(fixture.Petals);
            fixture.AssertCapsuleOnly();
            Assert.Equal("Choose", fixture.Text("Caption"));
            fixture.Wheel.Dismiss();
            await fixture.Enter([fixture.Sample, fixture.Second]);
            await fixture.WaitFor(() => fixture.Text("Caption") == "2 files", "batch size label");
            fixture.Capture(prefix + "-batch", scale, theme);
            Assert.Equal("2 files", fixture.Text("Caption"));
            fixture.AssertCapsuleOnly();
        }
        finally { themes.Apply(ThemeChoice.Light, reduceMotion: true); }
    });

    [Theory]
    [InlineData(1d)] [InlineData(1.25d)] [InlineData(1.5d)]
    [InlineData(1.75d)] [InlineData(2d)] [InlineData(2.25d)]
    public Task Actual_text_policy_keeps_hub_metadata_on_opaque_backing(double textScale) => desktop.RunAsync(async () =>
    {
        using var themes = new ThemeManager(Application.Current);
        try
        {
            foreach (var theme in new[] { ThemeChoice.Light, ThemeChoice.Dark })
            {
                themes.Apply(theme, true);
                using var fixture = new RenderFixture();
                string path = Path.Combine(fixture.Root, "citrus-with-a-long-descriptive-filename-for-preview.png");
                File.Copy(fixture.Sample, path);
                foreach (var mode in new[] { OperationMode.Convert, OperationMode.Actions })
                {
                    await fixture.Enter([path], mode);
                    await fixture.WaitFor(() => fixture.Text("Caption").EndsWith("B"), "real file size metadata");
                    fixture.Wheel.ApplyMaterialPolicy(new(false, false, "TextScaleFixture"), theme == ThemeChoice.Dark, textScale);
                    await fixture.WaitFor(() => !fixture.Wheel.MaterialPresentationPending, "material presentation after text policy");
                    fixture.Wheel.UpdateLayout();
                    VisualArtifacts.Capture(fixture.Surface, $"wheel-hub-{theme}-{mode}-text{textScale * 100:0}-raster100", 1, theme,
                        new { textScale, mode = mode.ToString(), actualPolicyApplied = true, rasterScale = 1 });
                    Assert.Equal(1, ((FrameworkElement)fixture.Wheel.FindName("Presentation")).Opacity);
                    fixture.AssertCapsuleOnly();
                    var visible = new List<Rect> { Bounds((FrameworkElement)fixture.Wheel.FindName("CaptionPlate")) };
                    foreach (string name in new[] { "Badge", "ModeLabel" })
                    {
                        var label = (TextBlock)fixture.Wheel.FindName(name);
                        if (!label.IsVisible) continue;
                        var bounds = Bounds(label);
                        foreach (var corner in new[] { bounds.TopLeft, bounds.TopRight, bounds.BottomLeft, bounds.BottomRight })
                            Assert.True(Math.Pow(corner.X - 184, 2) + Math.Pow(corner.Y - 184, 2) <= 78 * 78,
                                $"{name} escapes the neutral hub at text scale {textScale}: {bounds}");
                        Assert.All(visible, other => Assert.False(bounds.IntersectsWith(other), $"{name} overlaps adjacent hub content."));
                        visible.Add(bounds);
                    }
                    Assert.Contains(Path.GetFileName(path), System.Windows.Automation.AutomationProperties.GetName(fixture.Wheel));
                    Assert.Equal(368, fixture.Surface.ActualWidth); Assert.Equal(454, fixture.Surface.ActualHeight);
                    fixture.Wheel.Dismiss();
                    Rect Bounds(FrameworkElement element) => element.TransformToAncestor(fixture.Surface).TransformBounds(new Rect(new Point(), element.RenderSize));
                }
            }
        }
        finally { themes.Apply(ThemeChoice.Light, true); }
    });

    [Fact]
    public Task Warm_repeated_routed_hover_records_time_allocation_and_retained_identity() => desktop.RunAsync(async () =>
    {
        using var fixture = new RenderFixture();
        await fixture.Enter([fixture.Sample]);
        await fixture.WaitFor(() => fixture.Text("Caption").EndsWith("B"), "payload size before measurement");
        fixture.Hover(new Point(184,184));
        var first = fixture.Args(DragDrop.PreviewDragOverEvent, new Point(184,62));
        var secondPosition = WheelGeometry.At(122, 360d / fixture.Petals.Length);
        var second = fixture.Args(DragDrop.PreviewDragOverEvent, new Point(secondPosition.X,secondPosition.Y));
        var neutral = fixture.Args(DragDrop.PreviewDragOverEvent, new Point(184,184));
        var cycle = new[] { first, second, neutral };
        var originals = fixture.Petals;
        var geometry = originals.Select(petal => ((ShapePath)petal.Children[0]).Data).ToArray();
        const int warmup = 600, iterations = 6000, sampleCount = 5;
        for (int i = 0; i < warmup; i++) fixture.Raise(cycle[i % cycle.Length]);
        var samples = new List<object>();
        for (int sample = 0; sample < sampleCount; sample++)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            long allocated = GC.GetAllocatedBytesForCurrentThread(), started = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; i++) fixture.Raise(cycle[i % cycle.Length]);
            long elapsed = Stopwatch.GetTimestamp() - started;
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            samples.Add(new { sample, iterations, elapsedMilliseconds = elapsed * 1000d / Stopwatch.Frequency,
                allocatedBytesOnDispatcherThread = bytes, bytesPerHover = bytes / (double)iterations });
        }
        VisualArtifacts.Json("wheel-hover-performance", new
        {
            capturedUtc = DateTimeOffset.UtcNow, os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(),
            warmup, sampleCount, targets = originals.Length, stopwatchFrequency = Stopwatch.Frequency, samples,
            highContrast = SystemParameters.HighContrast, clientAreaAnimation = SystemParameters.ClientAreaAnimation,
            motionEnabled = false, soundEnabled = false,
            scope = "Synchronous real WPF routed DragOver + hit testing + selection. Preallocated synthetic args; alternating two targets and neutral center.",
            exclusions = "No Explorer/COM initiation, event allocation, thumbnail decode, animation, rasterization, compositor, presentation latency or other-thread allocations. No FPS guarantee or timing threshold."
        });
        for (int i = 0; i < originals.Length; i++)
        {
            Assert.Same(originals[i],fixture.Petals[i]);
            Assert.Same(geometry[i],((ShapePath)fixture.Petals[i].Children[0]).Data);
        }
        Assert.Equal(DragDropEffects.Copy, first.Effects);
        Assert.Equal(DragDropEffects.Copy, second.Effects);
        Assert.Equal(DragDropEffects.None, neutral.Effects);
        Assert.EndsWith("B", fixture.Text("Caption"));
    });

    private sealed class RenderFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "citrus-render-" + Guid.NewGuid().ToString("N"));
        public string Sample { get; }
        public string Second { get; }
        public WheelWindow Wheel { get; }
        public Canvas Surface => (Canvas)Wheel.FindName("Surface");
        public WheelPetal[] Petals => ((Canvas)Wheel.FindName("Sectors")).Children.Cast<WheelPetal>().ToArray();
        public IEnumerable<string> OperationIds => ((IReadOnlyList<OperationDescriptor>)typeof(WheelWindow)
            .GetField("pageOperations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Wheel)!).Select(operation => operation.Id);
        private DataObject data = new();
        public RenderFixture()
        {
            Directory.CreateDirectory(Root);
            Sample = Path.Combine(Root, "citrus-sample.png"); Second = Path.Combine(Root, "second.png");
            CitrusPreviewFixture.Save(Sample);
            File.Copy(Sample, Second);
            // Foreground/layout evidence uses a deterministic unavailable renderer;
            // native bootstrap pixels and readiness have dedicated guarded proofs.
            Wheel = new WheelWindow(new OperationCatalog([new ImageEngine(), new ArchiveEngine(), new PdfEngine(), new NativeActionCatalog()]),
                null, null, null, glassSession: (_, _, _) => null);
            Wheel.ApplySettings(new CitrusSettings { ReduceMotion = true, SoundEnabled = false });
        }
        public string Text(string name) => ((TextBlock)Wheel.FindName(name)).Text;
        public async Task Enter(string[] paths, OperationMode mode = OperationMode.Convert)
        {
            data = new DataObject(DataFormats.FileDrop, paths);
            Wheel.ShowAtPointer(mode);
            Raise(Args(DragDrop.PreviewDragEnterEvent, new Point(184,184)));
            await WaitFor(() => !Text("Hint").StartsWith("Preparing"), "production catalog preparation");
            Assert.True(Wheel.IsVisible);
            Assert.Equal(1, ((FrameworkElement)Wheel.FindName("Presentation")).Opacity);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        public async Task WaitFor(Func<bool> ready, string description)
        {
            var limit = Stopwatch.StartNew();
            while (!ready() && limit.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
            Assert.True(ready(), "Timed out waiting for " + description);
        }
        public DragEventArgs Args(RoutedEvent routedEvent, Point point)
        {
            // WPF has no public constructor; this is callback evidence, not real OLE initiation.
            var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                null, [data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, Surface, point], null)!;
            args.RoutedEvent = routedEvent; return args;
        }
        public void Raise(DragEventArgs args) { args.Handled = false; args.Effects = DragDropEffects.None; Surface.RaiseEvent(args); }
        public DragEventArgs Hover(Point point) { var args = Args(DragDrop.PreviewDragOverEvent,point); Raise(args); return args; }
        public void AssertCapsuleOnly()
        {
            foreach (string name in new[] { "Preview", "FileGlyph", "ThumbnailFrame", "CountBadge", "CountLabel" }) Assert.Null(Wheel.FindName(name));
            var caption = (Border)Wheel.FindName("CaptionPlate");
            Assert.Equal(160, Canvas.GetTop(caption));
            Assert.Equal(48, caption.Height);
            Assert.Equal(WheelPresentationMetrics.HubCapsuleWidth, caption.Width);
            Assert.Equal(24, caption.CornerRadius.TopLeft);
        }
        public void Capture(string name, double scale, ThemeChoice theme)
        {
            VisualArtifacts.Capture(Surface,name,scale,theme); // Save before bounds assertions, for failure inspection.
            Assert.Equal(368, Surface.ActualWidth); Assert.Equal(454, Surface.ActualHeight);
            AssertCapsuleOnly();
            VisualArtifacts.AssertInside((FrameworkElement)Wheel.FindName("Caption"), Surface);
            foreach (string child in new[] { "Hint", "ModeLabel", "Badge" })
            {
                var element = (FrameworkElement)Wheel.FindName(child);
                if (element.IsVisible) VisualArtifacts.AssertInside(element, Surface);
            }
            foreach (var petal in Petals)
            {
                VisualArtifacts.AssertInside(petal,Surface);
                foreach (FrameworkElement child in petal.Children) VisualArtifacts.AssertInside(child,Surface);
            }
        }
        public void Dispose() { Wheel.Dismiss(); Wheel.Close(); Directory.Delete(Root,true); }
    }
}
