using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Citrus.Core.Editing;

namespace Citrus.Conversion.Editing.Media;

/// <summary>Session-owned verified source copies and bounded frame windows. Dispose cancels and joins inspection before removing copies.</summary>
public sealed class MediaProbe : IAsyncDisposable
{
    private readonly MediaProcess process;
    private readonly ISourceSnapshotService snapshots;
    private readonly string directory;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim gate = new(1);
    private readonly object sync = new();
    private readonly Dictionary<Guid,Registration> sources = [];
    private readonly List<Window> windows = [];
    private bool disposed;
    private bool ownsDirectory;
    private long timestampScans;
    internal long TimestampScanCount => Interlocked.Read(ref timestampScans);
    private long sourceVerificationBytes;
    internal long SourceVerificationBytes=>Interlocked.Read(ref sourceVerificationBytes);
    private void SourceVerified(long bytes)=>Interlocked.Add(ref sourceVerificationBytes,bytes);
    private sealed record Registration(VerifiedMediaSource Binding,MediaSourceInfo Info);
    private sealed record Window(Guid SourceId,int Stream,TimeRange Coverage,ImmutableArray<FrameStamp> Frames);
    private sealed class ScanState(JsonElement metadata)
    {
        public JsonElement Metadata { get; }=metadata;
        public int Index { get; }=metadata.GetProperty("index").GetInt32();
        public RationalTime TimeBase { get; }=MediaTime.ParseRatio(metadata.GetProperty("time_base").GetString()!);
        public bool Video { get; }=Text(metadata,"codec_type")=="video";
        public long? First,Last;
        public RationalTime End=MediaTime.Zero;
        public readonly List<long> Initial=[];
        public long? Next;
    }
    public MediaProbe(MediaProcess process,ISourceSnapshotService snapshots,string privateDirectory)
    {
        this.process=process; this.snapshots=snapshots;
        if(!Path.IsPathFullyQualified(privateDirectory)||Directory.Exists(privateDirectory)||File.Exists(privateDirectory)) throw new ArgumentException("Probe requires a fresh absolute private directory.");
        directory=privateDirectory;
    }
    public ImmutableDictionary<Guid,string> StagedSources { get { lock(sync) { ThrowIfDisposed(); return sources.ToImmutableDictionary(p=>p.Key,p=>p.Value.Binding.Path); } } }
    internal MediaSourceInfo GetInfo(Guid id) { lock(sync) { ThrowIfDisposed(); return Find(id).Info; } }
    internal VerifiedMediaSource Bind(SourceSnapshot snapshot,string path)
    {
        lock(sync)
        {
            ThrowIfDisposed(); var registration=Find(snapshot.Id);
            if(registration.Binding.Snapshot!=snapshot||!Path.IsPathFullyQualified(path)||!EditorPathPolicy.Same(registration.Binding.Path,path)) throw new ArgumentException("Source identity differs from inspection.");
            SafeEditorFiles.RejectLinks(path);
            return registration.Binding;
        }
    }
    internal FrameStamp SelectFrame(Guid id,int stream,TimeRange clip,RationalTime requested)
    {
        lock(sync)
        {
            ThrowIfDisposed(); _=Find(id);
            foreach(var window in windows.AsEnumerable().Reverse().Where(w=>w.SourceId==id&&w.Stream==stream&&w.Coverage.Start.CompareTo(requested)<=0&&w.Coverage.End.CompareTo(requested)>0))
            {
                var included=window.Frames.Where(f=>f.SourceOffset.CompareTo(clip.Start)>=0&&f.SourceOffset.CompareTo(clip.End)<0).ToArray();
                var before=included.LastOrDefault(f=>f.SourceOffset.CompareTo(requested)<=0);
                if(before is not null) return before;
                if(window.Coverage.Start.CompareTo(clip.Start)<=0&&included.Length>0) return included[0];
            }
            throw new InvalidOperationException("Prepare a frame window covering this source time and its preceding included frame with ReadFrameTimesAsync before compiling.");
        }
    }
    public async Task<MediaSourceInfo> InspectAsync(SourceSnapshot source,CancellationToken ct)
    {
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token);
        await gate.WaitAsync(cancel.Token).ConfigureAwait(false);
        string? owned=null;
        VerifiedMediaSource? pendingBinding=null;
        try
        {
            lock(sync) ThrowIfDisposed();
            // Check captured identity even on cache hits: a changed original requires explicit Reload.
            await new VerifiedMediaSource(source,source.Path,SourceVerified).VerifyAsync(cancel.Token).ConfigureAwait(false);
            lock(sync) if(sources.TryGetValue(source.Id,out var found))
            {
                if(found.Binding.Snapshot!=source) throw new ArgumentException("Source ID is already registered with another snapshot.");
                return found.Info;
            }
            if(sources.Count>=32) throw new NotSupportedException("At most 32 sources per media probe session.");
            if(!ownsDirectory) { SafeEditorFiles.RejectLinks(directory); Directory.CreateDirectory(directory); ownsDirectory=true; }
            owned=Path.Combine(directory,Guid.NewGuid().ToString("N"));
            var staged=await snapshots.StageVerifiedAsync([source],owned,cancel.Token).ConfigureAwait(false);
            var binding=pendingBinding=new VerifiedMediaSource(source,staged[source.Id],SourceVerified,lockForSession:true);
            await binding.VerifyAsync(cancel.Token).ConfigureAwait(false);
            using var metadata=await process.ProbeJsonAsync(["-show_streams","-of","json",..MediaProcess.InputArguments(binding.Path)],2*1024*1024,cancel.Token).ConfigureAwait(false);
            var streams=metadata.RootElement.GetProperty("streams").EnumerateArray()
                .Where(s=>Text(s,"codec_type") is "video" or "audio")
                .Where(s=>!s.TryGetProperty("disposition",out var disposition)||!disposition.TryGetProperty("attached_pic",out var attached)||attached.GetInt32()==0)
                .Select(s=>new ScanState(s)).ToDictionary(s=>s.Index);
            if(streams.Count is <1 or >16) throw new NotSupportedException("Source requires 1..16 usable audio/video streams.");
            foreach(var stream in streams.Values.Where(s=>s.Video)) ValidateSize(stream.Metadata);
            await ScanAsync(binding.Path,fields=>
            {
                int index=Int(fields,"stream_index");
                if(!streams.TryGetValue(index,out var state)) return;
                long pts=Long(fields,"pts");
                if(state.Last is {} last&&pts<=last) throw new MediaProcessException("Ambiguous or nonmonotonic presentation timestamps.");
                state.First??=pts; state.Last=pts;
                var start=MediaTime.Pts(pts,state.TimeBase);
                RationalTime duration;
                if(!state.Video&&fields.TryGetValue("nb_samples",out var samples)) duration=new(long.Parse(samples,CultureInfo.InvariantCulture),int.Parse(Text(state.Metadata,"sample_rate"),CultureInfo.InvariantCulture));
                else if(TryLong(fields,"duration",out long ticks)&&ticks>0||TryLong(fields,"pkt_duration",out ticks)&&ticks>0) duration=MediaTime.Pts(ticks,state.TimeBase);
                else throw new MediaProcessException("Exact frame duration is unavailable.");
                state.End=MediaTime.Add(start,duration);
                if(state.Video) { if(state.Initial.Count<4096) state.Initial.Add(pts); else state.Next??=pts; }
            },cancel.Token).ConfigureAwait(false);
            if(streams.Values.Any(s=>s.First is null)) throw new MediaProcessException("Stream has no usable presentation timestamps.");
            var origin=streams.Values.Select(s=>MediaTime.Pts(s.First!.Value,s.TimeBase)).Min();
            var duration=MediaTime.Sub(streams.Values.Select(s=>s.End).Max(),origin);
            if(duration.CompareTo(MediaTime.Zero)<=0) throw new MediaProcessException("Source has no usable duration.");
            var videos=ImmutableArray.CreateBuilder<VideoStreamInfo>(); var audios=ImmutableArray.CreateBuilder<AudioStreamInfo>();
            foreach(var state in streams.Values)
            {
                var m=state.Metadata;
                if(state.Video)
                {
                    var coded=ValidateSize(m); var aspect=Text(m,"sample_aspect_ratio");
                    var sar=aspect is "" or "N/A" or "0:1"?new RationalTime(1,1):MediaTime.ParseRatio(aspect,':');
                    int displayWidth=checked((int)MediaTime.Ceiling(MediaTime.Mul(new(coded.Width,1),sar),1));
                    double rotation=0;
                    if(m.TryGetProperty("side_data_list",out var side)) foreach(var item in side.EnumerateArray()) if(item.TryGetProperty("rotation",out var r)) rotation=r.GetDouble();
                    if(!double.IsFinite(rotation)||rotation%90!=0) throw new NotSupportedException("Only quarter-turn display rotation is supported.");
                    rotation=((rotation%360)+360)%360;
                    var display=rotation is 90 or 270?new SizeI(coded.Height,displayWidth):new SizeI(displayWidth,coded.Height);
                    ValidateSize(display);
                    var frames=state.Initial.Select(p=>new FrameStamp(p,MediaTime.Sub(MediaTime.Pts(p,state.TimeBase),origin))).ToImmutableArray();
                    videos.Add(new(state.Index,state.TimeBase,state.First!.Value,frames,coded,display,rotation,sar,Text(m,"color_transfer") is "smpte2084" or "arib-std-b67")
                    { LastFrame = new(state.Last!.Value, MediaTime.Sub(MediaTime.Pts(state.Last.Value, state.TimeBase), origin)), PresentationEnd = MediaTime.Sub(state.End, origin) });
                }
                else
                {
                    int rate=int.Parse(Text(m,"sample_rate"),CultureInfo.InvariantCulture),channels=m.GetProperty("channels").GetInt32();
                    if(rate is <8000 or >192000) throw new NotSupportedException("Unsupported audio sample rate.");
                    audios.Add(new(state.Index,state.TimeBase,state.First!.Value,rate,ChannelNames(Text(m,"channel_layout"),channels),MediaTime.Sub(state.End,MediaTime.Pts(state.First.Value,state.TimeBase))));
                }
            }
            var info=new MediaSourceInfo(source.Id,origin,videos.ToImmutable(),audios.ToImmutable(),duration);
            await binding.VerifyAsync(cancel.Token).ConfigureAwait(false);
            lock(sync)
            {
                ThrowIfDisposed(); sources.Add(source.Id,new(binding,info));
                foreach(var video in info.Video)
                {
                    var next=streams[video.Index].Next;
                    AddWindow(new(source.Id,video.Index,new(MediaTime.Zero,next is {} p?MediaTime.Sub(MediaTime.Pts(p,video.TimeBase),origin):duration),video.Frames));
                }
            }
            pendingBinding=null;owned=null;return info;
        }
        finally {try{pendingBinding?.Dispose();if(owned is not null&&Directory.Exists(owned))Directory.Delete(owned,true);}finally{gate.Release();}}
    }
    public async Task<ImmutableArray<FrameStamp>> ReadFrameTimesAsync(Guid sourceId,int streamIndex,TimeRange sourceRange,int maximumFrames,CancellationToken ct)
    {
        if(maximumFrames is <1 or >4096) throw new ArgumentOutOfRangeException(nameof(maximumFrames));
        if(sourceRange.Start.CompareTo(MediaTime.Zero)<0||sourceRange.End.CompareTo(sourceRange.Start)<=0) throw new ArgumentException("Invalid normalized source range.");
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token);
        await gate.WaitAsync(cancel.Token).ConfigureAwait(false);
        try
        {
            Registration registration; VideoStreamInfo video;
            lock(sync) { ThrowIfDisposed(); registration=Find(sourceId); video=registration.Info.Video.FirstOrDefault(v=>v.Index==streamIndex)??throw new ArgumentException("Unknown video stream."); }
            await registration.Binding.VerifyAsync(cancel.Token).ConfigureAwait(false);
            var end=sourceRange.End.CompareTo(registration.Info.Duration)>0?registration.Info.Duration:sourceRange.End;
            if(sourceRange.Start.CompareTo(end)>=0) return [];
            lock(sync)
            {
                var cached=windows.LastOrDefault(w=>w.SourceId==sourceId&&w.Stream==streamIndex&&w.Coverage.Start.CompareTo(sourceRange.Start)<=0&&w.Coverage.End.CompareTo(end)>=0);
                if(cached is not null)return cached.Frames.Where(f=>f.SourceOffset.CompareTo(sourceRange.Start)>=0&&f.SourceOffset.CompareTo(end)<0).Take(maximumFrames).ToImmutableArray();
            }
            var frames=ImmutableArray.CreateBuilder<FrameStamp>(); RationalTime? next=null; long? previous=null;
            await ScanAsync(registration.Binding.Path,fields=>
            {
                if(Int(fields,"stream_index")!=streamIndex) return;
                long pts=Long(fields,"pts");
                if(previous is {} p&&pts<=p) throw new MediaProcessException("Ambiguous presentation timestamps.");
                previous=pts;
                var offset=MediaTime.Sub(MediaTime.Pts(pts,video.TimeBase),registration.Info.PresentationOrigin);
                if(offset.CompareTo(sourceRange.Start)<0||offset.CompareTo(end)>=0) return;
                if(frames.Count<maximumFrames) frames.Add(new(pts,offset)); else next??=offset;
            },cancel.Token).ConfigureAwait(false);
            await registration.Binding.VerifyAsync(cancel.Token).ConfigureAwait(false);
            var result=frames.ToImmutable();
            lock(sync) { ThrowIfDisposed(); AddWindow(new(sourceId,streamIndex,new(sourceRange.Start,next??end),result)); }
            return result;
        }
        finally { gate.Release(); }
    }
    /// <summary>Prepare a bounded window of exact neighboring stamps for paused seeking and frame stepping.
    /// A covered verified frame window needs neither a second timestamp scan nor PCM preparation.</summary>
    public async Task<ImmutableArray<FrameStamp>> PrepareFrameAtAsync(Guid sourceId,int streamIndex,TimeRange clip,RationalTime requested,CancellationToken ct)
    {
        if(clip.Start.CompareTo(MediaTime.Zero)<0||clip.End.CompareTo(clip.Start)<=0||requested.CompareTo(clip.Start)<0||requested.CompareTo(clip.End)>=0)
            throw new ArgumentOutOfRangeException(nameof(requested));
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token);
        await gate.WaitAsync(cancel.Token).ConfigureAwait(false);
        try
        {
            Registration registration;VideoStreamInfo video;
            lock(sync){ThrowIfDisposed();registration=Find(sourceId);video=registration.Info.Video.SingleOrDefault(v=>v.Index==streamIndex)??throw new ArgumentException("Unknown video stream.");}
            await registration.Binding.VerifyAsync(cancel.Token).ConfigureAwait(false);
            if(clip.End.CompareTo(registration.Info.Duration)>0)throw new ArgumentException("Clip exceeds source duration.");
            lock(sync)
            {
                foreach(var cached in windows.AsEnumerable().Reverse().Where(w=>w.SourceId==sourceId&&w.Stream==streamIndex&&w.Coverage.Start.CompareTo(requested)<=0&&w.Coverage.End.CompareTo(requested)>0))
                {
                    var frames=cached.Frames.Where(f=>f.SourceOffset.CompareTo(clip.Start)>=0&&f.SourceOffset.CompareTo(clip.End)<0).ToImmutableArray();
                    bool hasCurrent=frames.Any(f=>f.SourceOffset.CompareTo(requested)<=0)||cached.Coverage.Start.CompareTo(clip.Start)<=0&&!frames.IsEmpty;
                    var current=frames.LastOrDefault(f=>f.SourceOffset.CompareTo(requested)<=0)??frames.FirstOrDefault();
                    // Both omitted neighbors matter: publishing a boundary stamp without
                    // its predecessor or successor can strand repeated frame stepping.
                    bool hasPredecessor=current is not null&&(frames.Any(f=>f.SourceOffset.CompareTo(current.SourceOffset)<0)||cached.Coverage.Start.CompareTo(clip.Start)<=0);
                    bool hasSuccessor=frames.Any(f=>f.SourceOffset.CompareTo(requested)>0)||cached.Coverage.End.CompareTo(clip.End)>=0;
                    if(hasCurrent&&hasPredecessor&&hasSuccessor)return frames;
                }
            }
            FrameStamp? following=null;long? last=null;bool omittedPredecessor=false;
            // Amortize repeated steps in either direction without retaining an unbounded timeline.
            const int neighborLimit=256;
            var predecessors=new Queue<FrameStamp>();
            var successors=ImmutableArray.CreateBuilder<FrameStamp>();
            await ScanAsync(registration.Binding.Path,fields=>
            {
                if(Int(fields,"stream_index")!=streamIndex)return;
                long pts=Long(fields,"pts");
                if(last is {} p&&pts<=p)throw new MediaProcessException("Ambiguous presentation timestamps.");
                last=pts;
                var offset=MediaTime.Sub(MediaTime.Pts(pts,video.TimeBase),registration.Info.PresentationOrigin);
                if(offset.CompareTo(clip.Start)<0||offset.CompareTo(clip.End)>=0)return;
                var stamp=new FrameStamp(pts,offset);
                if(offset.CompareTo(requested)<=0)
                {
                    predecessors.Enqueue(stamp);
                    if(predecessors.Count>neighborLimit+1){predecessors.Dequeue();omittedPredecessor=true;}
                }
                else if(successors.Count<neighborLimit)successors.Add(stamp);
                else following??=stamp;
            },cancel.Token).ConfigureAwait(false);
            await registration.Binding.VerifyAsync(cancel.Token).ConfigureAwait(false);
            var selected=predecessors.Concat(successors).ToImmutableArray();
            if(selected.IsEmpty)throw new NotSupportedException("The selected interval has no actual video frame.");
            var coverage=new TimeRange(omittedPredecessor?selected[0].SourceOffset:clip.Start,following?.SourceOffset??clip.End);
            lock(sync){ThrowIfDisposed();AddWindow(new(sourceId,streamIndex,coverage,selected));}
            return selected;
        }
        finally{gate.Release();}
    }
    /// <summary>Find the actual neighboring displayed frame, returning null at a clip edge.</summary>
    public async Task<FrameStamp?> GetAdjacentFrameAsync(Guid sourceId,int streamIndex,TimeRange clip,RationalTime requested,int direction,CancellationToken ct)
    {
        if(direction is not (-1 or 1))throw new ArgumentOutOfRangeException(nameof(direction));
        var frames=await PrepareFrameAtAsync(sourceId,streamIndex,clip,requested,ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var current=frames.LastOrDefault(f=>f.SourceOffset.CompareTo(requested)<=0)??frames[0];
        return direction<0?frames.LastOrDefault(f=>f.SourceOffset.CompareTo(current.SourceOffset)<0):frames.FirstOrDefault(f=>f.SourceOffset.CompareTo(current.SourceOffset)>0);
    }
    private Task ScanAsync(string path,Action<Dictionary<string,string>> consume,CancellationToken ct)
    {
        Interlocked.Increment(ref timestampScans);
        return process.ProbeLinesAsync(
        ["-show_frames","-show_entries","frame=stream_index,pts,duration,pkt_duration,nb_samples","-of","compact=p=0:nk=0",..MediaProcess.InputArguments(path)],line=>
        {
            if(string.IsNullOrWhiteSpace(line)) return;
            var fields=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var part in line.Split('|')) { int equals=part.IndexOf('='); if(equals>0) fields[part[..equals]]=part[(equals+1)..]; }
            if(fields.ContainsKey("stream_index")) consume(fields);
        },ct);
    }
    private Registration Find(Guid id)=>sources.TryGetValue(id,out var source)?source:throw new ArgumentException("Unknown captured media source.");
    private void AddWindow(Window window) { windows.Add(window); while(windows.Count>64) windows.RemoveAt(0); }
    private void ThrowIfDisposed()=>ObjectDisposedException.ThrowIf(disposed,this);
    private static string Text(JsonElement item,string key)=>item.TryGetProperty(key,out var value)?value.ToString():"";
    private static long Long(Dictionary<string,string> fields,string key)=>TryLong(fields,key,out var value)?value:throw new MediaProcessException("Missing exact frame timestamp field: "+key);
    private static int Int(Dictionary<string,string> fields,string key)=>checked((int)Long(fields,key));
    private static bool TryLong(Dictionary<string,string> fields,string key,out long value) { value=0; return fields.TryGetValue(key,out var text)&&long.TryParse(text,NumberStyles.Integer,CultureInfo.InvariantCulture,out value); }
    private static SizeI ValidateSize(JsonElement metadata) { var size=new SizeI(metadata.GetProperty("width").GetInt32(),metadata.GetProperty("height").GetInt32()); ValidateSize(size); return size; }
    internal static void ValidateSize(SizeI size) { if(size.Width<=0||size.Height<=0||(long)size.Width*size.Height>64_000_000) throw new NotSupportedException("Declared media dimensions exceed the 64 megapixel limit."); }
    private static ImmutableArray<string> ChannelNames(string layout,int count)=> (layout,count) switch
    {
        (_,1)=>["FC"],(_,2)=>["FL","FR"],("2.1",3)=>["FL","FR","LFE"],("3.0",3)=>["FL","FR","FC"],("quad",4)=>["FL","FR","BL","BR"],
        ("5.1",6)=>["FL","FR","FC","LFE","BL","BR"],("5.1(side)",6)=>["FL","FR","FC","LFE","SL","SR"],("7.1",8)=>["FL","FR","FC","LFE","BL","BR","SL","SR"],
        _=>throw new NotSupportedException("Audio channel layout is unavailable or unsupported.")
    };
    internal CancellationToken LifetimeToken=>lifetime.Token;
    internal async Task<IDisposable> AcquireDecodeAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { lock(sync) ThrowIfDisposed(); return new DecodeLease(gate); }
        catch { gate.Release(); throw; }
    }
    private sealed class DecodeLease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? owned=semaphore;
        public void Dispose()=>Interlocked.Exchange(ref owned,null)?.Release();
    }
    public async ValueTask DisposeAsync()
    {
        lock(sync) { if(disposed) return; disposed=true; }
        await lifetime.CancelAsync().ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try { lock(sync) {foreach(var source in sources.Values)source.Binding.Dispose();sources.Clear();windows.Clear();}if(ownsDirectory&&Directory.Exists(directory))Directory.Delete(directory,true);}
        finally { gate.Release(); }
    }
}
