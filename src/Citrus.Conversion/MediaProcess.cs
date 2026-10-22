using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Citrus.Core;

namespace Citrus.Conversion;

public sealed class MediaProcessException(string message) : IOException(message);
public sealed record MediaInfo(double DurationSeconds, bool HasVideo, bool HasAudio, int Width, int Height);
public sealed record MediaCapabilities(IReadOnlySet<string> Encoders, IReadOnlySet<string> Muxers, IReadOnlySet<string> Demuxers, IReadOnlySet<string> Filters, bool MediaFoundationH264);

/// <summary>Owns local FFmpeg children. No PATH search, shell quoting, network inputs or runtime downloads.</summary>
public sealed class MediaProcess
{
    private const int ErrorLimit = 32 * 1024;
    private const int OutputLimit = 2 * 1024 * 1024;
    public string FfmpegPath { get; }
    public string FfprobePath { get; }
    public MediaProcess(string ffmpegPath, string ffprobePath)
    {
        if (!Path.IsPathFullyQualified(ffmpegPath) || !Path.IsPathFullyQualified(ffprobePath))
            throw new ArgumentException("Media executable paths must be absolute.");
        FfmpegPath = ffmpegPath; FfprobePath = ffprobePath;
    }
    public static MediaProcess Bundled() => new(Path.Combine(AppContext.BaseDirectory, "media", "ffmpeg.exe"), Path.Combine(AppContext.BaseDirectory, "media", "ffprobe.exe"));

    // Repeating PCM can outscore the WAV header in FFmpeg's MPEG-TS autodetection.
    // Prefer a recognized WAVE container signature, independent of the filename; the
    // native WAV demuxer still validates the complete header/chunks and sample data.
    internal static string[] InputArguments(string path)
    {
        using var input = File.OpenRead(path);
        Span<byte> header = stackalloc byte[16];
        int count = input.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        bool wave = count >= 12 && header[8..12].SequenceEqual("WAVE"u8) &&
            (header[..4].SequenceEqual("RIFF"u8) || header[..4].SequenceEqual("RIFX"u8) ||
             count >= 16 && header[12..16].SequenceEqual("ds64"u8) &&
             (header[..4].SequenceEqual("RF64"u8) || header[..4].SequenceEqual("BW64"u8)));
        return wave ? ["-f", "wav", "-i", path] : ["-i", path];
    }

