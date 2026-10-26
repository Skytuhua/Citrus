using System.Collections.Immutable;
using System.Globalization;
using Citrus.Core.Editing;

namespace Citrus.Core.Tests;

public sealed class EditorValidationPerformanceTests
{
    [Fact]
    public void Large_stroke_validation_stays_within_allocation_budget()
    {
        var document = EditorTestData.Image();
        var annotation = new Annotation(Guid.NewGuid(), true,
            new StrokeGeometry(Enumerable.Range(0, 100_000).Select(i => new PointD(i % 1000, i % 500)).ToImmutableArray(), false, false),
            new(new(0, 0, 0, 255), new(0, 0, 0, 0), 1, 1, null));
        document = document with { Recipe = EditorTestData.Recipe(document) with { Annotations = [annotation] } };

        Assert.InRange(AllocatedByValidation(document), 0, 60_000_000);
    }

    [Fact]
    public void Large_pdf_validation_stays_within_allocation_budget()
    {
        var document = EditorTestData.Image();
        document = document with
        {
            Recipe = new PdfRecipe(Enumerable.Range(1, 10_000).Select(i => new PdfPageRef(Guid.NewGuid(), document.Sources[0].Id, i, 0)).ToImmutableArray(),
                PdfOutputMode.Document, 300, PdfCompressionMode.PreserveStructure, 85, [])
        };

        Assert.InRange(AllocatedByValidation(document), 0, 8_000_000);
    }

    [Fact]
    public void Structural_issues_keep_exact_paths_messages_and_order()
    {
        var document = EditorTestData.Image();
        var annotation = new Annotation(Guid.NewGuid(), true, new StrokeGeometry([new(1, double.NaN), new(double.PositiveInfinity, 2)], false, false), null!);
        document = document with
        {
            Sources = [document.Sources[0] with { Path = null! }],
            Fonts = default,
            Recipe = EditorTestData.Recipe(document) with { Annotations = [annotation] },
            Export = document.Export with { Metadata = (MetadataPolicy)999 }
        };

        Assert.Equal<ValidationIssue>([
            new ValidationIssue("Document.Sources[0].Path", "Required value is missing."),
            new ValidationIssue("Document.Fonts", "Array is uninitialized."),
            new ValidationIssue("Document.Recipe.Annotations[0].Geometry.Points[0].Y", "Nonfinite value."),
            new ValidationIssue("Document.Recipe.Annotations[0].Geometry.Points[1].X", "Nonfinite value."),
            new ValidationIssue("Document.Recipe.Annotations[0].Style", "Required value is missing."),
            new ValidationIssue("Document.Export.Metadata", "Unknown enum value.")], EditorRecipeValidator.Validate(document));
    }

    [Theory]
    [InlineData("en-US", "Must be finite and between 0.1 and 5.")]
    [InlineData("fr-FR", "Must be finite and between 0,1 and 5.")]
    public void Numeric_errors_keep_current_culture_formatting(string culture, string message)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var document = EditorTestData.Image();
            document = document with { Recipe = EditorTestData.Recipe(document) with { Adjustments = EditorTestData.Identity with { Gamma = 0 } } };

            Assert.Equal<ValidationIssue>([new ValidationIssue("Adjustments.Gamma", message)], EditorRecipeValidator.Validate(document));
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    [Fact]
    public void Concurrent_validations_keep_independent_results()
    {
        var valid = EditorTestData.Image();
        var invalid = valid with { Recipe = EditorTestData.Recipe(valid) with { Geometry = null! } };

        Parallel.For(0, 100, index =>
        {
            if (index % 2 == 0) Assert.Empty(EditorRecipeValidator.Validate(valid));
            else Assert.Equal<ValidationIssue>([new("Document.Recipe.Geometry", "Required value is missing.")], EditorRecipeValidator.Validate(invalid));
        });
    }

    private static long AllocatedByValidation(EditorDocument document)
    {
        // Warm the reflection/runtime caches before measuring only the validation call.
        Assert.Empty(EditorRecipeValidator.Validate(document));
        long before = GC.GetAllocatedBytesForCurrentThread();
        var issues = EditorRecipeValidator.Validate(document);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Empty(issues);
        return allocated;
    }
}
