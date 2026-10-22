using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Citrus.Conversion;
using Citrus.Conversion.Editing.Media;
using Citrus.Core;
using Citrus.Core.Editing;
using Citrus.Windows.Editors.Media;
using Button=System.Windows.Controls.Button;
using CheckBox=System.Windows.Controls.CheckBox;
using ComboBox=System.Windows.Controls.ComboBox;
using Slider=System.Windows.Controls.Slider;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class MediaControlHydrationTests(DesktopFixture desktop)
{
    [Theory]
    [InlineData("audio.channels")]
    [InlineData("audio.bleep")]
    [InlineData("audio.compress")]
    [InlineData("audio.visualizer")]
    [InlineData("video.snapshot")]
    public Task Nondefault_controls_survive_switching_sources(string id)=>desktop.RunAsync(async()=>
    {
        string root=Path.Combine(Path.GetTempPath(),"citrus-media-hydration-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var process=MediaProcess.Bundled();var engine=new MediaEditorEngine(process,await process.DiscoverCapabilitiesAsync(default),new());
            string first=Path.Combine(root,"first.wav"),second=Path.Combine(root,"second.wav");
            WriteWave(first);File.Copy(first,second);
            if(id.StartsWith("video.",StringComparison.Ordinal))
            {
                string raw=Path.Combine(root,"frame.bgra");await File.WriteAllBytesAsync(raw,Enumerable.Repeat(new byte[]{32,64,128,255},32*24).SelectMany(v=>v).ToArray());
                first=Path.Combine(root,"first.mp4");second=Path.Combine(root,"second.mp4");
                await process.RunAsync(["-stream_loop","11","-f","rawvideo","-pixel_format","bgra","-video_size","32x24","-framerate","12","-i",raw,"-c:v","mpeg4",first],null,default);File.Copy(first,second);
            }
            var operation=engine.Operations.Single(o=>o.Id==id);var window=new MediaEditorWindow(engine){ConfirmDiscard=()=>true};Task? check=null;
            window.Loaded+=(_,_)=>check=Check();
            await window.ShowEditorAsync(new(operation,[first,second],operation.Label,new(),root,SubmissionOrigin.Home),default);await check!;
            async Task Check()
            {
                try
                {
                    await Wait(()=>window.IsReady);
                    if(id=="audio.channels")Controls<Slider>().Single(s=>s.Minimum==0&&s.Maximum==200).Value=37;
                    if(id=="audio.bleep")
                    {
                        Click("Add range");Controls<Slider>().Single(s=>s.Minimum==100).Value=1400;Controls<Slider>().Single(s=>s.Minimum==-60).Value=-9;
                        Controls<ComboBox>().Single(c=>c.Items.Count==2&&Equals(c.Items[0],"Tone")).SelectedIndex=1;
                    }
                    if(id=="audio.compress")
                    {
                        Controls<ComboBox>().Single(c=>c.Items.Count==3&&Equals(c.Items[0],"Percentage")).SelectedIndex=2;
                        Controls<Slider>().Single().Value=73;
                        Controls<ComboBox>().Single(c=>c.Items.Count==5&&Equals(c.Items[0],"MP3")).SelectedItem="FLAC";
                    }
                    if(id=="audio.visualizer")
                    {
                        Controls<ComboBox>().Single(c=>c.Items.Count==3&&Equals(c.Items[0],"Waveform")).SelectedIndex=2;
                        Controls<ComboBox>().Single(c=>c.Items.Count==3&&Equals(c.Items[0],"1920 × 1080")).SelectedIndex=2;
                        var include=Controls<CheckBox>().Single(c=>Equals(c.Content,"Include audio"));include.IsChecked=false;include.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    if(id=="video.snapshot")Controls<ComboBox>().Single(c=>c.Items.Count==3&&Equals(c.Items[0],"PNG")).SelectedItem="WEBP";
                    await Switch(1);await Switch(0);
                    if(id=="audio.channels")Assert.Equal(37,Controls<Slider>().Single(s=>s.Maximum==200).Value);
                    if(id=="audio.bleep")
                    {
                        Assert.Equal(1400,Controls<Slider>().Single(s=>s.Minimum==100).Value);Assert.Equal(-9,Controls<Slider>().Single(s=>s.Minimum==-60).Value);
                        Assert.Equal("Silence",Controls<ComboBox>().Single(c=>c.Items.Count==2&&Equals(c.Items[0],"Tone")).SelectedItem);
                    }
                    if(id=="audio.compress")
                    {
                        Assert.Equal(73,Controls<Slider>().Single().Value);Assert.Equal("FLAC",Controls<ComboBox>().Single(c=>c.Items.Count==5&&Equals(c.Items[0],"MP3")).SelectedItem);
                    }
                    if(id=="audio.visualizer")
                    {
                        Assert.Equal(2,Controls<ComboBox>().Single(c=>c.Items.Count==3&&Equals(c.Items[0],"1920 × 1080")).SelectedIndex);
                        Assert.False(Controls<CheckBox>().Single(c=>Equals(c.Content,"Include audio")).IsChecked);
                    }
                    if(id=="video.snapshot")Assert.Equal("WEBP",Controls<ComboBox>().Single(c=>c.Items.Count==3&&Equals(c.Items[0],"PNG")).SelectedItem);
                }
                finally{window.Close();}
            }
            IEnumerable<T> Controls<T>()where T:DependencyObject=>Descendants((FrameworkElement)window.Content).OfType<T>();
            ComboBox Picker()=>Controls<ComboBox>().Single(c=>c.Items.Count==2&&Equals(c.Items[0],Path.GetFileName(first)));
            async Task Switch(int index){var old=Picker();old.SelectedIndex=index;await Wait(()=>!ReferenceEquals(old,Picker())&&Picker().SelectedIndex==index);}
            void Click(string text)=>Controls<Button>().Single(b=>Equals(b.Content,text)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        finally{Directory.Delete(root,true);}
    });
    private static void WriteWave(string path)
    {
        using var writer=new BinaryWriter(File.Create(path));writer.Write("RIFF"u8);writer.Write(96036);writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)1);writer.Write((short)1);writer.Write(48000);writer.Write(96000);writer.Write((short)2);writer.Write((short)16);writer.Write("data"u8);writer.Write(96000);
        for(int i=0;i<48000;i++)writer.Write((short)(5000*Math.Sin(i*2*Math.PI*440/48000)));
    }
    private static async Task Wait(Func<bool> condition){for(int i=0;i<500&&!condition();i++)await Task.Delay(20);Assert.True(condition());}
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root){yield return root;for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Descendants(System.Windows.Media.VisualTreeHelper.GetChild(root,i)))yield return child;}
}
