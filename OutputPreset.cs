using System.IO;

namespace LightAudioConverter;

public enum OutputPresetKind
{
    SonyNwFlac,
    IpodShuffleAlac,
    IpodShuffleAac,
    UniversalMp3
}

public sealed record OutputPreset(
    OutputPresetKind Kind,
    string Name,
    string FormatSummary,
    string Extension,
    string AlgorithmSummary,
    string AlgorithmDetail)
{
    public static IReadOnlyList<OutputPreset> All { get; } =
    [
        new(
            OutputPresetKind.SonyNwFlac,
            "索尼 NW 系列",
            "FLAC · 16-bit · 44.1 kHz · 双声道",
            ".flac",
            "SoXR 最高精度重采样 · TPDF 抖动",
            "仅在必要时处理 · 输出 16-bit · 不做音量归一化"),
        new(
            OutputPresetKind.IpodShuffleAlac,
            "iPod shuffle 4",
            "ALAC · 16-bit · 44.1 kHz · 双声道",
            ".m4a",
            "SoXR 最高精度重采样 · TPDF 抖动",
            "Apple Lossless · 仅在必要时处理 · 不做音量归一化"),
        new(
            OutputPresetKind.IpodShuffleAac,
            "iPod shuffle 4",
            "AAC-LC · 320 kbps · 44.1 kHz · 双声道",
            ".m4a",
            "SoXR 最高精度重采样 · AAC-LC 320 kbps",
            "高质量有损编码 · 不做音量归一化"),
        new(
            OutputPresetKind.UniversalMp3,
            "通用播放器",
            "MP3 · 320 kbps CBR · 44.1 kHz · 双声道",
            ".mp3",
            "SoXR 最高精度重采样 · LAME MP3 320 kbps",
            "CBR · Joint Stereo · 不做音量归一化")
    ];

    public bool IsExactMatch(AudioFileItem item) => Kind switch
    {
        OutputPresetKind.SonyNwFlac =>
            item.Format.Equals("FLAC", StringComparison.OrdinalIgnoreCase)
            && item.SampleRate == 44100 && item.BitDepth == 16 && item.Channels == 2
            && Path.GetExtension(item.FilePath).Equals(".flac", StringComparison.OrdinalIgnoreCase),
        OutputPresetKind.IpodShuffleAlac =>
            item.Format.Equals("ALAC", StringComparison.OrdinalIgnoreCase)
            && item.SampleRate == 44100 && item.BitDepth == 16 && item.Channels == 2
            && Path.GetExtension(item.FilePath).Equals(".m4a", StringComparison.OrdinalIgnoreCase),
        OutputPresetKind.IpodShuffleAac =>
            item.Format.Equals("AAC", StringComparison.OrdinalIgnoreCase)
            && item.CodecProfile.Equals("LC", StringComparison.OrdinalIgnoreCase)
            && item.SampleRate == 44100 && item.Channels == 2
            && item.BitRate is >= 8000 and <= 320000
            && Path.GetExtension(item.FilePath).Equals(".m4a", StringComparison.OrdinalIgnoreCase),
        OutputPresetKind.UniversalMp3 =>
            item.Format.Equals("MP3", StringComparison.OrdinalIgnoreCase)
            && item.SampleRate == 44100 && item.Channels == 2
            && item.BitRate is >= 8000 and <= 320000
            && Path.GetExtension(item.FilePath).Equals(".mp3", StringComparison.OrdinalIgnoreCase),
        _ => false
    };
}
