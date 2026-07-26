using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace LightAudioConverter;

public sealed class AudioFileItem : INotifyPropertyChanged
{
    private string _status = "等待转换";
    private double _progress;

    public required string FilePath { get; init; }
    public string FileName => Path.GetFileName(FilePath);
    public string Format { get; init; } = "未知";
    public int SampleRate { get; init; }
    public int BitDepth { get; init; }
    public int BitRate { get; init; }
    public int Channels { get; init; }
    public TimeSpan Duration { get; init; }

    public string SourceSummary
    {
        get
        {
            var bits = BitDepth > 0 ? $"{BitDepth}-bit" : "未知位深";
            var rate = SampleRate > 0 ? $"{SampleRate / 1000d:0.#} kHz" : "未知采样率";
            var channels = Channels switch { 1 => "单声道", 2 => "双声道", > 2 => $"{Channels} 声道", _ => "未知声道" };
            return $"{Format} · {bits} · {rate} · {channels}";
        }
    }

    public string DurationText => Duration > TimeSpan.Zero
        ? Duration.ToString(Duration.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss")
        : "--:--";

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
