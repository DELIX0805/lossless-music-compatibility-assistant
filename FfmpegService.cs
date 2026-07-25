using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LightAudioConverter;

public sealed partial class FfmpegService
{
    private readonly string _ffmpegPath;

    public FfmpegService()
    {
        _ffmpegPath = ExtractEngine();
    }

    private static string ExtractEngine()
    {
        var assembly = typeof(FfmpegService).Assembly;
        const string resourceName = "LightAudioConverter.Engine.ffmpeg.exe";
        using var resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("内置音频引擎不存在。");

        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LosslessMusicCompatibilityAssistant", "Engine");
        Directory.CreateDirectory(cacheRoot);

        var hash = Convert.ToHexString(SHA256.HashData(resource)).Substring(0, 12);
        resource.Position = 0;
        var engineDir = Path.Combine(cacheRoot, hash);
        Directory.CreateDirectory(engineDir);
        var enginePath = Path.Combine(engineDir, "ffmpeg.exe");

        if (!File.Exists(enginePath) || new FileInfo(enginePath).Length != resource.Length)
        {
            using var file = new FileStream(enginePath, FileMode.Create, FileAccess.Write, FileShare.None);
            resource.CopyTo(file);
        }
        return enginePath;
    }

    public async Task<AudioFileItem> ProbeAsync(string filePath, CancellationToken cancellationToken)
    {
        var psi = CreateStartInfo();
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(filePath);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动音频引擎。");
        using var cancellation = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        });
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var text = await stderrTask;

        var duration = TimeSpan.Zero;
        var durationMatch = DurationRegex().Match(text);
        if (durationMatch.Success)
            TimeSpan.TryParse(durationMatch.Groups[1].Value, CultureInfo.InvariantCulture, out duration);

        var audioLine = text.Split('\n').FirstOrDefault(line => line.Contains("Audio:", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("没有检测到可转换的音频流。");

        var codecMatch = CodecRegex().Match(audioLine);
        var codec = codecMatch.Success ? codecMatch.Groups[1].Value.ToUpperInvariant() : Path.GetExtension(filePath).Trim('.').ToUpperInvariant();

        var rateMatch = SampleRateRegex().Match(audioLine);
        var sampleRate = rateMatch.Success ? int.Parse(rateMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 0;

        var bitMatch = BitDepthRegex().Match(audioLine);
        var bitDepth = bitMatch.Success ? int.Parse(bitMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        if (bitDepth == 0)
        {
            var sampleMatch = SampleFormatRegex().Match(audioLine);
            if (sampleMatch.Success)
                bitDepth = int.Parse(sampleMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        var channels = ParseChannels(audioLine);
        return new AudioFileItem
        {
            FilePath = filePath,
            Format = codec,
            SampleRate = sampleRate,
            BitDepth = bitDepth,
            Channels = channels,
            Duration = duration,
            Status = Path.GetExtension(filePath).Equals(".flac", StringComparison.OrdinalIgnoreCase)
                     && sampleRate == 44100 && bitDepth == 16 && channels == 2
                ? "无需处理"
                : "等待转换"
        };
    }

    public async Task ConvertAsync(
        AudioFileItem item,
        string outputPath,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        if (item.IsExactTarget)
        {
            item.Status = "无损复制";
            await CopyWithProgressAsync(item.FilePath, outputPath, progress, cancellationToken);
            return;
        }

        var psi = CreateStartInfo();
        foreach (var arg in new[]
        {
            "-y", "-hide_banner", "-nostdin",
            "-i", item.FilePath,
            "-map", "0:a:0", "-map_metadata", "0", "-vn",
            "-af", "aresample=44100:resampler=soxr:precision=33:dither_method=triangular:osf=s16",
            "-ar", "44100", "-ac", "2", "-sample_fmt", "s16",
            "-c:a", "flac", "-compression_level", "8",
            "-progress", "pipe:1", "-nostats", outputPath
        }) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动音频引擎。");
        using var cancellation = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        });
        var errors = new StringBuilder();
        var errorTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
                errors.AppendLine(line);
        }, cancellationToken);

        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(12), out var microseconds)
                && item.Duration.TotalMilliseconds > 0)
            {
                var value = microseconds / 1000d / item.Duration.TotalMilliseconds * 100d;
                progress.Report(Math.Clamp(value, 0, 99.5));
            }
        }

        await process.WaitForExitAsync(cancellationToken);
        await errorTask;
        if (process.ExitCode != 0)
        {
            TryDelete(outputPath);
            throw new InvalidOperationException(LastLines(errors.ToString(), 8));
        }
        progress.Report(100);
    }

    private ProcessStartInfo CreateStartInfo() => new()
    {
        FileName = _ffmpegPath,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8
    };

    private static int ParseChannels(string line)
    {
        if (line.Contains("7.1", StringComparison.OrdinalIgnoreCase)) return 8;
        if (line.Contains("5.1", StringComparison.OrdinalIgnoreCase)) return 6;
        if (line.Contains("quad", StringComparison.OrdinalIgnoreCase)) return 4;
        if (line.Contains("stereo", StringComparison.OrdinalIgnoreCase)) return 2;
        if (line.Contains("mono", StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    private static async Task CopyWithProgressAsync(string source, string destination, IProgress<double> progress, CancellationToken token)
    {
        const int bufferSize = 1024 * 1024;
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, true);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, true);
        var buffer = new byte[bufferSize];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            copied += read;
            progress.Report(input.Length == 0 ? 100 : copied * 100d / input.Length);
        }
        File.SetLastWriteTime(destination, File.GetLastWriteTime(source));
    }

    private static string LastLines(string text, int count) =>
        string.Join(Environment.NewLine, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(count));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    [GeneratedRegex(@"Duration:\s*(\d{2}:\d{2}:\d{2}(?:\.\d+)?)")]
    private static partial Regex DurationRegex();
    [GeneratedRegex(@"Audio:\s*([^,\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CodecRegex();
    [GeneratedRegex(@"(\d+)\s*Hz", RegexOptions.IgnoreCase)]
    private static partial Regex SampleRateRegex();
    [GeneratedRegex(@"\((\d+)\s*bit\)", RegexOptions.IgnoreCase)]
    private static partial Regex BitDepthRegex();
    [GeneratedRegex(@"\bs(?:16|24|32)(?:p|le|be)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex SampleFormatTokenRegex();
    [GeneratedRegex(@"\bs(16|24|32)(?:p|le|be)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex SampleFormatRegex();
}
