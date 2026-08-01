using Microsoft.Win32;
using System.Collections.Concurrent;
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
    private long _conversionBatchId;

    public ObservableCollection<AudioFileItem> Files { get; } = [];
    public string OutputFolder { get; private set; } = GetDefaultOutputFolder();
    private OutputPreset SelectedPreset =>
        OutputPreset.All[Math.Clamp(PresetComboBox.SelectedIndex, 0, OutputPreset.All.Count - 1)];
    private int SelectedParallelism => ConcurrencyComboBox.SelectedIndex switch
    {
        1 => 1,
        2 => 2,
        3 => 4,
        _ => Math.Clamp(Environment.ProcessorCount / 4, 1, 4)
    };

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
        HintText.Text = preset.Kind switch
        {
            OutputPresetKind.IpodShuffleAac =>
                "AAC 320 kbps 为高质量有损转换；已兼容的 M4A 文件会直接复制",
            OutputPresetKind.UniversalMp3 =>
                "MP3 320 kbps 为高质量有损转换；已兼容的 MP3 会直接复制，避免二次损失",
            _ => "与目标格式一致的文件将跳过不必要的处理"
        };
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
        var parallelism = SelectedParallelism;
        List<ConversionJob> jobs;
        try
        {
            Directory.CreateDirectory(outputFolder);
            jobs = CreateConversionJobs(Files.ToList(), outputFolder, preset.Extension);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                   or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            System.Windows.MessageBox.Show($"{outputFolder}\n{ex.Message}", "无法创建输出文件夹",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _busy = true;
        var batchId = ++_conversionBatchId;
        _conversionCts = new CancellationTokenSource();
        SetBusyUi(true);

        var progressValues = new double[jobs.Count];
        using var limiter = new SemaphoreSlim(parallelism, parallelism);
        var errors = new ConcurrentQueue<string>();
        var completed = 0;
        var failed = 0;
        var wasCancelled = false;
        try
        {
            foreach (var job in jobs)
            {
                job.Item.Status = "排队中";
                job.Item.Progress = 0;
            }

            var tasks = jobs.Select((job, index) => ConvertJobAsync(
                job, index, jobs.Count, preset, parallelism, limiter,
                progressValues, errors, batchId, _conversionCts.Token,
                () => Interlocked.Increment(ref completed),
                () => Interlocked.Increment(ref failed)));
            await Task.WhenAll(tasks);
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

        if (!wasCancelled && failed > 0 && IsVisible)
        {
            var details = string.Join(Environment.NewLine + Environment.NewLine, errors.Take(8));
            if (failed > 8)
                details += $"{Environment.NewLine}{Environment.NewLine}另有 {failed - 8} 个失败任务。";
            System.Windows.MessageBox.Show(details, $"{failed} 个文件转换失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        if (completed > 0 && failed == 0 && !wasCancelled && IsVisible)
            System.Windows.MessageBox.Show($"已完成 {completed} 个文件。\n\n{outputFolder}", "转换完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task ConvertJobAsync(
        ConversionJob job,
        int index,
        int totalCount,
        OutputPreset preset,
        int parallelism,
        SemaphoreSlim limiter,
        double[] progressValues,
        ConcurrentQueue<string> errors,
        long batchId,
        CancellationToken cancellationToken,
        Action markCompleted,
        Action markFailed)
    {
        var lockTaken = false;
        try
        {
            await limiter.WaitAsync(cancellationToken);
            lockTaken = true;
            cancellationToken.ThrowIfCancellationRequested();
            job.Item.Status = preset.IsExactMatch(job.Item) ? "直接复制" : "转换中";
            var progress = new Progress<double>(value =>
            {
                if (!_busy || batchId != _conversionBatchId)
                    return;
                job.Item.Progress = value;
                progressValues[index] = value;
                OverallProgress.Value = progressValues.Sum() / totalCount;
                if (job.Item.Status is "转换中" or "直接复制")
                    HintText.Text = $"并行处理中（最多 {parallelism} 个）：{job.Item.FileName}  {value:0}%";
            });

            await _ffmpeg!.ConvertAsync(job.Item, preset, job.Destination, progress, cancellationToken);
            job.Item.Progress = 100;
            progressValues[index] = 100;
            OverallProgress.Value = progressValues.Sum() / totalCount;
            job.Item.Status = "已完成";
            markCompleted();
        }
        catch (OperationCanceledException)
        {
            job.Item.Status = "已取消";
        }
        catch (Exception ex)
        {
            job.Item.Status = "失败";
            errors.Enqueue($"{job.Item.FileName}\n{ex.Message}");
            markFailed();
        }
        finally
        {
            if (lockTaken)
                limiter.Release();
        }
    }

    private static List<ConversionJob> CreateConversionJobs(
        IReadOnlyList<AudioFileItem> files,
        string outputFolder,
        string extension)
    {
        var destinations = OutputPathPlanner.Create(files, outputFolder, extension);
        var jobs = new List<ConversionJob>(files.Count);
        for (var index = 0; index < files.Count; index++)
            jobs.Add(new ConversionJob(files[index], destinations[index]));
        return jobs;
    }

    private void SetBusyUi(bool busy)
    {
        ConvertButton.IsEnabled = !busy && Files.Count > 0;
        BrowseOutputButton.IsEnabled = !busy;
        PresetComboBox.IsEnabled = !busy;
        ConcurrencyComboBox.IsEnabled = !busy;
        DropZone.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        OverallProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        OverallProgress.Value = 0;
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

    private sealed record ConversionJob(AudioFileItem Item, string Destination);
}
