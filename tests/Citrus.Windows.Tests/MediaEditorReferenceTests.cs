using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Citrus.Conversion;
using Citrus.Conversion.Editing.Media;
using Citrus.Core;
using Citrus.Core.Editing;
using Citrus.Windows.Editors.Media;
using Citrus.Windows.Presentation;
using Button=System.Windows.Controls.Button;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class MediaEditorReferenceTests(DesktopFixture desktop)
{
    [Theory]
    [InlineData("audio.bleep","Save Bleeped Copy",false)]
    [InlineData("audio.trim","Apply",true)]
    [InlineData("audio.channels","Apply",true)]
    [InlineData("audio.metadata","Save",true)]
    [InlineData("audio.compress","Compress",true)]
    [InlineData("audio.normalize","Apply",true)]
    [InlineData("audio.visualizer","Save Visualization",true)]
    [InlineData("video.compress","Compress",true)]
    [InlineData("video.metadata","Save",true)]
    [InlineData("video.mute","Apply",true)]
    [InlineData("video.trim","Apply",true)]
    [InlineData("video.speed","Apply",true)]
    [InlineData("video.snapshot","Save Snapshot",true)]
    [InlineData("video.split","Split",true)]
    [InlineData("video.redact","Apply",true)]
    [InlineData("video.resize","Apply",true)]
    public Task Real_reference_audio_views_load_and_use_tool_specific_actions(string id,string label,bool enabled)=>desktop.RunAsync(async()=>
    {
        string root=Path.Combine(Path.GetTempPath(),"citrus-media-ui-proof-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string wave=Path.Combine(root,"source.wav");
            using(var writer=new BinaryWriter(File.Create(wave)))
            {
                writer.Write("RIFF"u8);writer.Write(36+96000);writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)1);writer.Write((short)1);writer.Write(48000);writer.Write(96000);writer.Write((short)2);writer.Write((short)16);writer.Write("data"u8);writer.Write(96000);
                for(int i=0;i<48000;i++)writer.Write((short)(5000*Math.Sin(i*Math.PI*440/24000)));
            }
            var process=MediaProcess.Bundled();var capabilities=await process.DiscoverCapabilitiesAsync(default);var engine=new MediaEditorEngine(process,capabilities,new());
            string input=wave;
            if(id.StartsWith("video.",StringComparison.Ordinal))
            {
                string raw=Path.Combine(root,"pattern.bgra");
                using(var file=File.Create(raw))for(int frame=0;frame<12;frame++)for(int pixel=0;pixel<96*64;pixel++)file.Write([(byte)(frame*12),64,192,255]);
                input=Path.Combine(root,"video.mp4");
                await process.RunAsync(["-f","rawvideo","-pixel_format","bgra","-video_size","96x64","-framerate","12","-i",raw,"-i",wave,"-c:v","mpeg4","-c:a","aac","-shortest",input],null,default);
            }
            using var themes=new ThemeManager(System.Windows.Application.Current);
            using var presentation=new WindowPresentationService(System.Windows.Application.Current,themes,()=>new CitrusSettings());
            var operation=engine.Operations.Single(o=>o.Id==id);var window=new MediaEditorWindow(engine){ConfirmDiscard=()=>true};Task? check=null;
            presentation.Attach(window,(MaterialSurface)window.Content);
            window.Loaded+=(_,_)=>check=AssertState();
            await window.ShowEditorAsync(new(operation,[input],operation.Label,new(),root,SubmissionOrigin.Home),default);
            await check!;
            async Task AssertState()
            {
                try
                {
                    for(int attempts=0;attempts<250&&!window.IsReady;attempts++)await Task.Delay(20);
                    Assert.True(window.IsReady,((TextBlock)window.FindName("StatusText")).Text);
                    var action=(Button)window.FindName("ApplyButton");Assert.Equal(label,action.Content);Assert.Equal(enabled,action.IsEnabled);
                    if(id=="audio.bleep")Assert.Equal("Add at least one bleep range.",((TextBlock)window.FindName("StatusText")).Text);
                    foreach(double scale in new[]{1d,1.5,2d})VisualArtifacts.Capture((FrameworkElement)window.Content,"media-reference-default-"+id.Replace('.','-')+"-"+scale.ToString("0.0",System.Globalization.CultureInfo.InvariantCulture),scale,ThemeChoice.Light);
                    window.Width=window.MinWidth;window.Height=window.MinHeight;window.UpdateLayout();
                    VisualArtifacts.AssertInside(action,(FrameworkElement)window.Content);
                    foreach(double scale in new[]{1d,1.5,2d})VisualArtifacts.Capture((FrameworkElement)window.Content,"media-reference-minimum-"+id.Replace('.','-')+"-"+scale.ToString("0.0",System.Globalization.CultureInfo.InvariantCulture),scale,ThemeChoice.Light);
                    if(id=="audio.bleep")
                    {
                        var add=Descendants((FrameworkElement)window.Content).OfType<Button>().Single(b=>Equals(b.Content,"Add range"));
                        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.Single(((AudioRecipe)window.CurrentDocument!.Recipe).Bleeps);
                        Assert.True(action.IsEnabled);
                    }
                }
                finally{window.Close();}
            }
        }
        finally{Directory.Delete(root,true);}
    });
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++)
            foreach(var child in Descendants(System.Windows.Media.VisualTreeHelper.GetChild(root,i)))yield return child;
    }
}

