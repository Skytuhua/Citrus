using System.Globalization;
using System.Windows.Automation;
using System.Windows.Controls;
using Citrus.Conversion;
using Citrus.Conversion.Editing.Image;
using Citrus.Core.Editing;
using Microsoft.Win32;

namespace Citrus.Windows.Editors.Image;

/// <summary>A contextual inspector: choose a tool, then edit only the selected layer.</summary>
public sealed class AnnotationInspector : StackPanel
{
    private readonly EditorHostViewModel model;
    private readonly AnnotationOverlay overlay;
    private readonly ImageEditorServices services;
    private readonly Window owner;
    private readonly InstalledFontCatalog fontCatalog;
    private readonly Action refresh,rebuild;
    private readonly Action<string> error;
    private readonly CancellationToken lifetime;
    private readonly List<Action> pending=[];
    private readonly List<Action> geometryRefresh=[];
    private sealed class FieldState(string accepted){public string Accepted=accepted;public bool Committing;}
    public void SyncGeometry(){foreach(var update in geometryRefresh)update();}
    private EditorDocument Document=>model.Selected!.Draft!;
    private ImageRecipe Recipe=>(ImageRecipe)Document.Recipe;
    private bool busy;
    private MaskInspector? maskInspector;
    public AnnotationInspector(EditorHostViewModel model,AnnotationOverlay overlay,ImageEditorServices services,InstalledFontCatalog fontCatalog,Window owner,bool masks,Action refresh,Action rebuild,Action<string> error,CancellationToken lifetime)
    {
        this.model=model;this.overlay=overlay;this.services=services;this.owner=owner;this.fontCatalog=fontCatalog;this.refresh=refresh;this.rebuild=rebuild;this.error=error;this.lifetime=lifetime;
        Title(masks?"Manual masks":"Annotations");
        Note(masks?"Draw the area to cover.":"Drag to draw; click to place text.");
        var tools=masks?new[]{ImageLayerTool.Select,ImageLayerTool.Solid,ImageLayerTool.Gaussian,ImageLayerTool.Pixelate}:new[]{ImageLayerTool.Select,ImageLayerTool.Arrow,ImageLayerTool.Pen,ImageLayerTool.Rectangle,ImageLayerTool.Ellipse,ImageLayerTool.Highlight,ImageLayerTool.Text,ImageLayerTool.Number,ImageLayerTool.Inset,ImageLayerTool.Magnifier};
        var toolbar=new WrapPanel{Margin=new(0,8,0,12)};
        foreach(var tool in tools)
        {
            var b=MakeButton(Label(tool),()=>{FlushFields();overlay.Tool=tool;rebuild();});b.Margin=new(0,0,5,5);b.Padding=new(9,7,9,7);
            if(overlay.Tool==tool){b.SetResourceReference(BackgroundProperty,"Accent");b.SetResourceReference(Control.ForegroundProperty,"SelectedPetalText");}
            toolbar.Children.Add(b);
        }
        Children.Add(toolbar);
        if(overlay.Tool is ImageLayerTool.Text or ImageLayerTool.Number || Recipe.Annotations.FirstOrDefault(a=>a.Id==overlay.SelectedId)?.Geometry is TextGeometry or NumberGeometry) FontControls();
        if(overlay.Tool==ImageLayerTool.Inset) AddButton("Import inset image…",ImportInset);
        if(overlay.SelectedId is {} selected)
        {
            var a=Recipe.Annotations.FirstOrDefault(a=>a.Id==selected);var mask=Recipe.Masks.FirstOrDefault(m=>m.Id==selected);
            if(a is not null)Inspect(a);else if(mask is not null){maskInspector=new MaskInspector(mask,overlay,refresh,error,rebuild);Children.Add(maskInspector);}
        }
        LayerList();
        Note("Arrow keys: move · Shift: move faster · Ctrl+wheel: zoom · Middle-drag: pan.");
    }
    private static string Label(ImageLayerTool tool)=>tool switch{ImageLayerTool.Select=>"↖ Select",ImageLayerTool.Arrow=>"↗ Arrow",ImageLayerTool.Pen=>"∿ Pen",ImageLayerTool.Rectangle=>"□ Box",ImageLayerTool.Ellipse=>"○ Ellipse",ImageLayerTool.Highlight=>"▰ Highlight",ImageLayerTool.Text=>"T Text",ImageLayerTool.Number=>"① Number",ImageLayerTool.Inset=>"▧ Inset",ImageLayerTool.Magnifier=>"⌕ Magnify",ImageLayerTool.Solid=>"Solid",ImageLayerTool.Gaussian=>"Blur",_=>"Pixelate"};
    private void Title(string text)=>Children.Add(new TextBlock{Text=text,FontWeight=FontWeights.SemiBold,Margin=new(0,5,0,7),TextWrapping=TextWrapping.Wrap});
    private void Note(string text){var t=new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,Margin=new(0,3,0,9)};t.SetResourceReference(TextBlock.FontSizeProperty,"MetadataSize");t.SetResourceReference(TextBlock.ForegroundProperty,"SecondaryText");Children.Add(t);}
    private Button MakeButton(string text,Action action)
    {
        var b=new Button{Content=new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap},HorizontalContentAlignment=HorizontalAlignment.Center};b.SetResourceReference(StyleProperty,"SecondaryButton");b.Click+=(_,_)=>Try(action);return b;
    }
    private void AddButton(string text,Action action){var b=MakeButton(text,action);b.Margin=new(0,4,0,8);Children.Add(b);}
    private void Try(Action action){try{action();error("");}catch(Exception e)when(e is ArgumentException or IOException or NotSupportedException or InvalidOperationException){error(e.Message);}}
    public void FlushFields(){foreach(var commit in pending)commit();maskInspector?.FlushFields();}
    private TextBox Field(string label,string initial,Action<string> apply,bool multiline=false)
    {
        Children.Add(new TextBlock{Text=label,TextWrapping=TextWrapping.Wrap,Margin=new(0,10,0,4)});
        var box=new TextBox{Text=initial,Padding=new(8),AcceptsReturn=multiline,TextWrapping=multiline?TextWrapping.Wrap:TextWrapping.NoWrap,MinHeight=multiline?76:0,MaxHeight=multiline?180:double.PositiveInfinity,VerticalScrollBarVisibility=multiline?ScrollBarVisibility.Auto:ScrollBarVisibility.Disabled};
        AutomationProperties.SetName(box,label);Children.Add(box);var state=new FieldState(initial);box.Tag=state;
        void Commit(){if(state.Committing||box.Text==state.Accepted)return;state.Committing=true;try{apply(box.Text);state.Accepted=box.Text;}finally{state.Committing=false;}}
        pending.Add(Commit);box.LostKeyboardFocus+=(_,_)=>{Try(Commit);SyncGeometry();};box.KeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Enter&&!multiline){Try(Commit);SyncGeometry();e.Handled=true;}};return box;
    }
    private void TrackValue(TextBox box,Func<string> value)
    {
        geometryRefresh.Add(()=>
        {
            var state=(FieldState)box.Tag;
            if(!box.IsKeyboardFocusWithin&&!state.Committing&&box.Text==state.Accepted){state.Accepted=value();box.Text=state.Accepted;}
        });
    }
    private static double Number(string value,double min=-16384,double max=32768)=>double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out double d)&&double.IsFinite(d)&&d>=min&&d<=max?d:throw new ArgumentException($"Enter a finite value from {min} to {max}.");
    private static string N(double d)=>d.ToString("0.###",CultureInfo.InvariantCulture);
    private void Inspect(Annotation layer)
    {
        Title("Selected "+LayerName(layer));
        bool textLayer=layer.Geometry is TextGeometry;
        if(textLayer)Note("Width and height stay linked. Change wrap width to reflow text.");
        var preparing=new TextBlock{Text="Preparing text…",TextWrapping=TextWrapping.Wrap,Margin=new(0,4,0,8)};
        if(textLayer)Children.Add(preparing);
        var initial=overlay.SelectedBounds();
        foreach(string name in new[]{"X","Y","Width","Height"})
        {
            double Read(RectD b)=>name switch{"X"=>b.X,"Y"=>b.Y,"Width"=>b.Width,_=>b.Height};
            var box=Field(name+" · px",initial is {} value?N(Read(value)):"",s=>
            {
                var bounds=overlay.SelectedBounds()??throw new ArgumentException("Wait for text handles before resizing.");
                double v=Number(s,name is "Width" or "Height"?1:-16384,16384);
                bounds=name switch{"X"=>bounds with {X=v},"Y"=>bounds with {Y=v},"Width"=>bounds with {Width=v},_=>bounds with {Height=v}};
                overlay.SetBounds(bounds);refresh();
            });
            geometryRefresh.Add(()=>
            {
                var bounds=overlay.SelectedBounds();var state=(FieldState)box.Tag;
                box.IsEnabled=bounds is not null||state.Committing||box.IsKeyboardFocusWithin;
                if(bounds is {} current&&!box.IsKeyboardFocusWithin&&!state.Committing&&box.Text==state.Accepted){state.Accepted=N(Read(current));box.Text=state.Accepted;}
            });
        }
        if(textLayer)geometryRefresh.Add(()=>preparing.Visibility=overlay.SelectedBounds() is null?Visibility.Visible:Visibility.Collapsed);
        SyncGeometry();
        if(layer.Geometry is TextGeometry text)
        {
            var wrap=Field("Wrap width · px",N(text.WrapWidth),s=>{var a=Current();overlay.ReplaceAnnotation(a with {Geometry=((TextGeometry)a.Geometry) with {WrapWidth=Number(s,1,16384)}});refresh();});
            TrackValue(wrap,()=>Recipe.Annotations.FirstOrDefault(a=>a.Id==layer.Id)?.Geometry is TextGeometry current?N(current.WrapWidth):N(text.WrapWidth));
            Field("Text",text.Text,value=>{var a=Current();ValidateText(a,value);overlay.ReplaceAnnotation(a with {Geometry=((TextGeometry)a.Geometry) with {Text=value}});refresh();},true);
            Field("Rotation · degrees",N(text.Rotation),s=>{var a=Current();overlay.ReplaceAnnotation(a with {Geometry=((TextGeometry)a.Geometry) with {Rotation=Number(s,-360,360)}});refresh();});
            AddButton("Apply text",()=>{FlushFields();refresh();});
        }
        if(layer.Geometry is NumberGeometry number)Field("Callout number",number.Number.ToString(CultureInfo.InvariantCulture),s=>{if(!int.TryParse(s,out int n)||n<=0)throw new ArgumentException("Callout number must be a positive whole number.");var a=Current();ValidateText(a,s);overlay.ReplaceAnnotation(a with {Geometry=((NumberGeometry)a.Geometry) with {Number=n}});refresh();});
        if(layer.Geometry is MagnifierGeometry magnifier)
        {
            Field("Magnification",N(magnifier.Scale),s=>{var a=Current();overlay.ReplaceAnnotation(a with {Geometry=((MagnifierGeometry)a.Geometry) with {Scale=Number(s,1,100)}});refresh();});
            foreach(var (name,value) in new[]{("Sample X",magnifier.SampleBounds.X),("Sample Y",magnifier.SampleBounds.Y),("Sample width",magnifier.SampleBounds.Width),("Sample height",magnifier.SampleBounds.Height)})
                Field(name+" · px",N(value),s=>{var a=Current();var g=(MagnifierGeometry)a.Geometry;double v=Number(s,name is "Sample width" or "Sample height"?1:0,16384);var b=g.SampleBounds;b=name switch{"Sample X"=>b with {X=v},"Sample Y"=>b with {Y=v},"Sample width"=>b with {Width=v},_=>b with {Height=v}};overlay.ReplaceAnnotation(a with {Geometry=g with {SampleBounds=b}});refresh();});
        }
        Field("Stroke width · px",N(layer.Style.StrokeWidth),s=>{var a=Current();overlay.ReplaceAnnotation(a with {Style=a.Style with {StrokeWidth=Number(s,0,16384)}});refresh();});
        Field("Opacity · 0–100%",N(layer.Style.Opacity*100),s=>{var a=Current();overlay.ReplaceAnnotation(a with {Style=a.Style with {Opacity=Number(s,0,100)/100}});refresh();});
        ColorChoices("Stroke / text color",layer.Style.Stroke,c=>{var a=Current();overlay.ReplaceAnnotation(a with {Style=a.Style with {Stroke=c,Fill=a.Geometry is TextGeometry?c:a.Style.Fill}});refresh();});
        if(layer.Geometry is ShapeGeometry or NumberGeometry)ColorChoices("Fill",layer.Style.Fill,c=>{var a=Current();overlay.ReplaceAnnotation(a with {Style=a.Style with {Fill=c}});refresh();},true);
        var row=new WrapPanel{Margin=new(0,12,0,8)};foreach(var (label,action) in new[]{("Back",new Action(()=>overlay.Reorder(-1))),("Forward",new Action(()=>overlay.Reorder(1))),("Delete",new Action(overlay.Delete))}){var b=MakeButton(label,()=>{action();rebuild();refresh();});b.Margin=new(0,0,5,5);row.Children.Add(b);}Children.Add(row);
        Annotation Current()=>((ImageRecipe)model.Selected!.Draft!.Recipe).Annotations.Single(a=>a.Id==layer.Id);
    }
    private void ValidateText(Annotation a,string text)
    {
        if(string.IsNullOrEmpty(text)||text.Length>100000)throw new ArgumentException("Text must contain 1–100000 characters.");
        if(!Document.Fonts.Any(f=>f.Id==a.Style.Font!.ArtifactId))throw new ArgumentException("Choose a font first.");
        // Native font IO and glyph validation run in the preview worker and export preflight, never in pointer/keyboard handlers.
    }
    private void ColorChoices(string label,Rgba32 current,Action<Rgba32> choose,bool clear=false)
    {
        Title(label);var row=new WrapPanel();
        var values=new[]{("Orange",new Rgba32(255,107,22,255)),("Ink",new Rgba32(23,33,43,255)),("White",new Rgba32(255,255,255,255)),("Red",new Rgba32(225,45,55,255)),("Blue",new Rgba32(31,112,235,255)),("Yellow",new Rgba32(255,226,81,160))}.ToList();if(clear)values.Add(("Clear",new(0,0,0,0)));
        foreach(var (name,c) in values){var b=MakeButton(name,()=>choose(c));b.Padding=new(7,5,7,5);b.Margin=new(0,0,4,4);if(c==current)b.SetResourceReference(Control.BorderBrushProperty,"Accent");row.Children.Add(b);}Children.Add(row);
    }
    private void FontControls()
    {
        Title("Font");
        var selected=Recipe.Annotations.FirstOrDefault(a=>a.Id==overlay.SelectedId);var spec=selected?.Style.Font??overlay.NewStyle.Font;var artifact=Document.Fonts.FirstOrDefault(f=>f.Id==spec?.ArtifactId);
        Note(artifact is null?"Choose a font for your text.":artifact.Family+" · "+FriendlyStyle(artifact.FaceName));
        if(overlay.Tool==ImageLayerTool.Text && selected?.Geometry is not TextGeometry)
            Field("Text to place",overlay.NewText,value=>
            {
                if(string.IsNullOrWhiteSpace(value)||value.Length>100000)throw new ArgumentException("Enter text before placing it.");
                overlay.NewText=value;
            },true);
        var picker=new ComboBox{DisplayMemberPath=nameof(InstalledFontChoice.Label),Margin=new(0,4,0,6),MaxDropDownHeight=240,IsTextSearchEnabled=true,IsEnabled=false};AutomationProperties.SetName(picker,"Installed fonts");Children.Add(picker);
        var status=new TextBlock{Text="Loading fonts…",TextWrapping=TextWrapping.Wrap,Margin=new(0,3,0,6)};Children.Add(status);
        var face=new TextBox{Text=artifact?.FaceIndex.ToString(CultureInfo.InvariantCulture)??"0",Padding=new(8)};AutomationProperties.SetName(face,"Font collection face index");
        bool populating=true;Button? browse=null;
        picker.SelectionChanged+=(_,_)=>{if(!populating&&picker.SelectedItem is InstalledFontChoice choice)Resolve(choice.Path,0);};
        LoadFonts();
        async void LoadFonts()
        {
            try
            {
                var choices=await fontCatalog.GetAsync(lifetime);if(lifetime.IsCancellationRequested)return;
                picker.ItemsSource=choices;picker.SelectedItem=artifact is null?null:choices.FirstOrDefault(c=>c.Path.Equals(artifact.OriginalPath,StringComparison.OrdinalIgnoreCase));
                picker.IsEnabled=true;status.Text=choices.IsEmpty?"Choose a font file in Advanced.":"";populating=false;
            }
            catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
            catch(Exception e)when(e is not OutOfMemoryException){if(!lifetime.IsCancellationRequested){status.Text="Choose a font file in Advanced.";populating=false;}}
        }
        async void Resolve(string path,int index)
        {
            if(busy)return;
            busy=true;picker.IsEnabled=false;if(browse is not null)browse.IsEnabled=false;status.Text="Choosing font…";var targetRow=model.Selected;var targetLayer=overlay.SelectedId;var targetDraft=targetRow?.Draft;
            try
            {
                var font=await services.Fonts.CaptureAsync(path,index,lifetime);if(lifetime.IsCancellationRequested || model.Selected!=targetRow || overlay.SelectedId!=targetLayer || model.Selected?.Draft!=targetDraft)return;
                var doc=Document;var image=(ImageRecipe)doc.Recipe;var current=image.Annotations.FirstOrDefault(a=>a.Id==overlay.SelectedId);
                var fontSpec=new FontSpec(font.Id,current?.Style.Font?.SizePixels??32,current?.Style.Font?.LineHeight??40);
                string text=current?.Geometry switch{TextGeometry t=>t.Text,NumberGeometry n=>n.Number.ToString(CultureInfo.InvariantCulture),_=>overlay.Tool==ImageLayerTool.Number?"0123456789":overlay.NewText};
                var issues=await Task.Run(()=>FontTextValidator.Validate(font,text),lifetime);if(!issues.IsEmpty)throw new ArgumentException(string.Join(" ",issues.Select(i=>i.Message)));
                if(model.Selected!=targetRow || overlay.SelectedId!=targetLayer || model.Selected?.Draft!=targetDraft)return;
                if(current?.Geometry is TextGeometry or NumberGeometry)image=image with {Annotations=image.Annotations.SetItem(image.Annotations.IndexOf(current),current with {Style=current.Style with {Font=fontSpec}})};
                var references=EditorRecipeReferences.Fonts(image).ToHashSet();
                model.Change(doc with {Fonts=[..doc.Fonts.Where(f=>references.Contains(f.Id)),font],Recipe=image});overlay.NewStyle=overlay.NewStyle with {Font=fontSpec};overlay.Update(model.Selected!.Draft,model.Selected.Size);error("");rebuild();refresh();
            }
            catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
            catch(Exception e)when(e is not OutOfMemoryException){if(!lifetime.IsCancellationRequested)error("Font not selected. "+e.Message);}
            finally{busy=false;if(!lifetime.IsCancellationRequested){picker.IsEnabled=true;if(browse is not null)browse.IsEnabled=true;status.Text="";}}
        }
        var advanced=new Expander{Header="Advanced",IsExpanded=false,Margin=new(0,8,0,8)};var advancedBody=new StackPanel();advanced.Content=advancedBody;
        advancedBody.Children.Add(new TextBlock{Text=artifact is null?"Choose a font file.":Path.GetFileName(artifact.OriginalPath),TextWrapping=TextWrapping.Wrap,Margin=new(0,6,0,8)});
        advancedBody.Children.Add(new TextBlock{Text="Font face index (from 0)",TextWrapping=TextWrapping.Wrap});advancedBody.Children.Add(face);
        browse=MakeButton("Choose font file…",()=>
        {
            if(!int.TryParse(face.Text,out int index)||index<0)throw new ArgumentException("Face index must be a non-negative whole number.");
            var file=new OpenFileDialog{Title="Choose a font",Filter="Font files|*.ttf;*.otf;*.ttc;*.otc"};if(file.ShowDialog(owner)==true)Resolve(file.FileName,index);
        });browse.Margin=new(0,8,0,4);advancedBody.Children.Add(browse);Children.Add(advanced);
        if(spec is not null)
        {
            var size=Field("Font size · px",N(spec.SizePixels),s=>FontSize(Number(s,1,16384),null));var leading=Field("Line height · px",N(spec.LineHeight),s=>FontSize(null,Number(s,1,16384)));
            FontSpec CurrentFont()=>Recipe.Annotations.FirstOrDefault(a=>a.Id==overlay.SelectedId)?.Style.Font??overlay.NewStyle.Font??spec;
            TrackValue(size,()=>N(CurrentFont().SizePixels));TrackValue(leading,()=>N(CurrentFont().LineHeight));
            void FontSize(double? size,double? leading){var current=((ImageRecipe)Document.Recipe).Annotations.FirstOrDefault(a=>a.Id==overlay.SelectedId);if(current?.Style.Font is {} f)overlay.ReplaceAnnotation(current with {Style=current.Style with {Font=f with {SizePixels=size??f.SizePixels,LineHeight=leading??f.LineHeight}}});else if(overlay.NewStyle.Font is {} n)overlay.NewStyle=overlay.NewStyle with {Font=n with {SizePixels=size??n.SizePixels,LineHeight=leading??n.LineHeight}};refresh();}
        }

    }
    private async void ImportInset()
    {
        if(busy)return;var file=new OpenFileDialog{Title="Import an inset image",Filter="Raster images|*.png;*.jpg;*.jpeg;*.webp;*.tif;*.tiff;*.bmp;*.gif;*.avif"};if(file.ShowDialog(owner)!=true)return;
        busy=true;try{var source=await model.ImportAuxiliaryAsync(file.FileName,lifetime);if(lifetime.IsCancellationRequested)return;overlay.InsetSource=source;overlay.Tool=ImageLayerTool.Inset;error("");rebuild();refresh();}
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}catch(Exception e)when(e is not OutOfMemoryException){if(!lifetime.IsCancellationRequested)error(e.Message);}finally{busy=false;}
    }
    private void LayerList()
    {
        Title($"Layers · {Recipe.Annotations.Length+Recipe.Masks.Length}");var list=new ListBox{MaxHeight=200,Margin=new(0,4,0,10)};AutomationProperties.SetName(list,"Annotation and manual mask layers, topmost first");
        foreach(var layer in Recipe.Annotations.Reverse())list.Items.Add(new ListBoxItem{Content=new TextBlock{Text=LayerName(layer),TextWrapping=TextWrapping.Wrap},Tag=layer.Id,IsSelected=overlay.SelectedId==layer.Id});
        foreach(var mask in Recipe.Masks.Reverse())list.Items.Add(new ListBoxItem{Content=new TextBlock{Text=mask.Effect==MaskEffect.Solid?"Solid mask":"Visual obscuring · "+mask.Effect,TextWrapping=TextWrapping.Wrap},Tag=mask.Id,IsSelected=overlay.SelectedId==mask.Id});
        bool changing=false;
        list.SelectionChanged+=(_,_)=>
        {
            if(changing || list.SelectedItem is not ListBoxItem{Tag:Guid id})return;
            try{FlushFields();overlay.Tool=ImageLayerTool.Select;overlay.Select(id);}
            catch(ArgumentException e){changing=true;list.SelectedItem=list.Items.OfType<ListBoxItem>().FirstOrDefault(item=>Equals(item.Tag,overlay.SelectedId));changing=false;error(e.Message);}
        };Children.Add(list);
    }
    private static string LayerName(Annotation a)=>a.Geometry switch{StrokeGeometry s=>s.ArrowEnd||s.ArrowStart?"Arrow":"Pen stroke",ShapeGeometry s=>s.Ellipse?"Ellipse":a.Style.Fill.A>0?"Filled box / highlight":"Box",TextGeometry t=>"Text · "+(t.Text.Length>24?t.Text[..24]+"…":t.Text),NumberGeometry n=>"Callout "+n.Number,InsetImageGeometry=>"Inset image",MagnifierGeometry=>"Magnifier",_=>"Layer"};
    private static string FriendlyStyle(string faceName)
    {
        var parts=faceName.Split('/');return parts.Length==3&&int.TryParse(parts[0],out int weight)&&int.TryParse(parts[1],out int width)?InstalledFontCatalog.StyleName(weight,parts[2]=="Upright"?0:parts[2]=="Italic"?1:2,width):faceName;
    }
}
