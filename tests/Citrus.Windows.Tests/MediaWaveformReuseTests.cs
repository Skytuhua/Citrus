using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Citrus.Conversion;
using Citrus.Conversion.Editing;
using Citrus.Conversion.Editing.Media;
using Citrus.Core;
using Citrus.Core.Editing;
using Citrus.Windows.Editors.Media;

namespace Citrus.Windows.Tests;

/// <summary>Real stereo decode and unshown WPF controls; no native window or audio device.</summary>
[Collection("Desktop")]
public sealed class MediaWaveformReuseTests(DesktopFixture desktop)
{
    [Theory]
    [InlineData("audio.trim")]
    [InlineData("audio.bleep")]
    public Task Original_display_rows_survive_edits_without_restaging_or_decoding(string operation)=>desktop.RunAsync(async()=>
    {
        await using var fixture=await Fixture.CreateAsync(operation);
        await fixture.Load();
        var before=fixture.Range.Buckets;int stages=fixture.Snapshots.StageCalls;
        Assert.Equal(1024,before.Length);
        var expected=await fixture.Engine.BuildWaveformsAsync(fixture.Initial,1024,default);
        Assert.True(expected[0].SequenceEqual(before));
        Assert.NotEqual(expected[0][128].Maximum,expected[1][128].Maximum);
        stages=fixture.Snapshots.StageCalls;
        long verified=fixture.VerificationBytes;
        fixture.Edit();await fixture.Refresh();
        Assert.True(before.Equals(fixture.Range.Buckets));
        Assert.Equal(stages,fixture.Snapshots.StageCalls);
        Assert.Equal(fixture.Initial.Sources.Sum(source=>source.Length),fixture.VerificationBytes-verified);
        fixture.AssertUnshown();
    });

    [Fact]
    public Task A_cached_trim_refresh_needs_neither_media_executable()=>desktop.RunAsync(async()=>
    {
        await using var fixture=await Fixture.CreateAsync("audio.trim");await fixture.Load();
        var rows=fixture.Range.Buckets;
        Set(fixture.Engine,"<process>P",new MediaProcess(Path.Combine(fixture.Root,"must-not-launch-ffmpeg.exe"),Path.Combine(fixture.Root,"must-not-launch-ffprobe.exe")));
        fixture.Edit();await fixture.Refresh();
        Assert.True(rows.Equals(fixture.Range.Buckets));
    });

    [Fact]
    public Task Different_sources_have_distinct_rows_and_returning_to_the_first_reuses_its_rows()=>desktop.RunAsync(async()=>
    {
        await using var fixture=await Fixture.CreateAsync("audio.trim",twoSources:true);
        await fixture.Load();var first=fixture.Range.Buckets;
        await fixture.Switch(1);var second=fixture.Range.Buckets;
        Assert.False(first.SequenceEqual(second));
        int stages=fixture.Snapshots.StageCalls;
        await fixture.Switch(0);
        Assert.True(first.Equals(fixture.Range.Buckets));
        Assert.Equal(stages,fixture.Snapshots.StageCalls);
        fixture.AssertUnshown();
    });

