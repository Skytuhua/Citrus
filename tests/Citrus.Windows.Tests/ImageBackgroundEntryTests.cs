using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Citrus.Conversion;
using Citrus.Core;
using Citrus.Core.Editing;
using Citrus.Windows.Editors;
using ImageMagick;
using Button=System.Windows.Controls.Button;
using ComboBox=System.Windows.Controls.ComboBox;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class ImageBackgroundEntryTests(DesktopFixture desktop)
{
    [Fact]
    public Task Switching_to_background_publishes_the_displayed_default_canvas_in_preview_and_export()=>desktop.RunAsync(async()=>
    {
        using var fixture=new Fixture();byte[] original=File.ReadAllBytes(fixture.Input);
        var batch=await Show(fixture,false,async window=>
        {
            ((ComboBox)window.FindName("ToolPicker")).SelectedItem="Background";
            var canvas=Assert.IsType<CanvasSpec>(((ImageRecipe)window.Model!.Selected!.Draft!.Recipe).Canvas);
            Assert.Equal(new SizeI(420,370),canvas.Size);Assert.Equal(160,canvas.Placement.X);Assert.Equal(24,canvas.CornerRadius);
            await Until(()=>((System.Windows.Controls.Image)window.FindName("PreviewImage")).Source is BitmapSource bitmap&&bitmap.PixelWidth==420&&bitmap.PixelHeight==370);
            Click(window,"Save with Background");
        });
        Assert.NotNull(batch);var queue=new JobQueue([fixture.Services.Engine]);
        var job=Assert.Single(await new EditorSubmissionService(queue,fixture.Services.Routes,fixture.Services.Inspector).SubmitAsync(batch,default));
        await job.Completion;Assert.Equal(ConversionJobState.Succeeded,job.State);
        using var output=new MagickImage(Assert.Single(job.Outputs));Assert.Equal(420u,output.Width);Assert.Equal(370u,output.Height);
        var corner=output.GetPixels().GetPixel(20,20).ToColor()!;Assert.True(corner.B>20);
        Assert.Equal(original,File.ReadAllBytes(fixture.Input));
    });
    [Fact]
    public Task Reset_and_history_keep_an_inactive_background_honest_and_require_adding_before_save()=>desktop.RunAsync(async()=>
    {
        using var fixture=new Fixture();await Show(fixture,false,window=>
        {
            ((ComboBox)window.FindName("ToolPicker")).SelectedItem="Background";
            Assert.NotNull(((ImageRecipe)window.Model!.Selected!.Draft!.Recipe).Canvas);
            Click(window,"Undo");Inactive();
            Click(window,"Redo");Assert.NotNull(((ImageRecipe)window.Model.Selected.Draft!.Recipe).Canvas);
            Click(window,"Reset background");Inactive();
            Click(window,"Add background");Assert.NotNull(((ImageRecipe)window.Model.Selected.Draft!.Recipe).Canvas);
            Assert.True(((Button)window.FindName("ExportButton")).IsEnabled);
            Click(window,"Save with Background");return Task.CompletedTask;
            void Inactive()
            {
                Assert.Null(((ImageRecipe)window.Model!.Selected!.Draft!.Recipe).Canvas);
                var button=(Button)window.FindName("ExportButton");Assert.False(button.IsEnabled);
                Assert.Contains(Descendants(window).OfType<Button>(),button=>Equals(button.Content,"Add background")&&button.IsEnabled);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.True(window.IsVisible);
                Assert.Contains("background",((TextBlock)window.FindName("ErrorText")).Text,StringComparison.OrdinalIgnoreCase);
            }
        });
    });
    [Fact]
    public Task Direct_background_entry_is_ready_but_untouched_cancel_does_not_prompt_or_publish()=>desktop.RunAsync(async()=>
    {
        using var fixture=new Fixture();var result=await Show(fixture,true,window=>
        {
            Assert.NotNull(((ImageRecipe)window.Model!.Selected!.Draft!.Recipe).Canvas);Assert.False(window.Model.Dirty);
            Assert.True(((Button)window.FindName("ExportButton")).IsEnabled);Click(window,"Cancel");return Task.CompletedTask;
        });
        Assert.Null(result);Assert.Equal(new[]{fixture.Input},Directory.GetFiles(fixture.Root));
    });
    [Fact]
    public Task Background_save_requires_a_canvas_for_all_included_images_until_apply_to_all()=>desktop.RunAsync(async()=>
    {
        using var fixture=new Fixture();string second=Path.Combine(fixture.Root,"second.png");File.Copy(fixture.Input,second);
        var result=await Show(fixture,false,window=>
        {
            ((ComboBox)window.FindName("ToolPicker")).SelectedItem="Background";
            Assert.NotNull(((ImageRecipe)window.Model!.Sources[0].Draft!.Recipe).Canvas);
            Assert.Null(((ImageRecipe)window.Model.Sources[1].Draft!.Recipe).Canvas);
            var export=(Button)window.FindName("ExportButton");Assert.False(export.IsEnabled);
            Assert.Contains("included",System.Windows.Automation.AutomationProperties.GetHelpText(export),StringComparison.OrdinalIgnoreCase);
            Assert.True(ToolTipService.GetShowOnDisabled(export));
            Descendants(window).OfType<Expander>().Single(expander=>Equals(expander.Header,"Batch settings")).IsExpanded=true;
            window.UpdateLayout();
            Click(window,"Apply to all");Assert.True(((Button)window.FindName("ExportButton")).IsEnabled);
            Click(window,"Save with Background");return Task.CompletedTask;
        },[fixture.Input,second]);
        Assert.NotNull(result);Assert.Equal(2,result.Items.Length);
        Assert.All(result.Items,item=>Assert.NotNull(Assert.IsType<ImageRecipe>(item.Document.Recipe).Canvas));
    });
    [Fact]
    public Task Reloading_a_neutral_source_cannot_enable_background_export_without_a_canvas()=>desktop.RunAsync(async()=>
    {
        using var fixture=new Fixture();await Show(fixture,false,async window=>
        {
            ((ComboBox)window.FindName("ToolPicker")).SelectedItem="Background";Click(window,"Undo");
            var row=window.Model!.Selected!;Assert.Null(((ImageRecipe)row.Draft!.Recipe).Canvas);
            // This restored baseline has no edit to discard; the fixture bypasses only the stale dirty flag.
            row.Dirty=false;Guid previous=row.Source!.Id;
            Descendants(window).OfType<Expander>().Single(expander=>expander.Header is TextBlock heading&&heading.Name=="SourceHeading").IsExpanded=true;window.UpdateLayout();
            Click(window,"Reload image");
            await Until(()=>row.Source?.Id!=previous&&row.Draft is not null&&((System.Windows.Controls.Image)window.FindName("PreviewImage")).Source is BitmapSource bitmap&&bitmap.PixelWidth==100&&bitmap.PixelHeight==50);
            Assert.False(((Button)window.FindName("ExportButton")).IsEnabled);
            Click(window,"Cancel");
        });
    });
    private static async Task<EditorSubmissionBatch?> Show(Fixture fixture,bool direct,Func<EditorHostWindow,Task> exercise,string[]? inputs=null)
    {
        using var cancel=new CancellationTokenSource();var window=new EditorHostWindow(fixture.Services);Exception? failure=null;
        var operation=fixture.Services.Engine.Operations.Single(op=>op.Id==(direct?"image.background":"image.editor"));
        var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(30)};int ticks=0;
        timer.Tick+=async(_,_)=>
        {
            if(++ticks>500){timer.Stop();failure=new TimeoutException("Background host did not become ready.");cancel.Cancel();return;}
            if(((System.Windows.Controls.Image)window.FindName("PreviewImage")).Source is null)return;
            timer.Stop();try{await exercise(window);}catch(Exception error){failure=error;cancel.Cancel();}
        };
        timer.Start();EditorSubmissionBatch? result;
        try{result=await window.ShowEditorAsync(new(operation,[..inputs??[fixture.Input]],direct?"Background":"Adjust",new(),null,SubmissionOrigin.Home),cancel.Token);}
        finally{timer.Stop();}
        if(failure is not null)throw failure;return result;
    }
    private static async Task Until(Func<bool> condition){for(int i=0;i<500;i++){if(condition())return;await Task.Delay(20);}throw new TimeoutException("Matching background preview was not published.");}
    private static void Click(DependencyObject root,string text)=>Descendants(root).OfType<Button>().Single(b=>Equals(b.Content,text)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root){for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}}
    private sealed class Fixture:IDisposable
    {
        public string Root {get;}=Path.Combine(Path.GetTempPath(),"citrus-background-entry-"+Guid.NewGuid().ToString("N"));
        public string Input {get;}
        public ImageEditorServices Services {get;}=new(new());
        public Fixture(){Directory.CreateDirectory(Root);Input=Path.Combine(Root,"original.png");using var image=new MagickImage(MagickColors.Orange,100,50);image.Write(Input,MagickFormat.Png);}
        public void Dispose(){Services.Dispose();Directory.Delete(Root,true);}
    }
}
