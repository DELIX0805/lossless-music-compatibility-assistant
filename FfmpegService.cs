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

        var expectedHash = Convert.ToHexString(SHA256.HashData(resource));
        var hashPrefix = expectedHash.Substring(0, 12);
        resource.Position = 0;
        var engineDir = Path.Combine(cacheRoot, hashPrefix);
        Directory.CreateDirectory(engineDir);
        var enginePath = Path.Combine(engineDir, "ffmpeg.exe");

        using var extractionMutex = new Mutex(false, $@"Local\LosslessMusicCompatibilityAssistant-{hashPrefix}");
        var lockTaken = false;
        try
        {
            try
            {
                lockTaken = extractionMutex.WaitOne(TimeSpan.FromSeconds(60));
            }
            catch (AbandonedMutexException)
            {
                // The previous owner terminated while extracting. This thread now
                // owns the mutex, and the hash check below will repair the cache.
                lockTaken = true;
            }

            if (!lockTaken)
                throw new TimeoutException("等待音频引擎初始化超时，请关闭其他程序实例后重试。");

            if (File.Exists(enginePath) && ComputeFileHash(enginePath).Equals(expectedHash, StringComparison.Ordinal))
                return enginePath;

            var temporaryPath = Path.Combine(engineDir, $"ffmpeg-{Guid.NewGuid():N}.tmp");
            try
            {
                resource.Position = 0;
                using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    resource.CopyTo(file);
                    file.Flush(true);
                }

                if (!ComputeFileHash(temporaryPath).Equals(expectedHash, StringComparison.Ordinal))
                    throw new InvalidDataException("内置音频引擎校验失败。");

                File.Move(temporaryPath, enginePath, true);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
        finally
        {
            if (lockTaken)
                extractionMutex.ReleaseMutex();
        }

        return enginePath;
    }

    private static string ComputeFileHash(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(file));
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
        var item = new AudioFileItem
        {
            FilePath = filePath,
            Format = codec,
            SampleRate = sampleRate,
            BitDepth = bitDepth,
            Channels = channels,
            Duration = duration
        };
        item.Status = item.IsExactTarget ? "无需处理" : "等待转换";
        return item;
    }

    public async Task ConvertAsync(
        AudioFileItem item,
        string outputPath,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("输出路径无效。");
        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}.tmp.flac");

        try
        {
            if (item.IsExactTarget)
            {
                item.Status = "无损复制";
                await CopyWithProgressAsync(item.FilePath, temporaryPath, progress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, outputPath, false);
                TryCopyTimestamp(item.FilePath, outputPath);
                progress.Report(100);
                return;
            }

            var psi = CreateStartInfo();
            foreach (var arg in new[]
            {
                "-n", "-hide_banner", "-nostdin",
                "-i", item.FilePath,
                "-map", "0:a:0", "-map_metadata", "0", "-vn",
                "-af", "aresample=44100:resampler=soxr:precision=33:dither_method=triangular:osf=s16",
                "-ar", "44100", "-ac", "2", "-sample_fmt", "s16",
                "-c:a", "flac", "-compression_level", "8",
                "-progress", "pipe:1", "-nostats", temporaryPath
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
                throw new InvalidOperationException(LastLines(errors.ToString(), 8));

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, outputPath, false);
            progress.Report(100);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
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
        if (line.Contains("6.1", StringComparison.OrdinalIgnoreCase)) return 7;
        if (line.Contains("5.1", StringComparison.OrdinalIgnoreCase)) return 6;
        if (line.Contains("5.0", StringComparison.OrdinalIgnoreCase)) return 5;
        if (line.Contains("quad", StringComparison.OrdinalIgnoreCase)) return 4;
        if (line.Contains("4.0", StringComparison.OrdinalIgnoreCase)) return 4;
        if (line.Contains("3.0", StringComparison.OrdinalIgnoreCase)) return 3;
        if (line.Contains("2.1", StringComparison.OrdinalIgnoreCase)) return 3;
        if (line.Contains("stereo", StringComparison.OrdinalIgnoreCase)) return 2;
        if (line.Contains("mono", StringComparison.OrdinalIgnoreCase)) return 1;
        var countMatch = ChannelCountRegex().Match(line);
        if (countMatch.Success && int.TryParse(countMatch.Groups[1].Value, out var count))
            return count;
        return 0;
    }

    private static async Task CopyWithProgressAsync(string source, string destination, IProgress<double> progress, CancellationToken token)
    {
        const int bufferSize = 1024 * 1024;
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, true);
        var buffer = new byte[bufferSize];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            copied += read;
            progress.Report(input.Length == 0 ? 100 : copied * 100d / input.Length);
        }
    }

    private static void TryCopyTimestamp(string source, string destination)
    {
        try { File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source)); } catch { }
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
    [GeneratedRegex(@"(\d+)\s+channels?", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelCountRegex();
}
