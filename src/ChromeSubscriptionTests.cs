using System;
using System.Collections.Generic;
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
                Require(Request(port, new string('0', 64), body, "127.0.0.1:" + port, sendBody: false).StartsWith("HTTP/1.1 403"), "Wrong connection key admitted.");
                Require(Request(port, key, body, "evil.invalid:" + port, sendBody: false).StartsWith("HTTP/1.1 403"), "DNS-rebinding host admitted.");
                Require(Request(port, key, body, "127.0.0.1:" + port, "OPTIONS", false).StartsWith("HTTP/1.1 404"), "Page CORS preflight admitted.");
                Require(Request(port, key, new string('x', 65537), "127.0.0.1:" + port).StartsWith("HTTP/1.1 400"), "Oversized import admitted.");
                Require(requests == 1, "Rejected request reached the billing parser.");
                Require(Request(port, key, Payload(now.AddSeconds(1), "chrome-fixture", "plus", null), "127.0.0.1:" + port).StartsWith("HTTP/1.1 200"), "Authoritative no-date result rejected.");
                Require(!WebSubscriptionStore.Load(root, "chrome-fixture", "plus").Subscription.Date.HasValue, "New no-date result retained the old date.");
            }
            Require(File.ReadAllText(Path.Combine(root, "auth.json")) == auth, "Chrome bridge altered Codex credentials.");
            Require(!File.ReadAllText(WebSubscriptionStore.PathFor(root)).Contains("token"), "Date cache leaked credentials.");
            report("PASS real loopback POST, rejected key/host/CORS/oversize, DPAPI key reuse and credential preservation");
            ClaudeRouting(Path.Combine(evidence, "chrome-claude-fixture"), root, now);
            report("PASS claude.ai payloads reach only the Claude slot whose OAuth email+organization match; mismatch/unknown/logged-out slots and ChatGPT slots ignore them");
        }

        private const string ClaudeAccountA = "11111111-1111-4111-8111-111111111111", ClaudeAccountB = "22222222-2222-4222-8222-222222222222";
        private const string ClaudeOrgA = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", ClaudeOrgB = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";

        // Same shape the userscript posts from claude.ai (version, kind, observedAt, email, organizations[]).
        internal static string ClaudePayload(DateTime at, string email, params object[] organizations)
        {
            return new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "version", 1 }, { "kind", "claude" },
                { "observedAt", at.ToString("o") }, { "email", email }, { "organizations", organizations } });
        }
        internal static Dictionary<string, object> ClaudeOrganization(string uuid, string status, string nextChargeAt, string nextChargeDate, string endingAt, string endingBefore)
        {
            return new Dictionary<string, object> { { "uuid", uuid }, { "status", status }, { "next_charge_at", nextChargeAt }, { "next_charge_date", nextChargeDate },
                { "plan_ending_at", endingAt }, { "plan_ending_before", endingBefore } };
        }

        private static void ClaudeRouting(string root, string chatGptRoot, DateTime now)
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            string slotA = Path.Combine(root, "account-1"), slotB = Path.Combine(root, "account-2"), slotOut = Path.Combine(root, "account-3");
            foreach (string slot in new[] { slotA, slotB, slotOut }) Directory.CreateDirectory(slot);
            ChromeSubscriptionBridge.Stop();
            try
            {
                ChromeSubscriptionBridge.RegisterClaude(slotA, ClaudeAccountA, ClaudeOrgA, "a@example.test");
                ChromeSubscriptionBridge.RegisterClaude(slotB, ClaudeAccountB, ClaudeOrgB, "b@example.test");
                ChromeSubscriptionBridge.RegisterClaude(slotOut, null, null, null); // logged-out slot
                string charge = now.AddDays(12).ToString("yyyy-MM-ddTHH:mm:ssZ");
                Dictionary<string, object> renewal = ClaudeOrganization(ClaudeOrgA, "active", charge, null, null, null);
                renewal["billing_email"] = "secret@example.test"; renewal["sessionKey"] = "sk-ant-secret-cookie";

                // Match: only slot A (same email, case-insensitive, and its own organization among several).
                Require(ChromeSubscriptionBridge.Route(ClaudePayload(now, "A@Example.test", ClaudeOrganization(ClaudeOrgB, "active", null, null, null, null), renewal), now) == 1,
                    "Matching claude.ai payload was not routed to exactly one slot.");
                AccountSubscriptionInfo a = ClaudeSubscriptionStore.Read(slotA, ClaudeAccountA, ClaudeOrgA, now);
                Require(a != null && a.Kind == "renewal" && a.Date.Value.ToUniversalTime() == DateTime.Parse(charge, null, System.Globalization.DateTimeStyles.AdjustToUniversal) &&
                    a.Date.Value.Kind == DateTimeKind.Local && AccountSubscription.Format(a, now.ToLocalTime().Date).StartsWith("구독 갱신 ") && a.Error.StartsWith("Chrome에서 "),
                    "Routed renewal date was not stored/displayed like the ChatGPT Chrome date.");
                Require(ClaudeSubscriptionStore.Read(slotB, ClaudeAccountB, ClaudeOrgB, now) == null && !File.Exists(ClaudeSubscriptionStore.PathFor(slotOut)),
                    "Another or a logged-out slot received slot A's data.");
                string stored = File.ReadAllText(ClaudeSubscriptionStore.PathFor(slotA));
                Require(!stored.Contains("secret") && !stored.Contains("sk-ant") && !stored.Contains("a@example.test"), "Stored record contains page secrets or the email.");
                Require(ClaudeSubscriptionStore.Read(slotA, ClaudeAccountB, ClaudeOrgA, now) == null && ClaudeSubscriptionStore.Read(slotA, ClaudeAccountA, ClaudeOrgB, now) == null,
                    "Another OAuth owner of the slot inherited the stored date.");

                // Mismatch: wrong email, or the email without the slot's organization, changes nothing.
                Require(ChromeSubscriptionBridge.Route(ClaudePayload(now.AddSeconds(1), "other@example.test", ClaudeOrganization(ClaudeOrgA, "active", charge, null, null, null)), now) == 0 &&
                    ChromeSubscriptionBridge.Route(ClaudePayload(now.AddSeconds(1), "b@example.test", ClaudeOrganization(ClaudeOrgA, "active", charge, null, null, null)), now) == 0 &&
                    File.ReadAllText(ClaudeSubscriptionStore.PathFor(slotA)) == stored && ClaudeSubscriptionStore.Read(slotB, ClaudeAccountB, ClaudeOrgB, now) == null,
                    "A mismatched claude.ai payload was stored.");
                // Unknown slot: nobody registered for this account.
                Require(ChromeSubscriptionBridge.Route(ClaudePayload(now.AddSeconds(1), "c@example.test", ClaudeOrganization("cccccccc-cccc-4ccc-8ccc-cccccccccccc", "active", charge, null, null, null)), now) == 0,
                    "A payload without a matching slot was accepted.");
                // Freshness and ordering like ChatGPT.
                Require(ChromeSubscriptionBridge.Route(ClaudePayload(now.AddMinutes(-11), "a@example.test", renewal), now) == 0 &&
                    ChromeSubscriptionBridge.Route(ClaudePayload(now.AddMinutes(1), "a@example.test", renewal), now) == 0 &&
                    ChromeSubscriptionBridge.Route(ClaudePayload(now.AddSeconds(-1), "a@example.test", renewal), now) == 0, "Stale, future or out-of-order payload accepted.");
                Require(ChromeSubscriptionBridge.Route(new JavaScriptSerializer().Serialize(new { version = 2, kind = "claude", observedAt = now.ToString("o"),
                    email = "a@example.test", organizations = new[] { renewal } }), now) == 0, "Unknown payload version accepted.");

                // Ending date wins over the next charge; a calendar-only ending stays on its own local day.
                string endDay = now.ToLocalTime().AddDays(20).ToString("yyyy-MM-dd");
                Require(ChromeSubscriptionBridge.Route(ClaudePayload(now.AddSeconds(2), "a@example.test",
                    ClaudeOrganization(ClaudeOrgA, "active", charge, null, null, endDay)), now.AddSeconds(2)) == 1, "Ending payload rejected.");
                a = ClaudeSubscriptionStore.Read(slotA, ClaudeAccountA, ClaudeOrgA, now.AddSeconds(2));
                Require(a.Kind == "end" && a.Date.Value.ToString("yyyy-MM-dd") == endDay && AccountSubscription.Format(a, now.ToLocalTime().Date).StartsWith("구독 종료 "),
                    "plan_ending_before did not win over next_charge_at.");
                // Canceled without a date, unknown status and malformed dates never invent a date.
                foreach (Dictionary<string, object> entry in new[] { ClaudeOrganization(ClaudeOrgA, "canceled", charge, null, null, null),
                    ClaudeOrganization(ClaudeOrgA, "paused", charge, null, null, null), ClaudeOrganization(ClaudeOrgA, "active", "next week", null, null, null),
                    ClaudeOrganization(ClaudeOrgA, "active", null, "2030-02-30", null, null), ClaudeOrganization(ClaudeOrgA, "active", null, null, null, null) })
                {
                    AccountSubscriptionInfo parsed = ClaudeSubscriptionStore.Parse(entry, now);
                    Require(!parsed.Date.HasValue && !String.IsNullOrWhiteSpace(parsed.Error) && AccountSubscription.Format(parsed, now) == "구독 날짜 조회 불가",
                        "A claude.ai subscription without a valid date displayed one.");
                }

                // ChatGPT payloads never reach Claude slots, and claude.ai payloads never reach ChatGPT slots.
                ChromeSubscriptionBridge.Register(chatGptRoot, "chrome-user@example.invalid", "plus");
                string chatGptBefore = File.ReadAllText(WebSubscriptionStore.PathFor(chatGptRoot)), claudeBefore = File.ReadAllText(ClaudeSubscriptionStore.PathFor(slotA));
                Require(ChromeSubscriptionBridge.Route(ClaudePayload(now.AddSeconds(3), "chrome-user@example.invalid", ClaudeOrganization(ClaudeOrgA, "active", charge, null, null, null)), now.AddSeconds(3)) == 0 &&
                    File.ReadAllText(WebSubscriptionStore.PathFor(chatGptRoot)) == chatGptBefore, "A claude.ai payload reached a ChatGPT slot.");
                Require(ChromeSubscriptionBridge.Route(Payload(now.AddSeconds(3), "chrome-fixture", "plus", now.AddDays(25).ToString("o")), now.AddSeconds(3)) == 1 &&
                    File.ReadAllText(ClaudeSubscriptionStore.PathFor(slotA)) == claudeBefore, "The unchanged ChatGPT payload (old script) stopped working or reached a Claude slot.");
            }
            finally { ChromeSubscriptionBridge.Stop(); }
        }
        private static string Payload(DateTime at, string account, string plan, string date)
        {
            var data = AccountSubscription.ParseJson(AccountSubscriptionRegressionTests.Body(account, plan, date == null ? (bool?)null : true, date, null, null, null));
            data["version"] = 1; data["observedAt"] = at.ToString("o"); return new JavaScriptSerializer().Serialize(data);
        }
        private static string Request(int port, string key, string body, string host, string method = "POST", bool sendBody = true)
        {
            using (TcpClient client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, port); client.ReceiveTimeout = 5000;
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                byte[] header = Encoding.ASCII.GetBytes(method + " /subscription HTTP/1.1\r\nHost: " + host + "\r\nX-Codex-Meter-Key: " + key + "\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\n\r\n");
                NetworkStream stream = client.GetStream(); stream.Write(header, 0, header.Length);
                // Rejected headers must get a full response without waiting for a body.
                // Sending an unread body races the early close and can cause a TCP reset on Windows CI.
                if (sendBody && bytes.Length <= 65536) stream.Write(bytes, 0, bytes.Length);
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
