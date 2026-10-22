using System.Collections.Immutable;
using System.Windows.Input;
using System.Windows.Media;
using Citrus.Conversion.Editing.Image;
using Citrus.Core.Editing;

namespace Citrus.Windows.Editors.Image;

public enum ImageLayerTool { Select, Arrow, Pen, Rectangle, Ellipse, Highlight, Text, Number, Inset, Magnifier, Solid, Gaussian, Pixelate }

/// <summary>Hit handles and gesture state only. Every visible image pixel comes from ImageRecipeRenderer.</summary>
public sealed class AnnotationOverlay : FrameworkElement
{
    public EditorDocument? Document { get; private set; }
    public SizeI SourceSize { get; private set; }
    public Guid? SelectedId { get; private set; }
    public ImageLayerTool Tool { get; set; }
    public AnnotationStyle NewStyle { get; set; } = new(new(255,107,22,255),new(0,0,0,0),4,1,null);
    public SourceSnapshot? InsetSource { get; set; }
    public string NewText { get; set; } = "Text";
    public double Zoom { get; private set; } = 1;
    public PointD Pan { get; private set; }
    public bool IsDragging { get; private set; }
    public bool IsMask => Document is {Recipe:ImageRecipe image} && image.Masks.Any(m=>m.Id==SelectedId);
    public event Action<EditorDocument,bool>? Changed;
    public event Action? SelectionChanged;
    public event Action<double,PointD>? ViewChanged;
    public event Action<string>? Error;
    public Func<Annotation,RectD?>? TextBounds { get; set; }
    private EditorDocument? startDocument;
    private PointD origin,panOrigin;
    private RectD startBounds;
    private Annotation? gestureText;
    private RectD? gestureTextBounds;
    private int handle;
    private bool creating,panning;
    private List<PointD> stroke=[];
    private ImageRecipe Recipe=>(ImageRecipe)Document!.Recipe;
    private SizeI Canvas=>Recipe.Canvas?.Size??ImageGeometryMapper.Resolve(Recipe,SourceSize).OutputSize;
    private ImageLayerViewport View=>new(Canvas,new(ActualWidth,ActualHeight),Zoom,Pan);
    public AnnotationOverlay(){Focusable=true;ClipToBounds=true;Cursor=Cursors.Cross;}
    public void Update(EditorDocument? document,SizeI source)
    {
        Document=document;SourceSize=source;
        if(document?.Recipe is not ImageRecipe image || (!image.Annotations.Any(a=>a.Id==SelectedId)&&!image.Masks.Any(m=>m.Id==SelectedId)))SelectedId=null;
        InvalidateVisual();
    }
    public void Select(Guid? id){SelectedId=id;InvalidateVisual();SelectionChanged?.Invoke();}
    public void ResetView(){Zoom=1;Pan=default;ViewChanged?.Invoke(Zoom,Pan);InvalidateVisual();}
    public void SetZoom(double zoom)
    {
        Zoom=Math.Clamp(zoom,1,8);if(Zoom==1)Pan=default;ViewChanged?.Invoke(Zoom,Pan);InvalidateVisual();
    }
    public RectD? SelectedBounds()
    {
        if(Document is null||SelectedId is null)return null;
        var a=Recipe.Annotations.FirstOrDefault(a=>a.Id==SelectedId);if(a is not null){var local=LocalTextBounds(a);return a.Geometry is TextGeometry&&local is null?null:ImageLayerGeometry.Bounds(a,local);}
        var mask=Recipe.Masks.FirstOrDefault(m=>m.Id==SelectedId);return mask is null?null:ImageLayerGeometry.MaskToCanvas(Recipe,SourceSize,mask.Bounds);
    }
    private RectD? LocalTextBounds(Annotation layer)
    {
        if(IsDragging&&gestureTextBounds is {} original&&gestureText is {} baseline&&layer.Id==baseline.Id&&layer.Geometry is TextGeometry text)
        {
            double scale=layer.Style.Font!.SizePixels/baseline.Style.Font!.SizePixels;
            return new(text.Origin.X,text.Origin.Y,original.Width*scale,original.Height*scale);
        }
        return TextBounds?.Invoke(layer);
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);dc.DrawRectangle(Brushes.Transparent,null,new Rect(RenderSize));
        if(Document is null||ActualWidth<=0||ActualHeight<=0)return;
        if(SelectedBounds() is not {} b)return;
        var p=View.ToView(new(b.X,b.Y));var r=new Rect(p.X,p.Y,b.Width*View.Scale,b.Height*View.Scale);
        var accent=TryFindResource("Accent") as Brush??new SolidColorBrush(Color.FromRgb(255,107,22));
        var pen=new Pen(accent,2);dc.DrawRectangle(null,new Pen(Brushes.Black,4),r);dc.DrawRectangle(null,pen,r);
        foreach(var corner in new[]{r.TopLeft,r.TopRight,r.BottomRight,r.BottomLeft})dc.DrawRoundedRectangle(Brushes.White,pen,new(corner.X-5,corner.Y-5,10,10),3,3);
        // Magnifier sample is an editor handle, never an alternative rasterized lens.
        if(Recipe.Annotations.FirstOrDefault(a=>a.Id==SelectedId)?.Geometry is MagnifierGeometry m)
        {
            var q=View.ToView(new(m.SampleBounds.X,m.SampleBounds.Y));
            dc.DrawRectangle(null,new Pen(accent,1){DashStyle=DashStyles.Dash},new(q.X,q.Y,m.SampleBounds.Width*View.Scale,m.SampleBounds.Height*View.Scale));
        }
    }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);if(Document is null)return;
        if(e.ChangedButton==MouseButton.Middle){Focus();origin=Position(e);panOrigin=Pan;panning=CaptureMouse();e.Handled=true;return;}
        if(e.ChangedButton!=MouseButton.Left)return;
        Focus();origin=View.ToCanvas(Position(e));startDocument=Document;creating=false;handle=-1;gestureText=null;gestureTextBounds=null;
        if(Tool==ImageLayerTool.Select)
        {
            if(SelectedBounds() is {} bounds)
            {
                var points=ImageLayerGeometry.Corners(bounds).Select(View.ToView).ToArray();var p=Position(e);
                handle=Array.FindIndex(points,q=>Length(q,p)<=13);
            }
            if(handle<0)
            {
                double tolerance=6/View.Scale;
                var available=Recipe.Annotations.Where(a=>a.Geometry is not TextGeometry||LocalTextBounds(a) is not null).ToImmutableArray();
                var id=ImageLayerGeometry.HitTop(available,origin,tolerance,LocalTextBounds);
                id??=Recipe.Masks.Reverse().FirstOrDefault(m=>ImageLayerGeometry.Contains(ImageLayerGeometry.MaskToCanvas(Recipe,SourceSize,m.Bounds),origin))?.Id;
                if(id is null&&Recipe.Annotations.FirstOrDefault(a=>a.Id==SelectedId) is {Geometry:TextGeometry} pending&&LocalTextBounds(pending) is null)
                {
                    Error?.Invoke("Wait for text handles before resizing.");e.Handled=true;return;
                }
                Select(id);
            }
            if(SelectedBounds() is not {} selected)
            {
                if(Recipe.Annotations.Any(a=>a.Geometry is TextGeometry&&LocalTextBounds(a) is null))Error?.Invoke("Wait for text handles before resizing.");
                return;
            }
            startBounds=selected;
            var selectedLayer=Recipe.Annotations.FirstOrDefault(a=>a.Id==SelectedId);
            if(selectedLayer?.Geometry is TextGeometry){gestureText=selectedLayer;gestureTextBounds=LocalTextBounds(selectedLayer);}
        }
        else
        {
            if(!ImageLayerGeometry.Contains(new(0,0,Canvas.Width,Canvas.Height),origin))return;
            try{CreateAt(origin);}catch(Exception error)when(error is ArgumentException or InvalidOperationException){Error?.Invoke(error.Message);return;}
            creating=true;startBounds=SelectedBounds()??new(origin.X,origin.Y,1,1);
        }
        IsDragging=CaptureMouse();if(!IsDragging&&creating&&startDocument is {} original){Document=original;Changed?.Invoke(original,false);}
        InvalidateVisual();e.Handled=true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if(panning){var p=Position(e);Pan=new(panOrigin.X+p.X-origin.X,panOrigin.Y+p.Y-origin.Y);ViewChanged?.Invoke(Zoom,Pan);InvalidateVisual();return;}
        if(!IsDragging||Document is null||startDocument is null)return;
        var p2=View.ToCanvas(Position(e));var point=new PointD(Math.Clamp(p2.X,0,Canvas.Width),Math.Clamp(p2.Y,0,Canvas.Height));
        try
        {
            if(creating&&Recipe.Annotations.FirstOrDefault(a=>a.Id==SelectedId)?.Geometry is TextGeometry){e.Handled=true;return;}
            if(creating&&Tool is ImageLayerTool.Arrow or ImageLayerTool.Pen)
            {
                if(Tool==ImageLayerTool.Arrow)stroke=[origin,point];else if(Length(stroke[^1],point)>=.5&&stroke.Count<100000)stroke.Add(point);
                ReplaceAnnotation(Recipe.Annotations.Single(a=>a.Id==SelectedId) with {Geometry=new StrokeGeometry([..stroke],false,Tool==ImageLayerTool.Arrow)},true);
            }
            else
            {
                var bounds=creating?new RectD(Math.Min(origin.X,point.X),Math.Min(origin.Y,point.Y),Math.Max(1,Math.Abs(point.X-origin.X)),Math.Max(1,Math.Abs(point.Y-origin.Y))):gestureText is not null?ImageLayerGeometry.DragTextBounds(startBounds,handle,point.X-origin.X,point.Y-origin.Y):ImageLayerGeometry.DragBounds(startBounds,handle,point.X-origin.X,point.Y-origin.Y);
                SetBounds(bounds,true,creating?null:startDocument);
            }
            InvalidateVisual();
        }
        catch(ArgumentException error){Error?.Invoke(error.Message);}
        e.Handled=true;
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);if(panning&&e.ChangedButton==MouseButton.Middle){panning=false;ReleaseMouseCapture();e.Handled=true;return;}
        if(!IsDragging||e.ChangedButton!=MouseButton.Left)return;
        IsDragging=false;ReleaseMouseCapture();if(Document is not null)Changed?.Invoke(Document,true);startDocument=null;SelectionChanged?.Invoke();e.Handled=true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);if(IsDragging)CancelDrag();panning=false;
    }
    public void CancelDrag()
    {
        if(!IsDragging)return;IsDragging=false;ReleaseMouseCapture();
        if(startDocument is {} original){Document=original;Changed?.Invoke(original,false);}startDocument=null;InvalidateVisual();SelectionChanged?.Invoke();
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if(Document is null||!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)){base.OnMouseWheel(e);return;}
        var p=Position(e);var anchor=View.ToCanvas(p);Zoom=Math.Clamp(Zoom*(e.Delta>0?1.2:1/1.2),1,8);var after=View.ToView(anchor);Pan=new(Pan.X+p.X-after.X,Pan.Y+p.Y-after.Y);
        if(Zoom==1)Pan=default;ViewChanged?.Invoke(Zoom,Pan);InvalidateVisual();e.Handled=true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if(e.Key==Key.Delete){Delete();e.Handled=true;return;}
        double n=Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)?10:1;var d=e.Key switch{Key.Left=>new PointD(-n,0),Key.Right=>new(n,0),Key.Up=>new(0,-n),Key.Down=>new(0,n),_=>default};
        if(d!=default)
        {
            try
            {
                if(SelectedBounds() is {} bounds)SetBounds(ImageLayerGeometry.Translate(bounds,d.X,d.Y));
                else if(Document is not null&&Recipe.Annotations.FirstOrDefault(a=>a.Id==SelectedId) is {Geometry:TextGeometry} text)ReplaceAnnotation(ImageLayerGeometry.Move(text,d.X,d.Y));
                else return;
                SelectionChanged?.Invoke();
            }
            catch(ArgumentException error){Error?.Invoke(error.Message);}
            e.Handled=true;return;
        }
        base.OnKeyDown(e);
    }
    private void CreateAt(PointD p)
    {
        if(Recipe.Annotations.Length+Recipe.Masks.Length>=1000)throw new ArgumentException("At most 1000 layers.");
        var bounds=new RectD(p.X,p.Y,Math.Max(1,Math.Min(120,Canvas.Width-p.X)),Math.Max(1,Math.Min(80,Canvas.Height-p.Y)));var id=Guid.NewGuid();SelectedId=id;
        if(Tool is ImageLayerTool.Solid or ImageLayerTool.Gaussian or ImageLayerTool.Pixelate)
        {
            var effect=Tool==ImageLayerTool.Solid?MaskEffect.Solid:Tool==ImageLayerTool.Gaussian?MaskEffect.Gaussian:MaskEffect.Pixelate;
            var source=ClampSource(ImageLayerGeometry.MaskFromCanvas(Recipe,SourceSize,bounds));
            Publish(Document! with {Recipe=Recipe with {Masks=Recipe.Masks.Add(new(id,source,effect,new(0,0,0,255),effect==MaskEffect.Solid?0:12))}},false);return;
        }
        if(Tool is ImageLayerTool.Text or ImageLayerTool.Number && (NewStyle.Font is null || !Document!.Fonts.Any(f=>f.Id==NewStyle.Font.ArtifactId)))throw new ArgumentException("Choose a static font before placing text or a numbered callout.");
        if(Tool==ImageLayerTool.Inset&&InsetSource is null)throw new ArgumentException("Import an inset image first.");
        stroke=[p,new(p.X+1,p.Y+1)];
        AnnotationGeometry geometry=Tool switch
        {
            ImageLayerTool.Arrow or ImageLayerTool.Pen=>new StrokeGeometry([..stroke],false,Tool==ImageLayerTool.Arrow),
            ImageLayerTool.Rectangle or ImageLayerTool.Highlight=>new ShapeGeometry(bounds,false,0),ImageLayerTool.Ellipse=>new ShapeGeometry(bounds,true,0),
            ImageLayerTool.Text=>new TextGeometry(p,NewText,Math.Max(1,bounds.Width),0),ImageLayerTool.Number=>new NumberGeometry(new(p.X,p.Y,Math.Min(48,bounds.Width),Math.Min(48,bounds.Height)),ImageLayerGeometry.NextNumber(Recipe.Annotations)),
            ImageLayerTool.Inset=>new InsetImageGeometry(InsetSource!.Id,bounds),ImageLayerTool.Magnifier=>new MagnifierGeometry(bounds,bounds,2),_=>throw new ArgumentException("Select a drawing tool.")
        };
        var style=Tool switch{ImageLayerTool.Highlight=>NewStyle with {Fill=new(255,226,81,160),StrokeWidth=0},ImageLayerTool.Text=>NewStyle with {Fill=NewStyle.Stroke,StrokeWidth=0},ImageLayerTool.Number=>NewStyle with {Fill=new(255,255,255,255)},_=>NewStyle};
        var document=Document!;
        if(geometry is InsetImageGeometry && !document.Sources.Any(s=>s.Id==InsetSource!.Id))document=document with {Sources=document.Sources.Add(InsetSource!)};
        Publish(document with {Recipe=Recipe with {Annotations=Recipe.Annotations.Add(new(id,true,geometry,style))}},false);
    }
    public void SetBounds(RectD bounds,bool transient=false,EditorDocument? baseline=null)
    {
        if(Document is null||SelectedId is null)return;
        if(!double.IsFinite(bounds.X+bounds.Y+bounds.Width+bounds.Height)||bounds.Width<=0||bounds.Height<=0)throw new ArgumentException("Layer bounds must be finite and positive.");
        var sourceRecipe=(ImageRecipe)(baseline??Document).Recipe;var a=sourceRecipe.Annotations.FirstOrDefault(a=>a.Id==SelectedId);
        if(a is not null)ReplaceAnnotation(ImageLayerGeometry.SetBounds(a,bounds,baseline is not null&&gestureText?.Id==a.Id?gestureTextBounds:LocalTextBounds(a)),transient);
        else
        {
            var m=sourceRecipe.Masks.FirstOrDefault(m=>m.Id==SelectedId);if(m is null)return;
            var saved=ClampSource(ImageLayerGeometry.MaskFromCanvas(Recipe,SourceSize,bounds));
            int index=Recipe.Masks.FindIndex(m=>m.Id==SelectedId);Publish(Document with {Recipe=Recipe with {Masks=Recipe.Masks.SetItem(index,m with {Bounds=saved})}},!transient);
        }
    }
    private RectD ClampSource(RectD b)=>ImageLayerGeometry.ClampMaskToSource(b,SourceSize);
    public void ReplaceAnnotation(Annotation layer,bool transient=false)
    {
        int index=Recipe.Annotations.FindIndex(a=>a.Id==layer.Id);if(index<0)return;
        Publish(Document! with {Recipe=Recipe with {Annotations=Recipe.Annotations.SetItem(index,layer)}},!transient);
    }
    public void ReplaceMask(PixelMask mask)
    {
        int index=Recipe.Masks.FindIndex(m=>m.Id==mask.Id);if(index<0)return;
        Publish(Document! with {Recipe=Recipe with {Masks=Recipe.Masks.SetItem(index,mask with {Bounds=ClampSource(mask.Bounds)})}},true);
    }
    public void Delete()
    {
        if(Document is null||SelectedId is null)return;
        var recipe=Recipe with {Annotations=[..Recipe.Annotations.Where(a=>a.Id!=SelectedId)],Masks=[..Recipe.Masks.Where(m=>m.Id!=SelectedId)]};
        var refs=EditorRecipeReferences.Sources(recipe).ToHashSet();var fonts=EditorRecipeReferences.Fonts(recipe).ToHashSet();
        Publish(Document with {Recipe=recipe,Sources=[..Document.Sources.Where(s=>refs.Contains(s.Id))],Fonts=[..Document.Fonts.Where(f=>fonts.Contains(f.Id))]},true);Select(null);
    }
    public void Reorder(int delta)
    {
        if(Document is null)return;int i=Recipe.Annotations.FindIndex(a=>a.Id==SelectedId);
        if(i>=0){int n=Math.Clamp(i+delta,0,Recipe.Annotations.Length-1);if(n==i)return;var item=Recipe.Annotations[i];Publish(Document with {Recipe=Recipe with {Annotations=Recipe.Annotations.RemoveAt(i).Insert(n,item)}},true);}
        else {i=Recipe.Masks.FindIndex(m=>m.Id==SelectedId);if(i<0)return;int n=Math.Clamp(i+delta,0,Recipe.Masks.Length-1);if(n==i)return;var item=Recipe.Masks[i];Publish(Document with {Recipe=Recipe with {Masks=Recipe.Masks.RemoveAt(i).Insert(n,item)}},true);}
        SelectionChanged?.Invoke();
    }
    private void Publish(EditorDocument document,bool commit){EditorHostViewModel.Validate(document,SourceSize);Document=document;Changed?.Invoke(document,commit);InvalidateVisual();}
    private PointD Position(MouseEventArgs e){var p=e.GetPosition(this);return new(p.X,p.Y);}
    private static double Length(PointD p,PointD q)=>Math.Sqrt(Math.Pow(p.X-q.X,2)+Math.Pow(p.Y-q.Y,2));
}
internal static class LayerArrayExtensions
{
    public static int FindIndex<T>(this ImmutableArray<T> all,Func<T,bool> predicate){for(int i=0;i<all.Length;i++)if(predicate(all[i]))return i;return -1;}
}
