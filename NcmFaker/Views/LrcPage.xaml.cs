using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Windows.Storage;

namespace NcmFaker.Views
{
    // ==========================================
    // 任务数据模型 (与转换页类似，拥有独立令牌)
    // ==========================================
    public class LrcTaskItem : INotifyPropertyChanged
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

    public sealed partial class LrcPage : Page
    {
        private ObservableCollection<LrcTaskItem> _tasks = new ObservableCollection<LrcTaskItem>();
        private string _sourceDirectory = "";
        private static readonly HttpClient _httpClient = new HttpClient();

        // 状态控制
        private bool _isMatching = false;
        private CancellationTokenSource _globalCts;

        public LrcPage()
        {
            this.InitializeComponent();

            // 核心：开启页面缓存，确保切换页面不丢失进度
            this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;

            TaskListView.ItemsSource = _tasks;

            // 伪装请求头
            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                _httpClient.DefaultRequestHeaders.Add("Referer", "http://music.163.com/");
            }
        }

        private async void SetSourceBtn_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Current);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                _sourceDirectory = folder.Path;
                await ScanAudioFilesAsync(_sourceDirectory);
            }
        }

        private async Task ScanAudioFilesAsync(string directory)
        {
            _tasks.Clear();
            var audioFiles = await Task.Run(() =>
            {
                try
                {
                    var mp3s = Directory.GetFiles(directory, "*.mp3", SearchOption.TopDirectoryOnly);
                    var flacs = Directory.GetFiles(directory, "*.flac", SearchOption.TopDirectoryOnly);
                    return mp3s.Concat(flacs).ToArray();
                }
                catch { return Array.Empty<string>(); }
            });

            foreach (var f in audioFiles)
            {
                _tasks.Add(new LrcTaskItem { FileName = Path.GetFileName(f), FilePath = f });
            }

            MainWindow.Current.ShowToast($"扫描完成！共找到 {audioFiles.Length} 首歌曲。", true);
        }

        private async void StartMatchBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_tasks.Count == 0)
            {
                MainWindow.Current.ShowToast("没有找到可匹配的歌曲文件！", false);
                return;
            }

            // 全部取消逻辑
            if (_isMatching)
            {
                _globalCts?.Cancel();
                foreach (var t in _tasks) t.Cancel();
                return;
            }

            _isMatching = true;
            StartMatchBtn.Content = "全部取消 (双击中止)";
            MatchProgressBar.Maximum = _tasks.Count;
            MatchProgressBar.Value = 0;
            _globalCts = new CancellationTokenSource();

            int successCount = 0;

            // 读取界面选项
            bool useRegexClean = RegexCleanCheck.IsChecked == true;
            bool mergeTrans = MergeTransCheck.IsChecked == true;
            bool mergeRoma = MergeRomaCheck.IsChecked == true;
            bool saveLrcFile = SaveLrcFileCheck.IsChecked == true;
            bool embedLrc = EmbedLrcCheck.IsChecked == true;
            bool embedCover = EmbedCoverCheck.IsChecked == true;

            // 读取并发数设置（默认给2）
            int maxConcurrency = ApplicationData.Current.LocalSettings.Values["MaxConcurrency"] as int? ?? 2;

            // 并发参数：由用户滑块动态接管
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = maxConcurrency,
                CancellationToken = _globalCts.Token
            };


            // 封装带取消令牌的网络请求
            async Task<JsonElement?> TrySearchNeteaseAsync(string kw, CancellationToken token)
            {
                var searchContent = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("s", kw),
                    new KeyValuePair<string, string>("type", "1"),
                    new KeyValuePair<string, string>("offset", "0"),
                    new KeyValuePair<string, string>("limit", "1")
                });
                var resp = await _httpClient.PostAsync("http://music.163.com/api/search/get/", searchContent, token);
                resp.EnsureSuccessStatusCode();
                var jsonStr = await resp.Content.ReadAsStringAsync(token);
                var json = JsonDocument.Parse(jsonStr);

                if (json.RootElement.TryGetProperty("result", out var resultElement) &&
                    resultElement.TryGetProperty("songs", out var songs) &&
                    songs.GetArrayLength() > 0)
                {
                    return songs[0];
                }
                return null;
            }

            try
            {
                await Parallel.ForEachAsync(_tasks, options, async (task, globalToken) =>
                {
                    if (task.Cts.IsCancellationRequested || globalToken.IsCancellationRequested) return;
                    if (task.Status == "完成" || task.Status == "已取消") return;

                    // 组合令牌：任一触发即取消
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(task.Cts.Token, globalToken);
                    var token = linkedCts.Token;

                    DispatcherQueue.TryEnqueue(() => task.Status = "解析标签...");

                    bool isSuccess = false;
                    string errorMsg = "";

                    try
                    {
                        token.ThrowIfCancellationRequested();

                        // 1. 读取本地音乐标签
                        using var file = TagLib.File.Create(task.FilePath);
                        string title = file.Tag.Title;
                        string artist = file.Tag.FirstPerformer;

                        string originalKeyword = $"{title} {artist}".Trim();
                        if (string.IsNullOrWhiteSpace(originalKeyword))
                        {
                            originalKeyword = Path.GetFileNameWithoutExtension(task.FilePath);
                        }

                        DispatcherQueue.TryEnqueue(() => task.Status = "搜索匹配中...");

                        // 2. 双重搜索策略
                        JsonElement? matchedSong = await TrySearchNeteaseAsync(originalKeyword, token);

                        if (matchedSong == null && useRegexClean)
                        {
                            string cleanKeyword = System.Text.RegularExpressions.Regex.Replace(originalKeyword, @"[\[\(【（].*?[\]\)】）]", "");
                            cleanKeyword = System.Text.RegularExpressions.Regex.Replace(cleanKeyword, @"(?i)(feat\.|remix|cover|live|伴奏|无损|高音质|动态翻译)", "");
                            cleanKeyword = System.Text.RegularExpressions.Regex.Replace(cleanKeyword, @"\s+", " ").Trim();

                            if (!string.IsNullOrWhiteSpace(cleanKeyword) && cleanKeyword != originalKeyword)
                            {
                                matchedSong = await TrySearchNeteaseAsync(cleanKeyword, token);
                            }
                        }

                        if (matchedSong != null)
                        {
                            long songId = matchedSong.Value.GetProperty("id").GetInt64();
                            bool fileModified = false;

                            DispatcherQueue.TryEnqueue(() => task.Status = "下载数据...");

                            // 3. 下载封面
                            if (embedCover)
                            {
                                string detailUrl = $"http://music.163.com/api/song/detail/?id={songId}&ids=[{songId}]";
                                var detailJsonStr = await _httpClient.GetStringAsync(detailUrl, token);
                                var detailJson = JsonDocument.Parse(detailJsonStr);
                                if (detailJson.RootElement.TryGetProperty("songs", out var dSongs) && dSongs.GetArrayLength() > 0)
                                {
                                    if (dSongs[0].GetProperty("album").TryGetProperty("picUrl", out var picUrlElement))
                                    {
                                        string picUrl = picUrlElement.GetString();
                                        if (!string.IsNullOrEmpty(picUrl))
                                        {
                                            byte[] imageBytes = await _httpClient.GetByteArrayAsync(picUrl, token);
                                            file.Tag.Pictures = new TagLib.IPicture[] { new TagLib.Picture(new TagLib.ByteVector(imageBytes)) };
                                            fileModified = true;
                                        }
                                    }
                                }
                            }

                            // 4. 下载并合并歌词
                            if (saveLrcFile || embedLrc)
                            {
                                string lyricUrl = $"http://music.163.com/api/song/lyric?id={songId}&lv=1&tv=-1&rv=-1";
                                var lyricJsonStr = await _httpClient.GetStringAsync(lyricUrl, token);
                                var lyricJson = JsonDocument.Parse(lyricJsonStr);

                                string originalLrc = lyricJson.RootElement.TryGetProperty("lrc", out var l) && l.TryGetProperty("lyric", out var ll) ? ll.GetString() : "";
                                string transLrc = lyricJson.RootElement.TryGetProperty("tlyric", out var t) && t.TryGetProperty("lyric", out var tl) ? tl.GetString() : "";
                                string romaLrc = lyricJson.RootElement.TryGetProperty("romalrc", out var r) && r.TryGetProperty("lyric", out var rl) ? rl.GetString() : "";

                                string finalLrcText = originalLrc;
                                bool hasValidTrans = mergeTrans && !string.IsNullOrWhiteSpace(transLrc);
                                bool hasValidRoma = mergeRoma && !string.IsNullOrWhiteSpace(romaLrc);

                                if (hasValidTrans || hasValidRoma)
                                {
                                    var timeDict = new SortedDictionary<TimeSpan, List<string>>();

                                    void ParseToDict(string lrcStr)
                                    {
                                        if (string.IsNullOrWhiteSpace(lrcStr)) return;
                                        var lines = lrcStr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                                        var regex = new System.Text.RegularExpressions.Regex(@"\[(\d{2}):(\d{2})\.(\d{2,3})\](.*)");

                                        foreach (var line in lines)
                                        {
                                            var match = regex.Match(line);
                                            if (match.Success)
                                            {
                                                int m = int.Parse(match.Groups[1].Value);
                                                int s = int.Parse(match.Groups[2].Value);
                                                string msStr = match.Groups[3].Value;
                                                int ms = msStr.Length == 2 ? int.Parse(msStr) * 10 : int.Parse(msStr);

                                                var ts = new TimeSpan(0, 0, m, s, ms);
                                                string text = match.Groups[4].Value.Trim();

                                                if (string.IsNullOrWhiteSpace(text)) continue;

                                                if (!timeDict.ContainsKey(ts)) timeDict[ts] = new List<string>();
                                                if (!timeDict[ts].Contains(text)) timeDict[ts].Add(text);
                                            }
                                        }
                                    }

                                    ParseToDict(originalLrc);
                                    if (hasValidRoma) ParseToDict(romaLrc);
                                    if (hasValidTrans) ParseToDict(transLrc);

                                    var sb = new System.Text.StringBuilder();
                                    foreach (var kvp in timeDict)
                                    {
                                        string timeTag = $"[{kvp.Key.Minutes:D2}:{kvp.Key.Seconds:D2}.{kvp.Key.Milliseconds:D3}]";
                                        foreach (var textLine in kvp.Value)
                                        {
                                            sb.AppendLine($"{timeTag}{textLine}");
                                        }
                                    }
                                    finalLrcText = sb.ToString();
                                }

                                if (!string.IsNullOrWhiteSpace(finalLrcText))
                                {
                                    if (saveLrcFile)
                                    {
                                        string lrcPath = Path.ChangeExtension(task.FilePath, ".lrc");
                                        await System.IO.File.WriteAllTextAsync(lrcPath, finalLrcText, token);
                                    }
                                    if (embedLrc)
                                    {
                                        file.Tag.Lyrics = finalLrcText;
                                        fileModified = true;
                                    }
                                }
                            }

                            if (fileModified) file.Save();
                            isSuccess = true;
                        }
                        else
                        {
                            errorMsg = "未找到对应歌曲";
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // 被取消时抛出此异常，交由 finally 处理状态
                    }
                    catch (Exception ex)
                    {
                        errorMsg = ex.Message;
                    }

                    // 5. 更新单任务状态
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (task.Cts.IsCancellationRequested || globalToken.IsCancellationRequested)
                        {
                            task.Status = "已取消";
                        }
                        else if (isSuccess)
                        {
                            task.Status = "完成";
                            task.Progress = 100;
                            successCount++;
                            MatchProgressBar.Value++;
                        }
                        else
                        {
                            task.Status = "匹配失败";
                            // 如果你在 MainWindow 加了铃铛，可以把 errorMsg 传给铃铛
                            // MainWindow.Current.ShowToast($"匹配失败: {task.FileName}", false, errorMsg);
                        }
                        task.IsActionable = false;
                    });
                });
            }
            catch (OperationCanceledException)
            {
                // 全局取消
            }
            finally
            {
                _isMatching = false;
                StartMatchBtn.Content = "开始智能匹配";
                MainWindow.Current.ShowToast($"操作结束！成功匹配 {successCount} 首，共 {_tasks.Count} 首。", successCount == _tasks.Count);
            }
        }

        private void CancelTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is LrcTaskItem task)
            {
                task.Cancel();
            }
        }

        private void OpenFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(_sourceDirectory) && Directory.Exists(_sourceDirectory))
                {
                    System.Diagnostics.Process.Start("explorer.exe", _sourceDirectory);
                }
                else
                {
                    MainWindow.Current.ShowToast("请先选择或拖入音频文件夹！", false);
                }
            }
            catch { }
        }

        private void Grid_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "松开鼠标扫描音频目录";
        }

        private async void Grid_Drop(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count > 0 && items[0] is StorageFolder folder)
                {
                    _sourceDirectory = folder.Path;
                    await ScanAudioFilesAsync(_sourceDirectory);
                }
            }
        }

        public void ClearLists()
        {
            _globalCts?.Cancel();
            foreach (var t in _tasks) t.Cancel();

            _tasks.Clear();
            _sourceDirectory = "";
            MatchProgressBar.Value = 0;
            StartMatchBtn.Content = "开始智能匹配";
            _isMatching = false;
        }
    }
}