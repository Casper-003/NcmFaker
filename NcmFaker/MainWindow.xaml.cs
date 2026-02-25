using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace NcmFaker
{
    public sealed partial class MainWindow : Window
    {
        public static new MainWindow Current { get; private set; }

        // ==========================================
        // 新增：通知中心的数据集合，UI会自动追踪它的变化
        // ==========================================
        public ObservableCollection<LogItem> SystemLogs { get; } = new ObservableCollection<LogItem>();

        public MainWindow()
        {
            this.InitializeComponent();
            Current = this;
            this.Title = "NcmFaker";

            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);

            _newWndProc = new WindowProc(NewWindowProc);
            _oldWndProc = SetWindowLongPtr(hWnd, GWL_WNDPROC, _newWndProc);

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            MainNav.SelectedItem = MainNav.MenuItems[0];
        }

        // ==========================================
        // 1. 读秒卡片 + 通知中心静默记录
        // ==========================================
        // 新增了可选参数 details
        public void ShowToast(string message, bool isSuccess, string details = "")
        {
            // --- 核心新增：将信息记录到通知中心 ---
            this.DispatcherQueue?.TryEnqueue(() =>
            {
                var color = isSuccess ? new SolidColorBrush(Colors.LimeGreen) : new SolidColorBrush(Colors.Red);

                SystemLogs.Insert(0, new LogItem
                {
                    Time = DateTime.Now.ToString("HH:mm:ss"),
                    Message = message,
                    Details = details,
                    MessageColor = color
                });

                // 如果通知气泡没打开，就亮起小红点提示用户
                if (!NotifFlyout.IsOpen)
                {
                    UnreadBadge.Visibility = Visibility.Visible;
                }
            });

            // --- 弹窗 UI 逻辑 ---
            try
            {
                var bgBrush = Application.Current.Resources["AcrylicBackgroundFillColorDefaultBrush"] as Brush
                              ?? new SolidColorBrush(Windows.UI.Color.FromArgb(200, 30, 30, 30));
                var borderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush
                                  ?? new SolidColorBrush(Colors.Gray);

                var toastBorder = new Border
                {
                    Background = bgBrush,
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(0, 0, 0, 0),
                    Shadow = new ThemeShadow(),
                    MinWidth = 280,
                    HorizontalAlignment = HorizontalAlignment.Right
                };

                var mainGrid = new Grid();
                mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var contentGrid = new Grid
                {
                    Padding = new Thickness(20, 15, 10, 15),
                    ColumnSpacing = 12
                };
                contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var icon = new FontIcon
                {
                    Glyph = isSuccess ? "\uE10B" : "\uEA39",
                    Foreground = new SolidColorBrush(isSuccess ? Colors.LimeGreen : Colors.Red),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(icon, 0);

                var text = new TextBlock
                {
                    Text = message,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 700
                };
                Grid.SetColumn(text, 1);

                var closeButton = new Button
                {
                    Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(6),
                    Margin = new Thickness(0, -5, 0, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    CornerRadius = new CornerRadius(4)
                };
                Grid.SetColumn(closeButton, 2);

                contentGrid.Children.Add(icon);
                contentGrid.Children.Add(text);
                contentGrid.Children.Add(closeButton);
                Grid.SetRow(contentGrid, 0);
                mainGrid.Children.Add(contentGrid);

                var progressBar = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = 100,
                    Height = 4,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    CornerRadius = new CornerRadius(0, 0, 8, 8),
                    Foreground = new SolidColorBrush(isSuccess ? Colors.LimeGreen : Colors.Red)
                };
                Grid.SetRow(progressBar, 1);
                mainGrid.Children.Add(progressBar);

                toastBorder.Child = mainGrid;

                bool isClosed = false;

                Action closeToastAction = () =>
                {
                    if (!isClosed)
                    {
                        isClosed = true;
                        ToastContainer.Children.Remove(toastBorder);
                    }
                };

                closeButton.Click += (s, e) => closeToastAction();

                this.DispatcherQueue?.TryEnqueue(() =>
                {
                    if (ToastContainer != null)
                    {
                        ToastContainer.Children.Insert(0, toastBorder);

                        _ = Task.Run(async () =>
                        {
                            int delayMs = 50;
                            int steps = 5000 / delayMs;
                            for (int i = 0; i <= steps; i++)
                            {
                                if (isClosed) break;

                                await Task.Delay(delayMs);
                                this.DispatcherQueue?.TryEnqueue(() =>
                                {
                                    if (!isClosed) progressBar.Value = 100 - ((double)i / steps * 100);
                                });
                            }

                            this.DispatcherQueue?.TryEnqueue(() => closeToastAction());
                        });
                    }
                });
            }
            catch
            {
            }
        }

        // ==========================================
        // 新增：通知铃铛的交互逻辑
        // ==========================================
        private void NotifFlyout_Opened(object sender, object e)
        {
            // 气泡打开时，清理小红点
            UnreadBadge.Visibility = Visibility.Collapsed;
        }

        private void ClearLogs_Click(object sender, RoutedEventArgs e)
        {
            SystemLogs.Clear();
            NotifFlyout.Hide();
        }

        // ==========================================
        // 2. 刷新按钮逻辑
        // ==========================================
        private void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            if (ContentFrame.Content is Views.FileConvertPage filePage)
            {
                filePage.ClearLists();
            }
            else if (ContentFrame.Content is Views.LrcPage lrcPage)
            {
                lrcPage.ClearLists();
            }
        }

        // ==========================================
        // 3. 左侧导航栏切换逻辑
        // ==========================================
        private void MainNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            var navItemTag = args.SelectedItemContainer.Tag?.ToString();

            if (args.IsSettingsSelected || navItemTag == "Settings")
            {
                ContentFrame.Navigate(typeof(Views.SettingsPage));
                return;
            }

            switch (navItemTag)
            {
                case "FileConvert":
                    ContentFrame.Navigate(typeof(Views.FileConvertPage));
                    break;
                case "LrcMatch":
                    ContentFrame.Navigate(typeof(Views.LrcPage));
                    break;
                case "About":
                    ContentFrame.Navigate(typeof(Views.AboutPage));
                    break;
            }
        }

        // ==========================================
        // 4. Win32 最小尺寸拦截逻辑
        // ==========================================
        private delegate IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private WindowProc _newWndProc;
        private IntPtr _oldWndProc;
        private const int GWL_WNDPROC = -4;
        private const uint WM_GETMINMAXINFO = 0x0024;

        [StructLayout(LayoutKind.Sequential)]
        struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int x, y; }

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, WindowProc dwNewLong);
        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private IntPtr NewWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                MINMAXINFO mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                mmi.ptMinTrackSize.x = 800;
                mmi.ptMinTrackSize.y = 600;
                Marshal.StructureToPtr(mmi, lParam, true);
            }
            return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
        }
    }

    // ==========================================
    // 5. 日志数据模型
    // ==========================================
    public class LogItem
    {
        public string Time { get; set; }
        public string Message { get; set; }
        public string Details { get; set; }
        public Brush MessageColor { get; set; }
        public Visibility DetailsVisibility => string.IsNullOrWhiteSpace(Details) ? Visibility.Collapsed : Visibility.Visible;
    }
}