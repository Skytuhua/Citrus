using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Numerics;

namespace Citrus.Core.Editing;

public static class EditorRecipeValidator
{
    public static ImmutableArray<ValidationIssue> Validate(EditorDocument document)
    {
        var errors = ImmutableArray.CreateBuilder<ValidationIssue>();
        void Check(bool valid, string field, string message) { if (!valid) errors.Add(new(field, message)); }
        void Number(double value, double min, double max, string field)
        {
            if (!(double.IsFinite(value) && value >= min && value <= max))
                errors.Add(new(field, $"Must be finite and between {min} and {max}."));
        }
        void Size(SizeI size, string field) => Check(size.Width is > 0 and <= 16384 && size.Height is > 0 and <= 16384 && (long)size.Width * size.Height <= 64_000_000, field, "Invalid size or pixel budget exceeded.");
        void Rect(RectD r, string field) { Number(r.X, 0, 16384, field); Number(r.Y, 0, 16384, field); Number(r.Width, double.Epsilon, 16384, field); Number(r.Height, double.Epsilon, 16384, field); }
        void Ids(IEnumerable<Guid> ids, string field) { var array = ids.ToArray(); Check(!array.Contains(Guid.Empty) && array.Distinct().Count() == array.Length, field, "IDs must be nonempty and unique."); }
        void Time(RationalTime time, string field, bool assembled = true) => Check(time.Denominator > 0 && time.Numerator >= 0 && (!assembled || (BigInteger)time.Numerator <= 86400 * (BigInteger)time.Denominator), field, assembled ? "Invalid time or timeline exceeds 24 hours." : "Invalid source offset.");
        void Range(TimeRange range, string field, bool assembled = true) { Time(range.Start, field, assembled); Time(range.End, field, assembled); if (range.Start.Denominator > 0 && range.End.Denominator > 0) Check(range.Start.CompareTo(range.End) < 0, field, "Range must be nonempty."); }
        void Shadow(ShadowSpec? shadow, string field) { if (shadow is null) return; Number(shadow.X,-16384,16384,field); Number(shadow.Y,-16384,16384,field); Number(shadow.Sigma,0,100,field); }
        void Background(CanvasBackground background, string field)
        {
            switch (background)
            {
                case TransparentBackground or SolidBackground: break;
                case GradientBackground gradient:
                    Number(gradient.Angle,-360,360,field);
                    Check(gradient.Stops.Length >= 2,field,"At least two gradient stops required.");
                    double previous = -1;
                    foreach (var stop in gradient.Stops) { Number(stop.Position,0,1,field); Check(stop.Position >= previous,field,"Gradient stops must be ordered."); previous = stop.Position; }
                    break;
                case ImageBackground image:
                    Number(image.Anchor.X,0,1,field); Number(image.Anchor.Y,0,1,field); Number(image.Opacity,0,1,field); Number(image.BlurSigma,0,100,field); break;
                default: Check(false,field,"Unknown background."); break;
            }
        }
        void Metadata(ImmutableArray<MetadataEdit> edits)
        {
            var keys = new HashSet<(string,string)>();
            foreach (var edit in edits)
            {
                Check(!string.IsNullOrWhiteSpace(edit.Scope) && !string.IsNullOrWhiteSpace(edit.Key) && keys.Add((edit.Scope,edit.Key)),"MetadataEdits","Empty or duplicate metadata field.");
                Check(edit.Action == MetadataEditAction.Set ? edit.Value is not null : edit.Value is null,"MetadataEdits","Set requires a value; remove must not carry one.");
                Check(document.Export.Metadata != MetadataPolicy.StripAll || edit.Action != MetadataEditAction.Set,"MetadataEdits","Cannot set metadata with StripAll.");
                switch (edit.Value)
                {
                    case RealValue r: Check(double.IsFinite(r.Value),"MetadataEdits","Nonfinite value."); break;
                    case RationalValue r: Check(r.Denominator > 0,"MetadataEdits","Invalid rational denominator."); break;
                    case GpsValue gps: Number(gps.Latitude,-90,90,"MetadataEdits"); Number(gps.Longitude,-180,180,"MetadataEdits"); if (gps.AltitudeMeters is { } altitude) Number(altitude,-12000,100000,"MetadataEdits"); break;
                }
            }
        }
        ExactTime Clips(ImmutableArray<MediaClip> clips)
        {
            Check(clips.Length is >= 1 and <= 100,"Clips","Require 1..100 clips.");
            Ids(clips.Select(c => c.Id),"Clips");
            var duration = ExactTime.Zero;
            foreach (var clip in clips)
            {
                Range(clip.Range,"Clips.Range", assembled: false); Number(clip.Speed,.25,4,"Clips.Speed");
                Check(clip.VideoStream is >= 0 || clip.AudioStream is >= 0,"Clips.Stream","At least one stream required.");
                Check(clip.VideoStream is null or >= 0 && clip.AudioStream is null or >= 0,"Clips.Stream","Invalid stream index.");
                if (double.IsFinite(clip.Speed) && clip.Speed is >= .25 and <= 4)
                {
                    bool validSpeedPrecision = Math.Round(clip.Speed,6) == clip.Speed;
                    Check(validSpeedPrecision,"Clips.Speed","At most six decimal places.");
                    if (validSpeedPrecision && clip.Range.Start.Denominator > 0 && clip.Range.End.Denominator > 0)
                        duration = duration.Add(ExactTime.From(clip.Range.End).Subtract(ExactTime.From(clip.Range.Start)).Divide(ExactTime.FromDisplayedSpeed(clip.Speed)));
                }
            }
            Check(duration.CompareTo(ExactTime.Zero) > 0 && duration.CompareTo(new RationalTime(86400, 1)) <= 0,"Clips","Invalid assembled duration.");
            return duration;
        }
        void Audio(AudioRecipe audio)
        {
            ExactTime duration = Clips(audio.Clips);
            Check(audio.SampleRate is >= 8000 and <= 192000,"SampleRate","Unsupported sample rate.");
            var matrix = audio.Channels;
            Check(matrix.InputChannels.Length is >= 1 and <= 64 && matrix.OutputChannels.Length is >= 1 and <= 64 && matrix.Coefficients.Length == matrix.InputChannels.Length * matrix.OutputChannels.Length,"Channels","Invalid channel matrix dimensions.");
            Check(matrix.InputChannels.Distinct().Count() == matrix.InputChannels.Length && matrix.OutputChannels.Distinct().Count() == matrix.OutputChannels.Length,"Channels","Duplicate channel names.");
            foreach (var coefficient in matrix.Coefficients) Number(coefficient,-4,4,"Channels.Coefficients");
            for (int output = 0; output < matrix.OutputChannels.Length && matrix.Coefficients.Length == matrix.InputChannels.Length * matrix.OutputChannels.Length; output++)
                Check(matrix.LimitPeak || matrix.Coefficients.Skip(output * matrix.InputChannels.Length).Take(matrix.InputChannels.Length).Sum(Math.Abs) <= 1,"Channels.LimitPeak","Clipping matrix requires limiter.");
            Check(audio.Bleeps.Length <= 1000,"Bleeps","Too many regions."); Ids(audio.Bleeps.Select(b => b.Id),"Bleeps");
            foreach (var b in audio.Bleeps)
            {
                Range(b.Range,"Bleeps.Range"); Number(b.FrequencyHz,100,4000,"Bleeps.FrequencyHz"); Number(b.GainDbFs,-60,-3,"Bleeps.GainDbFs");
                Check(b.FadeMilliseconds is >= 0 and <= 50,"Bleeps.FadeMilliseconds","Invalid fade.");
                Check(b.Channels.Length > 0 && b.Channels.All(c => c >= 0 && c < matrix.OutputChannels.Length) && b.Channels.Distinct().Count() == b.Channels.Length,"Bleeps.Channels","Invalid channels.");
                if (b.Range.End.Denominator > 0) Check(duration.CompareTo(b.Range.End) >= 0,"Bleeps.Range","Region exceeds timeline.");
            }
            for (int i = 0; i < audio.Bleeps.Length; i++)
                for (int j = i + 1; j < audio.Bleeps.Length; j++)
                {
                    var left = audio.Bleeps[i]; var right = audio.Bleeps[j];
                    if (left.Range.Start.Denominator <= 0 || left.Range.End.Denominator <= 0 || right.Range.Start.Denominator <= 0 || right.Range.End.Denominator <= 0) continue;
                    bool overlap = left.Range.Start.CompareTo(right.Range.End) < 0 && right.Range.Start.CompareTo(left.Range.End) < 0 && left.Channels.Intersect(right.Channels).Any();
                    Check(!overlap || (left.Mode == right.Mode && (left.Mode == BleepMode.Silence || (left.FrequencyHz == right.FrequencyHz && left.GainDbFs == right.GainDbFs && left.FadeMilliseconds == right.FadeMilliseconds))),"Bleeps","Overlapping regions have conflicting tone settings.");
                }
            if (audio.Loudness is { } l) { Number(l.IntegratedLufs,-70,-5,"Loudness"); Number(l.LoudnessRangeLu,1,50,"Loudness"); Number(l.TruePeakDbTp,-9,0,"Loudness"); }
            Metadata(audio.MetadataEdits);
        }

        // Reject malformed serialized shapes before traversing required records/arrays.
        ValidateStructure(document, "Document", errors);
        if (errors.Count > 0) return errors.ToImmutable();
        Check(document.SchemaVersion == 1,"SchemaVersion","Only schema version 1 is supported.");
        Check(document.Revision >= 0,"Revision","Revision must not be negative.");
        Ids(document.Sources.Select(s => s.Id).Concat(document.Fonts.Select(f => f.Id)),"Sources/Fonts");
        Check(document.Sources.Length > 0,"Sources","At least one source required.");
        foreach (var source in document.Sources)
        {
            Check(Path.IsPathFullyQualified(source.Path),"Sources.Path","Source path must be absolute.");
            Check(source.Length >= 0 && source.LastWriteUtcTicks >= 0 && IsHash(source.Sha256),"Sources","Invalid source identity.");
        }
        foreach (var font in document.Fonts)
            Check(Path.IsPathFullyQualified(font.OriginalPath) && font.Length > 0 && IsHash(font.Sha256) && font.FaceIndex >= 0 && !string.IsNullOrWhiteSpace(font.Family) && !string.IsNullOrWhiteSpace(font.FaceName),"Fonts","Invalid font identity.");
        var sources = document.Sources.Select(s => s.Id).ToHashSet();
        var fonts = document.Fonts.Select(f => f.Id).ToHashSet();
        Check(EditorRecipeReferences.Sources(document.Recipe).All(sources.Contains),"Recipe.SourceId","Unknown source reference.");
        Check(EditorRecipeReferences.Fonts(document.Recipe).All(fonts.Contains),"Recipe.Font","Unknown font reference.");
        Check(EditorPathPolicy.IsLeaf(document.Export.OutputStem),"Export.OutputStem","Invalid output filename stem.");
        Check(document.Export.DestinationDirectory is null || Path.IsPathFullyQualified(document.Export.DestinationDirectory),"Export.DestinationDirectory","Destination must be absolute.");
        Check(!string.IsNullOrWhiteSpace(document.Export.FormatId),"Export.FormatId","Format is required.");
        Check(document.Export.Compression switch { QualityGoal q => q.Value is >= 1 and <= 100, ByteGoal b => b.MaximumBytes > 0, ReductionGoal r => r.Percent is > 0 and < 100, _ => false },"Export.Compression","Invalid compression goal.");
        switch (document.Recipe)
        {
            case ImageRecipe image:
                Check(image.Geometry.QuarterTurns is >= 0 and <= 3,"Geometry.QuarterTurns","Rotation must be 0..3.");
                if (image.Geometry.Crop is { } crop) Rect(crop,"Geometry.Crop");
                if (image.Geometry.OutputSize is { } output) Size(output,"Geometry.OutputSize");
                var a = image.Adjustments;
                Check(a.AlgorithmVersion == 1,"Adjustments.AlgorithmVersion","Unknown adjustment algorithm.");
                Number(a.ExposureEv,-5,5,"Adjustments.ExposureEv"); Number(a.Gamma,.1,5,"Adjustments.Gamma"); Number(a.Saturation,0,2,"Adjustments.Saturation"); Number(a.HueDegrees,-180,180,"Adjustments.HueDegrees"); Number(a.BlurSigma,0,100,"Adjustments.BlurSigma");
                foreach (double v in new[] { a.Brightness,a.Contrast,a.Highlights,a.Shadows,a.Whites,a.Blacks,a.Vibrance,a.Warmth,a.Tint,a.Vignette }) Number(v,-1,1,"Adjustments");
                foreach (double v in new[] { a.Clarity,a.Dehaze,a.Sharpen,a.Denoise,a.Grain,a.EffectAmount }) Number(v,0,1,"Adjustments");
                Check(image.Masks.Length + image.Annotations.Length <= 1000,"Layers","At most 1000 layers.");
                Ids(image.Masks.Select(m => m.Id).Concat(image.Annotations.Select(n => n.Id)),"Layers");
                foreach (var mask in image.Masks) { Rect(mask.Bounds,"Masks.Bounds"); Number(mask.Radius,0,100,"Masks.Radius"); }
                if (image.Canvas is { } canvas) { Size(canvas.Size,"Canvas.Size"); Rect(canvas.Placement,"Canvas.Placement"); Background(canvas.Background,"Canvas.Background"); Number(canvas.CornerRadius,0,16384,"Canvas.CornerRadius"); Shadow(canvas.Shadow,"Canvas.Shadow"); if(canvas.AspectRatio is {} aspect)Number(aspect,.01,100,"Canvas.AspectRatio"); }
                foreach (var annotation in image.Annotations)
                {
                    Number(annotation.Style.Opacity,0,1,"Annotations.Opacity"); Number(annotation.Style.StrokeWidth,0,16384,"Annotations.StrokeWidth");
                    if (annotation.Style.Font is { } font) { Number(font.SizePixels,double.Epsilon,16384,"Annotations.Font"); Number(font.LineHeight,double.Epsilon,16384,"Annotations.Font"); }
                    switch (annotation.Geometry)
                    {
                        case StrokeGeometry s: Check(s.Points.Length is >= 2 and <= 100000,"Annotations.Points","Invalid stroke point count."); foreach (var point in s.Points) { Number(point.X,-16384,32768,"Annotations.Points"); Number(point.Y,-16384,32768,"Annotations.Points"); } break;
                        case ShapeGeometry s: Rect(s.Bounds,"Annotations.Bounds"); Number(s.Radius,0,16384,"Annotations.Radius"); break;
                        case TextGeometry t: Check(annotation.Style.Font is not null && !string.IsNullOrEmpty(t.Text),"Annotations.Text","Text requires an explicit font and content."); Number(t.WrapWidth,double.Epsilon,16384,"Annotations.Text"); Number(t.Rotation,-360,360,"Annotations.Text"); break;
                        case NumberGeometry n: Rect(n.Bounds,"Annotations.Bounds"); Check(n.Number > 0 && annotation.Style.Font is not null,"Annotations.Number","Number requires positive value and explicit font."); break;
                        case InsetImageGeometry i: Rect(i.Bounds,"Annotations.Bounds"); break;
                        case MagnifierGeometry m: Rect(m.SampleBounds,"Annotations.SampleBounds"); Rect(m.LensBounds,"Annotations.LensBounds"); Number(m.Scale,1,100,"Annotations.Scale"); break;
                        default: Check(false,"Annotations.Geometry","Unknown annotation."); break;
                    }
                }
                Metadata(image.MetadataEdits); break;
            case CollageRecipe collage:
                Check(collage.Items.Length is >= 1 and <= 100,"Items","Require 1..100 items."); Ids(collage.Items.Select(i => i.Id),"Items"); Size(collage.Size,"Size");
                Check(collage.Columns is >= 1 and <= 100,"Columns","Invalid columns."); Number(collage.Margin,0,16384,"Margin"); Number(collage.Gap,0,16384,"Gap"); Number(collage.FeaturedFraction,.2,.8,"FeaturedFraction");
                Check(collage.FeaturedItemId is null || collage.Items.Any(i => i.Id == collage.FeaturedItemId),"FeaturedItemId","Unknown featured item.");
                if (collage.Layout is CollageLayout.FeaturedLeft or CollageLayout.FeaturedTop) Check(collage.FeaturedItemId is not null,"FeaturedItemId","Featured layout needs a selected item.");
                Check(CollageHasTileArea(collage),"Layout","Margins and gaps must leave positive tile extents.");
                foreach (var item in collage.Items) { if (item.Crop is { } r) Rect(r,"Items.Crop"); Number(item.Anchor.X,0,1,"Items.Anchor"); Number(item.Anchor.Y,0,1,"Items.Anchor"); Number(item.Weight,double.Epsilon,100000,"Items.Weight"); }
                Background(collage.Background,"Background"); Number(collage.CornerRadius,0,16384,"CornerRadius"); Shadow(collage.Shadow,"Shadow"); break;
            case VideoRecipe video:
                ExactTime duration = Clips(video.Clips); Size(video.Canvas,"Canvas");
                // Crop is in the first stream's normalized display plane, not final output Canvas.
                // Its actual upper bounds require inspected source geometry in the media compiler.
                if (video.Crop is { } vc) Rect(vc,"Crop");
                Check(video.SplitAt.Length <= 99 && (video.SplitAt.IsEmpty || video.SnapshotAt is null),"SplitAt","At most 100 segments; snapshot and split are exclusive.");
                RationalTime previous = new(0,1);
                foreach (var split in video.SplitAt) { Time(split,"SplitAt"); if (split.Denominator > 0) { Check(split.CompareTo(previous) > 0 && duration.CompareTo(split) > 0,"SplitAt","Splits must be unique, sorted and inside duration."); previous = split; } }
                if (video.SnapshotAt is { } snapshot) { Time(snapshot,"SnapshotAt"); if (snapshot.Denominator > 0) Check(duration.CompareTo(snapshot) > 0,"SnapshotAt","Snapshot exceeds timeline."); }
                Check(video.Masks.Length <= 1000,"Masks","Too many masks."); Ids(video.Masks.Select(m => m.Id),"Masks");
                foreach (var mask in video.Masks)
                {
                    Range(mask.Range,"Masks.Range");
                    if (mask.Range.End.Denominator > 0) Check(duration.CompareTo(mask.Range.End) >= 0,"Masks.Range","Mask exceeds timeline.");
                    Number(mask.Radius,0,100,"Masks.Radius"); Check(mask.Keys.Length > 0,"Masks.Keys","Keyframes required.");
                    RationalTime? last = null;
                    foreach (var key in mask.Keys) { Time(key.Time,"Masks.Keys"); Rect(key.Bounds,"Masks.Keys"); if (key.Time.Denominator > 0 && mask.Range.Start.Denominator > 0 && mask.Range.End.Denominator > 0) Check(key.Time.CompareTo(mask.Range.Start) >= 0 && key.Time.CompareTo(mask.Range.End) <= 0 && (last is null || key.Time.CompareTo(last.Value) > 0),"Masks.Keys","Keys must be sorted inside mask range."); last = key.Time; }
                }
                Metadata(video.MetadataEdits); break;
            case AudioRecipe audio: Audio(audio); break;
            case VisualizerRecipe visualizer: Audio(visualizer.Audio); Size(visualizer.Size,"Size"); Check(visualizer.FramesPerSecond is >= 1 and <= 60,"FramesPerSecond","Invalid frame rate."); break;
            case PdfRecipe pdf:
                Check(pdf.Pages.Length is >= 1 and <= 10000,"Pages","Require 1..10000 pages."); Ids(pdf.Pages.Select(p => p.Id),"Pages");
                foreach (var page in pdf.Pages) Check(page.OneBasedPage > 0 && page.QuarterTurns is >= 0 and <= 3,"Pages","Invalid page or rotation.");
                Check(pdf.Dpi is >= 36 and <= 600 && pdf.RasterQuality is >= 1 and <= 100,"Pdf","Invalid raster settings."); Metadata(pdf.MetadataEdits); break;
            default: Check(false,"Recipe","Unknown recipe family."); break;
        }
        return errors.ToImmutable();
    }

