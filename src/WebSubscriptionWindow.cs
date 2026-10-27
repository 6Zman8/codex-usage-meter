using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace CodexUsageMeter
{
    internal static class WebSubscriptionService
    {
        private static readonly HashSet<string> Pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        internal static void QueueRefresh(string root, string account, string email, string plan)
        {
            Application application = Application.Current;
            if (application == null || application.Dispatcher.HasShutdownStarted ||
                !WebSubscriptionStore.IsDue(WebSubscriptionStore.Load(root, account, null), DateTime.UtcNow, plan)) return;
            lock (Pending) { if (!Pending.Add(root)) return; }
            application.Dispatcher.BeginInvoke(new Action(async delegate {
                await Gate.WaitAsync();
                try
                {
                    if (WebSubscriptionStore.AccountId(root, email) != account ||
                        !WebSubscriptionStore.IsDue(WebSubscriptionStore.Load(root, account, null), DateTime.UtcNow, plan)) return;
                    using (WebSubscriptionWindow window = new WebSubscriptionWindow(root, account, email, plan, false)) await window.RunAsync(null);
                }
                catch { WebSubscriptionStore.Failed(root, account, plan, "웹 로그인 또는 연결 상태를 확인해 주세요.", DateTime.UtcNow); }
                finally { Gate.Release(); lock (Pending) Pending.Remove(root); }
            }));
        }

        internal static async Task<bool> ConnectAsync(Window owner, string root, string email, string plan)
        {
            string account = WebSubscriptionStore.AccountId(root, email);
            if (String.IsNullOrWhiteSpace(account)) throw new InvalidOperationException("먼저 미터기 계정의 연결 상태를 새로고침해 주세요.");
            lock (Pending) { if (!Pending.Add(root)) throw new InvalidOperationException("이 계정의 웹 구독 정보를 확인 중입니다. 잠시 후 다시 눌러 주세요."); }
            await Gate.WaitAsync();
            try { using (WebSubscriptionWindow window = new WebSubscriptionWindow(root, account, email, plan, true)) return await window.RunAsync(owner); }
            finally { Gate.Release(); lock (Pending) Pending.Remove(root); }
        }
    }

    internal sealed class WebSubscriptionWindow : IDisposable
    {
        private readonly string _root, _account, _email, _plan;
        private readonly bool _interactive;
        private readonly Window _window;
        private readonly Border _browserArea;
        private readonly TextBlock _status;
        private readonly TaskCompletionSource<bool> _done = new TaskCompletionSource<bool>();
        private CoreWebView2Controller _controller;
        private DispatcherTimer _timeout;
        private bool _disposed, _accepting;
        private static bool _loaderConfigured;
        internal Action<CoreWebView2Environment, CoreWebView2> ConfigureForTest;

        internal WebSubscriptionWindow(string root, string account, string email, string plan, bool interactive)
        {
            _root = root; _account = account; _email = email; _plan = plan; _interactive = interactive;
            _window = new Window { Title = "웹 구독 연결 · Codex 미터기", Width = 900, Height = 680, MinWidth = 640, MinHeight = 480,
                ShowInTaskbar = interactive, ShowActivated = interactive, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)), Foreground = Brushes.White };
            DarkTheme.Apply(_window);
            Grid panel = new Grid();
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            DockPanel header = new DockPanel { Margin = new Thickness(16, 12, 16, 12) };
            Button close = new Button { Content = "닫기", MinWidth = 62, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(12, 0, 0, 0) };
            close.Click += delegate { _window.Close(); }; DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
            _status = new TextBlock { Text = email + " 계정으로 웹 ChatGPT에 로그인해 주세요.\n구독 정보를 확인하면 자동으로 닫힙니다.", TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            header.Children.Add(_status); panel.Children.Add(header);
            _browserArea = new Border { Background = Brushes.Black }; Grid.SetRow(_browserArea, 1); panel.Children.Add(_browserArea);
            _window.Content = panel; _window.Closed += delegate { Complete(false); };
            _browserArea.SizeChanged += delegate { Resize(); };
        }

        internal async Task<bool> RunAsync(Window owner)
        {
            try
            {
                if (_interactive) { if (owner != null) _window.Owner = owner; _window.Show(); }
                IntPtr handle = new WindowInteropHelper(_window).EnsureHandle();
                if (!_loaderConfigured) { CoreWebView2Environment.SetLoaderDllFolderPath(WebViewRuntime.LoaderFolder()); _loaderConfigured = true; }
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_root, "web-subscription"));
                if (_disposed || _done.Task.IsCompleted) return false;
                _controller = await environment.CreateCoreWebView2ControllerAsync(handle);
                if (_disposed || _done.Task.IsCompleted) { _controller.Close(); return false; }
                CoreWebView2 web = _controller.CoreWebView2;
                web.Settings.IsPasswordAutosaveEnabled = false; web.Settings.IsGeneralAutofillEnabled = false;
                web.Settings.AreDevToolsEnabled = false; web.Settings.IsStatusBarEnabled = false;
                web.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
                web.PermissionRequested += delegate(object sender, CoreWebView2PermissionRequestedEventArgs e) { e.State = CoreWebView2PermissionState.Deny; };
                web.DownloadStarting += delegate(object sender, CoreWebView2DownloadStartingEventArgs e) { e.Cancel = true; };
                web.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e) {
                    e.Handled = true; Uri uri;
                    if (_interactive && Uri.TryCreate(e.Uri, UriKind.Absolute, out uri) && uri.Scheme == "https") web.Navigate(uri.AbsoluteUri);
                };
                web.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e) {
                    Uri uri; if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out uri) || uri.Scheme != "https") e.Cancel = true;
                };
                web.WebResourceResponseReceived += Receive;
                if (ConfigureForTest != null) ConfigureForTest(environment, web);
                _controller.IsVisible = _interactive; Resize();
                if (!_interactive)
                {
                    _timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
                    _timeout.Tick += delegate {
                        WebSubscriptionStore.Failed(_root, _account, _plan, "웹 로그인이 만료되었거나 응답을 받지 못했습니다. 날짜를 눌러 다시 연결해 주세요.", DateTime.UtcNow);
                        Complete(false);
                    };
                    _timeout.Start();
                }
                web.Navigate("https://chatgpt.com/");
                return await _done.Task;
            }
            catch (Exception)
            {
                if (_interactive) throw new InvalidOperationException("웹 구독 연결 창을 열지 못했습니다. Microsoft Edge WebView2 Runtime 설치 상태를 확인해 주세요.");
                WebSubscriptionStore.Failed(_root, _account, _plan, "웹 연결 창을 준비하지 못했습니다.", DateTime.UtcNow);
                return false;
            }
            finally { Dispose(); }
        }

        private async void Receive(object sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            if (_disposed || _accepting || _done.Task.IsCompleted || !IsAccountResponse(e.Request.Uri) || e.Response.StatusCode != 200) return;
            _accepting = true;
            try
            {
                string json;
                using (Stream stream = await e.Response.GetContentAsync())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    char[] buffer = new char[4096]; StringBuilder body = new StringBuilder(); int count;
                    while ((count = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    { if (body.Length + count > AccountSubscription.MaximumJsonLength) return; body.Append(buffer, 0, count); }
                    json = body.ToString();
                }
                if (_disposed || _done.Task.IsCompleted) return;
                AccountSubscriptionInfo value;
                if (WebSubscriptionStore.TryAccept(_root, _account, _email, _plan, json, DateTime.UtcNow, out value)) Complete(true);
                else if (_interactive) _status.Text = "이 웹 계정이 미터기의 " + _email + " 계정·플랜과 일치하지 않습니다.\n웹 프로필 메뉴에서 계정을 확인해 주세요. 미터기 정보는 변경하지 않았습니다.";
            }
            catch { if (_interactive) _status.Text = "구독 정보를 읽지 못했습니다. 웹 페이지의 로그인 상태를 확인해 주세요."; }
            finally { _accepting = false; }
        }

        internal static bool IsAccountResponse(string address)
        {
            Uri uri;
            return Uri.TryCreate(address, UriKind.Absolute, out uri) && uri.Scheme == "https" && uri.Host == "chatgpt.com" && uri.IsDefaultPort &&
                uri.AbsolutePath == "/backend-api/accounts/check/v4-2023-04-27";
        }

        private void Resize()
        {
            if (_controller == null || _disposed) return;
            if (!_interactive) { _controller.Bounds = new System.Drawing.Rectangle(0, 0, 900, 680); return; }
            PresentationSource source = PresentationSource.FromVisual(_window);
            if (source == null || source.CompositionTarget == null || !_browserArea.IsVisible) return;
            Matrix scale = source.CompositionTarget.TransformToDevice;
            Point origin = _browserArea.TranslatePoint(new Point(), _window);
            _controller.Bounds = new System.Drawing.Rectangle((int)(origin.X * scale.M11), (int)(origin.Y * scale.M22),
                Math.Max(1, (int)(_browserArea.ActualWidth * scale.M11)), Math.Max(1, (int)(_browserArea.ActualHeight * scale.M22)));
        }

        private void Complete(bool success) { if (_timeout != null) _timeout.Stop(); _done.TrySetResult(success); }
        internal void FinishProbe() { Complete(false); }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            Complete(false); if (_controller != null) { try { _controller.Close(); } catch (System.Runtime.InteropServices.COMException) { } _controller = null; }
            _window.Close();
        }
    }
}
