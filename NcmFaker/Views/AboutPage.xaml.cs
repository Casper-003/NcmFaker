using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NcmFaker.Views
{
    public sealed partial class AboutPage : Page
    {
        public AboutPage()
        {
            this.InitializeComponent();
        }

        // ==========================================
        // 唤起系统邮件客户端并预填信息
        // ==========================================
        private async void EmailButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 设置收件人
                string emailAddress = "casper-003@outlook.com";
                // 设置默认的邮件主题，方便你一眼认出是来自软件的反馈
                string subject = Uri.EscapeDataString("[NcmFaker 反馈] Bug报告与建议");

                string uriString = $"mailto:{emailAddress}?subject={subject}";
                var uri = new Uri(uriString);

                // 调用系统默认程序打开 Mailto 链接
                await Windows.System.Launcher.LaunchUriAsync(uri);
            }
            catch
            {
                // 如果用户电脑没装邮件客户端，可以在这里弹个 Toast 提示用户手动复制
                MainWindow.Current.ShowToast("无法唤起邮件客户端，请手动复制邮箱地址。", false);
            }
        }
    }
}