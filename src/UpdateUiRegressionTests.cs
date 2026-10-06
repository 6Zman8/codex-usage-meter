using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUsageMeter
{
    internal static class UpdateUiRegressionTests
    {
        public static void Run(Action<string> report, string previewDirectory)
        {
            string notes = String.Join("\n", Enumerable.Repeat(
                "Codex 재연결과 계정 전환 오류를 수정했습니다. 긴 변경사항도 버튼을 가리지 않아야 합니다.", 20));
            CheckLayout(430, 780, 1.5, notes, previewDirectory);
            CheckLayout(460, 780, 2.0, notes, previewDirectory);
            CheckLayout(320, 480, 2.0, notes, previewDirectory);
            CheckLayout(640, 360, 1.5, notes, previewDirectory);
            CheckLayout(320, 260, 1.0, notes, previewDirectory);
            report("PASS update modal actions stay inside the window at 100-200% font scale and compact sizes");
            CheckUtf8Response();
            report("PASS Korean release notes decode correctly from a UTF-8 HTTP response without charset");
        }

        private static void CheckLayout(int width, int height, double scale, string notes, string previews)
        {
            // Load the real embedded view without showing a window or opening user profiles.
            Window window = DashboardController.LoadWindow();
            Grid root = (Grid)window.Content;
            foreach (UIElement child in root.Children) child.Visibility = Visibility.Collapsed;
            Grid overlay = (Grid)window.FindName("ModalOverlay");
            overlay.Visibility = Visibility.Visible;
            ScaleFonts(overlay, scale);
            ((TextBlock)window.FindName("ModalTitle")).Text = "새 버전 사용 가능";
            ((TextBlock)window.FindName("ModalMessage")).Text =
                "현재 v1.0.4 → 최신 v1.0.7\n\n다운로드 후 프로그램을 종료하고 새 버전으로 다시 시작합니다.\n\n" + notes;
            Button primary = (Button)window.FindName("ModalPrimaryButton");
            Button secondary = (Button)window.FindName("ModalSecondaryButton");
            primary.Content = "다운로드 및 재시작";
            secondary.Content = "나중에";
            ScrollViewer scroll = window.FindName("ModalScrollViewer") as ScrollViewer;
            DashboardController controller = (DashboardController)FormatterServices.GetUninitializedObject(typeof(DashboardController));
            string[] modalFields = { "ModalOverlay", "ModalScrollViewer", "ModalTitle", "ModalMessage",
                "ModalCodePanel", "ModalCode", "ModalPrimaryButton", "ModalSecondaryButton" };
            foreach (string name in modalFields)
                typeof(DashboardController).GetField("_" + Char.ToLowerInvariant(name[0]) + name.Substring(1),
                    BindingFlags.NonPublic | BindingFlags.Instance).SetValue(controller, window.FindName(name));
            MethodInfo showModal = typeof(DashboardController).GetMethod("ShowModal", BindingFlags.NonPublic | BindingFlags.Instance);
            // Attach the view to a non-visible, non-activating native surface so WPF
            // can verify input hit testing without opening a window on the desktop.
            window.Content = null;
            root.Resources.MergedDictionaries.Add(window.Resources);
            root.SetValue(Control.FontFamilyProperty, window.FontFamily);
            root.SetValue(Control.ForegroundProperty, window.Foreground);
            root.SetValue(Control.FontSizeProperty, window.FontSize);
            HwndSourceParameters surfaceParameters = new HwndSourceParameters("CodexUsageMeter.UpdateUiTest");
            surfaceParameters.Width = width;
            surfaceParameters.Height = height;
            surfaceParameters.WindowStyle = unchecked((int)0x80000000); // WS_POPUP, without WS_VISIBLE
            surfaceParameters.ExtendedWindowStyle = 0x08000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            using (HwndSource surface = new HwndSource(surfaceParameters))
            {
            surface.RootVisual = root;
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
            foreach (Button button in new Button[] { primary, secondary })
            {
                Rect bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                Require(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= 0 && bounds.Top >= 0 &&
                    bounds.Right <= width + 0.5 && bounds.Bottom <= height + 0.5,
                    "Update button clipped at " + width + "x" + height + " scale=" + scale + ": " + bounds);
                Point center = button.TransformToAncestor(root).Transform(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                DependencyObject hit = root.InputHitTest(center) as DependencyObject;
                string hitDescription = hit == null ? "null" : hit.GetType().Name;
                while (hit != null && !Object.ReferenceEquals(hit, button)) hit = VisualTreeHelper.GetParent(hit);
                Require(hit != null, "Update button is not clickable at " + width + "x" + height +
                    ": bounds=" + bounds + ", root-visible=" + root.IsVisible + ", button-visible=" + button.IsVisible + ", hit=" + hitDescription);
            }
            Require(scroll != null && scroll.ScrollableHeight > 0, "Long content must scroll while actions remain visible.");
            scroll.ScrollToEnd();
            root.UpdateLayout();
            Require(scroll.VerticalOffset > 0, "The end of long notes must be reachable.");
            scroll.ScrollToHome();
            root.UpdateLayout();
            if (!String.IsNullOrWhiteSpace(previews))
            {
                Directory.CreateDirectory(previews);
                RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (FileStream file = File.Create(Path.Combine(previews, "update-" + width + "x" + height + ".png")))
                    encoder.Save(file);
            }
            scroll.ScrollToEnd();
            root.UpdateLayout();
            showModal.Invoke(controller, new object[] { "계정 연결", notes, "ABCD-EFGH", "코드 복사", null, null, null });
            root.UpdateLayout();
            Require(scroll.VerticalOffset == 0 && secondary.Visibility == Visibility.Collapsed,
                "A new modal must start at the top and support a single action.");
            scroll.ScrollToEnd();
            root.UpdateLayout();
            TextBlock code = (TextBlock)typeof(DashboardController).GetField("_modalCode", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(controller);
            Rect codeBounds = code.TransformToAncestor(scroll).TransformBounds(new Rect(code.RenderSize));
            Require(codeBounds.Top >= 0 && codeBounds.Bottom <= scroll.ActualHeight + 0.5,
                "The connection code must be reachable by scrolling.");
            AccountView chromeView = new AccountView {
                Client = (CodexRpcClient)FormatterServices.GetUninitializedObject(typeof(CodexRpcClient)),
                LastSnapshot = new AccountSnapshot { IsAuthenticated = true, Email = "chrome@example.invalid", PlanType = "plus" }
            };
            typeof(DashboardController).GetMethod("ConnectWebSubscription", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(controller, new object[] { chromeView });
            root.UpdateLayout();
            Require((string)primary.Content == "크롬 열기" && (string)secondary.Content == "스크립트 설치" && secondary.Visibility == Visibility.Visible, "Chrome connection actions missing.");
            foreach (Button button in new[] { primary, secondary })
            {
                Rect bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                Require(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= width + 0.5 && bounds.Bottom <= height + 0.5, "Chrome connection button clipped.");
            }
            if (!String.IsNullOrWhiteSpace(previews))
            {
                RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root); PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (FileStream file = File.Create(Path.Combine(previews, "chrome-connect-" + width + "x" + height + ".png"))) encoder.Save(file);
            }
            surface.RootVisual = null;
            }
        }

        private static void ScaleFonts(DependencyObject element, double scale)
        {
            TextBlock text = element as TextBlock;
            Control control = element as Control;
            if (text != null) text.FontSize *= scale;
            else if (control != null) control.FontSize *= scale;
            foreach (object child in LogicalTreeHelper.GetChildren(element))
                if (child is DependencyObject) ScaleFonts((DependencyObject)child, scale);
        }

        private static void CheckUtf8Response()
        {
            const string expected = "{\"body\":\"재연결 오류 수정 · 다운로드 및 재시작\"}";
            byte[] payload = Encoding.UTF8.GetBytes(expected);
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task response = Task.Run(delegate {
                    using (TcpClient socket = listener.AcceptTcpClient())
                    using (NetworkStream stream = socket.GetStream())
                    {
                        stream.ReadTimeout = 5000;
                        StreamReader reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                        while (!String.IsNullOrEmpty(reader.ReadLine())) { }
                        byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " +
                            payload.Length + "\r\nConnection: close\r\n\r\n");
                        stream.Write(headers, 0, headers.Length);
                        stream.Write(payload, 0, payload.Length);
                    }
                });
                MethodInfo factory = typeof(UpdateClient).GetMethod("CreateWebClient", BindingFlags.NonPublic | BindingFlags.Static);
                using (WebClient client = (WebClient)factory.Invoke(null, null))
                {
                    string actual = client.DownloadString("http://127.0.0.1:" + port + "/release");
                    Require(response.Wait(5000), "Local UTF-8 fixture did not complete.");
                    Require(actual == expected, "Korean release notes must use UTF-8 rather than the Windows default encoding.");
                }
            }
            finally { listener.Stop(); }
        }

        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