    [Fact]
    public Task A_cached_original_is_rehashed_and_same_length_content_changes_are_rejected()=>desktop.RunAsync(async()=>
    {
        await using var fixture=await Fixture.CreateAsync("audio.trim");
        await fixture.Load();var before=fixture.Range.Buckets;
        string path=fixture.Initial.Sources[0].Path;DateTime stamp=File.GetLastWriteTimeUtc(path);
        using(var file=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.Read)){file.Position=44;file.WriteByte(99);}
        File.SetLastWriteTimeUtc(path,stamp);
        var error=await Assert.ThrowsAsync<IOException>(()=>fixture.Refresh());
        Assert.Contains("Reload",error.Message);
        Assert.True(before.Equals(fixture.Range.Buckets));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Canceled_or_faulted_waveform_builds_do_not_seed_the_cache(bool fail)=>desktop.RunAsync(async()=>
    {
        await using var fixture=await Fixture.CreateAsync("audio.trim",twoSources:true);
        await fixture.Load();
        fixture.Snapshots.BlockNext=true;fixture.Snapshots.FailBlocked=fail;
        var pending=fixture.Switch(1);
        await fixture.Snapshots.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if(!fail)fixture.CancelRefresh();
        fixture.Snapshots.Release.TrySetResult();
        if(fail)await Assert.ThrowsAsync<IOException>(()=>pending);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
        Assert.Empty(fixture.Range.Buckets);
        int stages=fixture.Snapshots.StageCalls;
        await fixture.Refresh();
        Assert.Equal(stages+1,fixture.Snapshots.StageCalls);
        Assert.Equal(1024,fixture.Range.Buckets.Length);
    });

    [Fact]
    public Task A_source_switch_cancels_an_older_cache_hit_before_it_can_publish()=>desktop.RunAsync(async()=>
    {
        await using var fixture=await Fixture.CreateAsync("audio.trim",twoSources:true);
        await fixture.Load();var first=fixture.Range.Buckets;
        var gate=Field<SemaphoreSlim>(fixture.Probe,"gate");await gate.WaitAsync();
        Task pending=fixture.Refresh(),switched=fixture.Switch(1);
        gate.Release();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);await switched;
        Assert.False(first.SequenceEqual(fixture.Range.Buckets));
        var second=fixture.Range.Buckets;await fixture.Refresh();
        Assert.True(second.Equals(fixture.Range.Buckets));
    });

    [Fact]
    public Task Edited_channel_rows_are_rebuilt_from_the_current_recipe()=>desktop.RunAsync(async()=>
    {
        await using var fixture=await Fixture.CreateAsync("audio.channels");await fixture.Load();
        var channels=Field<List<WaveformView>>(fixture.Window,"channelViews");var first=channels[0].Buckets;
        int stages=fixture.Snapshots.StageCalls;
        var audio=(AudioRecipe)fixture.Window.CurrentDocument!.Recipe;
        fixture.SetRecipe(audio with {Channels=audio.Channels with {Coefficients=audio.Channels.Coefficients.Select(value=>value*.5).ToImmutableArray()}});
        await fixture.Refresh();
        Assert.Equal(stages+1,fixture.Snapshots.StageCalls);
        Assert.False(first.SequenceEqual(channels[0].Buckets));
        Assert.InRange(channels[0].Buckets.Max(bucket=>bucket.Maximum)/first.Max(bucket=>bucket.Maximum),.49f,.51f);
    });

    [Fact]
    public Task Automatic_silence_detection_keeps_its_finer_uncached_waveform()=>desktop.RunAsync(async()=>
    {
        await using var fixture=await Fixture.CreateAsync("audio.trim");await fixture.Load();
        int stages=fixture.Snapshots.StageCalls;var rows=fixture.Range.Buckets;
        var audible=await fixture.Engine.FindAudibleRangeAsync(fixture.Initial,default);
        Assert.Equal(stages+1,fixture.Snapshots.StageCalls);
        Assert.Equal(new TimeRange(new(0,1),new(8,1)),audible);
        await fixture.Refresh();Assert.True(rows.Equals(fixture.Range.Buckets));
        Assert.Equal(stages+1,fixture.Snapshots.StageCalls);
    });

    [Theory]
    [InlineData("viewGeneration")]
    [InlineData("sourceGeneration")]
    public Task A_successful_hit_cannot_publish_after_its_generation_changes(string generation)=>desktop.RunAsync(async()=>
    {
        await using var fixture=await Fixture.CreateAsync("audio.trim");await fixture.Load();
        var gate=Field<SemaphoreSlim>(fixture.Probe,"gate");await gate.WaitAsync();
        var pending=fixture.Refresh();fixture.Range.Buckets=[];
        Set(fixture.Window,generation,Field<long>(fixture.Window,generation)+1);
        gate.Release();await pending;
        Assert.Empty(fixture.Range.Buckets);
        await fixture.Refresh();Assert.Equal(1024,fixture.Range.Buckets.Length);
    });

