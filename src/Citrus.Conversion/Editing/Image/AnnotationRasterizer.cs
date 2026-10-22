using PointD = Citrus.Core.Editing.PointD;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Citrus.Core.Editing;
using ImageMagick;
using SkiaSharp;

namespace Citrus.Conversion.Editing.Image;

/// <summary>Saved geometry is always in final C pixels. WPF never supplies rasterized text or shapes.</summary>
public sealed class AnnotationRasterizer
{
    /// <summary>Renders only the annotation layer, with transparent untouched pixels. Sources must
    /// map every document source to its verified private copy. Fonts are independently staged.
    /// Magnifiers sample the same adjusted, masked pre-annotation image as the shared renderer.</summary>
    public PreviewFrame Render(EditorDocument document, PreviewRequest request, ImmutableDictionary<Guid,string> sources)
    {
        var recipe=ImageRecipeRenderer.Validate(document);
        if(request.Bounds.Width is <=0 or >2048||request.Bounds.Height is <=0 or >2048||request.Time is not null||request.PageId is not null) throw new ArgumentException("Invalid raster preview request.");
        foreach(var source in document.Sources)
        {
            using var stream=File.OpenRead(sources[source.Id]);
            if(stream.Length!=source.Length||!Convert.ToHexString(SHA256.HashData(stream)).Equals(source.Sha256,StringComparison.OrdinalIgnoreCase)) throw new IOException("Source content changed. Reload the source.");
        }
        string scope=Path.Combine(Path.GetTempPath(),"citrus-annotation-fonts-"+Guid.NewGuid().ToString("N"));
        try
        {
            var fonts=new FontArtifactService().StageVerifiedAsync(document.Fonts,scope,default).GetAwaiter().GetResult();
            using var photo=ImageRecipeRenderer.RenderBeforeAnnotations(document,sources[recipe.SourceId],default,out var sourceSize);
            using var composed=recipe.Canvas is {} spec?ImageCanvasComposer.Compose(photo,spec,new((int)photo.Width,(int)photo.Height),sources,1,default):null;
            var before=composed??photo;
            FontTextValidator.ValidateDocument(document,fonts,default);
            using var layer=RenderLayer(document,before,new((int)before.Width,(int)before.Height),sources,fonts,default);
            if(request.SourceTile is {} tile) ImageRecipeRenderer.CropDetail(layer,recipe,sourceSize,tile);
            double scale=Math.Min(1,Math.Min(request.Bounds.Width/(double)layer.Width,request.Bounds.Height/(double)layer.Height));
            if(scale<1) layer.Resize(new MagickGeometry((uint)Math.Max(1,Math.Floor(layer.Width*scale)),(uint)Math.Max(1,Math.Floor(layer.Height*scale))){IgnoreAspectRatio=true});
            return new(document.Revision,request.Generation,new((int)layer.Width,(int)layer.Height),(int)layer.Width*4,ImmutableCollectionsMarshal.AsImmutableArray(layer.GetPixels().ToByteArray(PixelMapping.BGRA)!),null,null,[]);
        }
        finally { if(Directory.Exists(scope)) Directory.Delete(scope,true); }
    }

    internal static void Apply(EditorDocument document,MagickImage image,SizeI canvasSize,ImmutableDictionary<Guid,string> sources,ImmutableDictionary<Guid,string> fonts,CancellationToken ct)
    {
        if(!((ImageRecipe)document.Recipe).Annotations.Any(a=>a.Visible)) return;
        using var layer=RenderLayer(document,image,canvasSize,sources,fonts,ct);
        image.Composite(layer,CompositeOperator.Over);
    }