    // Recipe time denominators are bounded by their schema; intermediate sums/products are not.
    // Normalize with BigInteger so endpoints, accumulated clips and displayed speeds stay exact.
    private readonly struct ExactTime
    {
        private readonly BigInteger numerator;
        private readonly BigInteger denominator;
        private ExactTime(BigInteger numerator, BigInteger denominator)
        {
            var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
            this.numerator = numerator / divisor;
            this.denominator = denominator / divisor;
        }
        public static ExactTime Zero => new(0, 1);
        public static ExactTime From(RationalTime time) => new(time.Numerator, time.Denominator);
        public static ExactTime FromDisplayedSpeed(double speed) => new((long)Math.Round(speed * 1_000_000), 1_000_000);
        public ExactTime Add(ExactTime other) => new(numerator * other.denominator + other.numerator * denominator, denominator * other.denominator);
        public ExactTime Subtract(ExactTime other) => new(numerator * other.denominator - other.numerator * denominator, denominator * other.denominator);
        public ExactTime Divide(ExactTime other) => new(numerator * other.denominator, denominator * other.numerator);
        public int CompareTo(ExactTime other) => (numerator * other.denominator).CompareTo(other.numerator * denominator);
        public int CompareTo(RationalTime other) => CompareTo(From(other));
    }

