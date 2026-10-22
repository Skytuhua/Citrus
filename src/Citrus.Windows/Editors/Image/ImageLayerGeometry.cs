using System.Collections.Immutable;
using Citrus.Conversion.Editing.Image;
using Citrus.Core.Editing;

namespace Citrus.Windows.Editors.Image;

/// <summary>The view owns DIPs; saved annotations own final C pixels, masks own normalized S pixels.</summary>
public readonly record struct ImageLayerViewport(SizeI Canvas, PointD ViewSize, double Zoom, PointD Pan)
{
    public double Scale => Math.Min(ViewSize.X / Canvas.Width, ViewSize.Y / Canvas.Height) * Zoom;
    public PointD ToView(PointD c) => new((ViewSize.X-Canvas.Width*Scale)/2+Pan.X+c.X*Scale,(ViewSize.Y-Canvas.Height*Scale)/2+Pan.Y+c.Y*Scale);
    public PointD ToCanvas(PointD v) => new((v.X-(ViewSize.X-Canvas.Width*Scale)/2-Pan.X)/Scale,(v.Y-(ViewSize.Y-Canvas.Height*Scale)/2-Pan.Y)/Scale);
}

/// <summary>Pure interaction math. Never rasterizes a second set of annotation pixels.</summary>
public static class ImageLayerGeometry
{
    public static RectD Bounds(Annotation layer, RectD? textBounds = null) => layer.Geometry switch
    {
        StrokeGeometry s => Enclose(s.Points), ShapeGeometry s => s.Bounds, NumberGeometry n => n.Bounds,
        InsetImageGeometry i => i.Bounds, MagnifierGeometry m => m.LensBounds,
        TextGeometry t => RotatedBounds(textBounds ?? TextBox(t,layer.Style.Font!),t.Origin,t.Rotation),
        _ => throw new NotSupportedException("Unknown layer geometry.")
    };
    public static RectD TextBox(TextGeometry t, FontSpec font) => new(t.Origin.X,t.Origin.Y,t.WrapWidth,Math.Max(font.LineHeight,font.LineHeight*(t.Text.Count(c=>c=='\n')+1+Math.Floor(t.Text.Length*font.SizePixels*.65/t.WrapWidth))));
    public static Guid? HitTop(ImmutableArray<Annotation> all, PointD point, double tolerance, Func<Annotation,RectD?>? textBounds=null)
    {
        for(int i=all.Length-1;i>=0;i--) if(all[i].Visible&&Hit(all[i],point,tolerance,textBounds?.Invoke(all[i]))) return all[i].Id;
        return null;
    }
    public static bool Hit(Annotation layer, PointD p, double tolerance, RectD? textBounds=null)
    {
        if(!layer.Visible||layer.Style.Opacity<=0)return false;
        double edge=tolerance+layer.Style.StrokeWidth/2;
        switch(layer.Geometry)
        {
            case StrokeGeometry s:
                for(int i=1;i<s.Points.Length;i++) if(Distance(p,s.Points[i-1],s.Points[i])<=edge) return true;
                // Arrow heads extend away from their shaft; select their actual triangular neighborhood.
                return (s.ArrowStart&&ArrowHit(p,s.Points[0],s.Points[1],edge,layer.Style.StrokeWidth)) || (s.ArrowEnd&&ArrowHit(p,s.Points[^1],s.Points[^2],edge,layer.Style.StrokeWidth));
            case ShapeGeometry s when s.Ellipse:
            case NumberGeometry:
                var b=Bounds(layer); double nx=(p.X-b.X-b.Width/2)/(b.Width/2),ny=(p.Y-b.Y-b.Height/2)/(b.Height/2),d=Math.Sqrt(nx*nx+ny*ny);
                double pad=edge/Math.Max(.5,Math.Min(b.Width,b.Height)/2);
                return d<=1+pad&&(layer.Style.Fill.A>0||Math.Abs(d-1)<=pad);
            case ShapeGeometry s:
                if(!RoundedContains(Inflate(s.Bounds,edge),Math.Min(s.Radius,Math.Min(s.Bounds.Width,s.Bounds.Height)/2)+edge,p))return false;
                return layer.Style.Fill.A>0 || !RoundedContains(Inflate(s.Bounds,-Math.Min(edge,Math.Min(s.Bounds.Width,s.Bounds.Height)/2)),Math.Max(0,s.Radius-edge),p);
            case TextGeometry t:
                p=Rotate(p,t.Origin,-t.Rotation);return Contains(Inflate(textBounds??TextBox(t,layer.Style.Font!),tolerance),p);
            default:return Contains(Inflate(Bounds(layer),tolerance),p);
        }
    }
    private static bool ArrowHit(PointD p,PointD tip,PointD previous,double tolerance,double stroke)
    {
        double dx=tip.X-previous.X,dy=tip.Y-previous.Y,n=Math.Sqrt(dx*dx+dy*dy);if(n==0)return false;dx/=n;dy/=n;double size=Math.Max(6,stroke*3);
        return Distance(p,new(tip.X-dx*size-dy*size*.5,tip.Y-dy*size+dx*size*.5),tip)<=tolerance||Distance(p,new(tip.X-dx*size+dy*size*.5,tip.Y-dy*size-dx*size*.5),tip)<=tolerance;
    }
    public static RectD MaskToCanvas(ImageRecipe recipe,SizeI source,RectD mask)=>MapRect(mask,p=>ImageGeometryMapper.MapToCanvas(recipe,source,p));
    public static RectD MaskFromCanvas(ImageRecipe recipe,SizeI source,RectD canvas)=>MapRect(canvas,p=>ImageGeometryMapper.MapFromCanvas(recipe,source,p));
    public static RectD ClampMaskToSource(RectD b,SizeI source)
    {
        // One C pixel can cover a fraction of an S pixel after upscaling. Never quantize it to a whole S pixel.
        const double minimum=1e-6;
        double x=Math.Clamp(b.X,0,source.Width-minimum),y=Math.Clamp(b.Y,0,source.Height-minimum);
        return new(x,y,Math.Clamp(b.Width,minimum,source.Width-x),Math.Clamp(b.Height,minimum,source.Height-y));
    }
    public static RectD Translate(RectD b,double dx,double dy)=>b with {X=b.X+dx,Y=b.Y+dy};
    public static int NextNumber(ImmutableArray<Annotation> all)
    {
        int max=all.Select(a=>a.Geometry).OfType<NumberGeometry>().Select(n=>n.Number).DefaultIfEmpty(0).Max();
        return max==int.MaxValue?throw new ArgumentException("The largest callout number is already in use. Choose a smaller positive number."):max+1;
    }
    public static Annotation Move(Annotation a,double dx,double dy)=>a with {Geometry=a.Geometry switch
    {
        StrokeGeometry s=>s with {Points=[..s.Points.Select(p=>new PointD(p.X+dx,p.Y+dy))]},ShapeGeometry s=>s with {Bounds=Translate(s.Bounds,dx,dy)},
        TextGeometry t=>t with {Origin=new(t.Origin.X+dx,t.Origin.Y+dy)},NumberGeometry n=>n with {Bounds=Translate(n.Bounds,dx,dy)},
        InsetImageGeometry i=>i with {Bounds=Translate(i.Bounds,dx,dy)},MagnifierGeometry m=>m with {LensBounds=Translate(m.LensBounds,dx,dy)},_=>a.Geometry
    }};
    public static Annotation SetBounds(Annotation a,RectD next,RectD? textBounds=null)
    {
        var old=Bounds(a,textBounds);
        if(a.Geometry is TextGeometry text)
        {
            if(textBounds is null)throw new ArgumentException("Wait for text handles before resizing.");
            double scale=ProportionalScale(old,next);var font=a.Style.Font!;
            if(font.SizePixels*scale is <1 or >16384 || font.LineHeight*scale is <1 or >16384 || text.WrapWidth*scale is <1 or >16384)
                throw new ArgumentException("Text scaling must keep font size, leading and wrap width between 1 and 16384 pixels.");
            return a with
            {
                Style=a.Style with {Font=font with {SizePixels=font.SizePixels*scale,LineHeight=font.LineHeight*scale}},
                Geometry=text with {Origin=new(next.X+(text.Origin.X-old.X)*scale,next.Y+(text.Origin.Y-old.Y)*scale),WrapWidth=text.WrapWidth*scale}
            };
        }
        double sx=next.Width/Math.Max(old.Width,.001),sy=next.Height/Math.Max(old.Height,.001);
        PointD Map(PointD p)=>new(next.X+(p.X-old.X)*sx,next.Y+(p.Y-old.Y)*sy);
        return a with {Geometry=a.Geometry switch
        {
            StrokeGeometry s=>s with {Points=[..s.Points.Select((p,i)=>new PointD(old.Width<=.001?next.X+next.Width*i/(s.Points.Length-1):Map(p).X,old.Height<=.001?next.Y+next.Height*i/(s.Points.Length-1):Map(p).Y))]},ShapeGeometry s=>s with {Bounds=next,Radius=Math.Min(s.Radius,Math.Min(next.Width,next.Height)/2)},
            TextGeometry t=>t with {Origin=Map(t.Origin),WrapWidth=Math.Max(1,t.WrapWidth*sx)},NumberGeometry n=>n with {Bounds=next},
            InsetImageGeometry i=>i with {Bounds=next},MagnifierGeometry m=>m with {LensBounds=next},_=>a.Geometry
        }};
    }
    public static double ProportionalScale(RectD start,RectD requested)
    {
        double x=requested.Width/start.Width,y=requested.Height/start.Height;
        return Math.Abs(x-1)>=Math.Abs(y-1)?x:y;
    }
    public static RectD DragTextBounds(RectD start,int handle,double dx,double dy)
    {
        var requested=DragBounds(start,handle,dx,dy);if(handle<0)return requested;
        double scale=ProportionalScale(start,requested),width=start.Width*scale,height=start.Height*scale;
        bool left=handle is 0 or 3,top=handle is 0 or 1;
        return new(left?start.X+start.Width-width:start.X,top?start.Y+start.Height-height:start.Y,width,height);
    }
    public static RectD DragBounds(RectD start,int handle,double dx,double dy)
    {
        if(handle<0)return Translate(start,dx,dy);
        bool left=handle is 0 or 3,top=handle is 0 or 1;double x=left?Math.Min(start.X+dx,start.X+start.Width-1):start.X,y=top?Math.Min(start.Y+dy,start.Y+start.Height-1):start.Y;
        return new(x,y,left?start.X+start.Width-x:Math.Max(1,start.Width+dx),top?start.Y+start.Height-y:Math.Max(1,start.Height+dy));
    }
    public static RectD Enclose(IEnumerable<PointD> points)
    {
        var all=points.ToArray();double x=all.Min(p=>p.X),y=all.Min(p=>p.Y);return new(x,y,Math.Max(.001,all.Max(p=>p.X)-x),Math.Max(.001,all.Max(p=>p.Y)-y));
    }
    public static RectD MapRect(RectD r,Func<PointD,PointD> map)=>Enclose(Corners(r).Select(map));
    public static PointD[] Corners(RectD b)=>[new(b.X,b.Y),new(b.X+b.Width,b.Y),new(b.X+b.Width,b.Y+b.Height),new(b.X,b.Y+b.Height)];
    public static bool Contains(RectD b,PointD p)=>p.X>=b.X&&p.Y>=b.Y&&p.X<=b.X+b.Width&&p.Y<=b.Y+b.Height;
    private static bool RoundedContains(RectD b,double radius,PointD p)
    {
        if(!Contains(b,p))return false;double r=Math.Clamp(radius,0,Math.Min(b.Width,b.Height)/2);if(r==0)return true;
        double x=Math.Clamp(p.X,b.X+r,b.X+b.Width-r),y=Math.Clamp(p.Y,b.Y+r,b.Y+b.Height-r);
        return Math.Pow(p.X-x,2)+Math.Pow(p.Y-y,2)<=r*r;
    }
    private static RectD Inflate(RectD b,double amount)=>new(b.X-amount,b.Y-amount,Math.Max(0,b.Width+amount*2),Math.Max(0,b.Height+amount*2));
    public static PointD Rotate(PointD p,PointD origin,double degrees){double angle=degrees*Math.PI/180,c=Math.Cos(angle),s=Math.Sin(angle),x=p.X-origin.X,y=p.Y-origin.Y;return new(origin.X+x*c-y*s,origin.Y+x*s+y*c);}
    private static RectD RotatedBounds(RectD b,PointD o,double degrees)=>MapRect(b,p=>Rotate(p,o,degrees));
    private static double Distance(PointD p,PointD a,PointD b){double dx=b.X-a.X,dy=b.Y-a.Y,n=dx*dx+dy*dy,t=n==0?0:Math.Clamp(((p.X-a.X)*dx+(p.Y-a.Y)*dy)/n,0,1);return Math.Sqrt(Math.Pow(p.X-a.X-t*dx,2)+Math.Pow(p.Y-a.Y-t*dy,2));}
}
