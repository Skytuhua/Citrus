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
using CheckBox=System.Windows.Controls.CheckBox;

namespace Citrus.Windows.Tests;

[Collection("Desktop")]
public sealed class ImageNativePreviewInteractionTests(DesktopFixture desktop)
{
    [Theory]
    [InlineData(100,70)] [InlineData(900,700)]
    public Task Actual_photo_100_percent_button_uses_native_pixels_and_Fit_restores_whole_image(int width,int height)=>desktop.RunAsync(async()=>
    {
        string root=Path.Combine(Path.GetTempPath(),"citrus-native-preview-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string source=Path.Combine(root,"source.png");using(var image=new MagickImage(MagickColors.Orange,(uint)width,(uint)height))image.Write(source,MagickFormat.Png);
            using var services=new ImageEditorServices(new());var window=new EditorHostWindow(services);using var cancel=new CancellationTokenSource();Exception? failure=null;
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(25)};bool entered=false;var deadline=System.Diagnostics.Stopwatch.StartNew();
            timer.Tick+=async(_,_)=>
            {
                if(entered)return;
                if(deadline.Elapsed>TimeSpan.FromSeconds(15)){entered=true;failure=new TimeoutException("Initial image preview did not arrive.");cancel.Cancel();return;}
                var preview=(System.Windows.Controls.Image)window.FindName("PreviewImage");if(preview.Source is null)return;
                entered=true;timer.Stop();
                try
                {
                    Assert.Equal("Fit",((TextBlock)window.FindName("ZoomValue")).Text);
                    var before=preview.Source;((Button)window.FindName("NativePreviewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Changed(preview,before);
                    Assert.True(((CheckBox)window.FindName("DetailPreview")).IsChecked);Assert.Equal(Stretch.None,preview.Stretch);
                    var bitmap=Assert.IsAssignableFrom<BitmapSource>(preview.Source);var dpi=VisualTreeHelper.GetDpi(preview);
                    Assert.Equal(96*dpi.DpiScaleX,bitmap.DpiX,6);Assert.Equal(96*dpi.DpiScaleY,bitmap.DpiY,6);
                    Assert.InRange(bitmap.PixelWidth,1,Math.Min(width,512));Assert.InRange(bitmap.PixelHeight,1,Math.Min(height,512));
                    // WPF exposes natural bitmap layout dimensions through single-precision
                    // intermediates. Allow less than 1/4096 physical pixel of layout rounding;
                    // decoded buffers and native-preview/export byte comparisons remain exact.
                    const double layoutEpsilon=1d/4096;
                    Assert.InRange(Math.Abs(bitmap.PixelWidth-bitmap.Width*dpi.DpiScaleX),0,layoutEpsilon);
                    Assert.InRange(Math.Abs(bitmap.PixelHeight-bitmap.Height*dpi.DpiScaleY),0,layoutEpsilon);
                    var status=(TextBlock)window.FindName("PreviewStatus");window.UpdateLayout();
                    Assert.Empty(status.Text);Assert.Equal(Visibility.Collapsed,status.Visibility);
                    before=preview.Source;((Button)window.FindName("FitPreviewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Changed(preview,before);
                    Assert.False(((CheckBox)window.FindName("DetailPreview")).IsChecked);Assert.Equal(Stretch.Uniform,preview.Stretch);Assert.Equal("Fit",((TextBlock)window.FindName("ZoomValue")).Text);
                }
                catch(Exception error){failure=error;}
                finally{cancel.Cancel();}
            };
            timer.Start();
            try{await window.ShowEditorAsync(new(new("image.editor","Edit Photo",OperationMode.Tools,null,false,true),[source],"Adjust",new(),null,SubmissionOrigin.Home),cancel.Token);}
            finally{timer.Stop();}
            if(failure is not null)throw failure;
        }
        finally{Directory.Delete(root,true);}
    });
    private static async Task Changed(System.Windows.Controls.Image preview,ImageSource? before)
    {
        var deadline=System.Diagnostics.Stopwatch.StartNew();
        while(ReferenceEquals(preview.Source,before))
        {
            if(deadline.Elapsed>TimeSpan.FromSeconds(10))throw new TimeoutException("Requested preview mode did not publish.");
            await Task.Delay(20);
        }
        Assert.NotNull(preview.Source);
    }
}
