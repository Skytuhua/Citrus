using System.Globalization;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Citrus.Core.Editing;
using Citrus.Conversion.Editing.Image;
using Microsoft.Win32;

namespace Citrus.Windows.Editors.Image;

/// <summary>Reference background controls; all state is saved in the immutable canvas recipe.</summary>
public sealed class ImageBackgroundView:StackPanel
{
    private readonly EditorHostViewModel model;private readonly Action refresh;private readonly Action<string> error;
    private double padding,corners,shadow;private double? ratio;private CanvasBackground background;
    private readonly List<Button> swatches=[];
    private ImageRecipe Recipe=>(ImageRecipe)model.Selected!.Draft!.Recipe;
    private EditorDocument Document=>model.Selected!.Draft!;
    public ImageBackgroundView(EditorHostViewModel model,Action refresh,Action<string> error,Action rebuild,CancellationToken lifetime=default)
    {
        this.model=model;this.refresh=refresh;this.error=error;var canvas=Recipe.Canvas;
        padding=canvas is null?160:Math.Min(canvas.Placement.X,canvas.Placement.Y);corners=canvas?.CornerRadius??24;shadow=canvas?.Shadow?.Sigma??24;background=canvas?.Background??Gradient(0);ratio=canvas?.AspectRatio;
        if(canvas is null)
        {
            var add=new Button {Content="Add background",HorizontalAlignment=HorizontalAlignment.Left};add.SetResourceReference(StyleProperty,"SecondaryButton");
            add.Click+=(_,_)=>{try{model.AddDefaultBackground();error("");rebuild();refresh();}catch(ArgumentException e){error(e.Message);}};Children.Add(add);return;
        }
        Children.Add(new TextBlock{Text="Background",FontWeight=FontWeights.SemiBold,Margin=new(0,4,0,8)});
        var gradients=new StackPanel{Orientation=Orientation.Horizontal};for(int i=0;i<9;i++){int index=i;var preset=Gradient(i);Swatch(gradients,Brush(preset),"Gradient "+(i+1),()=>{background=Gradient(index);Apply();},background is GradientBackground current&&current.Angle==preset.Angle&&current.Stops.SequenceEqual(preset.Stops));}
        Children.Add(new ScrollViewer{Content=gradients,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled});
        var solids=new StackPanel{Orientation=Orientation.Horizontal};foreach(string hex in new[]{"#FFFFFF","#F5CE45","#A4D52F","#43A574","#269E95","#4369DD","#50CDDD","#273C59","#8751C6","#222222"}){var c=(Color)ColorConverter.ConvertFromString(hex);Swatch(solids,new SolidColorBrush(c),hex,()=>{background=new SolidBackground(new(c.R,c.G,c.B,255));Apply();},background is SolidBackground solid&&solid.Color==new Rgba32(c.R,c.G,c.B,255));}
        Children.Add(new ScrollViewer{Content=solids,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled});
        var custom=new WrapPanel();var imageButton=new Button{Content="Custom image…",Margin=new(0,6,8,6)};imageButton.SetResourceReference(StyleProperty,"SecondaryButton");custom.Children.Add(imageButton);
        imageButton.Click+=async(_,_)=>
        {
            var picker=new OpenFileDialog{Title="Choose a background image",Filter="Raster images|*.png;*.jpg;*.jpeg;*.webp;*.tif;*.tiff;*.avif;*.heic;*.bmp"};if(picker.ShowDialog(Window.GetWindow(this))!=true)return;
            var captured=Document;imageButton.IsEnabled=false;
            try{var source=await model.ImportAuxiliaryAsync(picker.FileName,lifetime);lifetime.ThrowIfCancellationRequested();if(model.Selected?.Draft!=captured)throw new ArgumentException("The selected image changed. Choose the background again.");ClearSwatches();background=new ImageBackground(source.Id,ImageFit.Cover,new(.5,.5),1,0);Apply(source);}
            catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
            catch(Exception e)when(e is not OutOfMemoryException){error(e.Message);}finally{imageButton.IsEnabled=true;}
        };
        var colorButton=new Button{Content="Custom color…",Margin=new(0,6,0,6)};colorButton.SetResourceReference(StyleProperty,"SecondaryButton");custom.Children.Add(colorButton);Children.Add(custom);
        colorButton.Click+=(_,_)=>{using var dialog=new System.Windows.Forms.ColorDialog{FullOpen=true};if(dialog.ShowDialog()!=System.Windows.Forms.DialogResult.OK)return;var c=dialog.Color;ClearSwatches();background=new SolidBackground(new(c.R,c.G,c.B,255));Apply();};
        Slider("Padding",padding,0,1000,v=>padding=v);Slider("Corners",corners,0,512,v=>corners=v);Slider("Shadow",shadow,0,100,v=>shadow=v);
        Children.Add(new TextBlock{Text="Ratio",Margin=new(0,10,0,4)});var ratios=new WrapPanel();foreach(var item in new(string Label,double? Value)[]{("Auto",null),("1:1",1),("16:9",16d/9),("4:5",4d/5)}){var button=new Button{Content=item.Label,Margin=new(0,0,6,6)};button.SetResourceReference(StyleProperty,"SecondaryButton");if(ratio==item.Value){button.SetResourceReference(BackgroundProperty,"ReferenceActionBackground");button.SetResourceReference(Control.ForegroundProperty,"ReferenceActionText");}button.Click+=(_,_)=>{foreach(var other in ratios.Children.OfType<Button>()){other.ClearValue(BackgroundProperty);other.ClearValue(Control.ForegroundProperty);}button.SetResourceReference(BackgroundProperty,"ReferenceActionBackground");button.SetResourceReference(Control.ForegroundProperty,"ReferenceActionText");ratio=item.Value;Apply();};ratios.Children.Add(button);}Children.Add(ratios);
        var reset=new Button{Content="Reset background",Margin=new(0,8,0,8)};reset.SetResourceReference(StyleProperty,"SecondaryButton");reset.Click+=(_,_)=>{try{var photo=ImageGeometryMapper.Resolve(Recipe,model.Selected!.Size).OutputSize;model.Change(ImageCanvasPlacement.Change(Document,photo,null));error("");rebuild();refresh();}catch(ArgumentException e){error(e.Message);}};Children.Add(reset);
    }
    private void Slider(string label,double value,double min,double max,Action<double> set)
    {
        var row=new Grid{Margin=new(0,8,0,8)};row.ColumnDefinitions.Add(new(){Width=new(82)});row.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=new(66)});
        var caption=new TextBlock{Text=label,VerticalAlignment=VerticalAlignment.Center};var slider=new System.Windows.Controls.Slider{Minimum=min,Maximum=max,Value=value,TickFrequency=1,IsSnapToTickEnabled=true,SmallChange=1};var readout=new TextBlock{Text=value.ToString("0",CultureInfo.InvariantCulture)+" px",TextAlignment=TextAlignment.Right,VerticalAlignment=VerticalAlignment.Center};
        AutomationProperties.SetName(slider,label+" pixels");Grid.SetColumn(slider,1);Grid.SetColumn(readout,2);row.Children.Add(caption);row.Children.Add(slider);row.Children.Add(readout);Children.Add(row);
        slider.ValueChanged+=(_,_)=>{set(slider.Value);readout.Text=slider.Value.ToString("0",CultureInfo.InvariantCulture)+" px";Apply(transient:slider.IsMouseCaptureWithin);};
        slider.PreviewMouseLeftButtonUp+=(_,_)=>model.CommitGesture();slider.LostMouseCapture+=(_,_)=>model.CommitGesture();
    }
    private void Apply(SourceSnapshot? source=null,bool transient=false)
    {
        try
        {
            var doc=Document;if(source is not null&&!doc.Sources.Any(s=>s.Id==source.Id))doc=doc with{Sources=doc.Sources.Add(source)};
            SizeI photo=ImageGeometryMapper.Resolve((ImageRecipe)doc.Recipe,model.Selected!.Size).OutputSize;
            int width=checked(photo.Width+(int)padding*2),height=checked(photo.Height+(int)padding*2);
            if(ratio is {} r){if(width/(double)height<r)width=(int)Math.Ceiling(height*r);else height=(int)Math.Ceiling(width/r);}
            var canvas=new CanvasSpec(new(width,height),background,new((width-photo.Width)/2d,(height-photo.Height)/2d,photo.Width,photo.Height),ImageFit.Contain,corners,shadow>0?new(0,shadow/3,shadow,new(0,0,0,64)):null,ratio);
            model.Change(ImageCanvasPlacement.Change(doc,photo,canvas),transient);refresh();error("");
        }
        catch(ArgumentException e){error(e.Message);}
    }
    private void ClearSwatches(){foreach(var button in swatches)button.BorderBrush=Brushes.Transparent;}
    private void Swatch(Panel parent,Brush brush,string name,Action choose,bool selected=false)
    {
        var accent=new SolidColorBrush(Color.FromRgb(255,85,0));var button=new Button{Width=34,Height=34,Background=brush,BorderThickness=new(2),BorderBrush=selected?accent:Brushes.Transparent,Margin=new(0,0,6,6),ToolTip=name,Padding=new(0)};AutomationProperties.SetName(button,name);button.Click+=(_,_)=>{ClearSwatches();button.BorderBrush=accent;choose();};parent.Children.Add(button);swatches.Add(button);
    }
    private static GradientBackground Gradient(int n)
    {
        string[][] presets=[["#FF699B","#FFAF5C","#FFE726"],["#68C5F6","#FFF7EC","#FFFFFF"],["#A5DB49","#E2C0FF","#69DAFA"],["#ECC6D8","#FFFFFF","#C6D0F4"],["#FDB083","#FFDCD5","#BC95EA"],["#0C645E","#FB8E9E","#F8D67A"],["#78CEE8","#C1D9FF","#4C9FE0"],["#D1F2EA","#8DE5EE","#36B4C7"],["#E0D5FF","#FFE1E7","#FFFFFF"]];
        return new(45,[..presets[n].Select((h,i)=>{var c=(Color)ColorConverter.ConvertFromString(h);return new Citrus.Core.Editing.GradientStop(i/2d,new(c.R,c.G,c.B,255));})]);
    }
    private static Brush Brush(GradientBackground gradient)=>new LinearGradientBrush(new GradientStopCollection(gradient.Stops.Select(s=>new System.Windows.Media.GradientStop(Color.FromArgb(s.Color.A,s.Color.R,s.Color.G,s.Color.B),s.Position))),45);
}
