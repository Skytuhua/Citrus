using System.Collections.Immutable;
using Citrus.Core.Editing;

namespace Citrus.Conversion.Editing.Media;

public sealed class MediaBinaryDecoder(MediaProcess process)
{
    // Preview PCM is capped; export streams its declared sample count to a private file.
    public const long MaximumPcmBytes=512L*1024*1024;
    public Task<PreviewFrame> DecodeFrameAsync(MediaGraphPlan plan,CancellationToken ct)=>DecodeFrameCoreAsync(plan,2048,ct);
    public Task<PreviewFrame> DecodeSnapshotAsync(MediaGraphPlan plan,CancellationToken ct)=>DecodeFrameCoreAsync(plan,16384,ct);
    private async Task<PreviewFrame> DecodeFrameCoreAsync(MediaGraphPlan plan,int maximum,CancellationToken ct)
    {
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(ct,plan.Owner.LifetimeToken);
        ct=cancel.Token;
        using var lease=await plan.Owner.AcquireDecodeAsync(ct).ConfigureAwait(false);
        // Exactly one shared preview slot covers hashing, decoding and result materialization.
        using var previewLease=await PreviewWorkScheduler.AcquireAsync(ct).ConfigureAwait(false);
        if(plan.VideoSize is not {} size||size.Width<1||size.Width>maximum||size.Height<1||size.Height>maximum||(long)size.Width*size.Height>64_000_000||plan.FrameIdentity is null||plan.FrameTime is null)
            throw new ArgumentException("A bounded exact video frame plan is required.");
        int bytes=checked(checked(size.Width*size.Height)*4);
        await VerifyAsync(plan,ct).ConfigureAwait(false);
        using var output=new MemoryStream(bytes);
        long actual=await process.RunBinaryAsync(plan.Arguments,output,bytes,ct).ConfigureAwait(false);
        if(actual!=bytes) throw new MediaProcessException("Unexpected EOF in raw BGRA frame.");
        await VerifyAsync(plan,ct).ConfigureAwait(false);
        return new(plan.Revision,plan.Generation,size,checked(size.Width*4),ImmutableArray.Create(output.ToArray()),plan.FrameTime,plan.FrameIdentity,[]);
    }
    public Task<AudioPcmInfo> DecodeAudioAsync(MediaGraphPlan plan,string pcmPath,CancellationToken ct)=>DecodeAudioCoreAsync(plan,pcmPath,true,ct);
    internal Task<AudioPcmInfo> DecodeExportAudioAsync(MediaGraphPlan plan,string pcmPath,CancellationToken ct)=>DecodeAudioCoreAsync(plan,pcmPath,false,ct);
    private async Task<AudioPcmInfo> DecodeAudioCoreAsync(MediaGraphPlan plan,string pcmPath,bool preview,CancellationToken ct)
    {
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(ct,plan.Owner.LifetimeToken);
        ct=cancel.Token;
        using var lease=await plan.Owner.AcquireDecodeAsync(ct).ConfigureAwait(false);
        using var previewLease=preview?await PreviewWorkScheduler.AcquireAsync(ct).ConfigureAwait(false):null;
        if(plan.SampleRate is not {} rate||rate is <8000 or >192000||plan.Channels is not {} channels||channels is <1 or >64||plan.VideoSize is not null)
            throw new ArgumentException("A PCM audio plan is required.");
        if(!Path.IsPathFullyQualified(pcmPath)||plan.Sources.Any(s=>EditorPathPolicy.Same(s.Path,pcmPath)||EditorPathPolicy.Same(s.Snapshot.Path,pcmPath))) throw new ArgumentException("PCM must have a fresh private output path.");
        SafeEditorFiles.RejectLinks(Path.GetDirectoryName(pcmPath)!);
        long expectedFrames=plan.AudioSampleFrames??throw new ArgumentException("Missing compiled PCM sample count.");
        long maximum=checked(expectedFrames*channels*2);
        if(maximum<=0||preview&&maximum>MaximumPcmBytes)throw new NotSupportedException("PCM preview exceeds the 512 MiB cache limit; prepare a shorter audition range.");
        await VerifyAsync(plan,ct).ConfigureAwait(false);
        bool created=false;
        try
        {
            long bytes;
            await using(var output=new FileStream(pcmPath,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous|FileOptions.SequentialScan))
            {
                created=true;
                bytes=await process.RunBinaryAsync(plan.Arguments,output,maximum,ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
            if(bytes!=maximum||bytes%(channels*2)!=0) throw new MediaProcessException("Unexpected EOF in interleaved PCM16 audio.");
            await VerifyAsync(plan,ct).ConfigureAwait(false);
            return new(pcmPath,rate,channels,bytes/(channels*2));
        }
        catch { if(created) File.Delete(pcmPath); throw; }
    }
    private static async Task VerifyAsync(MediaGraphPlan plan,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(plan.Sources.IsDefaultOrEmpty) throw new ArgumentException("Media plans require verified source bindings.");
        foreach(var source in plan.Sources) await source.VerifyAsync(ct).ConfigureAwait(false);
    }
}