    private static MagickImage RenderLayer(EditorDocument document,MagickImage before,SizeI canvasSize,ImmutableDictionary<Guid,string> sources,ImmutableDictionary<Guid,string> fonts,CancellationToken ct)
    {
        var recipe=(ImageRecipe)document.Recipe;
        using var bitmap=new SKBitmap(new SKImageInfo((int)before.Width,(int)before.Height,SKColorType.Bgra8888,SKAlphaType.Premul));
        using var canvas=new SKCanvas(bitmap); canvas.Clear(SKColors.Transparent);
        canvas.Scale(before.Width/(float)canvasSize.Width,before.Height/(float)canvasSize.Height);
        using var sample=recipe.Annotations.Any(a=>a.Visible&&a.Geometry is MagnifierGeometry)?ToBitmap(before):null;
        foreach(var layer in recipe.Annotations)
        {
            ct.ThrowIfCancellationRequested(); if(!layer.Visible||layer.Style.Opacity==0) continue;
            var style=layer.Style;
            using var opacity=new SKPaint {Color=SKColors.White.WithAlpha((byte)Math.Round(style.Opacity*255))};
            canvas.SaveLayer(opacity);
            try
            {
                using var fill=new SKPaint {Color=Color(style.Fill),IsAntialias=true,Style=SKPaintStyle.Fill};
                using var stroke=new SKPaint {Color=Color(style.Stroke),IsAntialias=true,Style=SKPaintStyle.Stroke,StrokeWidth=(float)style.StrokeWidth,StrokeCap=SKStrokeCap.Round,StrokeJoin=SKStrokeJoin.Round};
                switch(layer.Geometry)
                {
                    case StrokeGeometry s:
                        using(var path=new SKPathBuilder())
                        {
                            path.MoveTo(P(s.Points[0])); foreach(var point in s.Points.Skip(1)) path.LineTo(P(point));
                            using var geometry=path.Detach();
                            if(style.StrokeWidth>0) canvas.DrawPath(geometry,stroke);
                        }
                        if(style.StrokeWidth>0)
                        {
                            if(s.ArrowStart) Arrow(canvas,s.Points[0],s.Points.FirstOrDefault(p=>p!=s.Points[0],s.Points[0]),stroke);
                            if(s.ArrowEnd) Arrow(canvas,s.Points[^1],s.Points.Reverse().FirstOrDefault(p=>p!=s.Points[^1],s.Points[^1]),stroke);
                        }
                        break;
                    case ShapeGeometry shape:
                        using(var path=new SKPathBuilder())
                        {
                            if(shape.Ellipse) path.AddOval(R(shape.Bounds)); else path.AddRoundRect(R(shape.Bounds),(float)shape.Radius,(float)shape.Radius);
                            using var geometry=path.Detach();
                            canvas.DrawPath(geometry,fill); if(style.StrokeWidth>0) canvas.DrawPath(geometry,stroke);
                        }
                        break;
                    case TextGeometry text:
                        using(var font=OpenFont(document,style,fonts,ct))
                        {
                            canvas.Save(); canvas.Translate((float)text.Origin.X,(float)text.Origin.Y); canvas.RotateDegrees((float)text.Rotation);
                            DrawText(canvas,font,text.Text,(float)text.WrapWidth,(float)style.Font!.LineHeight,fill,style.StrokeWidth > 0 ? stroke : null,ct);
                            canvas.Restore();
                        }
                        break;
                    case NumberGeometry number:
                        canvas.DrawOval(R(number.Bounds),fill); if(style.StrokeWidth>0) canvas.DrawOval(R(number.Bounds),stroke);
                        using(var font=OpenFont(document,style,fonts,ct))
                        using(var ink=new SKPaint{Color=Color(style.Stroke),IsAntialias=true})
                        {
                            string label=number.Number.ToString(CultureInfo.InvariantCulture);
                            var metrics=font.Metrics; float baseline=(float)(number.Bounds.Y+number.Bounds.Height/2)-(metrics.Ascent+metrics.Descent)/2;
                            canvas.DrawText(label,(float)(number.Bounds.X+number.Bounds.Width/2),baseline,SKTextAlign.Center,font,ink);
                        }
                        break;
                    case InsetImageGeometry inset:
                        using(var image=ImageRasterSource.Load(sources[inset.SourceId],ct))
                        using(var pixels=ToBitmap(image))
                            canvas.DrawBitmap(pixels,R(inset.Bounds),new SKSamplingOptions(SKFilterMode.Linear,SKMipmapMode.None));
                        if(style.StrokeWidth>0) canvas.DrawRect(R(inset.Bounds),stroke);
                        break;
                    case MagnifierGeometry magnifier:
                        // Scale zooms about SampleBounds' center; LensBounds is the saved destination.
                        var b=magnifier.SampleBounds;
                        var zoom=new RectD(b.X+b.Width*(1-1/magnifier.Scale)/2,b.Y+b.Height*(1-1/magnifier.Scale)/2,b.Width/magnifier.Scale,b.Height/magnifier.Scale);
                        float sx=before.Width/(float)canvasSize.Width,sy=before.Height/(float)canvasSize.Height;
                        var src=new SKRect((float)zoom.X*sx,(float)zoom.Y*sy,(float)(zoom.X+zoom.Width)*sx,(float)(zoom.Y+zoom.Height)*sy);
                        canvas.Save(); canvas.ClipRect(R(magnifier.LensBounds));
                        canvas.DrawBitmap(sample!,src,R(magnifier.LensBounds),new SKSamplingOptions(SKFilterMode.Linear,SKMipmapMode.None)); canvas.Restore();
                        if(style.StrokeWidth>0) canvas.DrawRect(R(magnifier.LensBounds),stroke);
                        break;
                    default: throw new NotSupportedException("Unknown annotation geometry.");
                }
            }
            finally {canvas.Restore();}
        }
        ct.ThrowIfCancellationRequested();
        using var straight=new SKBitmap(new SKImageInfo(bitmap.Width,bitmap.Height,SKColorType.Bgra8888,SKAlphaType.Unpremul));
        using var bitmapPixels=bitmap.PeekPixels();
        if(!bitmapPixels.ReadPixels(straight.Info,straight.GetPixels(),straight.RowBytes,0,0)) throw new InvalidDataException("Unable to read annotation pixels.");
        byte[] bytes=new byte[checked(straight.Width*straight.Height*4)]; Marshal.Copy(straight.GetPixels(),bytes,0,bytes.Length);
        return new MagickImage(bytes,new PixelReadSettings((uint)straight.Width,(uint)straight.Height,StorageType.Char,PixelMapping.BGRA));
    }

