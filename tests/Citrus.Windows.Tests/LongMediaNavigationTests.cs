using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Citrus.Conversion;
using Citrus.Conversion.Editing.Media;
using Citrus.Core;
using Citrus.Core.Editing;
using Citrus.Windows.Editors.Media;
using ImageMagick;
using Button=System.Windows.Controls.Button;
using ComboBox=System.Windows.Controls.ComboBox;
using Slider=System.Windows.Controls.Slider;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class LongMediaNavigationTests(DesktopFixture desktop)
{
    [Theory]
    [InlineData("video.snapshot")]
    [InlineData("audio.bleep")]
    public Task Late_timeline_navigation_and_processed_audition_work_after_two_minutes(string id)=>desktop.RunAsync(async()=>
    {
        string root=Path.Combine(Path.GetTempPath(),"citrus-late-navigation-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var process=MediaProcess.Bundled();var engine=new MediaEditorEngine(process,await process.DiscoverCapabilitiesAsync(default),new());string input;
            if(id.StartsWith("video.",StringComparison.Ordinal))
            {
                string raw=Path.Combine(root,"long.bgra");
                using(var file=File.Create(raw))for(int frame=0;frame<140;frame++)for(int i=0;i<32*24;i++)file.Write([(byte)frame,64,128,255]);
                input=Path.Combine(root,"long.mp4");await process.RunAsync(["-f","rawvideo","-pixel_format","bgra","-video_size","32x24","-framerate","1","-i",raw,"-c:v","mpeg4","-q:v","1",input],null,default);
            }
            else
            {
                input=Path.Combine(root,"long.wav");using var writer=new BinaryWriter(File.Create(input));const int count=140*48000;writer.Write("RIFF"u8);writer.Write(36+count*2);writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)1);writer.Write((short)1);writer.Write(48000);writer.Write(96000);writer.Write((short)2);writer.Write((short)16);writer.Write("data"u8);writer.Write(count*2);for(int i=0;i<count;i++)writer.Write((short)(5000*Math.Sin(i*2*Math.PI*440/48000)));
            }
            var operation=engine.Operations.Single(o=>o.Id==id);var window=new MediaEditorWindow(engine);Task? checking=null;EditorDocument? document=null;
            window.Loaded+=(_,_)=>checking=Check();await window.ShowEditorAsync(new(operation,[input],operation.Label,new(),root,SubmissionOrigin.Home),default);await checking!;
            Assert.NotNull(document);
            if(id=="video.snapshot")
            {
                string output=Assert.Single((await engine.ExecuteAsync(new(operation,[input],new(),root,document),null,default)).Outputs);
                using var image=new MagickImage(output);byte[] pixels=image.GetPixels().ToByteArray(PixelMapping.BGRA)!;Assert.InRange(pixels[0],(byte)127,(byte)133);
            }
            else
            {
                await using var preparation=new MediaPlaybackPreparation(process,engine.Sources,Path.Combine(root,"late-audition"),new(32,24));
                await using var prepared=await preparation.PrepareAsync(document!,new(new(130,1),new(140,1)),false,1,default);
                byte[] samples=await File.ReadAllBytesAsync(prepared.Audio!.Path);
                Assert.All(samples.Take(48000*2),b=>Assert.Equal(0,b));Assert.Contains(samples.Skip(48000*2),b=>b!=0);
            }
            async Task Check()
            {
                try
                {
                    await Wait(()=>window.IsReady);
                    var full=window.FindName("FullTimeline") as Slider;Assert.NotNull(full);Assert.True(full.Maximum>=140);full.Value=130;
                    if(id=="video.snapshot")
                    {
                        var controller=(MediaPreviewController)typeof(MediaEditorWindow).GetField("playback",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
                        await Wait(()=>MediaEffectFilters.Seconds(controller.Position)>=130);
                        Click("Use current frame");Assert.InRange(MediaEffectFilters.Seconds(((VideoRecipe)window.CurrentDocument!.Recipe).SnapshotAt!.Value),130,131);
                    }
                    else
                    {
                        Click("Add range");var recipe=(AudioRecipe)window.CurrentDocument!.Recipe;Assert.Equal(new RationalTime(130,1),recipe.Bleeps[0].Range.Start);
                        Descendants((FrameworkElement)window.Content).OfType<ComboBox>().Single(c=>c.Items.Count==2&&Equals(c.Items[0],"Tone")).SelectedIndex=1;
                        Click("▶ With Bleeps");
                        var controller=(MediaPreviewController)typeof(MediaEditorWindow).GetField("playback",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
                        await Wait(()=>MediaEffectFilters.Seconds(controller.Position)>=130);Assert.NotEqual(MediaPlaybackState.Failed,controller.State);
                    }
                    document=window.CurrentDocument;
                }
                finally{window.Close();}
            }
            void Click(string text)=>Descendants((FrameworkElement)window.Content).OfType<Button>().Single(b=>Equals(b.Content,text)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        finally{Directory.Delete(root,true);}
    },TimeSpan.FromSeconds(90));
    private static async Task Wait(Func<bool> condition){for(int i=0;i<1500&&!condition();i++)await Task.Delay(20);Assert.True(condition());}
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root){yield return root;for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Descendants(System.Windows.Media.VisualTreeHelper.GetChild(root,i)))yield return child;}
}