    [Fact]
    public Task Visualizer_bitmap_preserves_decoded_pixels_dimensions_DPI_and_freeze()=>desktop.RunAsync(async()=>
    {
        await using var fixture=await Fixture.CreateAsync("audio.visualizer");await fixture.Load();
        var bitmap=Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapSource>(Field<System.Windows.Controls.Image>(fixture.Window,"visualizationPreview").Source);
        var expected=await fixture.Engine.RenderVisualizationPreviewAsync(fixture.Window.CurrentDocument!,new(1,new(720,400),null,null,null),default);
        byte[] pixels=new byte[expected.Stride*expected.Size.Height];bitmap.CopyPixels(pixels,expected.Stride,0);
        Assert.True(expected.Bgra32.AsSpan().SequenceEqual(pixels));
        Assert.Equal(expected.Size.Width,bitmap.PixelWidth);Assert.Equal(expected.Size.Height,bitmap.PixelHeight);
        Assert.Equal(96,bitmap.DpiX);Assert.Equal(96,bitmap.DpiY);Assert.True(bitmap.IsFrozen);
        fixture.AssertUnshown();
    });

    [Fact]
    public Task Measure_initial_load_and_four_committed_trims()=>desktop.RunAsync(async()=>
    {
        await using var fixture=await Fixture.CreateAsync("audio.trim");
        var measurements=new List<object>();
        await Measure("initial",fixture.Load);
        var rows=fixture.Range.Buckets;
        for(int trim=1;trim<=4;trim++)
        {
            fixture.Edit(trim);await Measure("trim-"+trim,fixture.Refresh);
            Assert.True(rows.SequenceEqual(fixture.Range.Buckets));
        }
        fixture.AssertUnshown();
        string? output=Environment.GetEnvironmentVariable("CITRUS_WAVEFORM_EVIDENCE");
        if(output is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{fixture="8 second PCM16 stereo WAV, FL 440 Hz, FR 733 Hz, 48000 Hz, 1024 display buckets",unshown=true,measurements},new JsonSerializerOptions{WriteIndented=true}));
        }
        async Task Measure(string phase,Func<Task> action)
        {
            int stages=fixture.Snapshots.StageCalls;long bytes=fixture.Snapshots.StageBytes,verified=fixture.VerificationBytes,allocated=GC.GetTotalAllocatedBytes(true),start=Stopwatch.GetTimestamp();
            await action();
            measurements.Add(new{phase,milliseconds=Stopwatch.GetElapsedTime(start).TotalMilliseconds,managedAllocatedBytes=GC.GetTotalAllocatedBytes(true)-allocated,sourceStageCalls=fixture.Snapshots.StageCalls-stages,sourceStageBytes=fixture.Snapshots.StageBytes-bytes,sessionVerificationBytes=fixture.VerificationBytes-verified});
        }
    },TimeSpan.FromSeconds(90));

    private static T Field<T>(object value,string name)=>(T)value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(value)!;
    private static void Set(object value,string name,object content)=>value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(value,content);
    private static Task Invoke(object value,string name,params object[] arguments)=>(Task)value.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic,null,arguments.Select(argument=>argument.GetType()).ToArray(),null)!.Invoke(value,arguments)!;

    private sealed class Fixture:IAsyncDisposable
    {
        public string Root{get;}=Path.Combine(Path.GetTempPath(),"citrus-waveform-reuse-"+Guid.NewGuid().ToString("N"));
        public MediaEditorEngine Engine{get;private set;}=null!;
        public MediaEditorWindow Window{get;private set;}=null!;
        public CountingSnapshots Snapshots{get;}=new();
        public MediaProbe Probe=>Field<MediaProbe>(Window,"sessionProbe");
        public EditorDocument Initial=>Field<List<EditorDocument>>(Window,"initialDocuments")[Field<int>(Window,"selected")];
        public MediaRangeView Range=>Field<MediaRangeView>(Window,"range");
        public long VerificationBytes=>Field<MediaProbe?>(Window,"sessionProbe") is {} probe?(long)typeof(MediaProbe).GetProperty("SourceVerificationBytes",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(probe)!:0;
        public static async Task<Fixture> CreateAsync(string id,bool twoSources=false)
        {
            var fixture=new Fixture();Directory.CreateDirectory(fixture.Root);
            string first=Path.Combine(fixture.Root,"first.wav"),second=Path.Combine(fixture.Root,"second.wav");
            WriteStereo(first,440,12000);if(twoSources)WriteStereo(second,660,6000);
            var process=MediaProcess.Bundled();fixture.Engine=new(process,await process.DiscoverCapabilitiesAsync(default),new());
            Set(fixture.Engine,"<Sources>k__BackingField",fixture.Snapshots);
            fixture.Window=new(fixture.Engine){ConfirmDiscard=()=>true};
            var operation=fixture.Engine.Operations.Single(operation=>operation.Id==id);
            Set(fixture.Window,"request",new EditorOpenRequest(operation,twoSources?[first,second]:[first],operation.Label,new(),fixture.Root,SubmissionOrigin.Home));
            return fixture;
        }
        public Task Load()=>Invoke(Window,"LoadAsync");
        public Task Refresh(){Field<System.Windows.Threading.DispatcherTimer>(Window,"previewDelay").Stop();return Invoke(Window,"RefreshPreviewAsync");}
        public Task Switch(int index)=>Invoke(Window,"ShowSourceAsync",index);
        public void CancelRefresh()=>Field<CancellationTokenSource>(Window,"previewWork").Cancel();
        public void SetRecipe(AudioRecipe recipe)=>typeof(MediaEditorWindow).GetMethod("SetRecipe",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Window,[recipe,"test-edit"]);
        public void Edit(int trim=1)
        {
            var audio=(AudioRecipe)Window.CurrentDocument!.Recipe;
            if(audio.Bleeps.IsEmpty&&Field<EditorOpenRequest>(Window,"request").Operation.Id=="audio.bleep")
                SetRecipe(audio with {Bleeps=[new(Guid.NewGuid(),new(new(1,1),new(2,1)),BleepMode.Tone,1000,-12,5,[0,1])]});
            else SetRecipe(audio with {Clips=audio.Clips.Select(clip=>clip with {Range=new(new(trim,8),new(15,2))}).ToImmutableArray()});
        }
        public void AssertUnshown(){Assert.False(Window.IsVisible);Assert.Null(PresentationSource.FromVisual(Window));}
        public async ValueTask DisposeAsync()
        {
            Field<System.Windows.Threading.DispatcherTimer>(Window,"previewDelay").Stop();
            Field<CancellationTokenSource>(Window,"lifetime").Cancel();
            Field<CancellationTokenSource?>(Window,"previewWork")?.Cancel();
            Window.Close();
            if(Field<MediaPreviewController?>(Window,"playback") is {} playback)await playback.DisposeAsync();
            if(Field<MediaProbe?>(Window,"sessionProbe") is {} probe)await probe.DisposeAsync();
            Field<CancellationTokenSource>(Window,"lifetime").Dispose();
            string preview=Field<string>(Window,"previewRoot");if(Directory.Exists(preview))Directory.Delete(preview,true);
            Directory.Delete(Root,true);
        }
        private static void WriteStereo(string path,int frequency,int amplitude)
        {
            const int frames=8*48000;using var writer=new BinaryWriter(File.Create(path));
            writer.Write("RIFF"u8);writer.Write(36+frames*4);writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)1);writer.Write((short)2);writer.Write(48000);writer.Write(192000);writer.Write((short)4);writer.Write((short)16);writer.Write("data"u8);writer.Write(frames*4);
            for(int frame=0;frame<frames;frame++){writer.Write((short)(amplitude*Math.Sin(frame*2*Math.PI*frequency/48000)));writer.Write((short)(3500*Math.Sin(frame*2*Math.PI*733/48000)));}
        }
    }
    private sealed class CountingSnapshots:ISourceSnapshotService
    {
        private readonly SourceSnapshotService inner=new();
        public int StageCalls;public long StageBytes;public bool BlockNext,FailBlocked;
        public TaskCompletionSource Entered{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ImmutableArray<SourceCaptureResult>> CaptureAsync(ImmutableArray<string> paths,CancellationToken ct)=>inner.CaptureAsync(paths,ct);
        public async Task<ImmutableDictionary<Guid,string>> StageVerifiedAsync(ImmutableArray<SourceSnapshot> sources,string directory,CancellationToken ct)
        {
            Interlocked.Increment(ref StageCalls);Interlocked.Add(ref StageBytes,sources.Sum(source=>source.Length));
            if(BlockNext){BlockNext=false;Entered.TrySetResult();await Release.Task.WaitAsync(ct);if(FailBlocked)throw new IOException("Injected staging failure.");}
            return await inner.StageVerifiedAsync(sources,directory,ct);
        }
    }
}