    private static bool CollageHasTileArea(CollageRecipe collage)
    {
        int count = collage.Items.Length;
        double width = collage.Size.Width - 2 * collage.Margin;
        double height = collage.Size.Height - 2 * collage.Margin;
        if (count == 0 || width <= 0 || height <= 0) return false;
        switch (collage.Layout)
        {
            case CollageLayout.Grid:
                if (collage.Columns <= 0) return false;
                int rows = (count + collage.Columns - 1) / collage.Columns;
                return width - (collage.Columns - 1) * collage.Gap > 0 && height - (rows - 1) * collage.Gap > 0;
            case CollageLayout.Row:
                return width - (count - 1) * collage.Gap > 0;
            case CollageLayout.Column:
                return height - (count - 1) * collage.Gap > 0;
            case CollageLayout.FeaturedLeft:
                return count == 1 || (width - collage.Gap > 0 && height - (count - 2) * collage.Gap > 0);
            case CollageLayout.FeaturedTop:
                return count == 1 || (height - collage.Gap > 0 && width - (count - 2) * collage.Gap > 0);
            default:
                return false;
        }
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private sealed record StructureMetadata(PropertyInfo? IsDefault, StructureProperty[] Properties);
    private readonly record struct StructureProperty(PropertyInfo Property, bool AllowsNull);
    private static readonly ConcurrentDictionary<Type, StructureMetadata> StructureMetadataCache = new();

    private static StructureMetadata DescribeStructure(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
            return new(type.GetProperty("IsDefault")!, []);

        // Cache only immutable reflection metadata. Each factory has its own nullability context,
        // so concurrent validations never share mutable NullabilityInfoContext state.
        var nullability = new NullabilityInfoContext();
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0)
            .Select(property => new StructureProperty(property,
                Nullable.GetUnderlyingType(property.PropertyType) is not null ||
                nullability.Create(property).ReadState == NullabilityState.Nullable))
            .ToArray();
        return new(null, properties);
    }

