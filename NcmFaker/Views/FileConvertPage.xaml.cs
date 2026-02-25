using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Windows.Storage;
using libncmdump_demo_cli;

namespace NcmFaker.Views
{
    // ==========================================
    // 新增：支持动态绑定和取消的任务数据模型
    // ==========================================
    public class ConvertTaskItem : INotifyPropertyChanged
    {
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public CancellationTokenSource Cts { get; set; } = new CancellationTokenSource();

        private double _progress;
        public double Progress { get => _progress; set { _progress = value; OnPropertyChanged(nameof(Progress)); } }

        private string _status = "等待中";
        public string Status { get => _status; set { _status = value; OnPropertyChanged(nameof(Status)); } }

        private bool _isActionable = true;
        public bool IsActionable { get => _isActionable; set { _isActionable = value; OnPropertyChanged(nameof(IsActionable)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        // 单任务取消逻辑
        public void Cancel()
        {
            if (!Cts.IsCancellationRequested && IsActionable)
            {
                Cts.Cancel();
                Status = "已取消";
                IsActionable = false;
            }
        }
    }

    public sealed partial class FileConvertPage : Page
    {
        // 将原有的双列表替换为单一的强类型任务列表
        private ObservableCollection<ConvertTaskItem> _tasks = new ObservableCollection<ConvertTaskItem>();

        private string _lastOutputDirectory = "";

        // 新增：全局转换状态与全局取消令牌
        private bool _isConverting = false;
        private CancellationTokenSource _globalCts;

        public FileConvertPage()
        {
            this.InitializeComponent();

            // 核心修复：开启页面缓存，确保切换左侧导航栏时，本页面的后台任务和UI状态不被销毁
            this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;

            TaskListView.ItemsSource = _tasks;
        }

        private async void AddFilesItem_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Current);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add(".ncm");

            var files = await picker.PickMultipleFilesAsync();
            if (files != null && files.Count > 0)
            {
                foreach (var file in files)
                {
                    if (!_tasks.Any(t => t.FilePath == file.Path))
                    {
                        _tasks.Add(new ConvertTaskItem { FileName = file.Name, FilePath = file.Path });
                    }
                }
            }
        }

        private async void AddFolderItem_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Current);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                var ncmFiles = await Task.Run(() =>
                {
                    try { return Directory.GetFiles(folder.Path, "*.ncm", SearchOption.TopDirectoryOnly); }
                    catch { return Array.Empty<string>(); }
                });

