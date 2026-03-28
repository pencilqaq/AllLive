using AllLive.UWP.Helper;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace AllLive.UWP.Controls
{
    public sealed partial class DouyinLoginDialog : ContentDialog
    {
        private const string CHROME_UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";
        public bool LoginSuccess { get; private set; } = false;

        public DouyinLoginDialog()
        {
            this.InitializeComponent();
            this.Loaded += DouyinLoginDialog_Loaded;
        }

        private async void DouyinLoginDialog_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                txtStatus.Text = "正在初始化 WebView2...";
                await webView.EnsureCoreWebView2Async();
                webView.CoreWebView2.Settings.UserAgent = CHROME_UA;
                webView.NavigationCompleted += WebView_NavigationCompleted;
                webView.CoreWebView2.Navigate("https://www.douyin.com/passport/general/login_guiding_strategy/?aid=6383");
            }
            catch (Exception ex)
            {
                LogHelper.Log("WebView2初始化失败", LogType.ERROR, ex);
                txtStatus.Text = "WebView2 初始化失败，请确保已安装 Edge WebView2 Runtime\n" + ex.Message;
                txtStatus.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Red);
            }
        }

        private void WebView_NavigationCompleted(Microsoft.UI.Xaml.Controls.WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess)
            {
                txtStatus.Text = "请登录抖音账号，登录成功后点击「完成登录」";
            }
            else
            {
                txtStatus.Text = $"页面加载失败({args.WebErrorStatus})，请重试";
            }
        }

        private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            args.Cancel = true;
            try
            {
                if (webView.CoreWebView2 == null)
                {
                    txtStatus.Text = "WebView2 未初始化";
                    return;
                }

                var cookies = await webView.CoreWebView2.CookieManager.GetCookiesAsync("https://www.douyin.com");

                var cookieParts = new List<string>();
                foreach (var cookie in cookies)
                {
                    cookieParts.Add($"{cookie.Name}={cookie.Value}");
                }

                if (cookieParts.Count == 0)
                {
                    txtStatus.Text = "未检测到Cookie，请先登录";
                    return;
                }

                var cookieStr = string.Join(";", cookieParts);

                // 检查是否包含关键cookie（登录后才有的）
                bool hasSessionId = cookieParts.Any(c =>
                    c.StartsWith("sessionid") ||
                    c.StartsWith("sid_tt") ||
                    c.StartsWith("sid_guard"));

                if (!hasSessionId)
                {
                    txtStatus.Text = "似乎还未登录成功，请确认已登录后再点击完成";
                    return;
                }

                DouyinAccount.Instance.SetCookie(cookieStr);
                LoginSuccess = true;
                Utils.ShowMessageToast("抖音登录成功");
                this.Hide();
            }
            catch (Exception ex)
            {
                LogHelper.Log("获取抖音Cookie失败", LogType.ERROR, ex);
                txtStatus.Text = "获取Cookie失败: " + ex.Message;
            }
        }

        private void ContentDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
        }
    }
}
