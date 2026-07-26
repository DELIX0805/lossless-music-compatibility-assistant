using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Forms = System.Windows.Forms;

namespace LightAudioConverter;

public partial class MainWindow : Window
{
    private static readonly string[] SupportedExtensions =
    [
        ".flac", ".wav", ".wave", ".aif", ".aiff", ".m4a", ".alac", ".ape",
        ".wv", ".tta", ".mp3", ".aac", ".ogg", ".opus", ".wma"
    ];

    private readonly FfmpegService? _ffmpeg;
    private CancellationTokenSource? _conversionCts;
    private bool _busy;
    private bool _isClosing;

    public ObservableCollection<AudioFileItem> Files { get; } = [];
    public string OutputFolder { get; private set; } = GetDefaultOutputFolder();
    private OutputPreset SelectedPreset =>
        OutputPreset.All[Math.Clamp(PresetComboBox.SelectedIndex, 0, OutputPreset.All.Count - 1)];

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            _ffmpeg = new FfmpegService();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"无法初始化内置音频引擎。\n\n{ex.Message}",
                "无损音乐兼容助手",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Loaded += (_, _) => Close();
            return;
        }

        DataContext = this;
        Files.CollectionChanged += (_, _) => UpdateEmptyState();
        OutputFolderText.Text = OutputFolder;
        UpdateEmptyState();
        Loaded += async (_, _) =>
        {
            var commandLineFiles = Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).ToArray();
            if (commandLineFiles.Length > 0)
                await AddPathsAsync(commandLineFiles);
        };
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = Files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConvertButton.IsEnabled = Files.Count > 0 && !_busy;
        ClearButton.IsEnabled = Files.Count > 0 && !_busy;
        ConvertButton.Content = Files.Count > 0 ? $"开始转换（{Files.Count}）" : "开始转换";
    }

    private async Task AddPathsAsync(IEnumerable<string> paths)
    {
        if (_busy || _ffmpeg is null) return;
        var expandedPaths = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                    expandedPaths.AddRange(Directory.EnumerateFiles(path, "*.*", SearchOption.TopDirectoryOnly));
                else if (File.Exists(path))
                    expandedPaths.Add(path);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                System.Windows.MessageBox.Show($"{path}\n{ex.Message}", "无法读取文件夹",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        var files = expandedPaths
            .Where(File.Exists)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !Files.Any(f => f.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var path in files)
        {
            HintText.Text = $"正在读取：{Path.GetFileName(path)}";
            try
            {
                var item = await _ffmpeg.ProbeAsync(path, CancellationToken.None);
                item.Status = SelectedPreset.IsExactMatch(item) ? "无需处理" : "等待转换";
                Files.Add(item);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"{Path.GetFileName(path)}\n{ex.Message}", "无法读取音频",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        HintText.Text = "与目标格式一致的文件将跳过不必要的处理";
    }

    private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || QualitySummary is null || QualityDetail is null)
            return;

        var preset = SelectedPreset;
        QualitySummary.Text = preset.AlgorithmSummary;
        QualityDetail.Text = preset.AlgorithmDetail;
        HintText.Text = preset.Kind == OutputPresetKind.IpodShuffleAac
            ? "AAC 320 kbps 为高质量有损转换；已兼容的 M4A 文件会直接复制"
            : "与目标格式一致的文件将跳过不必要的处理";
        foreach (var item in Files)
            item.Status = preset.IsExactMatch(item) ? "无需处理" : "等待转换";
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "音频文件|*.flac;*.wav;*.wave;*.aif;*.aiff;*.m4a;*.alac;*.ape;*.wv;*.tta;*.mp3;*.aac;*.ogg;*.opus;*.wma|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) == true)
            await AddPathsAsync(dialog.FileNames);
    }

    private void DropZone_Click(object sender, MouseButtonEventArgs e) => AddFiles_Click(sender, e);

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
            await AddPathsAsync(paths);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy && sender is System.Windows.Controls.Button { Tag: AudioFileItem item })
            Files.Remove(item);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy)
            Files.Clear();
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择转换后文件的保存位置",
            SelectedPath = Directory.Exists(OutputFolder) ? OutputFolder : string.Empty,
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            OutputFolder = dialog.SelectedPath;
            OutputFolderText.Text = OutputFolder;
        }
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Files.Count == 0) return;
        var outputFolder = OutputFolder;
        var preset = SelectedPreset;
        try
        {
            Directory.CreateDirectory(outputFolder);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            System.Windows.MessageBox.Show($"{outputFolder}\n{ex.Message}", "无法创建输出文件夹",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _busy = true;
        _conversionCts = new CancellationTokenSource();
        SetBusyUi(true);

        var completed = 0;
        var failed = 0;
        var wasCancelled = false;
        try
        {
            for (var i = 0; i < Files.Count; i++)
            {
                var item = Files[i];
                if (_conversionCts.IsCancellationRequested) break;
                item.Status = preset.IsExactMatch(item) ? "直接复制" : "转换中";
                var index = i;
                var progress = new Progress<double>(value =>
                {
                    item.Progress = value;
                    OverallProgress.Value = (index + value / 100d) / Files.Count * 100d;
                    HintText.Text = $"{item.Status}：{item.FileName}  {value:0}%";
                });

                try
                {
                    var destination = UniqueOutputPath(item, outputFolder, preset.Extension);
                    await _ffmpeg!.ConvertAsync(item, preset, destination, progress, _conversionCts.Token);
                    item.Status = "已完成";
                    completed++;
                }
                catch (OperationCanceledException)
                {
                    item.Status = "已取消";
                    break;
                }
                catch (Exception ex)
                {
                    item.Status = "失败";
                    failed++;
                    System.Windows.MessageBox.Show($"{item.FileName}\n{ex.Message}", "转换失败",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            wasCancelled = _conversionCts.IsCancellationRequested;
        }
        finally
        {
            wasCancelled |= _conversionCts.IsCancellationRequested;
            _busy = false;
            if (!_isClosing)
                SetBusyUi(false);
            _conversionCts.Dispose();
            _conversionCts = null;
        }

        if (_isClosing)
        {
            Close();
            return;
        }

        HintText.Text = wasCancelled
            ? $"已取消，完成 {completed} 个文件"
            : $"转换完成：成功 {completed} 个，失败 {failed} 个";

        if (completed > 0 && !wasCancelled && IsVisible)
            System.Windows.MessageBox.Show($"已完成 {completed} 个文件。\n\n{outputFolder}", "转换完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string UniqueOutputPath(AudioFileItem item, string outputFolder, string extension)
    {
        var baseName = Path.GetFileNameWithoutExtension(item.FileName);
        var path = Path.Combine(outputFolder, baseName + extension);
        if (!File.Exists(path) && !path.Equals(item.FilePath, StringComparison.OrdinalIgnoreCase)) return path;
        for (var i = 2; ; i++)
        {
            path = Path.Combine(outputFolder, $"{baseName} ({i}){extension}");
            if (!File.Exists(path) && !path.Equals(item.FilePath, StringComparison.OrdinalIgnoreCase)) return path;
        }
    }

    private void SetBusyUi(bool busy)
    {
        ConvertButton.IsEnabled = !busy && Files.Count > 0;
        BrowseOutputButton.IsEnabled = !busy;
        PresetComboBox.IsEnabled = !busy;
        DropZone.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        OverallProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!busy) OverallProgress.Value = 0;
        UpdateEmptyState();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _conversionCts?.Cancel();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize();
        else DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        if (_isClosing)
        {
            e.Cancel = true;
            return;
        }

        if (System.Windows.MessageBox.Show("正在转换，确定退出吗？", "无损音乐兼容助手",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        _isClosing = true;
        _conversionCts?.Cancel();
    }

    private static string GetDefaultOutputFolder()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop))
            desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
        if (string.IsNullOrWhiteSpace(desktop))
            desktop = AppContext.BaseDirectory;
        return Path.Combine(desktop, "无损音乐兼容助手");
    }
}
