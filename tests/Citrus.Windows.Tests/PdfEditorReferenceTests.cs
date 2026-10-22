using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Citrus.Conversion;
using Citrus.Core;
using Citrus.Core.Editing;
using Citrus.Windows.Editors.Pdf;
using Citrus.Windows.Presentation;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class PdfEditorReferenceTests(DesktopFixture desktop)
{
    [Theory]
    [InlineData("pdf.compress","Compress","")]
    [InlineData("pdf.metadata","Save","Search metadata")]
    public Task Text_field_Delete_does_not_remove_hidden_PDF_pages(string id,string action,string name)=>desktop.RunAsync(async()=>
    {
        using var fixture=new Fixture();var result=await Show(fixture,id,action,window=>
        {
            var input=Descendants(window).OfType<System.Windows.Controls.TextBox>().Single(t=>name.Length==0?t.IsVisible:System.Windows.Automation.AutomationProperties.GetName(t)==name);
            var key=new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,Key.Delete){RoutedEvent=Keyboard.PreviewKeyDownEvent};
            input.RaiseEvent(key);Assert.False(key.Handled);Click(window,action);
        });
        Assert.NotNull(result);Assert.Equal(2,Assert.IsType<PdfRecipe>(Assert.Single(result.Items).Document.Recipe).Pages.Length);
    });
    [Theory]
    [InlineData("pdf.editor","Apply")][InlineData("pdf.compress","Compress")][InlineData("pdf.metadata","Save")]
    public Task Untouched_PDF_tool_cancels_once_without_queuing_or_saving(string id,string action)=>desktop.RunAsync(async()=>
    {
        using var fixture=new Fixture();
        var result=await Show(fixture,id,action,window=>Click(window,"Cancel"));Assert.Null(result);Assert.Equal(new[]{fixture.Input},Directory.GetFiles(fixture.Root));
    });
    [Fact]
    public Task Native_organizer_controls_export_real_page_duplicates_and_rotation()=>desktop.RunAsync(async()=>
    {
        using var fixture=new Fixture();
        var result=await Show(fixture,"pdf.editor","Apply",window=>
        {
            var list=Descendants(window).OfType<ListBox>().Single();list.SelectedIndex=1;Click(window,"Duplicate");
            list.SelectedIndex=0;Click(window,"Rotate");Click(window,"Apply");
        });
        Assert.NotNull(result);var queue=new JobQueue([fixture.Engine]);var service=new EditorSubmissionService(queue,fixture.Engine.Routes,fixture.Engine);
        var job=Assert.Single(await service.SubmitAsync(result,default));await job.Completion;Assert.Equal(ConversionJobState.Succeeded,job.State);
        using var output=UglyToad.PdfPig.PdfDocument.Open(Assert.Single(job.Outputs));Assert.Equal(new[]{"First","Second","Second"},output.GetPages().Select(p=>p.Text));Assert.Equal(90,(int)output.GetPage(1).Rotation.Value);
    });
    [Fact]
    public Task PDF_raster_quality_tracks_the_explicit_quality_goal()=>desktop.RunAsync(async()=>
    {
        using var fixture=new Fixture();var result=await Show(fixture,"pdf.compress","Compress",window=>
        {
            Descendants(window).OfType<System.Windows.Controls.ComboBox>().Single(c=>c.ItemsSource is string[]).SelectedIndex=2;
            Descendants(window).OfType<System.Windows.Controls.TextBox>().Single(t=>t.IsVisible).Text="20";
            Descendants(window).OfType<System.Windows.Controls.CheckBox>().Single().IsChecked=true;Click(window,"Compress");
        });
        Assert.NotNull(result);var document=Assert.Single(result.Items).Document;Assert.Equal(new QualityGoal(20),document.Export.Compression);
        Assert.Equal(20,Assert.IsType<PdfRecipe>(document.Recipe).RasterQuality);
    });
    [Fact]
    public Task PDF_quality_requires_explicit_page_image_rebuilding_before_submission()=>desktop.RunAsync(async()=>
    {
        using var fixture=new Fixture();var result=await Show(fixture,"pdf.compress","Compress",window=>
        {
            Descendants(window).OfType<System.Windows.Controls.ComboBox>().Single(c=>c.ItemsSource is string[]).SelectedIndex=2;
            Descendants(window).OfType<System.Windows.Controls.TextBox>().Single(t=>t.IsVisible).Text="20";
            Click(window,"Compress");
            Assert.True(window.IsVisible);
            Assert.Contains(Descendants(window).OfType<TextBlock>(),text=>text.Text.Contains("Enable page image rebuilding",StringComparison.Ordinal));
            Descendants(window).OfType<System.Windows.Controls.CheckBox>().Single().IsChecked=true;
            Click(window,"Compress");
        });
        Assert.NotNull(result);var document=Assert.Single(result.Items).Document;
        Assert.Equal(new QualityGoal(20),document.Export.Compression);
        Assert.Equal(PdfCompressionMode.RasterRebuild,Assert.IsType<PdfRecipe>(document.Recipe).CompressionMode);
    });
    [Fact]
    public Task Metadata_shows_the_original_filename_instead_of_truncating_its_parent_path()=>desktop.RunAsync(async()=>
    {
        using var fixture=new Fixture();var result=await Show(fixture,"pdf.metadata","Save",window=>
        {
            Assert.Contains(Descendants(window).OfType<TextBlock>(),text=>text.Text==Path.GetFileName(fixture.Input));
            Click(window,"Save");
        });
        Assert.NotNull(result);
    });
    [Fact]
    public Task PDF_compression_Reset_restores_the_complete_default_goal()=>desktop.RunAsync(async()=>
    {
        using var fixture=new Fixture();var result=await Show(fixture,"pdf.compress","Compress",window=>
        {
            var mode=Descendants(window).OfType<System.Windows.Controls.ComboBox>().Single(c=>c.ItemsSource is string[]);mode.SelectedIndex=2;
            var amount=Descendants(window).OfType<System.Windows.Controls.TextBox>().Single(t=>t.IsVisible);amount.Text="20";
            var raster=Descendants(window).OfType<System.Windows.Controls.CheckBox>().Single();raster.IsChecked=true;Click(window,"Reset");
            Assert.Equal(0,mode.SelectedIndex);Assert.Equal("50",amount.Text);Assert.False(raster.IsChecked);Click(window,"Compress");
        });
        Assert.NotNull(result);var document=Assert.Single(result.Items).Document;Assert.Equal(new ReductionGoal(50),document.Export.Compression);
        Assert.Equal(PdfCompressionMode.PreserveStructure,Assert.IsType<PdfRecipe>(document.Recipe).CompressionMode);
    });
    [Theory]
    [InlineData("pdf.editor","Apply")][InlineData("pdf.split","Apply")][InlineData("pdf.compress","Compress")][InlineData("pdf.metadata","Save")]
    public Task Actual_white_PDF_views_capture_default_and_minimum_layouts(string id,string action)=>desktop.RunAsync(async()=>
    {
        using var fixture=new Fixture();using var themes=new ThemeManager(System.Windows.Application.Current);
        using var presentation=new WindowPresentationService(System.Windows.Application.Current,themes,()=>new CitrusSettings());
        using var cancel=new CancellationTokenSource();var window=new PdfEditorWindow(fixture.Engine);presentation.Attach(window,(MaterialSurface)window.Content);Task? checks=null;
        var operation=fixture.Engine.Operations.Single(o=>o.Id==id);Assert.True(fixture.Engine.Routes.TryGet(id,out var route));
        window.Loaded+=(_,_)=>checks=Check();await window.ShowEditorAsync(new(operation,[fixture.Input],route.EntryTool,new(),null,SubmissionOrigin.Home),cancel.Token);await checks!;
        async Task Check()
        {
            try
            {
                Button? primary=null;
                for(int attempt=0;attempt<400;attempt++)
                {
                    primary=Descendants(window).OfType<Button>().SingleOrDefault(b=>Equals(b.Content,action));
                    bool previewReady=id is "pdf.compress" or "pdf.metadata"||Descendants(window).OfType<System.Windows.Controls.Image>().Any(i=>i.Source is not null);
                    if(primary is {IsEnabled:true}&&previewReady)break;await Task.Delay(20);
                }
                Assert.NotNull(primary);Assert.True(primary.IsEnabled);Assert.Equal(System.Windows.Application.Current.FindResource("PrimaryButton"),primary.Style);
                Assert.All(Descendants(window).OfType<Button>().Where(b=>Equals(b.Content,"Reset")||Equals(b.Content,"Cancel")),button=>Assert.Equal(System.Windows.Application.Current.FindResource("SecondaryButton"),button.Style));
                foreach(double scale in new[]{1d,1.5,2d})VisualArtifacts.Capture((FrameworkElement)window.Content,"pdf-reference-default-"+id.Replace('.','-')+"-"+scale.ToString("0.0",System.Globalization.CultureInfo.InvariantCulture),scale,ThemeChoice.Light);
                window.Width=window.MinWidth;window.Height=window.MinHeight;window.UpdateLayout();VisualArtifacts.AssertInside(primary,(FrameworkElement)window.Content);
                foreach(double scale in new[]{1d,1.5,2d})VisualArtifacts.Capture((FrameworkElement)window.Content,"pdf-reference-minimum-"+id.Replace('.','-')+"-"+scale.ToString("0.0",System.Globalization.CultureInfo.InvariantCulture),scale,ThemeChoice.Light);
            }
            finally{cancel.Cancel();}
        }
    });
    [Fact]
    public Task Compact_metadata_keeps_removal_reachable_above_fixed_footer()=>desktop.RunAsync(async()=>
    {
        using var fixture=new Fixture();
        await Show(fixture,"pdf.metadata","Save",window=>
        {
            window.Width=560;window.Height=440;window.UpdateLayout();
            var root=(FrameworkElement)window.Content;
            var remove=Descendants(window).OfType<Button>().Single(b=>Equals(b.Content,"Remove All"));
            var bodyScroll=Ancestors(remove).OfType<ScrollViewer>().FirstOrDefault();
            Assert.NotNull(bodyScroll);
            bodyScroll.ScrollToEnd();window.UpdateLayout();
            var save=Descendants(window).OfType<Button>().Single(b=>Equals(b.Content,"Save"));
            VisualArtifacts.AssertInside(remove,root);Assert.NotNull(remove.ToolTip);
            double contentBottom=remove.TranslatePoint(new System.Windows.Point(0,remove.ActualHeight),root).Y;
            Assert.True(contentBottom<=save.TranslatePoint(default,root).Y,"Metadata actions must stay above the fixed action footer.");
            Click(window,"Cancel");
        });
    });
    [Fact]
    public Task Metadata_can_replace_Title_after_Remove_All_and_reopen_the_saved_value()=>desktop.RunAsync(async()=>
    {
        using var fixture=new Fixture();
        var result=await Show(fixture,"pdf.metadata","Save",window=>
        {
            Click(window,"Remove All");
            var row=Descendants(window).OfType<DockPanel>().Single(p=>p.Children.OfType<TextBlock>().Any(t=>t.Text=="Title"));
            var title=row.Children.OfType<System.Windows.Controls.TextBox>().Single();
            Assert.True(title.IsEnabled);title.Text="Replacement title";Click(window,"Save");
        });
        Assert.NotNull(result);var document=Assert.Single(result.Items).Document;
        Assert.Contains(Assert.IsType<PdfRecipe>(document.Recipe).MetadataEdits,e=>e.Key=="Title"&&e.Action==MetadataEditAction.Set&&e.Value is TextValue{Value:"Replacement title"});
        var service=new EditorSubmissionService(new JobQueue([fixture.Engine]),fixture.Engine.Routes,fixture.Engine);
        var job=Assert.Single(await service.SubmitAsync(result,default));await job.Completion;Assert.Equal(ConversionJobState.Succeeded,job.State);
        using var pdf=PdfSharp.Pdf.IO.PdfReader.Open(Assert.Single(job.Outputs),PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);Assert.Equal("Replacement title",pdf.Info.Title);
    });
    [Fact]
    public Task Metadata_restore_uses_each_sources_own_value_and_preserves_a_replaced_draft()=>desktop.RunAsync(async()=>
    {
        using var fixture=new Fixture();string second=Path.Combine(fixture.Root,"second.pdf");File.Copy(fixture.Input,second);
        foreach(var (path,title) in new[]{(fixture.Input,"Title A"),(second,"Title B")})
        {using var pdf=PdfSharp.Pdf.IO.PdfReader.Open(path,PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);pdf.Info.Title=title;if(path==second)pdf.AddPage();pdf.Save(path);}
        var result=await Show(fixture,"pdf.metadata","Save",window=>
        {
            Descendants(window).OfType<System.Windows.Controls.ComboBox>().Single().SelectedIndex=1;
            var title=Descendants(window).OfType<System.Windows.Controls.TextBox>().Single(t=>System.Windows.Automation.AutomationProperties.GetName(t)=="Title");
            Assert.Equal("Title B",title.Text);
            Assert.Contains(Descendants(window).OfType<TextBlock>(),text=>text.Text.Contains("Pages: 3",StringComparison.Ordinal));
            Remove();Restore();Assert.Equal("Title B",title.Text);
            title.Text="Edited B";Remove();Restore();Assert.Equal("Edited B",title.Text);Click(window,"Save");
            void Remove()=>Descendants(window).OfType<Button>().Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="Remove Title").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            void Restore()=>Descendants(window).OfType<Button>().Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="Restore Title").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        },[fixture.Input,second]);
        Assert.NotNull(result);var service=new EditorSubmissionService(new JobQueue([fixture.Engine]),fixture.Engine.Routes,fixture.Engine);
        var jobs=await service.SubmitAsync(result,default);Assert.Equal(2,jobs.Count);
        foreach(var job in jobs)
        {
            await job.Completion;Assert.Equal(ConversionJobState.Succeeded,job.State);
            using var pdf=PdfSharp.Pdf.IO.PdfReader.Open(Assert.Single(job.Outputs),PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            Assert.Equal(job.Request.Inputs[0]==second?"Edited B":"Title A",pdf.Info.Title);
        }
    });
    private static async Task<EditorSubmissionBatch?> Show(Fixture fixture,string id,string action,Action<PdfEditorWindow> exercise,string[]? inputs=null)
    {
        using var cancel=new CancellationTokenSource();var window=new PdfEditorWindow(fixture.Engine);Exception? failure=null;int ticks=0;
        var operation=fixture.Engine.Operations.Single(o=>o.Id==id);Assert.True(fixture.Engine.Routes.TryGet(id,out var route));
        var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(30)};
        timer.Tick+=(_,_)=>
        {
            try
            {
                if(++ticks>500)throw new TimeoutException("The PDF tool did not become ready.");
                if(!Descendants(window).OfType<Button>().Any(b=>Equals(b.Content,action)&&b.IsEnabled))return;
                timer.Stop();exercise(window);
                window.Dispatcher.BeginInvoke(new Action(()=> {if(window.IsVisible){failure=new InvalidOperationException("The PDF tool did not close after its action.");cancel.Cancel();}}));
            }
            catch(Exception error){failure=error;timer.Stop();cancel.Cancel();}
        };
        timer.Start();EditorSubmissionBatch? result;
        try{result=await window.ShowEditorAsync(new(operation,[..inputs??[fixture.Input]],route.EntryTool,new(),null,SubmissionOrigin.Home),cancel.Token);}
        finally{timer.Stop();}
        if(failure is not null)throw failure;return result;
    }
    private static void Click(DependencyObject root,string label)=>Descendants(root).OfType<Button>().Single(b=>Equals(b.Content,label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}}
    private static IEnumerable<DependencyObject> Ancestors(DependencyObject child)
    {for(var parent=VisualTreeHelper.GetParent(child);parent is not null;parent=VisualTreeHelper.GetParent(parent))yield return parent;}
    private sealed class Fixture:IDisposable
    {
        public string Root {get;}=Path.Combine(Path.GetTempPath(),"citrus-native-pdf-"+Guid.NewGuid().ToString("N"));
        public string Input {get;}
        public PdfEngine Engine {get;}=new();
        public Fixture(){Directory.CreateDirectory(Root);Input=Path.Combine(Root,"pages.pdf");var builder=new PdfDocumentBuilder();var font=builder.AddStandard14Font(Standard14Font.Helvetica);foreach(string text in new[]{"First","Second"})builder.AddPage(200,150).AddText(text,14,new PdfPoint(10,60),font);File.WriteAllBytes(Input,builder.Build());}
        public void Dispose()=>Directory.Delete(Root,true);
    }
}