    // A closed record graph has no cycles; nullable properties are allowed, required ones are rejected.
    private static void ValidateStructure(object? value, string path, ImmutableArray<ValidationIssue>.Builder errors)
    {
        if (value is null) { errors.Add(new(path,"Required value is missing.")); return; }
        var type = value.GetType();
        if (type.IsEnum) { if (!Enum.IsDefined(type,value)) errors.Add(new(path,"Unknown enum value.")); return; }
        if (value is double d) { if (!double.IsFinite(d)) errors.Add(new(path,"Nonfinite value.")); return; }
        if (type.IsPrimitive || value is string or decimal or DateTime or TimeSpan or Guid) return;
        var metadata = StructureMetadataCache.GetOrAdd(type, DescribeStructure);
        if (metadata.IsDefault is { } isDefault)
        {
            if ((bool)isDefault.GetValue(value)!) { errors.Add(new(path,"Array is uninitialized.")); return; }
            int index = 0;
            foreach (var item in (System.Collections.IEnumerable)value) ValidateStructure(item,$"{path}[{index++}]",errors);
            return;
        }
        foreach (var entry in metadata.Properties)
        {
            var property = entry.Property;
            var child = property.GetValue(value);
            if (child is null && entry.AllowsNull) continue;
            ValidateStructure(child,path + "." + property.Name,errors);
        }
    }
}
