using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexUsageMeter
{
    // Serves the phone remote page on the home network. Everything lives under /r/<secret>/, so only a phone
    // that scanned the QR code can open the page or send Screen off / Sleep.
    internal sealed class PhoneRemoteServer : IDisposable
    {
        internal const int DefaultPort = 43130;
        internal static readonly string[] Actions = { "screen-off", "sleep" };
        private static readonly byte[] Page = ReadPage();
        private const uint HandleFlagInherit = 1;
        private readonly TcpListener _listener;
        private volatile string _token;
        private readonly Action<string> _run;
        private readonly byte[] _icon;
        private readonly SemaphoreSlim _clients = new SemaphoreSlim(8, 8);
        private volatile bool _stopped;
        // Whole-request deadline, so a slow sender cannot hold one of the few connection slots.
        internal int RequestMilliseconds = 5000;

        internal PhoneRemoteServer(IPAddress address, int port, string token, byte[] icon, Action<string> run)
        {
            _token = token; _icon = icon; _run = run;
            _listener = new TcpListener(address, port);
            // .NET Framework sockets are inheritable: codex app-server, the updater and other children started
            // without shell execute would keep the port open after Stop, so switching off or on again would fail.
            if (!SetHandleInformation(_listener.Server.Handle, HandleFlagInherit, 0))
            {
                Win32Exception error = new Win32Exception();
                _listener.Stop();
                throw error;
            }
            try { _listener.Start(16); }
            catch { _listener.Stop(); throw; }
            Task.Run((Func<Task>)Listen);
        }

        internal int Port { get { return ((IPEndPoint)_listener.LocalEndpoint).Port; } }

        // A new link takes effect at once on the same port; the old link stops working.
        internal string Token { set { _token = value; } }

        internal static string NewToken()
        {
            byte[] bytes = new byte[16];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        // The home-network IPv4 address the phone on the same Wi-Fi can reach: the source address of the
        // default route when it is a private address on an adapter with a gateway, else the first such address.
        internal static IPAddress LanAddress()
        {
            IPAddress routed = null;
            try
            {
                // Connecting a UDP socket only consults the routing table; no packet is sent.
                using (Socket probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    probe.Connect(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9));
                    routed = ((IPEndPoint)probe.LocalEndPoint).Address;
                }
            }
            catch (SocketException) { /* No IPv4 route while offline; the adapter list below decides. */ }
            List<IPAddress> home = new List<IPAddress>();
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                IPInterfaceProperties properties = adapter.GetIPProperties();
                if (!properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any))) continue;
                home.AddRange(properties.UnicastAddresses.Select(item => item.Address).Where(address =>
                    address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(address)));
            }
            if (routed != null && home.Contains(routed)) return routed;
            return home.FirstOrDefault();
        }

        private static bool IsPrivate(IPAddress address)
        {
            byte[] b = address.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        }

        internal static string Link(IPAddress address, int port, string token)
        {
            return "http://" + address + ":" + port.ToString(CultureInfo.InvariantCulture) + "/r/" + token + "/";
        }

        // Rendered on the UI thread: the 50 px app icon is too small for a phone home screen.
        internal static byte[] RenderIcon()
        {
            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x15, 0x15, 0x15)), null, new Rect(0, 0, 192, 192), 40, 40);
                Pen pen = new Pen(new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)), 14) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                PathGeometry ring = new PathGeometry(new[] { new PathFigure(new Point(70, 66), new PathSegment[] {
                    new ArcSegment(new Point(122, 66), new Size(46, 46), 0, true, SweepDirection.Counterclockwise, true) }, false) });
                context.DrawGeometry(null, pen, ring);
                context.DrawLine(pen, new Point(96, 44), new Point(96, 98));
            }
            RenderTargetBitmap bitmap = new RenderTargetBitmap(192, 192, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (MemoryStream stream = new MemoryStream()) { encoder.Save(stream); return stream.ToArray(); }
        }

        private static byte[] ReadPage()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexUsageMeter.PhoneRemote.html"))
            using (MemoryStream copy = new MemoryStream()) { stream.CopyTo(copy); return copy.ToArray(); }
        }

        private async Task Listen()
        {
            while (!_stopped)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                catch (InvalidOperationException) { break; }
                // Same reason as the listener; a short-lived connection socket needs no failure handling.
                SetHandleInformation(client.Client.Handle, HandleFlagInherit, 0);
                if (!_clients.Wait(0)) { client.Close(); continue; }
                ThreadPool.QueueUserWorkItem(delegate { try { Handle(client); } finally { _clients.Release(); } });
            }
        }

        private void Handle(TcpClient client)
        {
            using (client)
            {
                client.SendTimeout = 5000;
                try
                {
                    NetworkStream stream = client.GetStream();
                    byte[] buffer = new byte[8192];
                    int total = 0, end = -1;
                    Stopwatch clock = Stopwatch.StartNew();
                    while (end < 0)
                    {
                        int remaining = RequestMilliseconds - (int)clock.ElapsedMilliseconds;
                        if (remaining <= 0) return;
                        if (total == buffer.Length) { Reply(stream, 400, "text/plain", Encoding.ASCII.GetBytes("bad request")); return; }
                        client.ReceiveTimeout = remaining;
                        int read = stream.Read(buffer, total, buffer.Length - total);
                        if (read <= 0) return;
                        int from = Math.Max(0, total - 3);
                        total += read;
                        for (int i = from; i + 3 < total && end < 0; i++)
                            if (buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10) end = i;
                    }
                    string[] lines = Encoding.ASCII.GetString(buffer, 0, end).Split(new[] { "\r\n" }, StringSplitOptions.None);
                    string[] request = lines[0].Split(' ');
                    if (request.Length != 3 || !request[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) { Reply(stream, 400, "text/plain", Encoding.ASCII.GetBytes("bad request")); return; }
                    Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 1; i < lines.Length && lines[i].Length > 0; i++)
                    {
                        int colon = lines[i].IndexOf(':');
                        if (colon > 0) fields[lines[i].Substring(0, colon)] = lines[i].Substring(colon + 1).Trim();
                    }
                    string length;
                    if (fields.ContainsKey("Transfer-Encoding") || (fields.TryGetValue("Content-Length", out length) && length != "0"))
                    { Reply(stream, 413, "text/plain", Encoding.ASCII.GetBytes("no body")); return; }
                    Route(stream, request[0], request[1], fields);
                }
                catch (Exception) { /* A broken phone connection must never disturb the meter. */ }
            }
        }

        private void Route(Stream stream, string method, string target, Dictionary<string, string> fields)
        {
            string token = _token;
            int query = target.IndexOf('?');
            if (query >= 0) target = target.Substring(0, query);
            string[] parts = target.Split('/');
            if (parts.Length < 3 || parts[0].Length != 0 || parts[1] != "r" || !SameToken(parts[2], token)) { NotFound(stream); return; }
            if (parts.Length == 3) { Redirect(stream, "/r/" + token + "/"); return; }
            string resource = parts.Length == 4 ? parts[3] : null;
            if (resource == "" || resource == "manifest.webmanifest" || resource == "icon.png")
            {
                if (method != "GET") { Reply(stream, 405, "text/plain", Encoding.ASCII.GetBytes("method not allowed")); return; }
                if (resource == "") Reply(stream, 200, "text/html; charset=utf-8", Page);
                else if (resource == "icon.png") Reply(stream, 200, "image/png", _icon);
                else Reply(stream, 200, "application/manifest+json; charset=utf-8", Encoding.UTF8.GetBytes(
                    "{\"name\":\"PC 리모컨\",\"short_name\":\"PC 리모컨\",\"start_url\":\"./\",\"scope\":\"./\",\"display\":\"standalone\"," +
                    "\"background_color\":\"#151515\",\"theme_color\":\"#151515\",\"icons\":[{\"src\":\"icon.png\",\"sizes\":\"192x192\",\"type\":\"image/png\"}]}"));
                return;
            }
            if (resource == null || Array.IndexOf(Actions, resource) < 0) { NotFound(stream); return; }
            if (method != "POST") { Reply(stream, 405, "text/plain", Encoding.ASCII.GetBytes("method not allowed")); return; }
            // A custom header cannot be sent cross-site without a preflight this server never approves.
            string marker;
            if (!fields.TryGetValue("X-Codex-Remote", out marker) || marker != "1") { Reply(stream, 403, "text/plain", Encoding.ASCII.GetBytes("forbidden")); return; }
            Reply(stream, 200, "application/json", Encoding.ASCII.GetBytes("{\"status\":\"ok\"}"));
            stream.Flush();
            _run(resource);
        }

        private static bool SameToken(string left, string right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int different = 0; for (int i = 0; i < left.Length; i++) different |= left[i] ^ right[i]; return different == 0;
        }

        private static void NotFound(Stream stream) { Reply(stream, 404, "text/plain", Encoding.ASCII.GetBytes("not found")); }

        private static void Redirect(Stream stream, string location)
        {
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 301 Moved\r\nLocation: " + location + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
        }

        private static void Reply(Stream stream, int status, string type, byte[] body)
        {
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Result\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
                "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n");
            stream.Write(head, 0, head.Length); stream.Write(body, 0, body.Length);
        }

        public void Dispose() { _stopped = true; _listener.Stop(); }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    }

    // Where the phone remote keeps its switch and secret; tests supply an in-memory store and a loopback address.
    internal sealed class PhoneRemoteStore
    {
        internal Func<bool> LoadEnabled;
        internal Action<bool> SaveEnabled;
        internal Func<string> LoadToken;
        internal Action<string> SaveToken;
        internal IPAddress Address = IPAddress.Any;
        internal int Port = PhoneRemoteServer.DefaultPort;
        internal Func<IPAddress> FindAddress = PhoneRemoteServer.LanAddress;

        internal static PhoneRemoteStore User()
        {
            return new PhoneRemoteStore { LoadEnabled = UserSettings.LoadPhoneRemoteEnabled, SaveEnabled = UserSettings.SavePhoneRemoteEnabled,
                LoadToken = UserSettings.LoadPhoneRemoteToken, SaveToken = UserSettings.SavePhoneRemoteToken };
        }
    }

    // The Settings section: toggle, QR code, link and a button to replace the secret link.
    internal sealed class PhoneRemoteSettings : IDisposable
    {
        private readonly Window _window;
        private readonly PhoneRemoteStore _store;
        private readonly CheckBox _toggle;
        private readonly Button _reset;
        private readonly Action<string> _footer;
        private readonly Action _screenOff, _sleep;
        private readonly DispatcherTimer _retry;
        private PhoneRemoteServer _server;
        private byte[] _icon;
        private bool _allowed, _setting, _disposed;

        internal PhoneRemoteSettings(Window window, PhoneRemoteStore store, Action<string> footer, Action screenOff, Action sleep)
        {
            _window = window; _store = store; _footer = footer; _screenOff = screenOff; _sleep = sleep;
            _toggle = (CheckBox)window.FindName("PhoneRemoteCheckBox");
            _reset = (Button)window.FindName("PhoneRemoteResetButton");
            _retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _retry.Tick += delegate { Apply(); };
            _setting = true; _toggle.IsChecked = store.LoadEnabled(); _setting = false;
            _toggle.Checked += delegate { Changed(); };
            _toggle.Unchecked += delegate { Changed(); };
            _reset.Click += delegate
            {
                string token = PhoneRemoteServer.NewToken();
                _store.SaveToken(token);
                if (_server != null) _server.Token = token;
                Apply();
                _footer("폰 리모컨 링크를 새로 만들었습니다. 폰에서 QR 코드를 다시 찍어 주세요.");
            };
            Show(null, "켜면 이 PC에서 폰 리모컨을 엽니다.");
        }

        // Only the real meter listens; self-tests and previews construct the controller without it.
        internal void Enable()
        {
            _allowed = true;
            // A new DHCP lease or a different network changes the address in the QR code.
            NetworkChange.NetworkAddressChanged += AddressChanged;
            Apply();
        }

        private void AddressChanged(object sender, EventArgs e) { _window.Dispatcher.BeginInvoke(new Action(Apply)); }

        private void Changed()
        {
            if (_setting) return;
            try { _store.SaveEnabled(_toggle.IsChecked == true); }
            catch (Exception ex) { _footer("폰 리모컨 설정 저장 실패: " + ex.Message); }
            if (_toggle.IsChecked != true) Stop();
            Apply();
        }

        private void Apply()
        {
            if (_disposed) return;
            _retry.Stop();
            if (_toggle.IsChecked != true) { Show(null, "켜면 이 PC에서 폰 리모컨을 엽니다."); return; }
            if (!_allowed) { Show(null, "미터기를 다시 시작하면 열립니다."); return; }
            string token = _store.LoadToken();
            if (String.IsNullOrEmpty(token)) { token = PhoneRemoteServer.NewToken(); _store.SaveToken(token); }
            if (_server == null)
            {
                try
                {
                    if (_icon == null) _icon = PhoneRemoteServer.RenderIcon();
                    _server = new PhoneRemoteServer(_store.Address, _store.Port, token, _icon, Run);
                }
                catch (SocketException)
                {
                    // Right after an update the previous meter can still hold the port for a moment.
                    Show(null, "폰 리모컨 포트(" + _store.Port + ")를 열지 못했습니다. 잠시 뒤 다시 시도합니다.");
                    _retry.Start();
                    return;
                }
                catch (Win32Exception ex) { Show(null, "폰 리모컨을 열지 못했습니다: " + ex.Message); return; }
            }
            IPAddress address = _store.FindAddress();
            if (address == null) { Show(null, "네트워크 연결을 찾지 못했습니다. 인터넷 공유기에 연결된 뒤 다시 확인합니다."); _retry.Start(); return; }
            Show(PhoneRemoteServer.Link(address, _server.Port, token),
                "폰 카메라로 QR 코드를 찍어 열고, 브라우저 메뉴에서 '홈 화면에 추가'를 누르세요. 폰은 이 PC와 같은 공유기(와이파이)에 연결돼 있어야 합니다.");
        }

        private void Run(string action)
        {
            _window.Dispatcher.BeginInvoke(new Action(delegate
            {
                if (action == "sleep")
                {
                    _footer("폰 리모컨: 절전 요청을 받았습니다.");
                    PowerHoldButton.RunLater(TimeSpan.FromMilliseconds(1500), _sleep);
                }
                else
                {
                    _footer("폰 리모컨: 화면 끄기 요청을 받았습니다.");
                    PowerHoldButton.RunLater(TimeSpan.FromMilliseconds(300), _screenOff);
                }
            }));
        }

        private void Show(string link, string status) { ShowLink(_window, link, status); }

        internal static void ShowLink(Window window, string link, string status)
        {
            Image image = (Image)window.FindName("PhoneRemoteQrImage");
            TextBox text = (TextBox)window.FindName("PhoneRemoteLinkText");
            ((TextBlock)window.FindName("PhoneRemoteStatusText")).Text = status;
            ((FrameworkElement)window.FindName("PhoneRemoteLinkPanel")).Visibility = link == null ? Visibility.Collapsed : Visibility.Visible;
            ((FrameworkElement)window.FindName("PhoneRemoteResetButton")).Visibility = link == null ? Visibility.Collapsed : Visibility.Visible;
            image.Source = link == null ? null : QrImage(QrCode.Encode(link), 8);
            text.Text = link ?? "";
        }

        // Black modules on white with the 4-module quiet zone scanners need, even in the dark theme.
        internal static BitmapSource QrImage(bool[,] modules, int scale)
        {
            int count = modules.GetLength(0), size = (count + 8) * scale;
            byte[] pixels = new byte[size * size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = 255;
            for (int y = 0; y < count; y++)
                for (int x = 0; x < count; x++)
                    if (modules[y, x])
                        for (int dy = 0; dy < scale; dy++)
                            for (int dx = 0; dx < scale; dx++)
                                pixels[((y + 4) * scale + dy) * size + (x + 4) * scale + dx] = 0;
            BitmapSource bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
            bitmap.Freeze();
            return bitmap;
        }

        private void Stop()
        {
            if (_server != null) { _server.Dispose(); _server = null; }
        }

        public void Dispose()
        {
            _disposed = true;
            NetworkChange.NetworkAddressChanged -= AddressChanged;
            _retry.Stop();
            Stop();
        }
    }
}
