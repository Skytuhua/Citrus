using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Citrus.Conversion;
using Citrus.Conversion.Editing.Media;
using Citrus.Core;
using Citrus.Core.Editing;
using Citrus.Windows.Editors.Media;
using Button=System.Windows.Controls.Button;
using CheckBox=System.Windows.Controls.CheckBox;
using ComboBox=System.Windows.Controls.ComboBox;
using TextBox=System.Windows.Controls.TextBox;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class MediaEditorInputRegressionTests(DesktopFixture desktop)
{
    [Theory]
    [InlineData("audio.compress","Compression value","73")]
    [InlineData("audio.metadata","title","New title")]
    [InlineData("audio.visualizer","Wave color hexadecimal","#123456")]
    [InlineData("video.resize","Video output width in pixels","16")]
    public Task Default_apply_uses_text_without_requiring_focus_loss(string id,string field,string text)=>desktop.RunAsync(async()=>
    {
        using var fixture=await Fixture.CreateAsync(id.StartsWith("video.",StringComparison.Ordinal));var result=await Show(fixture,id,async window=>
        {
            if(id=="video.resize"){((Expander)window.FindName("ResizeOptions")).IsExpanded=true;window.UpdateLayout();}
            var input=Controls<TextBox>(window).Single(c=>AutomationProperties.GetName(c)==field);
            input.Text=text;
            var action=(Button)window.FindName("ApplyButton");Assert.True(action.IsDefault);action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>!window.IsVisible);
        });
        Assert.NotNull(result);var document=Assert.Single(result.Items).Document;
        if(id=="audio.compress")Assert.Equal(new ReductionGoal(73),document.Export.Compression);
        if(id=="audio.metadata")Assert.Contains(Assert.IsType<AudioRecipe>(document.Recipe).MetadataEdits,e=>e.Key=="title"&&e.Value is TextValue{Value:"New title"});
        if(id=="audio.visualizer")Assert.Equal(new Rgba32(0x12,0x34,0x56,255),Assert.IsType<VisualizerRecipe>(document.Recipe).Foreground);
        if(id=="video.resize")Assert.Equal(new SizeI(16,12),Assert.IsType<VideoRecipe>(document.Recipe).Canvas);
    });

    [Theory]
    [InlineData("audio.compress","Compression value","bad")]
    [InlineData("audio.visualizer","Wave color hexadecimal","#badcolor")]
    public Task Invalid_text_stays_inadmissible_after_another_control_changes(string id,string field,string text)=>desktop.RunAsync(async()=>
    {
        using var fixture=await Fixture.CreateAsync();string second=Path.Combine(fixture.Root,"second.wav");File.Copy(fixture.Input,second);await Show(fixture,id,window=>
        {
            Controls<TextBox>(window).Single(c=>AutomationProperties.GetName(c)==field).Text=text;
            if(id=="audio.compress")Controls<ComboBox>(window).Single(c=>c.Items.Count==5).SelectedItem="FLAC";
            else Controls<ComboBox>(window).Single(c=>Equals(c.Items[0],"1920 × 1080")).SelectedIndex=1;
            Assert.False(((Button)window.FindName("ApplyButton")).IsEnabled);
            var picker=Controls<ComboBox>(window).Single(c=>c.Items.Count==2);picker.SelectedIndex=1;
            Assert.Equal(0,picker.SelectedIndex);Assert.Equal(fixture.Input,window.CurrentDocument!.Sources[0].Path);window.Close();return Task.CompletedTask;
        },[fixture.Input,second]);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Automatic_trim_discards_results_after_source_or_draft_changes(bool switchSource)=>desktop.RunAsync(async()=>
    {
        using var fixture=await Fixture.CreateAsync();string second=Path.Combine(fixture.Root,"second.wav");WriteWave(second,2,false);
        await Show(fixture,"audio.trim",async window=>
        {
            Controls<CheckBox>(window).Single(c=>Equals(c.Content,"Trim silent ends automatically")).IsChecked=true;
            var pending=((HashSet<Task>)typeof(MediaEditorWindow).GetField("work",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!).ToArray();
            if(switchSource)Controls<ComboBox>(window).Single(c=>c.Items.Count==2).SelectedIndex=1;
            else Controls<MediaRangeView>(window).Single().RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,System.Windows.Input.Key.Right){RoutedEvent=System.Windows.Input.Keyboard.KeyDownEvent});
            await Task.WhenAll(pending);await Wait(()=>window.IsReady&&window.CurrentDocument!.Sources[0].Path==(switchSource?second:fixture.Input));
            var clip=Assert.Single(Assert.IsType<AudioRecipe>(window.CurrentDocument!.Recipe).Clips);
            Assert.Equal(switchSource?0:.01,MediaEffectFilters.Seconds(clip.Range.Start));Assert.Equal(switchSource?2:1,MediaEffectFilters.Seconds(clip.Range.End));window.Close();
        },[fixture.Input,second]);
    });

    [Fact]
    public Task Visualizer_color_validation_only_blocks_modes_that_use_the_color()=>desktop.RunAsync(async()=>
    {
        using var fixture=await Fixture.CreateAsync();await Show(fixture,"audio.visualizer",window=>
        {
            var color=Controls<TextBox>(window).Single(c=>AutomationProperties.GetName(c)=="Wave color hexadecimal");
            var mode=Controls<ComboBox>(window).Single(c=>Equals(c.Items[0],"Waveform"));var action=(Button)window.FindName("ApplyButton");
            color.Text="#badcolor";Assert.False(action.IsEnabled);
            mode.SelectedIndex=1;Assert.False(color.IsEnabled);Assert.True(action.IsEnabled);
            mode.SelectedIndex=0;Assert.True(color.IsEnabled);Assert.False(action.IsEnabled);
            color.Text="#123456";Assert.True(action.IsEnabled);window.Close();return Task.CompletedTask;
        });
    });

    [Fact]
    public Task Late_resize_focus_loss_cannot_change_the_next_source()=>desktop.RunAsync(async()=>
    {
        using var fixture=await Fixture.CreateAsync(true);string second=Path.Combine(fixture.Root,"second.mp4");File.Copy(fixture.Input,second);
        await Show(fixture,"video.resize",async window=>
        {
            var oldWidth=(TextBox)window.FindName("ResizeWidth");oldWidth.Text="16";
            Controls<ComboBox>(window).Single(c=>c.Items.Count==2).SelectedIndex=1;
            await Wait(()=>window.IsReady&&window.CurrentDocument!.Sources[0].Path==second);
            oldWidth.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,0,oldWidth,window){RoutedEvent=System.Windows.Input.Keyboard.LostKeyboardFocusEvent});
            Assert.Equal(new SizeI(32,24),Assert.IsType<VideoRecipe>(window.CurrentDocument!.Recipe).Canvas);window.Close();
        },[fixture.Input,second]);
    });

    private static async Task<EditorSubmissionBatch?> Show(Fixture fixture,string id,Func<MediaEditorWindow,Task> exercise,string[]? inputs=null)
    {
        var operation=fixture.Engine.Operations.Single(o=>o.Id==id);var window=new MediaEditorWindow(fixture.Engine);Task? check=null;
        window.Loaded+=(_,_)=>check=Check();var result=await window.ShowEditorAsync(new(operation,[..inputs??[fixture.Input]],operation.Label,new(),fixture.Root,SubmissionOrigin.Home),default);await check!;return result;
        async Task Check(){try{await Wait(()=>window.IsReady);await exercise(window);}finally{if(window.IsVisible)window.Close();}}
    }
    private static IEnumerable<T> Controls<T>(DependencyObject root)where T:DependencyObject
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T match)yield return match;foreach(var nested in Controls<T>(child))yield return nested;}}
    private static async Task Wait(Func<bool> condition){for(int i=0;i<750&&!condition();i++)await Task.Delay(20);Assert.True(condition());}
    private static void WriteWave(string path,int seconds,bool silence)
    {
        int samples=48000*seconds;using var writer=new BinaryWriter(File.Create(path));writer.Write("RIFF"u8);writer.Write(36+samples*2);writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)1);writer.Write((short)1);writer.Write(48000);writer.Write(96000);writer.Write((short)2);writer.Write((short)16);writer.Write("data"u8);writer.Write(samples*2);
        for(int i=0;i<samples;i++)writer.Write(silence&&(i<12000||i>=samples-12000)?(short)0:(short)(5000*Math.Sin(i*2*Math.PI*440/48000)));
    }
    private sealed class Fixture:IDisposable
    {
        public string Root{get;}=Path.Combine(Path.GetTempPath(),"citrus-input-regression-"+Guid.NewGuid().ToString("N"));
        public string Input{get;private set;}="";
        public MediaEditorEngine Engine{get;private set;}=null!;
        public static async Task<Fixture> CreateAsync(bool video=false)
        {
            var fixture=new Fixture();Directory.CreateDirectory(fixture.Root);fixture.Input=Path.Combine(fixture.Root,video?"first.mp4":"first.wav");var process=MediaProcess.Bundled();
            if(video)
            {
                string raw=Path.Combine(fixture.Root,"frame.bgra");await File.WriteAllBytesAsync(raw,Enumerable.Repeat(new byte[]{32,64,128,255},32*24*12).SelectMany(v=>v).ToArray());
                await process.RunAsync(["-f","rawvideo","-pixel_format","bgra","-video_size","32x24","-framerate","12","-i",raw,"-c:v","mpeg4",fixture.Input],null,default);
            }
            else WriteWave(fixture.Input,1,true);
            fixture.Engine=new(process,await process.DiscoverCapabilitiesAsync(default),new());return fixture;
        }
        public void Dispose()=>Directory.Delete(Root,true);
    }
}
