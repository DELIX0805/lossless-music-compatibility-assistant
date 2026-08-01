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
        "-sample_fmt", "s32", "-metadata", "title=Compatibility Test",
        Path.Combine(testRoot, "high-resolution.flac"));
    await RunFfmpegAsync("-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "sine=frequency=523:sample_rate=96000:duration=120", "-ac", "2", "-c:a", "flac",
        "-sample_fmt", "s32", Path.Combine(testRoot, "long-running.flac"));

    var service = new FfmpegService();
    var sonyPreset = OutputPreset.All.Single(preset => preset.Kind == OutputPresetKind.SonyNwFlac);
    var alacPreset = OutputPreset.All.Single(preset => preset.Kind == OutputPresetKind.IpodShuffleAlac);
    var aacPreset = OutputPreset.All.Single(preset => preset.Kind == OutputPresetKind.IpodShuffleAac);
    var mp3Preset = OutputPreset.All.Single(preset => preset.Kind == OutputPresetKind.UniversalMp3);
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
    Assert(aac.CodecProfile == "LC", "Converted AAC profile was not detected as AAC-LC.");
    var heAac = new AudioFileItem
    {
        FilePath = Path.Combine(testRoot, "simulated-he-aac.m4a"),
        Format = "AAC",
        CodecProfile = "HE-AAC",
        SampleRate = 44100,
        Channels = 2,
        BitRate = 128000
    };
    Assert(!aacPreset.IsExactMatch(heAac), "HE-AAC must not bypass the AAC-LC conversion preset.");

    var mp3Output = Path.Combine(testRoot, "converted-mp3.mp3");
    await service.ConvertAsync(highResolution, mp3Preset, mp3Output, new Progress<double>(), CancellationToken.None);
    var mp3 = await service.ProbeAsync(mp3Output, CancellationToken.None);
    Assert(mp3Preset.IsExactMatch(mp3), "Converted output is not a compatible MP3/44.1 kHz/stereo file.");
    Assert(mp3.BitRate is >= 300000 and <= 320000,
        $"Converted MP3 bitrate ({mp3.BitRate} bps) is not close to the requested 320 kbps CBR.");
    Assert(HasId3v23AndId3v1(mp3Output), "Converted MP3 is missing ID3v2.3 or ID3v1 compatibility tags.");

    var mp3CopyOutput = Path.Combine(testRoot, "copied-mp3.mp3");
    await service.ConvertAsync(mp3, mp3Preset, mp3CopyOutput, new Progress<double>(), CancellationToken.None);
    Assert(Hash(mp3Output) == Hash(mp3CopyOutput), "Compatible MP3 input was re-encoded instead of copied.");

    var parallelOutputs = Enumerable.Range(1, 4)
        .Select(number => Path.Combine(testRoot, $"parallel-{number}.mp3"))
        .ToArray();
    await Task.WhenAll(parallelOutputs.Select(path =>
        service.ConvertAsync(highResolution, mp3Preset, path, new Progress<double>(), CancellationToken.None)));
    foreach (var path in parallelOutputs)
    {
        var parallelMp3 = await service.ProbeAsync(path, CancellationToken.None);
        Assert(mp3Preset.IsExactMatch(parallelMp3), "A parallel MP3 conversion produced an incompatible file.");
    }
    Assert(!Directory.EnumerateFiles(testRoot, "*.tmp.*").Any(), "Parallel conversion left a temporary file.");

    var firstSourceFolder = Path.Combine(testRoot, "source-a");
    var secondSourceFolder = Path.Combine(testRoot, "source-b");
    var plannedOutputFolder = Path.Combine(testRoot, "planned-output");
    Directory.CreateDirectory(firstSourceFolder);
    Directory.CreateDirectory(secondSourceFolder);
    Directory.CreateDirectory(plannedOutputFolder);
    var firstSameName = Path.Combine(firstSourceFolder, "same-name.flac");
    var secondSameName = Path.Combine(secondSourceFolder, "same-name.wav");
    File.WriteAllBytes(firstSameName, [1]);
    File.WriteAllBytes(secondSameName, [2]);
    File.WriteAllBytes(Path.Combine(plannedOutputFolder, "same-name.mp3"), [3]);
    Directory.CreateDirectory(Path.Combine(plannedOutputFolder, "same-name (2).mp3"));
    var plannedPaths = OutputPathPlanner.Create(
        [new AudioFileItem { FilePath = firstSameName }, new AudioFileItem { FilePath = secondSameName }],
        plannedOutputFolder,
        ".mp3");
    Assert(Path.GetFileName(plannedPaths[0]) == "same-name (3).mp3"
           && Path.GetFileName(plannedPaths[1]) == "same-name (4).mp3",
        "Parallel output planning did not reserve unique names before conversion.");

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

    var longRunning = await service.ProbeAsync(Path.Combine(testRoot, "long-running.flac"), CancellationToken.None);
    var activeCancelledOutput = Path.Combine(testRoot, "active-cancelled.mp3");
    using (var activeCancellation = new CancellationTokenSource())
    {
        var cancelAfterProgress = new CallbackProgress<double>(value =>
        {
            if (value > 0) activeCancellation.Cancel();
        });
        await AssertThrowsAsync<OperationCanceledException>(
            () => service.ConvertAsync(longRunning, mp3Preset, activeCancelledOutput,
                cancelAfterProgress, activeCancellation.Token),
            "An active FFmpeg conversion did not report cancellation.");
    }
    Assert(!File.Exists(activeCancelledOutput), "Active cancellation left a final output file.");
    Assert(!Directory.EnumerateFiles(testRoot, "*.tmp.*").Any(),
        "Active cancellation left a temporary output file or a running pipe task.");

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

static bool HasId3v23AndId3v1(string path)
{
    using var stream = File.OpenRead(path);
    if (stream.Length < 132) return false;

    Span<byte> header = stackalloc byte[4];
    stream.ReadExactly(header);
    if (header[0] != (byte)'I' || header[1] != (byte)'D' || header[2] != (byte)'3' || header[3] != 3)
        return false;

    stream.Seek(-128, SeekOrigin.End);
    Span<byte> footer = stackalloc byte[3];
    stream.ReadExactly(footer);
    return footer[0] == (byte)'T' && footer[1] == (byte)'A' && footer[2] == (byte)'G';
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

sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
