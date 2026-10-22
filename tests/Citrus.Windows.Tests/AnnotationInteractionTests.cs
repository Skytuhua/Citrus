using System.IO;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Citrus.Conversion;
using Citrus.Core;
using Citrus.Core.Editing;
using Citrus.Windows.Editors;
using Citrus.Windows.Editors.Image;
using ImageMagick;
using Button=System.Windows.Controls.Button;
using TextBox=System.Windows.Controls.TextBox;
using ComboBox=System.Windows.Controls.ComboBox;
using KeyEventArgs=System.Windows.Input.KeyEventArgs;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class AnnotationInteractionTests(DesktopFixture desktop)
{
    [Fact]
    public Task Actual_layer_selection_nudge_delete_undo_and_export_use_the_host_recipe()=>desktop.RunAsync(async()=>
    {
        using var f=new Fixture();
        var batch=await Show(f,window=>
        {
            var model=window.Model!;var doc=model.Selected!.Draft!;var image=(ImageRecipe)doc.Recipe;
            var first=new Annotation(Guid.NewGuid(),true,new ShapeGeometry(new(10,10,20,20),false,0),new(new(255,0,0,255),new(255,0,0,255),0,1,null));
            model.Change(doc with {Recipe=image with {Annotations=[first]}});
            ((ComboBox)window.FindName("ToolPicker")).SelectedItem="Annotate";
            var overlay=(AnnotationOverlay)window.FindName("LayerHandles");overlay.Select(first.Id);
            SendKey(overlay,window,Key.Right);Assert.Equal(11,((ShapeGeometry)((ImageRecipe)model.Selected.Draft!.Recipe).Annotations[0].Geometry).Bounds.X);
            Click(window,"Undo");Assert.Equal(10,((ShapeGeometry)((ImageRecipe)model.Selected.Draft!.Recipe).Annotations[0].Geometry).Bounds.X);
            Click(window,"Redo");Assert.Equal(11,((ShapeGeometry)((ImageRecipe)model.Selected.Draft!.Recipe).Annotations[0].Geometry).Bounds.X);
            SendKey(overlay,window,Key.Delete);Assert.Empty(((ImageRecipe)model.Selected.Draft!.Recipe).Annotations);Click(window,"Undo");Assert.Single(((ImageRecipe)model.Selected.Draft!.Recipe).Annotations);
            Click(window,"Save");
        });
        Assert.NotNull(batch);var queue=new JobQueue([f.Services.Engine]);var job=Assert.Single(await new EditorSubmissionService(queue,f.Services.Routes,f.Services.Inspector).SubmitAsync(batch,default));await job.Completion;Assert.Equal(ConversionJobState.Succeeded,job.State);
        using var stream=File.OpenRead(job.Outputs.Single());using var image=new MagickImage(stream,new MagickReadSettings{Format=MagickFormat.Png});var bytes=image.GetPixels().ToByteArray(PixelMapping.BGRA)!;Assert.Equal(new byte[]{0,0,255,255},bytes.Skip((15*100+15)*4).Take(4));
    });
    [Fact]
    public Task Invalid_manual_mask_field_prevents_accept_and_double_text_footer_remains_visible()=>desktop.RunAsync(async()=>
    {
        using var f=new Fixture();bool checkedError=false;
        var batch=await Show(f,window=>
        {
            var model=window.Model!;var doc=model.Selected!.Draft!;var image=(ImageRecipe)doc.Recipe;var mask=new PixelMask(Guid.NewGuid(),new(10,10,20,20),MaskEffect.Pixelate,new(0,0,0,255),12);
            model.Change(doc with {Recipe=image with {Masks=[mask]}});((ComboBox)window.FindName("ToolPicker")).SelectedItem="Manual masks";
            ((AnnotationOverlay)window.FindName("LayerHandles")).Select(mask.Id);
            window.Width=760;window.Height=560;window.Resources["BodySize"]=28d;window.Resources["BodyLeading"]=40d;window.Resources["EditorTitleSize"]=44d;window.UpdateLayout();
            var export=(Button)window.FindName("ExportButton");var bounds=export.TransformToAncestor(window).TransformBounds(new Rect(export.RenderSize));Assert.InRange(bounds.Bottom,0,window.ActualHeight);Assert.InRange(bounds.Right,0,window.ActualWidth);
            var text=Descendants(window).OfType<TextBox>().Single(t=>AutomationProperties.GetName(t)=="Radius · normalized source pixels");text.Text="invalid";Click(window,"Save");Assert.True(window.IsVisible);Assert.NotEmpty(((TextBlock)window.FindName("ErrorText")).Text);checkedError=true;
            text.Text="18";SendKey(text,window,Key.Enter);Assert.Equal(18,((ImageRecipe)model.Selected.Draft!.Recipe).Masks[0].Radius);
            Click(window,"Save");
        });Assert.True(checkedError);Assert.NotNull(batch);Assert.Equal(18,((ImageRecipe)batch.Items[0].Document.Recipe).Masks[0].Radius);
    });
    [Fact]
    public Task Annotate_has_real_font_selection_and_layers_do_not_render_via_Wpf_text()=>desktop.RunAsync(async()=>
    {
        using var f=new Fixture();var batch=await Show(f,window=>
        {
            ((ComboBox)window.FindName("ToolPicker")).SelectedItem="Annotate";var overlay=(AnnotationOverlay)window.FindName("LayerHandles");overlay.Tool=ImageLayerTool.Text;
            // A selection event rebuilds the contextual inspector without manufacturing a text layer.
            overlay.Select(null);
            Assert.Contains(Descendants(window).OfType<ComboBox>(),c=>AutomationProperties.GetName(c)=="Installed fonts");
            var advanced=Descendants(window).OfType<Expander>().Single(e=>Equals(e.Header,"Advanced"));Assert.False(advanced.IsExpanded);
            advanced.IsExpanded=true;window.UpdateLayout();
            Assert.Contains(Descendants(window).OfType<Button>(),b=>b.Content is TextBlock t&&t.Text=="Choose font file…");
            Assert.Empty(((ImageRecipe)window.Model!.Selected!.Draft!.Recipe).Annotations);
            Click(window,"Cancel");
        });Assert.Null(batch);
    });
    [Fact]
    public Task Edited_text_font_and_wrap_refresh_exact_handles_without_reselecting()=>desktop.RunAsync(async()=>
    {
        using var f=new Fixture();
        var batch=await ShowAsync(f,async window=>
        {
            var font=await f.Services.Fonts.CaptureAsync(Path.Combine(AppContext.BaseDirectory,"Fixtures","Fonts","Fixture1.ttf"),0,default);
            var model=window.Model!;var doc=model.Selected!.Draft!;var recipe=(ImageRecipe)doc.Recipe;
            var layer=new Annotation(Guid.NewGuid(),true,new TextGeometry(new(10,10),"A",100,0),new(new(0,0,0,255),new(0,0,0,255),0,1,new(font.Id,20,20)));
            model.Change(doc with {Fonts=[font],Recipe=recipe with {Annotations=[layer]}});((ComboBox)window.FindName("ToolPicker")).SelectedItem="Annotate";
            var overlay=(AnnotationOverlay)window.FindName("LayerHandles");overlay.Select(layer.Id);
            await Ready(20);
            var content=Field("Text");content.Text=new string('A',15);
            Descendants(window).OfType<Button>().Single(b=>b.Content is TextBlock text&&text.Text=="Apply text").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Ready(60);Assert.Equal(layer.Id,overlay.SelectedId);
            // Font size and leading are independent: two lines retain 20px leading plus 10px glyph metrics.
            var size=Field("Font size · px");size.Text="10";SendKey(size,window,Key.Enter);await Ready(30);
            var wrap=Field("Wrap width · px");wrap.Text="200";SendKey(wrap,window,Key.Enter);await Ready(10);
            size.Text="20";SendKey(size,window,Key.Enter);await Ready(40);
            var rotation=Field("Rotation · degrees");rotation.Text="90";SendKey(rotation,window,Key.Enter);
            var before=overlay.SelectedBounds()!.Value;var width=Field("Width · px");width.Text="80";SendKey(width,window,Key.Enter);
            for(int i=0;i<150&&overlay.SelectedBounds() is null;i++)await Task.Delay(20);
            var after=overlay.SelectedBounds()!.Value;Assert.Equal(before.X,after.X,5);Assert.Equal(before.Y,after.Y,5);Assert.Equal(80,after.Width,5);Assert.Equal(400,after.Height,5);
            Assert.Equal("400",Field("Wrap width · px").Text);Assert.Equal("40",Field("Font size · px").Text);
            Click(window,"Save");
            TextBox Field(string name)=>Descendants(window).OfType<TextBox>().Single(t=>AutomationProperties.GetName(t)==name);
            async Task Ready(double height)
            {
                for(int i=0;i<150;i++){if(overlay.SelectedBounds() is {} bounds&&Math.Abs(bounds.Height-height)<.001)return;await Task.Delay(20);}
                Assert.Equal(height,overlay.SelectedBounds()?.Height);
            }
        });Assert.NotNull(batch);
    });
    [Theory]
    [InlineData(ThemeChoice.Light,1d)] [InlineData(ThemeChoice.Dark,1d)]
    [InlineData(ThemeChoice.Light,2d)] [InlineData(ThemeChoice.Dark,2d)]
    public Task Real_annotation_host_visual_evidence_and_scroll_reachability(ThemeChoice choice,double textScale)=>desktop.RunAsync(async()=>
    {
        using var theme=new ThemeManager(System.Windows.Application.Current);using var f=new Fixture();CitrusPreviewFixture.Save(f.PathIn);
        try
        {
            theme.Apply(choice,true);theme.ApplyTypography(textScale);
            var batch=await ShowAsync(f,async window=>
            {
                if(textScale==2){window.Width=760;window.Height=560;}
                var model=window.Model!;var doc=model.Selected!.Draft!;var recipe=(ImageRecipe)doc.Recipe;
                var arrow=new Annotation(Guid.NewGuid(),true,new StrokeGeometry([new(100,185),new(230,205)],false,true),new(new(23,33,43,255),new(0,0,0,0),6,1,null));
                var box=arrow with {Id=Guid.NewGuid(),Geometry=new ShapeGeometry(new(170,125,215,220),false,12),Style=arrow.Style with {StrokeWidth=3}};
                model.Change(doc with {Recipe=recipe with {Annotations=[box,arrow]}});((ComboBox)window.FindName("ToolPicker")).SelectedItem="Annotate";((AnnotationOverlay)window.FindName("LayerHandles")).Select(arrow.Id);
                for(int i=0;i<100;i++){await Task.Delay(30);if(!((TextBlock)window.FindName("PreviewStatus")).Text.StartsWith("Rendering",StringComparison.Ordinal))break;}
                window.UpdateLayout();var root=(FrameworkElement)window.Content;var export=(Button)window.FindName("ExportButton");VisualArtifacts.AssertInside(export,root);
                var scroll=(ScrollViewer)window.FindName("InspectorScroll");scroll.ScrollToTop();window.UpdateLayout();
                VisualArtifacts.Capture(root,$"annotation-{choice}-text{textScale*100:0}-top",1,choice,new{source="Original deterministic Citrus still life",pixels="Actual shared renderer; WPF overlay handles only"});
                scroll.ScrollToBottom();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);VisualArtifacts.AssertInside(export,root);Assert.True(scroll.VerticalOffset>0||scroll.ScrollableHeight==0);
                VisualArtifacts.Capture(root,$"annotation-{choice}-text{textScale*100:0}-bottom",1,choice,new{lastInspectorSection="Save location",pinnedFooter=true});
                Click(window,"Save");
            });Assert.NotNull(batch);
        }
        finally{theme.ApplyTypography(1);theme.Apply(ThemeChoice.Light,true);}
    });
    private static void SendKey(UIElement target,Window window,Key key)=>target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,key){RoutedEvent=Keyboard.KeyDownEvent});
    private static void Click(DependencyObject root,string text)=>Descendants(root).OfType<Button>().Single(b=>Equals(b.Content,text)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}
    }
    private static Task<EditorSubmissionBatch?> Show(Fixture f,Action<EditorHostWindow> exercise)=>ShowAsync(f,w=>{exercise(w);return Task.CompletedTask;});
    private static async Task<EditorSubmissionBatch?> ShowAsync(Fixture f,Func<EditorHostWindow,Task> exercise)
    {
        var window=new EditorHostWindow(f.Services);using var cancel=new CancellationTokenSource();Exception? failure=null;int ticks=0;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(30)};
        timer.Tick+=async(_,_)=>{try{if(++ticks>500)throw new TimeoutException("Image preview did not become available.");if(((System.Windows.Controls.Image)window.FindName("PreviewImage")).Source is null)return;timer.Stop();await exercise(window);}catch(Exception e){failure=e;timer.Stop();cancel.Cancel();}};
        timer.Start();EditorSubmissionBatch? result;try{result=await window.ShowEditorAsync(f.Request,cancel.Token);}finally{timer.Stop();}if(failure is not null)throw failure;return result;
    }
    private sealed class Fixture:IDisposable
    {
        public string Root{get;}=Path.Combine(Path.GetTempPath(),"citrus-annotation-host-"+Guid.NewGuid().ToString("N"));public string PathIn=>Path.Combine(Root,"source.png");public ImageEditorServices Services{get;}=new(new());public EditorOpenRequest Request=>new(new("image.annotate","Annotate",OperationMode.Tools,null,false,true),[PathIn],"Annotate",new(),null,SubmissionOrigin.Wheel);
        public Fixture(){Directory.CreateDirectory(Root);using var image=new MagickImage(MagickColors.White,100,70);image.Write(PathIn,MagickFormat.Png);}
        public void Dispose(){Services.Dispose();Directory.Delete(Root,true);}
    }
}
