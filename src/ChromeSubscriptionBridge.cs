using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace CodexUsageMeter
{
    // Receives only allowlisted billing fields from the user's Tampermonkey script.
    // This listener never reads browser cookies, passwords or authentication headers.
    internal sealed class ChromeSubscriptionBridge : IDisposable
    {
        internal const int Port = 43129;
        internal const string ScriptUrl = "https://raw.githubusercontent.com/6Zman8/codex-usage-meter/main/browser/codex-meter-subscription.user.js";
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Target> Targets = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
        private static ChromeSubscriptionBridge _shared;
        internal static event Action Changed;
        private readonly TcpListener _listener;
        private readonly string _key;
        private readonly Func<string, int> _receive;
        private readonly SemaphoreSlim _clients = new SemaphoreSlim(4, 4);
        private volatile bool _stopped;

        private sealed class Target { internal string Root, Account, Email, Plan; }
        internal static void Register(string root, string email, string plan)
        {
            string account = WebSubscriptionStore.AccountId(root, email);
            lock (Sync)
            {
                if (String.IsNullOrWhiteSpace(account)) { Targets.Remove(root); return; }
                Targets[root] = new Target { Root = root, Account = account, Email = email, Plan = plan };
            }
        }

        internal static string SettingsRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageMeter", "chrome-bridge"); } }
        internal static void StartIfConfigured()
        {
            if (!File.Exists(Path.Combine(SettingsRoot, "connection.bin"))) return;
            try { EnsureStarted(); } catch { /* Usage remains available; the connect action explains the failure. */ }
        }
        private static ChromeSubscriptionBridge EnsureStarted()
        {
            lock (Sync)
            {
                if (_shared == null) _shared = new ChromeSubscriptionBridge(Port, ReadKey(SettingsRoot), ReceiveRegistered);
                return _shared;
            }
        }
        internal static void Stop() { lock (Sync) { if (_shared != null) _shared.Dispose(); _shared = null; Targets.Clear(); } }
        internal static string ReadKey(string directory)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "connection.bin");
            if (!File.Exists(path))
            {
                byte[] bytes = new byte[32]; using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
                byte[] protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                using (FileStream file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(protectedBytes, 0, protectedBytes.Length);
            }
            byte[] secret = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            if (secret.Length != 32) throw new InvalidDataException("크롬 연결 정보를 읽지 못했습니다.");
            return BitConverter.ToString(secret).Replace("-", "").ToLowerInvariant();
        }
        internal static void OpenConnection()
        {
            ChromeSubscriptionBridge bridge;
            try { bridge = EnsureStarted(); }
            catch { throw new InvalidOperationException("크롬 연결을 준비하지 못했습니다. 다른 미터기가 실행 중인지 확인한 뒤 다시 시도해 주세요."); }
            OpenChrome("https://chatgpt.com/settings/billing#codex-meter-connect=" + bridge._key);
        }
        internal static void OpenChrome(string url)
        {
            string chrome = null;
            foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using (RegistryKey key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"))
                { if (key != null) { string path = Convert.ToString(key.GetValue(null)).Trim('"'); if (File.Exists(path)) { chrome = path; break; } } }
            }
            if (chrome == null)
                foreach (string basePath in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
                { string path = Path.Combine(basePath, @"Google\Chrome\Application\chrome.exe"); if (File.Exists(path)) { chrome = path; break; } }
            if (chrome == null) throw new InvalidOperationException("Google Chrome 설치 위치를 찾지 못했습니다.");
            Process.Start(new ProcessStartInfo(chrome, "\"" + url + "\"") { UseShellExecute = true });
        }

        private static int ReceiveRegistered(string json)
        {
            List<Target> targets; lock (Sync) targets = new List<Target>(Targets.Values);
            int count = 0;
            foreach (Target target in targets)
                if (Accept(target.Root, target.Account, target.Email, target.Plan, json, DateTime.UtcNow)) count++;
            if (count > 0) { Action changed = Changed; try { if (changed != null) changed(); } catch (InvalidOperationException) { } }
            return count;
        }
        internal static bool Accept(string root, string account, string email, string plan, string json, DateTime now)
        {
            if (String.IsNullOrEmpty(json) || json.Length > 65536) return false;
            var body = AccountSubscription.ParseJson(json);
            if (Convert.ToString(AccountSubscription.Get(body, "version"), CultureInfo.InvariantCulture) != "1") return false;
            DateTime? observed = AccountSubscription.Timestamp(AccountSubscription.Get(body, "observedAt"));
            if (!observed.HasValue || observed.Value > now.ToUniversalTime().AddSeconds(30) || observed.Value < now.ToUniversalTime().AddMinutes(-10)) return false;
            AccountSubscriptionInfo value;
            return WebSubscriptionStore.TryAccept(root, account, email, plan, json, observed.Value, out value, "chrome");
        }

        internal ChromeSubscriptionBridge(int port, string key, Func<string, int> receive)
        {
            _key = key; _receive = receive; _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start(8); Task.Run((Func<Task>)Listen);
        }
        internal int ListeningPort { get { return ((IPEndPoint)_listener.LocalEndpoint).Port; } }
        private async Task Listen()
        {
            while (!_stopped)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                if (!_clients.Wait(0)) { client.Close(); continue; }
                ThreadPool.QueueUserWorkItem(delegate { try { Handle(client); } finally { _clients.Release(); } });
            }
        }
        private void Handle(TcpClient client)
        {
            using (client)
            {
                client.ReceiveTimeout = 3000; client.SendTimeout = 3000;
                try
                {
                    NetworkStream stream = client.GetStream();
                    List<byte> header = new List<byte>();
                    while (header.Count < 8192)
                    {
                        int b = stream.ReadByte(); if (b < 0) return; header.Add((byte)b);
                        int n = header.Count;
                        if (n >= 4 && header[n-4] == 13 && header[n-3] == 10 && header[n-2] == 13 && header[n-1] == 10) break;
                    }
                    if (header.Count >= 8192) { Reply(stream, 400, "invalid_request"); return; }
                    string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
                    if (lines[0] != "POST /subscription HTTP/1.1") { Reply(stream, 404, "not_found"); return; }
                    Dictionary<string,string> fields = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 1; i < lines.Length && lines[i].Length > 0; i++)
                    {
                        int colon = lines[i].IndexOf(':');
                        if (colon < 1 || fields.ContainsKey(lines[i].Substring(0, colon))) { Reply(stream, 400, "invalid_request"); return; }
                        fields.Add(lines[i].Substring(0, colon), lines[i].Substring(colon + 1).Trim());
                    }
                    string host, auth, length, type;
                    if (!fields.TryGetValue("Host", out host) || host != "127.0.0.1:" + ListeningPort.ToString(CultureInfo.InvariantCulture)) { Reply(stream, 403, "forbidden"); return; }
                    if (!fields.TryGetValue("X-Codex-Meter-Key", out auth) || !SameKey(auth, _key)) { Reply(stream, 403, "forbidden"); return; }
                    int size;
                    if (fields.ContainsKey("Transfer-Encoding") || !fields.TryGetValue("Content-Type", out type) || !type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ||
                        !fields.TryGetValue("Content-Length", out length) || !Int32.TryParse(length, out size) || size < 2 || size > 65536) { Reply(stream, 400, "invalid_request"); return; }
                    byte[] bytes = new byte[size]; int offset = 0;
                    while (offset < size) { int read = stream.Read(bytes, offset, size - offset); if (read <= 0) return; offset += read; }
                    int count = _receive(new UTF8Encoding(false, true).GetString(bytes));
                    Reply(stream, count > 0 ? 200 : 409, count > 0 ? "updated" : "account_or_data_mismatch");
                }
                catch (Exception) { /* Isolate malformed requests and disconnects; never log payloads or keys. */ }
            }
        }
        private static bool SameKey(string left, string right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int different = 0; for (int i = 0; i < left.Length; i++) different |= left[i] ^ right[i]; return different == 0;
        }
        private static void Reply(Stream stream, int status, string result)
        {
            byte[] body = Encoding.UTF8.GetBytes("{\"status\":\"" + result + "\"}");
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Result\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            stream.Write(head, 0, head.Length); stream.Write(body, 0, body.Length);
        }
        public void Dispose() { _stopped = true; _listener.Stop(); }
    }
}
