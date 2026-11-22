using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUsageMeter
{
    internal static class PhoneRemoteTests
    {
        private const string Token = "AbCdEfGhIjKlMnOpQrStUv";

        internal static void Run(Action<string> report, string evidenceDirectory)
        {
            VerifyQrCode();
            report("PASS phone remote QR: byte mode, level M, versions 1/4/6 with forced masks match the Nayuki reference module for module; quiet-zone bitmap matches the matrix; longest pairing link and the app install link fit");
            VerifyServer();
            report("PASS phone remote server: secret path only, GET status with id/name/version (UTF-8), POST-only actions, remote header required everywhere, old web page paths gone, one action per request, 404/403/405/413 rejections, whole-request deadline for slow senders");
            VerifyLifecycle();
            report("PASS phone remote lifecycle: off by default, switch on with a stable PC id and pairing link, name edits reach the status, new secret on the same port keeps the id, blocked firewall/public network explained with the allow button, allow refreshes, switch off frees the port, switch on again and close, all while a child process started without shell execute is running");
            VerifyFirewall();
            report("PASS phone remote firewall: rule limited to this program, TCP port and private networks; program-less any-port (Store/service) rules ignored; port lists/ranges matched; policy readable without elevation");
            VerifySettings(evidenceDirectory);
            report("PASS phone remote settings: one QR code at a time (PC pairing first, the install tab swaps it) at full 150 px with its tab highlighted at 560 px and 320 px, with the app name box inside the settings panel");
        }

        private static void VerifyQrCode()
        {
            // Reference matrices from qrcodegen (Nayuki) 1.8, Ecc.MEDIUM, boost off, row-major bits in hex.
            Check("HELLO", 3, 21, "fe93fc17906e8abb7555dba12ec10d07faafe01300b75a5b6bf32294076227a5f892807917fa6410543eba0fddd597aeb2c9044b1fea500");
            Check("http://192.168.0.107:43130/r/" + Token + "/", 2, 33, "fe0dd7bfc117e2506ea33d4bb75452c5dba99232ec158c6107faaaaafe01267a00be57793e029c0afb27ae4b0ad08e43d5cde5122dd20cdc903deb198a1ca8253abcff76e918f6866bfb50a0f02adcb09cadfd3e88fd440f09f9361cbeb0c5503618bc8ff169fb00530a453f924a6ad05ba0d15bacd2afc5d72c50feeaa18bd1048519fcfe84e9990");
            Check("https://example.com/" + new string('x', 70), 7, 41, "fe1a15153fc11fa08e106e8051eb8bb74d49d955dba1ac5182ec147cf29d07faaaaaaafe00148a3c0096e51ef950180654455342b66e18cbc98993be0969b0ddd4d747e84d4b9c10adffcc78e7d98c9a9a7296b1461c10c0d5b3c71a7b8672b2a2f6b410ab8eb23f6da37e67a7d856453e5b74fb0ebd92683ca397260c55f5f45aaf9c60822ceaf8c1a66ea6ea8b9b2cb3b469c50aa703d4b1a65c808e8a2e78048d38bbb60c0c61fe00553575477f83efb92bd051970a916ba4f3130fc5d447d72356e86d0fba2704bdaaade2fec5a794990");
            bool tooLong = false;
            try { QrCode.Encode(new string('x', 120)); } catch (ArgumentException) { tooLong = true; }
            Require(tooLong, "an oversized QR payload was not rejected");
            string longest = PhoneRemoteServer.PairLink(PhoneRemoteServer.NewId(), IPAddress.Parse("255.255.255.255"), 65535, PhoneRemoteServer.NewToken());
            Require(longest.Length <= 106 && QrCode.Encode(longest).GetLength(0) <= 41 && QrCode.Encode(PhoneRemoteServer.InstallLink).GetLength(0) <= 41,
                "the pairing or install link does not fit a version 6 QR code: " + longest);

            bool[,] modules = QrCode.Encode("http://192.168.0.107:43130/r/" + Token + "/");
            BitmapSource bitmap = PhoneRemoteSettings.QrImage(modules, 3);
            int count = modules.GetLength(0), size = bitmap.PixelWidth;
            byte[] pixels = new byte[size * size]; bitmap.CopyPixels(pixels, size, 0);
            Require(size == (count + 8) * 3 && pixels[0] == 255 && pixels[size * size - 1] == 255, "QR bitmap lacks its white quiet zone");
            for (int y = 0; y < count; y++)
                for (int x = 0; x < count; x++)
                    Require((pixels[((y + 4) * 3 + 1) * size + (x + 4) * 3 + 1] == 0) == modules[y, x], "QR bitmap differs from the matrix");
        }

        private static void Check(string text, int mask, int size, string hex)
        {
            bool[,] modules = QrCode.Encode(text, mask);
            Require(modules.GetLength(0) == size && modules.GetLength(1) == size, "QR version differs for " + text.Length + " bytes");
            for (int i = 0; i < size * size; i++)
            {
                int nibble = Convert.ToInt32(hex.Substring(i / 4, 1), 16);
                Require(((nibble >> (3 - i % 4)) & 1) == 1 == modules[i / size, i % size], "QR module differs from the reference at " + (i % size) + "," + (i / size) + " for " + text.Length + " bytes");
            }
        }

        private static void VerifyServer()
        {
            List<string> actions = new List<string>();
            using (PhoneRemoteServer server = new PhoneRemoteServer(IPAddress.Loopback, 0, Token, action => { lock (actions) actions.Add(action); }))
            {
                server.RequestMilliseconds = 400;
                int port = server.Port;
                string prefix = "/r/" + Token + "/";
                // A byte every 100 ms beats any per-read timeout; only a whole-request deadline closes it.
                using (Socket slow = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    slow.Connect(IPAddress.Loopback, port);
                    Stopwatch waited = Stopwatch.StartNew();
                    bool closed = false;
                    while (!closed && waited.ElapsedMilliseconds < 3000)
                    {
                        try { slow.Send(new[] { (byte)'G' }); }
                        catch (SocketException) { closed = true; break; }
                        closed = slow.Poll(100000, SelectMode.SelectRead) && slow.Available == 0;
                    }
                    Require(closed && waited.ElapsedMilliseconds < 1500, "a trickling request kept its connection past the whole-request deadline: " + waited.ElapsedMilliseconds + " ms");
                }
                server.SetStatus("Ab_9-xYz012", "데스크톱 \"메인\"", "1.9.31");
                Response status = Send(port, "GET " + prefix + "status HTTP/1.1", "X-Codex-Remote: 1");
                Dictionary<string, object> body = status.Status == 200 ? new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(status.Text) : null;
                Require(body != null && status.Head.Contains("application/json; charset=utf-8") && (string)body["status"] == "ok" && (string)body["id"] == "Ab_9-xYz012" &&
                    (string)body["name"] == "데스크톱 \"메인\"" && (string)body["version"] == "1.9.31", "the status was not answered: " + status.Text);
                Require(Send(port, "GET " + prefix + "status?t=1 HTTP/1.1", "X-Codex-Remote: 1").Status == 200, "a query string broke the status");
                Require(Send(port, "GET " + prefix + "status HTTP/1.1").Status == 403, "the status was answered without the remote header");
                Require(Send(port, "POST " + prefix + "status HTTP/1.1", "X-Codex-Remote: 1").Status == 405, "POST was accepted for the status");

                foreach (string wrong in new[] { "/", "/r/", "/r/" + Token, prefix, prefix + "manifest.webmanifest", prefix + "icon.png", "/r/" + Token.Substring(1) + "x/status",
                    "/r/" + Token.ToLowerInvariant() + "/status", "/r/" + Token + "/../", "/r/" + Token + "/screen-off/x" })
                    Require(Send(port, "GET " + wrong + " HTTP/1.1", "X-Codex-Remote: 1").Status == 404, "an unknown path was answered: " + wrong);
                Require(Send(port, "POST /r/WRONGWRONGWRONGWRONG12/screen-off HTTP/1.1", "X-Codex-Remote: 1").Status == 404, "a wrong secret reached an action");
                Require(Send(port, "GET " + prefix + "screen-off HTTP/1.1", "X-Codex-Remote: 1").Status == 405, "GET ran an action");
                Require(Send(port, "HEAD " + prefix + "status HTTP/1.1", "X-Codex-Remote: 1").Status == 405, "HEAD was answered with a body");
                Require(Send(port, "POST " + prefix + "screen-off HTTP/1.1").Status == 403, "an action ran without the remote header");
                Require(Send(port, "POST " + prefix + "sleep HTTP/1.1", "X-Codex-Remote: 1", "Content-Length: 5").Status == 413, "an action with a body was accepted");
                Require(Send(port, "POST " + prefix + "reboot HTTP/1.1", "X-Codex-Remote: 1").Status == 404, "an unknown action was accepted");
                Require(Send(port, "GET " + prefix + "status HTTP/1.1", "X-Big: " + new string('a', 9000)).Status == 400, "an oversized header was accepted");
                lock (actions) Require(actions.Count == 0, "a rejected request ran an action");

                Require(Send(port, "POST " + prefix + "screen-off HTTP/1.1", "X-Codex-Remote: 1", "Content-Length: 0").Status == 200, "screen off was not accepted");
                Require(Send(port, "POST " + prefix + "sleep HTTP/1.1", "X-Codex-Remote: 1").Status == 200, "sleep was not accepted");
                lock (actions) Require(actions.Count == 2 && actions[0] == "screen-off" && actions[1] == "sleep", "accepted requests did not run exactly one matching action each");
            }
            Require(PhoneRemoteServer.NewToken().Length == 22 && PhoneRemoteServer.NewToken() != PhoneRemoteServer.NewToken(), "secret links are not random 128-bit values");
            Require(PhoneRemoteServer.NewId().Length == 11 && PhoneRemoteServer.NewId() != PhoneRemoteServer.NewId(), "PC ids are not random 64-bit values");
            Require(PhoneRemoteServer.PairLink("Ab_9-xYz012", IPAddress.Parse("192.168.0.107"), 43130, Token) ==
                "codexmeter://pair?v=1&id=Ab_9-xYz012&host=192.168.0.107&port=43130&token=" + Token, "the pairing link differs from docs/PHONE_APP.md");
        }

        private static void VerifyLifecycle()
        {
            int port;
            TcpListener free = new TcpListener(IPAddress.Loopback, 0);
            free.Start(); port = ((IPEndPoint)free.LocalEndpoint).Port; free.Stop();
            bool enabled = false;
            Dictionary<string, string> values = new Dictionary<string, string>();
            PhoneRemoteStore store = new PhoneRemoteStore { LoadEnabled = () => enabled, SaveEnabled = value => enabled = value,
                LoadValue = key => values.ContainsKey(key) ? values[key] : null, SaveValue = (key, value) => values[key] = value,
                Address = IPAddress.Loopback, Port = port, FindAddress = () => IPAddress.Loopback, Version = "1.9.31" };
            Func<string> token = () => values.ContainsKey(PhoneRemoteStore.TokenKey) ? values[PhoneRemoteStore.TokenKey] : null;
            Func<string, Dictionary<string, object>> status = secret =>
            {
                Response reply = Send(port, "GET /r/" + secret + "/status HTTP/1.1", "X-Codex-Remote: 1");
                return reply.Status == 200 ? new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reply.Text) : null;
            };
            PhoneRemoteFirewall.State firewall = PhoneRemoteFirewall.State.Allowed;
            int allowRequests = 0;
            store.CheckFirewall = checkedPort => firewall;
            store.AllowFirewall = checkedPort => { allowRequests++; firewall = PhoneRemoteFirewall.State.Allowed; return true; };
            Window window = DashboardController.LoadWindow();
            CheckBox toggle = (CheckBox)window.FindName("PhoneRemoteCheckBox");
            TextBox name = (TextBox)window.FindName("PhoneRemoteNameText");
            Image pairQr = (Image)window.FindName("PhoneRemoteQrImage"), installQr = (Image)window.FindName("PhoneRemoteInstallQrImage");
            Process child = null;
            try
            {
                using (PhoneRemoteSettings settings = new PhoneRemoteSettings(window, store, line => { }, () => { }, () => { }))
                {
                    Func<string> link = () => settings.PairLink ?? "";
                    settings.Enable();
                    Require(toggle.IsChecked != true && link() == "" && pairQr.Source == null && CanBind(port) && name.Text == Environment.MachineName,
                        "the remote listened while switched off or the default name is not the computer name");
                    toggle.IsChecked = true;
                    string id = values.ContainsKey(PhoneRemoteStore.IdKey) ? values[PhoneRemoteStore.IdKey] : null;
                    Dictionary<string, object> first = status(token());
                    Require(enabled && token() != null && id != null && id.Length == 11 && pairQr.Source != null && installQr.Source != null &&
                        link() == "codexmeter://pair?v=1&id=" + id + "&host=127.0.0.1&port=" + port + "&token=" + token() &&
                        first != null && (string)first["id"] == id && (string)first["name"] == Environment.MachineName && (string)first["version"] == "1.9.31",
                        "switching on did not open the remote: " + link());
                    Func<string> storedName = () => values.ContainsKey(PhoneRemoteStore.NameKey) ? values[PhoneRemoteStore.NameKey] : null;
                    name.Text = "";
                    PressEnter(name);
                    Require(name.Text == Environment.MachineName && String.IsNullOrEmpty(storedName()), "clearing the box before any name was saved left it empty");
                    PressEnter(name);
                    Require(name.Text == Environment.MachineName && String.IsNullOrEmpty(storedName()), "leaving the cleared box saved the computer name as a custom name");
                    name.Text = "  노트북  ";
                    PressEnter(name);
                    Require(values[PhoneRemoteStore.NameKey] == "노트북" && name.Text == "노트북" && (string)status(token())["name"] == "노트북", "a new name did not reach the status");
                    name.Text = "";
                    PressEnter(name);
                    Require(name.Text == Environment.MachineName && (string)status(token())["name"] == Environment.MachineName, "an empty name did not fall back to the computer name");
                    // Children started without shell execute (codex app-server, updater) inherit inheritable handles.
                    child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--lifetime-test-child") { UseShellExecute = false, CreateNoWindow = true });
                    string old = token();
                    ((Button)window.FindName("PhoneRemoteResetButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(token() != old && link().EndsWith("&token=" + token()) && link().Contains("&id=" + id + "&") && status(old) == null &&
                        status(token()) != null && (string)status(token())["id"] == id, "a new secret did not replace the old one on the same port with the same PC id");
                    Button firewallButton = (Button)window.FindName("PhoneRemoteFirewallButton");
                    TextBlock statusText = (TextBlock)window.FindName("PhoneRemoteStatusText");
                    Require(firewallButton.Visibility != Visibility.Visible, "the firewall button showed although the port is allowed");
                    firewall = PhoneRemoteFirewall.State.Blocked;
                    toggle.IsChecked = false; toggle.IsChecked = true;
                    Require(firewallButton.Visibility == Visibility.Visible && statusText.Text.Contains("방화벽") && link() != "", "a blocked firewall was not explained with its button");
                    firewallButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Stopwatch waited = Stopwatch.StartNew();
                    while (firewallButton.Visibility == Visibility.Visible && waited.ElapsedMilliseconds < 5000) DoEvents(window);
                    Require(allowRequests == 1 && firewallButton.Visibility != Visibility.Visible && firewallButton.IsEnabled, "allowing the firewall did not refresh the section");
                    firewall = PhoneRemoteFirewall.State.PublicNetwork;
                    toggle.IsChecked = false; toggle.IsChecked = true;
                    Require(firewallButton.Visibility == Visibility.Visible && statusText.Text.Contains("공용"), "a public network was not explained");
                    firewall = PhoneRemoteFirewall.State.Allowed;
                    toggle.IsChecked = false;
                    Require(!enabled && link() == "" && CanBind(port) && firewallButton.Visibility != Visibility.Visible, "switching off left the port open while a child process runs");
                    toggle.IsChecked = true;
                    Require(status(token()) != null && values[PhoneRemoteStore.IdKey] == id, "switching on again failed while a child process runs or changed the PC id");
                }
                Require(CanBind(port), "closing the meter left the remote port open");
            }
            finally
            {
                if (child != null) { if (!child.HasExited) child.Kill(); child.Dispose(); }
                window.Close();
            }
        }

        private static void PressEnter(TextBox box)
        {
            using (HwndSource source = new HwndSource(new HwndSourceParameters("CodexUsageMeter.PhoneRemoteKeyTest")))
                box.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Enter) { RoutedEvent = UIElement.KeyDownEvent });
        }

        private static void DoEvents(Window window)
        {
            System.Windows.Threading.DispatcherFrame frame = new System.Windows.Threading.DispatcherFrame();
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            System.Threading.Thread.Sleep(10);
        }

        private static void VerifyFirewall()
        {
            Require(PhoneRemoteFirewall.PortMatches(null, 43130) && PhoneRemoteFirewall.PortMatches("*", 43130) && PhoneRemoteFirewall.PortMatches("80,43130", 43130) &&
                PhoneRemoteFirewall.PortMatches("43000-43200", 43130) && !PhoneRemoteFirewall.PortMatches("80,443", 43130) && !PhoneRemoteFirewall.PortMatches("43131-43200", 43130),
                "firewall port matching is wrong");
            const string exe = @"B:\42b\mod\CodexUsageMeter.exe";
            Require(!PhoneRemoteFirewall.RuleAllows(null, null, exe, 43130) && !PhoneRemoteFirewall.RuleAllows("", "*", exe, 43130) &&
                PhoneRemoteFirewall.RuleAllows(exe.ToLowerInvariant(), null, exe, 43130) && PhoneRemoteFirewall.RuleAllows(exe, "43130", exe, 43130) &&
                !PhoneRemoteFirewall.RuleAllows(@"C:\other.exe", "43130", exe, 43130) && PhoneRemoteFirewall.RuleAllows(null, "43000-43200", exe, 43130) &&
                !PhoneRemoteFirewall.RuleAllows(exe, "80", exe, 43130),
                "package-scoped or unrelated firewall rules were treated as allowing the remote");
            string command = PhoneRemoteFirewall.Command(@"B:\42b\mod\CodexUsageMeter.exe", 43130);
            Require(command == "/c netsh advfirewall firewall delete rule name=\"CodexUsageMeter Phone Remote\" >nul & netsh advfirewall firewall add rule name=\"CodexUsageMeter Phone Remote\" dir=in action=allow protocol=TCP localport=43130 profile=private program=\"B:\\42b\\mod\\CodexUsageMeter.exe\"",
                "the firewall rule command is not limited to this program, port and private networks: " + command);
            // Reading the real policy must work without elevation; the result depends on the PC.
            PhoneRemoteFirewall.State state = PhoneRemoteFirewall.Check(PhoneRemoteFirewall.Program, 43130);
            Require(Enum.IsDefined(typeof(PhoneRemoteFirewall.State), state), "the firewall policy could not be read");
        }

        private static bool CanBind(int port)
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, port);
            try { probe.Start(); return true; }
            catch (SocketException) { return false; }
            finally { probe.Stop(); }
        }

        private sealed class Response { internal int Status; internal string Head, Text; internal byte[] Body; }

        private static Response Send(int port, string requestLine, params string[] headers)
        {
            using (TcpClient client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, port);
                client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
                NetworkStream stream = client.GetStream();
                StringBuilder request = new StringBuilder(requestLine + "\r\nHost: 127.0.0.1:" + port + "\r\n");
                foreach (string header in headers) request.Append(header + "\r\n");
                byte[] bytes = Encoding.UTF8.GetBytes(request.Append("\r\n").ToString());
                stream.Write(bytes, 0, bytes.Length);
                MemoryStream received = new MemoryStream();
                try { stream.CopyTo(received); } catch (IOException) { }
                byte[] all = received.ToArray();
                int split = 0;
                while (split + 3 < all.Length && !(all[split] == 13 && all[split + 1] == 10 && all[split + 2] == 13 && all[split + 3] == 10)) split++;
                Response response = new Response();
                response.Head = Encoding.ASCII.GetString(all, 0, Math.Min(split, all.Length));
                response.Body = new byte[Math.Max(0, all.Length - split - 4)];
                Array.Copy(all, Math.Min(all.Length, split + 4), response.Body, 0, response.Body.Length);
                response.Text = Encoding.UTF8.GetString(response.Body);
                string[] status = response.Head.Split(' ');
                response.Status = status.Length > 1 ? Int32.Parse(status[1]) : 0;
                return response;
            }
        }

        private static void VerifySettings(string evidenceDirectory)
        {
            Window window = DashboardController.LoadWindow();
            Grid root = (Grid)window.Content;
            foreach (UIElement child in root.Children) child.Visibility = Visibility.Collapsed;
            ((Grid)window.FindName("SettingsOverlay")).Visibility = Visibility.Visible;
            PhoneRemoteSettings.ShowCodes(window, PhoneRemoteServer.PairLink("Ab_9-xYz012", IPAddress.Parse("192.168.0.107"), 43130, Token), "폰 앱에서 QR을 찍으세요.");
            window.Content = null; root.Resources.MergedDictionaries.Add(window.Resources);
            root.SetValue(Control.FontFamilyProperty, window.FontFamily);
            root.SetValue(Control.ForegroundProperty, window.Foreground);
            root.SetValue(Control.FontSizeProperty, window.FontSize);
            // Only one code is ever on screen, at its scannable 150 px, in a wide and a narrow window.
            foreach (int width in new[] { 560, 320 })
            {
                HwndSourceParameters parameters = new HwndSourceParameters("CodexUsageMeter.PhoneRemoteTest") {
                    Width = width, Height = 900, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080
                };
                using (HwndSource surface = new HwndSource(parameters))
                {
                    surface.RootVisual = root;
                    Image install = (Image)window.FindName("PhoneRemoteInstallQrImage"), pair = (Image)window.FindName("PhoneRemoteQrImage");
                    Require(install.Source != null && pair.Source != null, "a QR code has no image");
                    ScrollViewer scroll = null;
                    for (DependencyObject node = pair; node != null && scroll == null; node = VisualTreeHelper.GetParent(node)) scroll = node as ScrollViewer;
                    // The pairing code is shown first; the install tab swaps it for the install code and back.
                    bool[] steps = { false, true, false };
                    for (int step = 0; step < steps.Length; step++)
                    {
                        bool showInstall = steps[step];
                        // The 560 px window first checks the state the settings open in.
                        if (step > 0 || width != 560) PhoneRemoteSettings.SelectCode(window, showInstall);
                        root.Measure(new Size(width, 900)); root.Arrange(new Rect(0, 0, width, 900)); root.UpdateLayout();
                        Image shown = showInstall ? install : pair, hidden = showInstall ? pair : install;
                        Require(shown.IsVisible && !hidden.IsVisible, "both or neither QR code is on screen at " + width + " px (install tab: " + showInstall + ")");
                        Rect inContent = shown.TransformToAncestor(scroll).TransformBounds(new Rect(shown.RenderSize));
                        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + inContent.Top - 80); root.UpdateLayout();
                        Rect bounds = shown.TransformToAncestor(scroll).TransformBounds(new Rect(shown.RenderSize));
                        Require(Math.Abs(bounds.Width - 150) < 1 && Math.Abs(bounds.Height - 150) < 1 && bounds.Top >= 0 && bounds.Bottom <= scroll.ViewportHeight &&
                            bounds.Left >= 0 && bounds.Right <= scroll.ViewportWidth,
                            "a QR code is not fully visible at 150 px inside the settings scroll area of a " + width + " px window: " + bounds + " viewport=" + scroll.ViewportWidth + "x" + scroll.ViewportHeight);
                        Button tab = (Button)window.FindName(showInstall ? "PhoneRemoteInstallTab" : "PhoneRemotePairTab");
                        Require(tab.IsVisible && tab.Style == (Style)tab.FindResource("AccentButton"), "the tab of the shown QR code is not highlighted at " + width + " px");
                    }
                    Require(((TextBox)window.FindName("PhoneRemoteNameText")).IsVisible && ((Button)window.FindName("PhoneRemoteResetButton")).IsVisible,
                        "the name box or the new-QR button is hidden while the codes are shown");
                    if (evidenceDirectory != null)
                    {
                        Directory.CreateDirectory(evidenceDirectory);
                        RenderTargetBitmap bitmap = new RenderTargetBitmap(width, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                        PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using (FileStream output = File.Create(Path.Combine(evidenceDirectory, "phone-remote-settings-" + width + ".png"))) encoder.Save(output);
                    }
                    surface.RootVisual = null;
                }
            }
            window.Close();
        }

        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
