using System.Diagnostics;
using System.Text;
using Citrus.Core.Editing;
using ImageMagick;

namespace Citrus.Conversion.Editing.Image;

/// <summary>App-private HEVC helper; never falls back to an AV1 file with a HEIC extension.</summary>
public static class HeicCodec
{
    public static string Executable { get; private set; }=Locate();
    private static bool verified;
    private static readonly SemaphoreSlim preparation=new(1,1);
    public static bool Available=>Volatile.Read(ref verified)&&File.Exists(Executable);
    public static void Configure(string executable){Executable=Path.GetFullPath(executable);Volatile.Write(ref verified,false);}
    private static string Locate()
    {
        string direct=Path.Combine(AppContext.BaseDirectory,"image","heic","citrus-heic.exe");if(File.Exists(direct))return direct;
        for(string? dir=AppContext.BaseDirectory;dir is not null;dir=Path.GetDirectoryName(dir)){string path=Path.Combine(dir,"packaging","image","win-x64","heic","citrus-heic.exe");if(File.Exists(path))return path;}
        return direct;
    }
    public static Task VerifyAsync(CancellationToken ct)=>RunPreparedAsync(async()=>
    {
        if(Available)return;
        string scope=Path.Combine(Path.GetTempPath(),"citrus-heic-probe-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scope);
        try
        {
            using var image=new MagickImage(MagickColors.Orange,9,7);string path=Path.Combine(scope,"probe.heic");
            await WriteAsync(image,path,85,ct).ConfigureAwait(false);using var decoded=Load(path,ct);
            if(decoded.Width!=9||decoded.Height!=7)throw new InvalidDataException("HEIC encoder/decoder capability did not preserve dimensions.");
            if(!Encoding.ASCII.GetString(await File.ReadAllBytesAsync(path,ct)).Contains("hvcC",StringComparison.Ordinal))throw new InvalidDataException("HEIC output is not genuine HEVC.");
        }
        finally{Directory.Delete(scope,true);}
        Volatile.Write(ref verified,true);
    },ct);
    internal static async Task RunPreparedAsync(Func<Task> prepare,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prepare);await preparation.WaitAsync(ct).ConfigureAwait(false);
        try{await prepare().ConfigureAwait(false);}finally{preparation.Release();}
    }
    internal static MagickImage Load(string path,CancellationToken ct)
    {
        string scope=Path.Combine(Path.GetTempPath(),"citrus-heic-read-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scope);
        try
        {
            string decoded=Path.Combine(scope,"pixels.rgba");RunAsync(["decode",Path.GetFullPath(path),decoded],ct).GetAwaiter().GetResult();
            using var input=File.OpenRead(decoded);using var reader=new BinaryReader(input);
            int width=reader.ReadInt32(),height=reader.ReadInt32(),profileLength=reader.ReadInt32();ImageGeometryMapper.CheckSize(new(width,height));
            if(profileLength is <0 or >4*1024*1024||input.Length!=12L+profileLength+(long)width*height*4)throw new InvalidDataException("HEIC decode returned invalid pixel lengths.");
            byte[] profile=reader.ReadBytes(profileLength),pixels=reader.ReadBytes(checked(width*height*4));
            var image=new MagickImage(pixels,new PixelReadSettings((uint)width,(uint)height,StorageType.Char,PixelMapping.RGBA));
            try{if(profile.Length>0)image.SetProfile(new ColorProfile(profile));else image.SetProfile(ColorProfiles.SRGB);image.TransformColorSpace(ColorProfiles.SRGB);image.Orientation=Orientation.TopLeft;ct.ThrowIfCancellationRequested();return image;}catch{image.Dispose();throw;}
        }
        finally{Directory.Delete(scope,true);}
    }
    internal static async Task WriteAsync(MagickImage image,string output,int quality,CancellationToken ct)
    {
        ImageGeometryMapper.CheckSize(new((int)image.Width,(int)image.Height));
        string scope=Path.Combine(Path.GetTempPath(),"citrus-heic-write-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scope);
        try
        {
            string raw=Path.Combine(scope,"pixels.rgba");await File.WriteAllBytesAsync(raw,image.GetPixels().ToByteArray(PixelMapping.RGBA)!,ct).ConfigureAwait(false);
            await RunAsync(["encode",raw,image.Width.ToString(System.Globalization.CultureInfo.InvariantCulture),image.Height.ToString(System.Globalization.CultureInfo.InvariantCulture),quality.ToString(System.Globalization.CultureInfo.InvariantCulture),Path.GetFullPath(output)],ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch{if(File.Exists(output))File.Delete(output);throw;}
        finally{Directory.Delete(scope,true);}
    }
    private static async Task RunAsync(string[] arguments,CancellationToken ct)
    {
        if(!File.Exists(Executable))throw new NotSupportedException("HEIC tools are missing. Reinstall Citrus.");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(120));
        var start=new ProcessStartInfo(Executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(Executable)!};
        foreach(string argument in arguments)start.ArgumentList.Add(argument);
        using var process=Process.Start(start)??throw new IOException("Could not launch the HEIC codec.");
        var stdout=process.StandardOutput.ReadToEndAsync(timeout.Token);var stderr=process.StandardError.ReadToEndAsync(timeout.Token);
        try{await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);_=await stdout.ConfigureAwait(false);string error=await stderr.ConfigureAwait(false);if(process.ExitCode!=0)throw new InvalidDataException("HEIC codec: "+error[..Math.Min(error.Length,2048)]);}
        catch{if(!process.HasExited)process.Kill(true);await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);throw;}
    }
}
