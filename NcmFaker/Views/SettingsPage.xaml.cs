using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;

namespace NcmFaker.Views
{
    public sealed partial class SettingsPage : Page
    {
        // 核心：获取应用原生的本地设置容器
        private ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;

        // 用于防止页面初始化加载时误触发保存逻辑
        private bool isInitializing = true;

        public SettingsPage()
        {
            this.InitializeComponent();
            LoadSettings();
            isInitializing = false;
        }

        // ==========================================
        // 加载已保存的配置
        // ==========================================
        private void LoadSettings()
        {
            // 1. 加载主题
            string theme = localSettings.Values["AppTheme"] as string ?? "Default";
            foreach (var item in ThemeRadioButtons.Items)
            {
                if (item is RadioButton rb && rb.Tag.ToString() == theme)
                {
                    ThemeRadioButtons.SelectedItem = rb;
                    break;
                }
            }

            // 2. 加载并发数 (默认给 2)
            if (localSettings.Values["MaxConcurrency"] is int concurrency)
            {
                ConcurrencySlider.Value = concurrency;
            }
            else
            {
                localSettings.Values["MaxConcurrency"] = 2;
                ConcurrencySlider.Value = 2;
            }

            // 3. 加载输出策略
            string output = localSettings.Values["OutputStrategy"] as string ?? "Subfolder";
            foreach (var item in OutputRadioButtons.Items)
            {
                if (item is RadioButton rb && rb.Tag.ToString() == output)
                {
                    OutputRadioButtons.SelectedItem = rb;
                    break;
                }
            }
        }

        // ==========================================
        // 动态切换主题与保存
        // ==========================================
        private void ThemeRadioButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isInitializing) return;

            if (ThemeRadioButtons.SelectedItem is RadioButton rb)
            {
                string themeTag = rb.Tag.ToString();
                localSettings.Values["AppTheme"] = themeTag;

                // 即时生效：通过修改 MainWindow 内容的 RequestedTheme 属性来切换当前窗口的主题
                if (MainWindow.Current.Content is FrameworkElement rootElement)
                {
                    rootElement.RequestedTheme = themeTag switch
                    {
                        "Light" => ElementTheme.Light,
                        "Dark" => ElementTheme.Dark,
                        _ => ElementTheme.Default,
                    };
                }
            }
        }

        // ==========================================
        // 保存并发数
        // ==========================================
        private void ConcurrencySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (isInitializing) return;
            localSettings.Values["MaxConcurrency"] = (int)e.NewValue;
        }

        // ==========================================
        // 保存输出策略
        // ==========================================
        private void OutputRadioButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isInitializing) return;

            if (OutputRadioButtons.SelectedItem is RadioButton rb)
            {
                localSettings.Values["OutputStrategy"] = rb.Tag.ToString();
            }
        }
    }
}