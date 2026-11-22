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
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexUsageMeter
{
    // Serves the phone app's API on the home network (docs/PHONE_APP.md). Everything lives under /r/<secret>/,
    // so only a phone that scanned the pairing QR code can read the status or send Screen off / Sleep.
    internal sealed class PhoneRemoteServer : IDisposable
    {
        internal const int DefaultPort = 43130;
        internal const string InstallLink = "https://github.com/6Zman8/codex-usage-meter/releases/latest/download/CodexMeterRemote.apk";
        internal static readonly string[] Actions = { "screen-off", "sleep" };
        private const uint HandleFlagInherit = 1;
        private readonly TcpListener _listener;
        private volatile string _token;
        private volatile byte[] _status = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
        private readonly Action<string> _run;
        private readonly SemaphoreSlim _clients = new SemaphoreSlim(8, 8);
        private volatile bool _stopped;
        // Whole-request deadline, so a slow sender cannot hold one of the few connection slots.
        internal int RequestMilliseconds = 5000;

        internal PhoneRemoteServer(IPAddress address, int port, string token, Action<string> run)
        {
            _token = token; _run = run;
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

        // What GET status answers; the PC id and name the app shows.
        internal void SetStatus(string id, string name, string version)
        {
            _status = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new Dictionary<string, string> {
                { "status", "ok" }, { "id", id }, { "name", name }, { "version", version } }));
        }

        internal static string NewToken() { return RandomText(16); }

        // The PC's stable id in the pairing QR code; 64 bits keep the QR code within version 6.
        internal static string NewId() { return RandomText(8); }

        private static string RandomText(int byteCount)
        {
            byte[] bytes = new byte[byteCount];
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

        // The pairing QR code; the id and token are base64url, so nothing needs escaping.
        internal static string PairLink(string id, IPAddress address, int port, string token)
        {
            return "codexmeter://pair?v=1&id=" + id + "&host=" + address + "&port=" + port.ToString(CultureInfo.InvariantCulture) + "&token=" + token;
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
                        if (total == buffer.Length)
                        {
                            Reply(stream, 400, "text/plain", Encoding.ASCII.GetBytes("bad request"));
                            // Closing with unread bytes sends a reset, which can discard the reply before the sender reads it.
                            client.Client.Shutdown(SocketShutdown.Send);
                            while (clock.ElapsedMilliseconds < RequestMilliseconds)
                            {
                                client.ReceiveTimeout = Math.Max(1, RequestMilliseconds - (int)clock.ElapsedMilliseconds);
                                if (stream.Read(buffer, 0, buffer.Length) <= 0) break;
                            }
                            return;
                        }
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
            if (parts.Length != 4 || parts[0].Length != 0 || parts[1] != "r" || !SameToken(parts[2], token)) { NotFound(stream); return; }
            string resource = parts[3];
            bool status = resource == "status";
            if (!status && Array.IndexOf(Actions, resource) < 0) { NotFound(stream); return; }
            if (method != (status ? "GET" : "POST")) { Reply(stream, 405, "text/plain", Encoding.ASCII.GetBytes("method not allowed")); return; }
            // A custom header cannot be sent cross-site without a preflight this server never approves.
            string marker;
            if (!fields.TryGetValue("X-Codex-Remote", out marker) || marker != "1") { Reply(stream, 403, "text/plain", Encoding.ASCII.GetBytes("forbidden")); return; }
            if (status) { Reply(stream, 200, "application/json; charset=utf-8", _status); return; }
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

        private static void Reply(Stream stream, int status, string type, byte[] body)
        {
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Result\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
                "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n");
            stream.Write(head, 0, head.Length); stream.Write(body, 0, body.Length);
        }

        public void Dispose() { _stopped = true; _listener.Stop(); }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    }

    // Where the phone remote keeps its switch, secret, PC id and name; tests supply an in-memory store and a loopback address.
    internal sealed class PhoneRemoteStore
    {
        internal const string TokenKey = "PhoneRemoteToken", IdKey = "PhoneRemoteId", NameKey = "PhoneRemoteName";
        internal Func<bool> LoadEnabled;
        internal Action<bool> SaveEnabled;
        internal Func<string, string> LoadValue;
        internal Action<string, string> SaveValue;
        internal string Version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        internal IPAddress Address = IPAddress.Any;
        internal int Port = PhoneRemoteServer.DefaultPort;
        internal Func<IPAddress> FindAddress = PhoneRemoteServer.LanAddress;
        internal Func<int, PhoneRemoteFirewall.State> CheckFirewall = port => PhoneRemoteFirewall.Check(PhoneRemoteFirewall.Program, port);
        // Runs off the UI thread; true when the rule was added.
        internal Func<int, bool> AllowFirewall = port => PhoneRemoteFirewall.Allow(PhoneRemoteFirewall.Program, port);

        internal static PhoneRemoteStore User()
        {
            return new PhoneRemoteStore { LoadEnabled = UserSettings.LoadPhoneRemoteEnabled, SaveEnabled = UserSettings.SavePhoneRemoteEnabled,
                LoadValue = UserSettings.LoadPhoneRemoteValue, SaveValue = UserSettings.SavePhoneRemoteValue };
        }

        // Created on first use and kept, so the app recognises a re-scanned PC.
        internal string Ensure(string key, Func<string> create)
        {
            string value = LoadValue(key);
            if (String.IsNullOrEmpty(value)) { value = create(); SaveValue(key, value); }
            return value;
        }

        internal string Name()
        {
            string name = LoadValue(NameKey);
            return String.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim();
        }
    }

    // Windows Firewall blocks the phone unless an inbound rule exists. This PC had prompts on new listeners
    // turned off, so the meter checks the rules itself and offers a one-time elevated rule.
    internal static class PhoneRemoteFirewall
    {
        internal enum State { Allowed, Blocked, PublicNetwork }
        internal const string RuleName = "CodexUsageMeter Phone Remote";
        private const int ProfilePrivate = 2, ProfilePublic = 4;

        internal static string Program { get { return Assembly.GetExecutingAssembly().Location; } }

        // Reads the policy through the firewall COM API, which needs no elevation.
        internal static State Check(string program, int port)
        {
            object policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
            int current = (int)Get(policy, "CurrentProfileTypes");
            bool allowed = false;
            foreach (object rule in (System.Collections.IEnumerable)Get(policy, "Rules"))
            {
                if (!(bool)Get(rule, "Enabled") || (int)Get(rule, "Direction") != 1 || (int)Get(rule, "Action") != 1) continue;
                if (((int)Get(rule, "Profiles") & current) == 0) continue;
                int protocol = (int)Get(rule, "Protocol");
                if (protocol != 6 && protocol != 256) continue;
                if (RuleAllows(Get(rule, "ApplicationName") as string, Get(rule, "LocalPorts") as string, program, port)) { allowed = true; break; }
            }
            if (allowed) return State.Allowed;
            return (current & ProfilePublic) != 0 && (current & ProfilePrivate) == 0 ? State.PublicNetwork : State.Blocked;
        }

        // Rules without a program and port are usually scoped to a Store package or service, which COM does not
        // show reliably; trust only rules naming this program or naming this port.
        internal static bool RuleAllows(string application, string ports, string program, int port)
        {
            bool forProgram = !String.IsNullOrEmpty(application) && String.Equals(Environment.ExpandEnvironmentVariables(application), program, StringComparison.OrdinalIgnoreCase);
            bool forPort = String.IsNullOrEmpty(application) && !String.IsNullOrEmpty(ports) && ports != "*";
            return (forProgram || forPort) && PortMatches(ports, port);
        }

        internal static bool PortMatches(string ports, int port)
        {
            if (String.IsNullOrEmpty(ports) || ports == "*") return true;
            foreach (string part in ports.Split(','))
            {
                string[] range = part.Trim().Split('-');
                int low, high;
                if (range.Length == 1 && Int32.TryParse(range[0], out low) && low == port) return true;
                if (range.Length == 2 && Int32.TryParse(range[0], out low) && Int32.TryParse(range[1], out high) && low <= port && port <= high) return true;
            }
            return false;
        }

        // Private networks only, this program only, this port only.
        internal static string Command(string program, int port)
        {
            string p = port.ToString(CultureInfo.InvariantCulture);
            return "/c netsh advfirewall firewall delete rule name=\"" + RuleName + "\" >nul & netsh advfirewall firewall add rule name=\"" + RuleName +
                "\" dir=in action=allow protocol=TCP localport=" + p + " profile=private program=\"" + program + "\"";
        }

        internal static bool Allow(string program, int port)
        {
            try
            {
                using (Process process = Process.Start(new ProcessStartInfo("cmd.exe", Command(program, port)) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }))
                {
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
            }
            catch (Win32Exception) { return false; } // The user declined the administrator prompt.
        }

        private static object Get(object target, string name)
        {
            return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);
        }
    }

    // The Settings section: toggle, app install and PC pairing QR codes, the name the app shows,
    // and a button to replace the secret.
    internal sealed class PhoneRemoteSettings : IDisposable
    {
        private readonly Window _window;
        private readonly PhoneRemoteStore _store;
        private readonly CheckBox _toggle;
        private readonly Button _reset, _firewall;
        private readonly TextBox _name;
        private readonly Action<string> _footer;
        private readonly Action _screenOff, _sleep;
        private readonly DispatcherTimer _retry;
        private PhoneRemoteServer _server;
        private bool _allowed, _setting, _disposed;

        // The pairing link the QR code shows, or null while it is hidden.
        internal string PairLink { get; private set; }

        internal PhoneRemoteSettings(Window window, PhoneRemoteStore store, Action<string> footer, Action screenOff, Action sleep)
        {
            _window = window; _store = store; _footer = footer; _screenOff = screenOff; _sleep = sleep;
            _toggle = (CheckBox)window.FindName("PhoneRemoteCheckBox");
            _reset = (Button)window.FindName("PhoneRemoteResetButton");
            _firewall = (Button)window.FindName("PhoneRemoteFirewallButton");
            _name = (TextBox)window.FindName("PhoneRemoteNameText");
            _name.Text = store.Name();
            _name.LostKeyboardFocus += delegate { SaveName(); };
            _name.KeyDown += (sender, e) => { if (e.Key == System.Windows.Input.Key.Enter) SaveName(); };
            _firewall.Click += delegate
            {
                _firewall.IsEnabled = false;
                int port = _store.Port;
                Task.Run(() => _store.AllowFirewall(port)).ContinueWith(task => _window.Dispatcher.BeginInvoke(new Action(delegate
                {
                    _firewall.IsEnabled = true;
                    bool added = task.Status == TaskStatus.RanToCompletion && task.Result;
                    _footer(added ? "폰 리모컨: Windows 방화벽에서 폰 연결을 허용했습니다." : "폰 리모컨: 방화벽 허용이 취소되었거나 실패했습니다.");
                    Apply();
                })));
            };
            _retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _retry.Tick += delegate { Apply(); };
            _setting = true; _toggle.IsChecked = store.LoadEnabled(); _setting = false;
            _toggle.Checked += delegate { Changed(); };
            _toggle.Unchecked += delegate { Changed(); };
            _reset.Click += delegate
            {
                string token = PhoneRemoteServer.NewToken();
                _store.SaveValue(PhoneRemoteStore.TokenKey, token);
                if (_server != null) _server.Token = token;
                Apply();
                _footer("폰 리모컨 등록 QR을 새로 만들었습니다. 폰 앱에서 QR을 다시 찍어 주세요.");
            };
            ((Button)window.FindName("PhoneRemoteInstallTab")).Click += delegate { SelectCode(_window, true); };
            ((Button)window.FindName("PhoneRemotePairTab")).Click += delegate { SelectCode(_window, false); };
            Show(null, "켜면 이 PC에서 폰 리모컨을 엽니다.");
        }

        internal static void SelectCode(Window window, bool install)
        {
            ((FrameworkElement)window.FindName("PhoneRemoteInstallQrPanel")).Visibility = install ? Visibility.Visible : Visibility.Collapsed;
            ((FrameworkElement)window.FindName("PhoneRemotePairQrPanel")).Visibility = install ? Visibility.Collapsed : Visibility.Visible;
            Button installTab = (Button)window.FindName("PhoneRemoteInstallTab"), pairTab = (Button)window.FindName("PhoneRemotePairTab");
            Style selected = (Style)installTab.FindResource("AccentButton"), plain = (Style)installTab.FindResource(typeof(Button));
            installTab.Style = install ? selected : plain;
            pairTab.Style = install ? plain : selected;
        }

        private void SaveName()
        {
            string name = _name.Text.Trim();
            // Compared with the name in use, so leaving the box showing the computer name keeps "no custom name".
            if (name != _store.Name())
            {
                try { _store.SaveValue(PhoneRemoteStore.NameKey, name); }
                catch (Exception ex) { _footer("폰 리모컨 이름 저장 실패: " + ex.Message); return; }
                UpdateStatus();
            }
            // An emptied box shows the computer name the app receives instead.
            _name.Text = _store.Name();
        }

        private void UpdateStatus()
        {
            if (_server != null) _server.SetStatus(_store.Ensure(PhoneRemoteStore.IdKey, PhoneRemoteServer.NewId), _store.Name(), _store.Version);
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
            string token = _store.Ensure(PhoneRemoteStore.TokenKey, PhoneRemoteServer.NewToken);
            string id = _store.Ensure(PhoneRemoteStore.IdKey, PhoneRemoteServer.NewId);
            if (_server == null)
            {
                try
                {
                    _server = new PhoneRemoteServer(_store.Address, _store.Port, token, Run);
                    UpdateStatus();
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
            string link = PhoneRemoteServer.PairLink(id, address, _server.Port, token);
            PhoneRemoteFirewall.State firewall;
            try { firewall = _store.CheckFirewall(_server.Port); }
            catch (Exception) { firewall = PhoneRemoteFirewall.State.Blocked; }
            if (firewall == PhoneRemoteFirewall.State.Blocked)
            {
                Show(link, "Windows 방화벽이 폰 연결을 막고 있습니다. 아래 '방화벽 허용'을 누르고 관리자 승인 창에서 '예'를 누르세요.");
                _firewall.Visibility = Visibility.Visible;
                return;
            }
            if (firewall == PhoneRemoteFirewall.State.PublicNetwork)
            {
                Show(link, "이 PC의 네트워크가 '공용'으로 설정돼 있어 폰이 연결할 수 없습니다. Windows 설정 → 네트워크 및 인터넷에서 연결을 '개인 네트워크'로 바꾼 뒤 '방화벽 허용'을 누르세요.");
                _firewall.Visibility = Visibility.Visible;
                return;
            }
            Show(link, "폰의 'PC 리모컨' 앱에서 'QR 찍어서 PC 추가'를 누르고 '2. PC 등록' QR을 찍으세요. 앱이 없으면 위의 '1. 폰에 앱 설치'를 눌러 나온 QR로 먼저 설치합니다. 폰은 이 PC와 같은 공유기(와이파이)에 연결돼 있어야 합니다.");
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

        private void Show(string link, string status) { PairLink = link; ShowCodes(_window, link, status); }

        internal static void ShowCodes(Window window, string link, string status)
        {
            ((TextBlock)window.FindName("PhoneRemoteStatusText")).Text = status;
            ((FrameworkElement)window.FindName("PhoneRemoteLinkPanel")).Visibility = link == null ? Visibility.Collapsed : Visibility.Visible;
            ((FrameworkElement)window.FindName("PhoneRemoteResetButton")).Visibility = link == null ? Visibility.Collapsed : Visibility.Visible;
            ((FrameworkElement)window.FindName("PhoneRemoteFirewallButton")).Visibility = Visibility.Collapsed;
            ((Image)window.FindName("PhoneRemoteQrImage")).Source = link == null ? null : QrImage(QrCode.Encode(link), 3);
            ((Image)window.FindName("PhoneRemoteInstallQrImage")).Source = link == null ? null : QrImage(QrCode.Encode(PhoneRemoteServer.InstallLink), 3);
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