    public async Task RunAsync(IReadOnlyList<string> arguments, IProgress<ConversionProgress>? progress, CancellationToken cancellationToken)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-loglevel", "error", "-y", "-progress", "pipe:1", "-protocol_whitelist", "file,pipe" };
        args.AddRange(arguments);
        await InvokeAsync(FfmpegPath, args, progress, cancellationToken).ConfigureAwait(false);
    }

    // Shared by preview/probe jobs; limits native process and pipe memory even across editor sessions.
    private static readonly SemaphoreSlim BinaryWorkers = new(2);

    public async Task<long> RunBinaryAsync(IReadOnlyList<string> arguments, Stream output, long maximumBytes, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        var args = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-loglevel", "error", "-protocol_whitelist", "file,pipe" };
        args.AddRange(arguments);
        long bytes = 0;
        await InvokeStreamAsync(FfmpegPath, args, async stream =>
        {
            byte[] buffer = new byte[64 * 1024]; int count;
            while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                if (count > maximumBytes - bytes) throw new MediaProcessException("Binary media output exceeds its declared limit.");
                await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                bytes += count;
            }
        }, ct).ConfigureAwait(false);
        return bytes;
    }

    /// <summary>Structured output is rejected on overflow, never tail-truncated like diagnostics.</summary>
    public async Task<JsonDocument> ProbeJsonAsync(IReadOnlyList<string> arguments, int maximumBytes, CancellationToken ct)
    {
        if (maximumBytes is < 1 or > OutputLimit) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var output = new MemoryStream();
        await InvokeStreamAsync(FfprobePath, ProbeArguments(arguments), async stream =>
        {
            byte[] buffer = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                if (count > maximumBytes - output.Length) throw new MediaProcessException("Structured probe output exceeds its limit.");
                output.Write(buffer, 0, count);
            }
        }, ct).ConfigureAwait(false);
        return JsonDocument.Parse(output.ToArray());
    }

    // Incremental frame records keep long sources bounded. Only trusted probe fields are requested.
    internal Task ProbeLinesAsync(IReadOnlyList<string> arguments, Action<string> consume, CancellationToken ct) =>
        InvokeStreamAsync(FfprobePath, ProbeArguments(arguments), async stream =>
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            char[] buffer = new char[4096]; var line = new StringBuilder(); int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
                for (int i = 0; i < count; i++)
                {
                    if (buffer[i] == '\n') { consume(line.ToString().TrimEnd('\r')); line.Clear(); }
                    else { if (line.Length == 8192) throw new MediaProcessException("Probe record exceeds its limit."); line.Append(buffer[i]); }
                }
            if (line.Length > 0) consume(line.ToString());
        }, ct);

    private static List<string> ProbeArguments(IReadOnlyList<string> arguments)
    {
        var result = new List<string> { "-v", "error", "-protocol_whitelist", "file,pipe" };
        result.AddRange(arguments); return result;
    }

    private static async Task InvokeStreamAsync(string executable, IReadOnlyList<string> arguments, Func<Stream, Task> consume, CancellationToken ct)
    {
        await BinaryWorkers.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(executable)) throw new FileNotFoundException("Media tools are missing. Reinstall Citrus.", executable);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var child = new Process { StartInfo = start };
            child.Start();
            void Kill() => TerminateChild(child.Kill);
            using var registration = ct.Register(Kill);
            async Task ReadOutput() { try { await consume(child.StandardOutput.BaseStream).ConfigureAwait(false); } catch { Kill(); throw; } }
            async Task<string> ReadError() { try { return await ReadBoundedAsync(child.StandardError, ErrorLimit, null).ConfigureAwait(false); } catch { Kill(); throw; } }
            var stderr = ReadError(); var stdout = ReadOutput();
            try
            {
                await Task.WhenAll(stdout, stderr, child.WaitForExitAsync()).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (child.ExitCode != 0) throw new MediaProcessException($"The media engine failed (exit {child.ExitCode}). {await stderr.ConfigureAwait(false)}");
            }
            catch when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            finally { Kill(); await child.WaitForExitAsync().ConfigureAwait(false); }
        }
        finally { BinaryWorkers.Release(); }
    }

    public async Task<MediaInfo> ProbeAsync(string input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var json = JsonDocument.Parse(await InvokeAsync(FfprobePath,
            ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_entries", "format=duration:stream=codec_type,width,height", "-of", "json", ..InputArguments(Path.GetFullPath(input))], null, cancellationToken).ConfigureAwait(false));
        double duration = 0;
        if (json.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var time))
            double.TryParse(time.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
        bool video = false, audio = false; int width = 0, height = 0;
        foreach (var stream in json.RootElement.GetProperty("streams").EnumerateArray())
        {
            if (stream.GetProperty("codec_type").GetString() == "audio") audio = true;
            if (stream.GetProperty("codec_type").GetString() == "video")
            {
                video = true;
                if (stream.TryGetProperty("width", out var w)) width = w.GetInt32();
                if (stream.TryGetProperty("height", out var h)) height = h.GetInt32();
            }
        }
        return new(duration, video, audio, width, height);
    }

    public async Task<MediaCapabilities> DiscoverCapabilitiesAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await InvokeAsync(FfprobePath, ["-version"], null, token).ConfigureAwait(false);
        async Task<IReadOnlySet<string>> Catalogue(string option)
        {
            string text = await InvokeAsync(FfmpegPath, ["-hide_banner", option], null, token).ConfigureAwait(false);
            return text.Split('\n').Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 2 && parts[0].All(c => c == '.' || char.IsAsciiLetterUpper(c)) && parts[1] != "=")
                .SelectMany(parts => parts[1].Split(',')).ToFrozenSet(StringComparer.Ordinal);
        }
        var encoders = await Catalogue("-encoders").ConfigureAwait(false);
        var muxers = await Catalogue("-muxers").ConfigureAwait(false);
        var demuxers = await Catalogue("-demuxers").ConfigureAwait(false);
        var filters = await Catalogue("-filters").ConfigureAwait(false);
        bool mf = false;
        if (OperatingSystem.IsWindows() && encoders.Contains("h264_mf") && muxers.Contains("mp4"))
        {
            string directory = Path.Combine(Path.GetTempPath(), "Citrus-MF-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var probeToken = probeTimeout.Token;
            try
            {
                string raw = Path.Combine(directory, "probe.yuv"); string output = Path.Combine(directory, "probe.mp4");
                await File.WriteAllBytesAsync(raw, new byte[64 * 64 * 3], probeToken).ConfigureAwait(false);
                await RunAsync(["-f", "rawvideo", "-pixel_format", "yuv420p", "-video_size", "64x64", "-framerate", "24", "-i", raw,
                    "-frames:v", "2", "-c:v", "h264_mf", "-hw_encoding", "0", output], null, probeToken).ConfigureAwait(false);
                mf = (await ProbeAsync(output, probeToken).ConfigureAwait(false)).HasVideo;
            }
            catch (MediaProcessException) { /* Windows N and unavailable MFTs retain native MPEG-4. */ }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { /* Unresponsive MFT: keep fallback presets. */ }
            finally { Directory.Delete(directory, true); }
        }
        return new(encoders, muxers, demuxers, filters, mf);
    }

    private static async Task<string> InvokeAsync(string executable, IReadOnlyList<string> arguments, IProgress<ConversionProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(executable)) throw new FileNotFoundException("Media tools are missing. Reinstall Citrus.", executable);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        static void Kill(Process child) => TerminateChild(child.Kill);
        using var registration = token.Register(() => Kill(process));
        async Task<string> Drain(StreamReader reader, int limit, IProgress<ConversionProgress>? observer)
        {
            try { return await ReadBoundedAsync(reader, limit, observer).ConfigureAwait(false); }
            catch { Kill(process); throw; }
        }
        var stderr = Drain(process.StandardError, ErrorLimit, null);
        var stdout = Drain(process.StandardOutput, OutputLimit, progress);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(), stdout, stderr).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new MediaProcessException($"The media engine failed (exit {process.ExitCode}). {await stderr.ConfigureAwait(false)}");
            return await stdout.ConfigureAwait(false);
        }
        finally
        {
            Kill(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    internal static void TerminateChild(Action<bool> kill)
    {
        try { kill(true); }
        catch (InvalidOperationException) { /* Already exited or disposed. */ }
        catch (Exception error) when (TerminationUnavailable(error))
        {
            // Windows can deny tree enumeration even for our own FFmpeg process.
            // Cancellation callbacks must not throw through the editor's Closed event.
            try { kill(false); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        static bool TerminationUnavailable(Exception error) => error is System.ComponentModel.Win32Exception ||
            error is AggregateException aggregate && aggregate.Flatten().InnerExceptions.All(inner =>
                inner is System.ComponentModel.Win32Exception or InvalidOperationException);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, IProgress<ConversionProgress>? progress)
    {
        var retained = new StringBuilder(); var line = new StringBuilder(); char[] buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            retained.Append(buffer, 0, count);
            if (retained.Length > limit) retained.Remove(0, retained.Length - limit);
            if (progress is null) continue;
            for (int i = 0; i < count; i++)
            {
                if (buffer[i] == '\n')
                {
                    string value = line.ToString(); line.Clear();
                    if (value.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(value.AsSpan(12), CultureInfo.InvariantCulture, out long microseconds))
                        progress.Report(new(null, $"Encoding {TimeSpan.FromMicroseconds(Math.Max(0, microseconds)):g}"));
                }
                else if (line.Length < 1024) line.Append(buffer[i]);
            }
        }
        return retained.ToString();
    }
}
