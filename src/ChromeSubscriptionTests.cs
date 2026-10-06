using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexUsageMeter
{
    internal static class ChromeSubscriptionTests
    {
        internal static void Run(Action<string> report, string evidence)
        {
            DateTime now = DateTime.UtcNow;
            string json = new JavaScriptSerializer().Serialize(new {
                AccountId = "chrome-fixture", Plan = "plus", Source = "chrome",
                Subscription = new AccountSubscriptionInfo { Date = now.AddDays(20), Kind = "renewal", CheckedAt = now.AddDays(-2) }
            });
            WebSubscriptionRecord record = new JavaScriptSerializer().Deserialize<WebSubscriptionRecord>(json);
            AccountSubscriptionInfo value = WebSubscriptionStore.Fresh(record, now);
            Require(value != null && value.Date.HasValue && AccountSubscription.Format(value, now).Contains("재확인"),
                "Chrome's last observed date must remain visible and explicitly stale after 24 hours.");
            Require(!WebSubscriptionStore.IsDue(record, now), "Chrome observations must not start the embedded browser.");
            report("PASS Chrome observations retain an explicitly stale date without embedded web refresh");

            string root = Path.Combine(evidence, "chrome-subscription-fixture"); Directory.CreateDirectory(root);
            string auth = AccountSubscriptionRegressionTests.Auth("chrome-fixture", "chrome-user", "plus", null, null, "fixture");
            File.WriteAllText(Path.Combine(root, "auth.json"), auth);
            string body = Payload(now, "chrome-fixture", "plus", now.AddDays(20).ToString("o"));
            Require(ChromeSubscriptionBridge.Accept(root, "chrome-fixture", "chrome-user@example.invalid", "plus", body, now), "Matching Chrome response rejected.");
            WebSubscriptionRecord stored = WebSubscriptionStore.Load(root, "chrome-fixture", "plus");
            Require(stored.Source == "chrome" && !stored.WebProfileLinked && !WebSubscriptionStore.IsDue(stored, now.AddDays(3)), "Chrome import activated an embedded browser.");
            AccountSubscriptionInfo legacy;
            Require(!WebSubscriptionStore.TryAccept(root, "chrome-fixture", "chrome-user@example.invalid", "plus", body, now.AddSeconds(1), out legacy), "An in-flight embedded response replaced the Chrome connection.");
            Require(!ChromeSubscriptionBridge.Accept(root, "chrome-fixture", "other@example.invalid", "plus", body, now), "Wrong identity accepted.");
            Require(!ChromeSubscriptionBridge.Accept(root, "chrome-fixture", "chrome-user@example.invalid", "pro", body, now), "Wrong plan accepted.");
            Require(!ChromeSubscriptionBridge.Accept(root, "chrome-fixture", "chrome-user@example.invalid", "plus", Payload(now.AddMinutes(-11), "chrome-fixture", "plus", now.AddDays(20).ToString("o")), now), "Stale page response accepted.");
            Require(!ChromeSubscriptionBridge.Accept(root, "chrome-fixture", "chrome-user@example.invalid", "plus", Payload(now.AddMinutes(1), "chrome-fixture", "plus", now.AddDays(20).ToString("o")), now), "Future page response accepted.");
            Require(!ChromeSubscriptionBridge.Accept(root, "chrome-fixture", "chrome-user@example.invalid", "plus", Payload(now.AddSeconds(-1), "chrome-fixture", "plus", now.AddDays(20).ToString("o")), now), "Out-of-order response overwrote a newer date.");
            Require(WebSubscriptionStore.Fresh(stored, now.AddDays(21)).IsStale && AccountSubscription.Format(WebSubscriptionStore.Fresh(stored, now.AddDays(21)), now.AddDays(21)).Contains("재확인"), "Elapsed date was silently advanced or presented as current.");
            File.WriteAllText(Path.Combine(root, "auth.json"), AccountSubscriptionRegressionTests.Auth("other", "chrome-user", "plus", null, null, "fixture"));
            Require(!ChromeSubscriptionBridge.Accept(root, "chrome-fixture", "chrome-user@example.invalid", "plus", body, now), "Changed account retained import permission.");
            File.WriteAllText(Path.Combine(root, "auth.json"), auth);
            report("PASS Chrome account/plan isolation, response freshness, ordering and elapsed date handling");

            string connectionRoot = Path.Combine(root, "connection");
            string key = ChromeSubscriptionBridge.ReadKey(connectionRoot);
            Require(key.Length == 64 && ChromeSubscriptionBridge.ReadKey(connectionRoot) == key && !File.ReadAllText(Path.Combine(connectionRoot, "connection.bin")).Contains(key), "Local connection key was not protected/reused.");
            int requests = 0;
            using (ChromeSubscriptionBridge bridge = new ChromeSubscriptionBridge(0, key, delegate(string payload) {
                requests++; return ChromeSubscriptionBridge.Accept(root, "chrome-fixture", "chrome-user@example.invalid", "plus", payload, now) ? 1 : 0;
            }))
            {
                int port = bridge.ListeningPort;
                Require(Request(port, key, body, "127.0.0.1:" + port).StartsWith("HTTP/1.1 200"), "Local POST did not update the date.");
                Require(Request(port, new string('0', 64), body, "127.0.0.1:" + port).StartsWith("HTTP/1.1 403"), "Wrong connection key admitted.");
                Require(Request(port, key, body, "evil.invalid:" + port).StartsWith("HTTP/1.1 403"), "DNS-rebinding host admitted.");
                Require(Request(port, key, body, "127.0.0.1:" + port, "OPTIONS").StartsWith("HTTP/1.1 404"), "Page CORS preflight admitted.");
                Require(Request(port, key, new string('x', 65537), "127.0.0.1:" + port).StartsWith("HTTP/1.1 400"), "Oversized import admitted.");
                Require(requests == 1, "Rejected request reached the billing parser.");
                Require(Request(port, key, Payload(now.AddSeconds(1), "chrome-fixture", "plus", null), "127.0.0.1:" + port).StartsWith("HTTP/1.1 200"), "Authoritative no-date result rejected.");
                Require(!WebSubscriptionStore.Load(root, "chrome-fixture", "plus").Subscription.Date.HasValue, "New no-date result retained the old date.");
            }
            Require(File.ReadAllText(Path.Combine(root, "auth.json")) == auth, "Chrome bridge altered Codex credentials.");
            Require(!File.ReadAllText(WebSubscriptionStore.PathFor(root)).Contains("token"), "Date cache leaked credentials.");
            report("PASS real loopback POST, rejected key/host/CORS/oversize, DPAPI key reuse and credential preservation");
        }
        private static string Payload(DateTime at, string account, string plan, string date)
        {
            var data = AccountSubscription.ParseJson(AccountSubscriptionRegressionTests.Body(account, plan, date == null ? (bool?)null : true, date, null, null, null));
            data["version"] = 1; data["observedAt"] = at.ToString("o"); return new JavaScriptSerializer().Serialize(data);
        }
        private static string Request(int port, string key, string body, string host, string method = "POST")
        {
            using (TcpClient client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, port); client.ReceiveTimeout = 5000;
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                byte[] header = Encoding.ASCII.GetBytes(method + " /subscription HTTP/1.1\r\nHost: " + host + "\r\nX-Codex-Meter-Key: " + key + "\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\n\r\n");
                NetworkStream stream = client.GetStream(); stream.Write(header, 0, header.Length);
                if (bytes.Length <= 65536) stream.Write(bytes, 0, bytes.Length);
                using (StreamReader reader = new StreamReader(stream))
                {
                    string status = reader.ReadLine(), line; int length = 0;
                    while (!String.IsNullOrEmpty(line = reader.ReadLine())) if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = Int32.Parse(line.Substring(15).Trim());
                    char[] content = new char[length]; int offset = 0;
                    while (offset < length) { int read = reader.Read(content, offset, length - offset); if (read <= 0) break; offset += read; }
                    return status + "\n" + new string(content, 0, offset);
                }
            }
        }
        internal static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