    private static SKFont OpenFont(EditorDocument document,AnnotationStyle style,ImmutableDictionary<Guid,string> fonts,CancellationToken ct)
    {
        var spec=style.Font!; var artifact=document.Fonts.Single(f=>f.Id==spec.ArtifactId);
        using var face=FontArtifactService.OpenVerified(artifact,fonts[artifact.Id],ct);
        // SKFont keeps a native reference to the explicit face after the managed wrapper is disposed.
        return new SKFont(face,(float)spec.SizePixels){Subpixel=true,Hinting=SKFontHinting.None,Edging=SKFontEdging.Antialias};
    }
    private static void DrawText(SKCanvas canvas,SKFont font,string text,float width,float lineHeight,SKPaint ink,SKPaint? outline,CancellationToken ct)
    {
        float x=0,y=-font.Metrics.Ascent;
        // Scalar advances deliberately match the validator's bounded no-shaping contract.
        // Wrap at a scalar boundary, including CJK; never split a surrogate pair.
        foreach(var rune in text.Replace("\r\n","\n",StringComparison.Ordinal).Replace('\r','\n').EnumerateRunes())
        {
            ct.ThrowIfCancellationRequested(); if(rune.Value==10){x=0;y+=lineHeight;continue;}
            string value=rune.ToString(); float advance=font.MeasureText(value);
            if(x>0&&x+advance>width){x=0;y+=lineHeight;}
            canvas.DrawText(value,x,y,SKTextAlign.Left,font,ink);
            if(outline is not null) canvas.DrawText(value,x,y,SKTextAlign.Left,font,outline);
            x+=advance;
        }
    }
    private static void Arrow(SKCanvas canvas,PointD tip,PointD previous,SKPaint stroke)
    {
        double dx=tip.X-previous.X,dy=tip.Y-previous.Y,length=Math.Sqrt(dx*dx+dy*dy);if(length==0)return;
        dx/=length;dy/=length;double size=Math.Max(6,stroke.StrokeWidth*3);
        using var head=new SKPathBuilder();head.MoveTo((float)(tip.X-dx*size-dy*size*.5),(float)(tip.Y-dy*size+dx*size*.5));head.LineTo(P(tip));head.LineTo((float)(tip.X-dx*size+dy*size*.5),(float)(tip.Y-dy*size-dx*size*.5));using var arrow=head.Detach();canvas.DrawPath(arrow,stroke);
    }
    private static SKBitmap ToBitmap(MagickImage image)
    {
        byte[] bytes=image.GetPixels().ToByteArray(PixelMapping.BGRA)!;
        using var straight=new SKBitmap(new SKImageInfo((int)image.Width,(int)image.Height,SKColorType.Bgra8888,SKAlphaType.Unpremul));
        Marshal.Copy(bytes,0,straight.GetPixels(),bytes.Length);
        var bitmap=new SKBitmap(new SKImageInfo((int)image.Width,(int)image.Height,SKColorType.Bgra8888,SKAlphaType.Premul));
        using var straightPixels=straight.PeekPixels();
        if(!straightPixels.ReadPixels(bitmap.Info,bitmap.GetPixels(),bitmap.RowBytes,0,0)){bitmap.Dispose();throw new InvalidDataException("Unable to read image pixels.");}
        return bitmap;
    }
    private static SKColor Color(Rgba32 c)=>new(c.R,c.G,c.B,c.A);
    private static SKPoint P(PointD p)=>new((float)p.X,(float)p.Y);
    private static SKRect R(RectD b)=>new((float)b.X,(float)b.Y,(float)(b.X+b.Width),(float)(b.Y+b.Height));
}
