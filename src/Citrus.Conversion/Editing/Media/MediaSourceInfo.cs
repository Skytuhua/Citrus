using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using Citrus.Core.Editing;

namespace Citrus.Conversion.Editing.Media;

// Source/stream/frame records are the authoritative Core contracts. Plans are process-local state.
public sealed record MediaGraphPlan
{
    internal MediaGraphPlan(long revision, long generation, RationalTime? frameTime, SourceFrameRef? frameIdentity,
        ImmutableArray<string> arguments, SizeI? videoSize, int? sampleRate, int? channels, RationalTime duration,
        ImmutableArray<VerifiedMediaSource> sources, MediaProbe owner, long? audioSampleFrames = null)
    {
        Revision=revision; Generation=generation; FrameTime=frameTime; FrameIdentity=frameIdentity; Arguments=arguments;
        VideoSize=videoSize; SampleRate=sampleRate; Channels=channels; Duration=duration; Sources=sources; Owner=owner; AudioSampleFrames=audioSampleFrames;
    }
    public long Revision { get; }
    public long Generation { get; }
    public RationalTime? FrameTime { get; }
    public SourceFrameRef? FrameIdentity { get; }
    public ImmutableArray<string> Arguments { get; }
    public SizeI? VideoSize { get; }
    public int? SampleRate { get; }
    public int? Channels { get; }
    public RationalTime Duration { get; }
    internal ImmutableArray<VerifiedMediaSource> Sources { get; }
    internal MediaProbe Owner { get; }
    internal long? AudioSampleFrames { get; }
}
/// <summary>Interleaved signed PCM16 little-endian. SampleFrames counts frames, not channel samples.</summary>
public sealed record AudioPcmInfo(string Path, int SampleRate, int Channels, long SampleFrames);
/// <summary>Sample-frame interval [FirstSample, LastSample), aggregated over all channels.</summary>
public sealed record WaveformBucket(float Minimum, float Maximum, long FirstSample, long LastSample);

internal sealed class VerifiedMediaSource(SourceSnapshot snapshot,string path,Action<long>? verified=null,bool lockForSession=false) : IDisposable
{
    public SourceSnapshot Snapshot { get; }=snapshot;
    public string Path { get; }=path;
    private FileStream? readLock;
    public async Task VerifyAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(readLock is not null)return;
        FileStream? file=new(Path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
        try
        {
            if(file.Length!=Snapshot.Length)throw new IOException("Source content changed. Reload the file.");
            string hash=Convert.ToHexString(await SHA256.HashDataAsync(file,ct).ConfigureAwait(false));
            verified?.Invoke(file.Length);
            if(!hash.Equals(Snapshot.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("Source content changed. Reload the file.");
            // Windows sharing denies writes and deletion for the entire verified session.
            if(lockForSession&&OperatingSystem.IsWindows()){readLock=file;file=null;}
        }
        finally{if(file is not null)await file.DisposeAsync().ConfigureAwait(false);}
    }
    public void Dispose(){readLock?.Dispose();readLock=null;}
}

internal static class MediaTime
{
    public static readonly RationalTime Zero = new(0,1);
    public static RationalTime Make(BigInteger n, BigInteger d)
    {
        if (d <= 0) throw new ArgumentOutOfRangeException(nameof(d));
        var gcd=BigInteger.GreatestCommonDivisor(n,d);
        return new(checked((long)(n/gcd)),checked((int)(d/gcd)));
    }
    public static RationalTime Add(RationalTime a,RationalTime b)=>Make((BigInteger)a.Numerator*b.Denominator+(BigInteger)b.Numerator*a.Denominator,(BigInteger)a.Denominator*b.Denominator);
    public static RationalTime Sub(RationalTime a,RationalTime b)=>Make((BigInteger)a.Numerator*b.Denominator-(BigInteger)b.Numerator*a.Denominator,(BigInteger)a.Denominator*b.Denominator);
    public static RationalTime Mul(RationalTime a,RationalTime b)=>Make((BigInteger)a.Numerator*b.Numerator,(BigInteger)a.Denominator*b.Denominator);
    public static RationalTime Div(RationalTime a,RationalTime b)=>Make((BigInteger)a.Numerator*b.Denominator,(BigInteger)a.Denominator*b.Numerator);
    public static RationalTime Pts(long pts,RationalTime timeBase)=>Make((BigInteger)pts*timeBase.Numerator,timeBase.Denominator);
    public static RationalTime Speed(double speed)
    {
        if (!double.IsFinite(speed)||speed<.25||speed>4||Math.Round(speed,6)!=speed) throw new ArgumentOutOfRangeException(nameof(speed));
        return new(checked((long)((decimal)speed*1000000m)),1000000);
    }
    public static RationalTime ParseRatio(string value,char separator='/')
    {
        var parts=value.Split(separator);
        if(parts.Length!=2) throw new MediaProcessException("Missing rational stream identity.");
        var result=Make(BigInteger.Parse(parts[0],CultureInfo.InvariantCulture),BigInteger.Parse(parts[1],CultureInfo.InvariantCulture));
        if(result.Numerator<=0) throw new MediaProcessException("Invalid stream ratio.");
        return result;
    }
    public static string Expression(RationalTime value)=>$"({value.Numerator.ToString(CultureInfo.InvariantCulture)}/{value.Denominator.ToString(CultureInfo.InvariantCulture)})";
    public static long Ceiling(RationalTime value,int scale)
    {
        BigInteger n=(BigInteger)value.Numerator*scale;
        return checked((long)((n+value.Denominator-1)/value.Denominator));
    }
    public static long CeilingTicks(RationalTime value,RationalTime timeBase)
    {
        BigInteger n=(BigInteger)value.Numerator*timeBase.Denominator,d=(BigInteger)value.Denominator*timeBase.Numerator;
        var q=BigInteger.DivRem(n,d,out var remainder);
        return checked((long)(q+(remainder>0?1:0)));
    }
}