                foreach (var f in ncmFiles)
                {
                    if (!_tasks.Any(t => t.FilePath == f))
                    {
                        _tasks.Add(new ConvertTaskItem { FileName = Path.GetFileName(f), FilePath = f });
                    }
                }
                MainWindow.Current.ShowToast($"已从文件夹导入 {ncmFiles.Length} 个文件", true);
            }
        }

        // ==========================================
        // 核心改造：基于 Parallel.ForEachAsync 的多线程转换
        // ==========================================
        private async void StartConvertBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_tasks.Count == 0)
            {
                MainWindow.Current.ShowToast("请先添加需要转换的 NCM 文件！", false);
                return;
            }

            // 如果正在转换，则将此按钮作为“全部取消”按钮使用
            if (_isConverting)
            {
                _globalCts?.Cancel();
                foreach (var t in _tasks) t.Cancel();
                return;
            }

            _isConverting = true;
            StartConvertBtn.Content = "全部取消 (双击中止)";
            ConvertProgressBar.Maximum = _tasks.Count;
            ConvertProgressBar.Value = 0;
            _globalCts = new CancellationTokenSource();

            int successCount = 0;
            string baseDir = Path.GetDirectoryName(_tasks[0].FilePath);

            // 1. 读取输出策略设置
            string outputStrategy = ApplicationData.Current.LocalSettings.Values["OutputStrategy"] as string ?? "Subfolder";
            if (outputStrategy == "SameFolder")
            {
                _lastOutputDirectory = baseDir; // 直接输出到源目录（覆盖混合）
            }
            else
            {
                _lastOutputDirectory = Path.Combine(baseDir, "transferred"); // 新建 transferred 文件夹
            }

            if (!Directory.Exists(_lastOutputDirectory))
            {
                Directory.CreateDirectory(_lastOutputDirectory);
            }

            // 2. 读取并发数设置（默认给2）
            int maxConcurrency = ApplicationData.Current.LocalSettings.Values["MaxConcurrency"] as int? ?? 2;

            // 配置并发参数：由用户滑块动态接管
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = maxConcurrency,
                CancellationToken = _globalCts.Token
            };

            try
            {
                await Parallel.ForEachAsync(_tasks, options, async (task, token) =>
                {
                    // 如果任务已经被手动取消，或者触发了全局取消，直接跳过
                    if (task.Cts.IsCancellationRequested || token.IsCancellationRequested) return;

                    // 避免重复转换已经完成的任务
                    if (task.Status == "完成" || task.Status == "已取消") return;

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        task.Status = "转换中...";
                    });

                    bool isSuccess = false;

                    // 将底层的同步黑盒方法包装在 Task.Run 中，避免阻塞主线程
                    await Task.Run(() =>
                    {
                        try
                        {
                            token.ThrowIfCancellationRequested();
                            task.Cts.Token.ThrowIfCancellationRequested();

                            var crypt = new NeteaseCrypt(task.FilePath);
                            int result = crypt.Dump(_lastOutputDirectory);
                            crypt.FixMetadata();
                            crypt.Destroy();
                            isSuccess = (result == 0);
                        }
                        catch { isSuccess = false; }
                    }, task.Cts.Token);

                    // 转换结束，切回 UI 线程更新状态
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (task.Cts.IsCancellationRequested || token.IsCancellationRequested)
                        {
                            task.Status = "已取消";
                        }
                        else if (isSuccess)
                        {
                            task.Status = "完成";
                            task.Progress = 100;
                            successCount++;
                            ConvertProgressBar.Value++;
                        }
                        else
                        {
                            task.Status = "失败";
                        }
                        task.IsActionable = false; // 转换结束，禁用取消按钮
                    });
                });
            }
            catch (OperationCanceledException)
            {
                // 捕获全局取消抛出的异常
            }
            catch (Exception ex)
            {
                MainWindow.Current.ShowToast("转换过程发生异常", false, ex.Message);
            }
            finally
            {
                // 收尾工作
                _isConverting = false;
                StartConvertBtn.Content = "开始极速转换";

                // 如果全部成功则弹绿窗，有失败/取消则弹红窗
                MainWindow.Current.ShowToast($"处理结束！成功 {successCount} 个，共 {_tasks.Count} 个。", successCount == _tasks.Count);
            }
        }

        // ==========================================
        // 新增：单任务取消交互逻辑
        // ==========================================
        private void CancelTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ConvertTaskItem task)
            {
                task.Cancel();
            }
        }

        private void OpenFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(_lastOutputDirectory) && Directory.Exists(_lastOutputDirectory))
                {
                    System.Diagnostics.Process.Start("explorer.exe", _lastOutputDirectory);
                    return;
                }

                if (_tasks.Count > 0)
                {
                    string baseDir = Path.GetDirectoryName(_tasks[0].FilePath);
                    string targetDir = Path.Combine(baseDir, "transferred");

                    if (Directory.Exists(targetDir))
                    {
                        System.Diagnostics.Process.Start("explorer.exe", targetDir);
                    }
                    else if (Directory.Exists(baseDir))
                    {
                        System.Diagnostics.Process.Start("explorer.exe", baseDir);
                    }
                }
                else
                {
                    MainWindow.Current.ShowToast("列表为空，尚未确立输出目录！", false);
                }
            }
            catch { }
        }

        private void Grid_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "松开鼠标添加文件/文件夹";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
        }

        private async void Grid_Drop(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    if (item is StorageFile file && file.FileType.ToLower() == ".ncm")
                    {
                        if (!_tasks.Any(t => t.FilePath == file.Path))
                        {
                            _tasks.Add(new ConvertTaskItem { FileName = file.Name, FilePath = file.Path });
                        }
                    }
                    else if (item is StorageFolder folder)
                    {
                        var ncmFiles = await Task.Run(() =>
                        {
                            try { return Directory.GetFiles(folder.Path, "*.ncm", SearchOption.TopDirectoryOnly); }
                            catch { return Array.Empty<string>(); }
                        });
                        foreach (var f in ncmFiles)
                        {
                            if (!_tasks.Any(t => t.FilePath == f))
                            {
                                _tasks.Add(new ConvertTaskItem { FileName = Path.GetFileName(f), FilePath = f });
                            }
                        }
                    }
                }
            }
        }

        public void ClearLists()
        {
            // 清空前先尝试掐断正在运行的任务
            _globalCts?.Cancel();
            foreach (var t in _tasks) t.Cancel();

            _tasks.Clear();
            ConvertProgressBar.Value = 0;
            StartConvertBtn.Content = "开始极速转换";
            _lastOutputDirectory = "";
            _isConverting = false;
        }
    }
}