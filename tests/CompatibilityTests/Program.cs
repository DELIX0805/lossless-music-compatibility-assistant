using System.Diagnostics;
using System.Security.Cryptography;
using LightAudioConverter;

var enginePath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Engine", "ffmpeg.exe"));

if (!File.Exists(enginePath))
    throw new FileNotFoundException("Pass the path to a libsoxr-enabled ffmpeg.exe as the first argument.", enginePath);

var testRoot = Path.Combine(Path.GetTempPath(), $"lmca-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(testRoot);

try
{
    await RunFfmpegAsync("-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "sine=frequency=997:sample_rate=44100:duration=0.4", "-ac", "2", "-c:a", "pcm_s16le",
        "-f", "wav", Path.Combine(testRoot, "disguised.flac"));
    await RunFfmpegAsync("-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "sine=frequency=997:sample_rate=44100:duration=0.4", "-ac", "2", "-c:a", "flac",
        "-sample_fmt", "s16", Path.Combine(testRoot, "exact.flac"));
    await RunFfmpegAsync("-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "sine=frequency=997:sample_rate=96000:duration=0.4", "-ac", "2", "-c:a", "flac",
        "-sample_fmt", "s32", Path.Combine(testRoot, "high-resolution.flac"));

    var service = new FfmpegService();
    var sonyPreset = OutputPreset.All.Single(preset => preset.Kind == OutputPresetKind.SonyNwFlac);
    var alacPreset = OutputPreset.All.Single(preset => preset.Kind == OutputPresetKind.IpodShuffleAlac);
    var aacPreset = OutputPreset.All.Single(preset => preset.Kind == OutputPresetKind.IpodShuffleAac);
    var disguised = await service.ProbeAsync(Path.Combine(testRoot, "disguised.flac"), CancellationToken.None);
    Assert(!sonyPreset.IsExactMatch(disguised), "A WAV file renamed to .flac must not bypass conversion.");

    var exact = await service.ProbeAsync(Path.Combine(testRoot, "exact.flac"), CancellationToken.None);
    Assert(sonyPreset.IsExactMatch(exact), "A real 16-bit/44.1 kHz/stereo FLAC should use byte-for-byte copy.");
    var exactOutput = Path.Combine(testRoot, "exact-output.flac");
    await service.ConvertAsync(exact, sonyPreset, exactOutput, new Progress<double>(), CancellationToken.None);
    Assert(Hash(exact.FilePath) == Hash(exactOutput), "Exact-target copy changed the file bytes.");

    var highResolution = await service.ProbeAsync(Path.Combine(testRoot, "high-resolution.flac"), CancellationToken.None);
    var convertedOutput = Path.Combine(testRoot, "converted.flac");
    await service.ConvertAsync(highResolution, sonyPreset, convertedOutput, new Progress<double>(), CancellationToken.None);
    var converted = await service.ProbeAsync(convertedOutput, CancellationToken.None);
    Assert(sonyPreset.IsExactMatch(converted), "Converted output is not FLAC/16-bit/44.1 kHz/stereo.");

    var alacOutput = Path.Combine(testRoot, "converted-alac.m4a");
    await service.ConvertAsync(highResolution, alacPreset, alacOutput, new Progress<double>(), CancellationToken.None);
    var alac = await service.ProbeAsync(alacOutput, CancellationToken.None);
    Assert(alacPreset.IsExactMatch(alac), "Converted output is not ALAC/16-bit/44.1 kHz/stereo.");

    var aacOutput = Path.Combine(testRoot, "converted-aac.m4a");
    await service.ConvertAsync(highResolution, aacPreset, aacOutput, new Progress<double>(), CancellationToken.None);
    var aac = await service.ProbeAsync(aacOutput, CancellationToken.None);
    Assert(aacPreset.IsExactMatch(aac), "Converted output is not an iPod-compatible AAC M4A.");

    var cancelledOutput = Path.Combine(testRoot, "cancelled.flac");
    using (var cancellation = new CancellationTokenSource())
    {
        cancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(
            () => service.ConvertAsync(exact, sonyPreset, cancelledOutput, new Progress<double>(), cancellation.Token),
            "A pre-cancelled copy did not report cancellation.");
    }
    Assert(!File.Exists(cancelledOutput), "Cancellation left a final output file.");
    Assert(!Directory.EnumerateFiles(testRoot, "*.tmp.*").Any(), "Cancellation left a temporary output file.");

    var concurrentOutput = Path.Combine(testRoot, "concurrent.flac");
    var conversions = new[]
    {
        CaptureAsync(() => service.ConvertAsync(exact, sonyPreset, concurrentOutput, new Progress<double>(), CancellationToken.None)),
        CaptureAsync(() => service.ConvertAsync(exact, sonyPreset, concurrentOutput, new Progress<double>(), CancellationToken.None))
    };
    var results = await Task.WhenAll(conversions);
    Assert(results.Count(error => error is null) == 1, "Concurrent writers did not produce exactly one winner.");
    Assert(results.Count(error => error is IOException) == 1, "The losing concurrent writer was not rejected safely.");
    Assert(Hash(exact.FilePath) == Hash(concurrentOutput), "Concurrent conversion corrupted or replaced the winner.");
    Assert(!Directory.EnumerateFiles(testRoot, "*.tmp.*").Any(), "Concurrent conversion left a temporary file.");

    Console.WriteLine("All compatibility and file-safety tests passed.");
}
finally
{
    try { Directory.Delete(testRoot, true); } catch { }
}

return;

async Task RunFfmpegAsync(params string[] arguments)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = enginePath,
        UseShellExecute = false,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    foreach (var argument in arguments)
        startInfo.ArgumentList.Add(argument);

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start test FFmpeg.");
    var error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"Fixture generation failed:{Environment.NewLine}{error}");
}

static string Hash(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static async Task AssertThrowsAsync<TException>(Func<Task> action, string message) where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static async Task<Exception?> CaptureAsync(Func<Task> action)
{
    try
    {
        await action();
        return null;
    }
    catch (Exception ex)
    {
        return ex;
    }
}
