using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexUsageMeter
{
    internal sealed class WebSubscriptionRecord
    {
        public string AccountId { get; set; }
        public string Plan { get; set; }
        public AccountSubscriptionInfo Subscription { get; set; }
        public DateTime LastAttemptUtc { get; set; }
        public string LastError { get; set; }
        public bool WebProfileLinked { get; set; }
        public string Source { get; set; }
    }

    internal static class WebSubscriptionStore
    {
        private static readonly object Gate = new object();
        internal static string PathFor(string profileRoot) { return Path.Combine(profileRoot, "subscription-web.json"); }

        internal static string AccountId(string profileRoot, string expectedEmail)
        {
            try
            {
                var tokens = AccountSubscription.Map(AccountSubscription.Get(AccountSubscription.ParseJson(AccountSubscription.ReadAuthJson(profileRoot)), "tokens"));
                var payload = AccountSubscription.TokenPayload(AccountSubscription.Text(tokens, "access_token"));
                var claims = AccountSubscription.Map(AccountSubscription.Get(payload, "https://api.openai.com/auth"));
                var profile = AccountSubscription.Map(AccountSubscription.Get(payload, "https://api.openai.com/profile"));
                string email = AccountSubscription.Text(profile, "email");
                if (String.IsNullOrWhiteSpace(email)) email = AccountSubscription.Text(AccountSubscription.TokenPayload(AccountSubscription.Text(tokens, "id_token")), "email");
                string accountId = AccountSubscription.Text(tokens, "account_id");
                return !String.IsNullOrWhiteSpace(accountId) && accountId == AccountSubscription.Text(claims, "chatgpt_account_id") &&
                    !String.IsNullOrWhiteSpace(expectedEmail) && String.Equals(email, expectedEmail.Trim(), StringComparison.OrdinalIgnoreCase) ? accountId : null;
            }
            catch { return null; }
        }

        internal static WebSubscriptionRecord Load(string root, string accountId, string plan)
        {
            if (String.IsNullOrWhiteSpace(root) || String.IsNullOrWhiteSpace(accountId)) return null;
            lock (Gate)
            {
                try
                {
                    string path = PathFor(root);
                    if (!File.Exists(path) || new FileInfo(path).Length > 16384) return null;
                    WebSubscriptionRecord record = new JavaScriptSerializer().Deserialize<WebSubscriptionRecord>(File.ReadAllText(path));
                    return record != null && record.AccountId == accountId && (plan == null || AccountSubscription.Plan(record.Plan) == AccountSubscription.Plan(plan)) ? record : null;
                }
                catch { return null; }
            }
        }

        internal static AccountSubscriptionInfo Fresh(WebSubscriptionRecord record, DateTime now)
        {
            if (record == null || record.Subscription == null) return null;
            AccountSubscriptionInfo value = record.Subscription;
            DateTime checkedAt = value.CheckedAt.ToUniversalTime(); now = now.ToUniversalTime();
            if (checkedAt > now) return null;
            bool stale = now - checkedAt >= TimeSpan.FromHours(24) || (value.Date.HasValue && value.Date.Value.ToUniversalTime() <= now);
            if (stale && record.Source != "chrome") return null;
            AccountSubscriptionInfo result = value.Copy();
            result.IsStale = stale;
            // JavaScriptSerializer restores persisted timestamps as UTC.
            // Dashboard dates/countdowns are expressed in the PC's local calendar.
            if (result.Date.HasValue) result.Date = result.Date.Value.ToLocalTime();
            if (record.Source == "chrome")
            {
                result.Error = "Chrome에서 " + checkedAt.ToLocalTime().ToString("M/d HH:mm") + " 마지막 확인\n" +
                    (stale ? "저장된 이전 정보입니다. 크롬에서 구독 페이지를 열어 재확인해 주세요." : "크롬에서 구독 페이지를 열면 새 정보가 반영됩니다.") +
                    (String.IsNullOrWhiteSpace(value.Error) ? "" : "\n" + value.Error);
                return result;
            }
            result.Error = "웹 ChatGPT에서 " + checkedAt.ToLocalTime().ToString("M/d HH:mm") + " 확인" +
                (record.WebProfileLinked ? "" : "\n자동 확인을 위해 날짜를 눌러 웹 구독을 연결해 주세요.") +
                (String.IsNullOrWhiteSpace(value.Error) ? "" : "\n" + value.Error) +
                (String.IsNullOrWhiteSpace(record.LastError) ? "" : "\n최근 자동 확인 실패: " + record.LastError);
            return result;
        }

        internal static bool IsDue(WebSubscriptionRecord record, DateTime now, string currentPlan = null)
        {
            if (record == null || record.Source == "chrome" || !record.WebProfileLinked) return false;
            DateTime attempt = record.LastAttemptUtc.ToUniversalTime(); now = now.ToUniversalTime();
            if (String.IsNullOrWhiteSpace(record.LastError) && ((currentPlan != null && AccountSubscription.Plan(currentPlan) != AccountSubscription.Plan(record.Plan)) ||
                (record.Subscription != null && record.Subscription.Date.HasValue && record.Subscription.Date.Value.ToUniversalTime() <= now))) return true;
            double hours = String.IsNullOrWhiteSpace(record.LastError) ? 6 : 1;
            return now < attempt || now - attempt >= TimeSpan.FromHours(hours);
        }

        internal static bool TryAccept(string root, string expectedAccount, string expectedEmail, string plan, string json, DateTime now, out AccountSubscriptionInfo value, string source = null)
        {
            lock (Gate)
            {
            value = null;
            var accounts = AccountSubscription.Map(AccountSubscription.Get(AccountSubscription.ParseJson(json), "accounts"));
            var details = AccountSubscription.Map(AccountSubscription.Get(accounts, expectedAccount));
            var account = AccountSubscription.Map(AccountSubscription.Get(details, "account"));
            if (String.IsNullOrWhiteSpace(expectedAccount) || AccountId(root, expectedEmail) != expectedAccount ||
                AccountSubscription.Text(account, "account_id") != expectedAccount ||
                AccountSubscription.Plan(AccountSubscription.Text(account, "plan_type")) != AccountSubscription.Plan(plan)) return false;
            WebSubscriptionRecord previous = Load(root, expectedAccount, null);
            if (previous != null && previous.Source == "chrome" && source != "chrome") return false;
            if (source == "chrome" && previous != null && previous.Subscription != null && previous.Subscription.CheckedAt.ToUniversalTime() > now.ToUniversalTime()) return false;
            value = AccountSubscription.ParseAccountResponse(json, expectedAccount, plan, now);
            Save(root, new WebSubscriptionRecord { AccountId = expectedAccount, Plan = plan, Subscription = value, LastAttemptUtc = now.ToUniversalTime(), WebProfileLinked = source != "chrome", Source = source });
            return true;
            }
        }

        internal static void Failed(string root, string accountId, string plan, string error, DateTime now)
        {
            lock (Gate)
            {
                WebSubscriptionRecord record = Load(root, accountId, null);
                if (record == null) return;
                record.LastAttemptUtc = now.ToUniversalTime(); record.LastError = error;
                try { Save(root, record); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        internal static void Save(string root, WebSubscriptionRecord record) { Save(PathFor(root), root, record); }

        internal static void Save(string path, string root, WebSubscriptionRecord record)
        {
            lock (Gate)
            {
                Directory.CreateDirectory(root);
                string temp = path + ".new";
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(record), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
        }

        internal static WebSubscriptionRecord LoadFile(string path, string accountId)
        {
            lock (Gate)
            {
                try
                {
                    if (!File.Exists(path) || new FileInfo(path).Length > 16384) return null;
                    WebSubscriptionRecord record = new JavaScriptSerializer().Deserialize<WebSubscriptionRecord>(File.ReadAllText(path));
                    return record != null && record.AccountId == accountId ? record : null;
                }
                catch { return null; }
            }
        }
    }

    // claude.ai subscription date observed by the Tampermonkey script in the person's own Chrome (same bridge as ChatGPT).
    // Only email, organization uuid, status and billing dates arrive; cookies and tokens never leave the page.
    // Field meanings follow CodexBar docs/claude.md (/api/organizations/{org}/subscription_details).
    internal static class ClaudeSubscriptionStore
    {
        private static readonly object Gate = new object();
        internal static string PathFor(string root) { return Path.Combine(root, "subscription-claude.json"); }
        private static string Key(string account, string organization) { return account.ToLowerInvariant() + "/" + organization.ToLowerInvariant(); }

        // Stores the slot's own organization entry when the payload's email and organization match its verified OAuth owner.
        internal static bool Accept(string root, string account, string organization, string email, string json, DateTime now)
        {
            if (String.IsNullOrEmpty(json) || json.Length > 65536 || String.IsNullOrWhiteSpace(account) ||
                String.IsNullOrWhiteSpace(email) || String.IsNullOrWhiteSpace(root)) return false;
            Guid expected;
            if (!Guid.TryParse(organization, out expected)) return false;
            Dictionary<string, object> body;
            try { body = AccountSubscription.ParseJson(json); }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            if (Convert.ToString(AccountSubscription.Get(body, "version"), System.Globalization.CultureInfo.InvariantCulture) != "1" ||
                AccountSubscription.Text(body, "kind") != "claude") return false;
            DateTime? observed = AccountSubscription.Timestamp(AccountSubscription.Get(body, "observedAt"));
            DateTime utcNow = now.ToUniversalTime();
            if (!observed.HasValue || observed.Value > utcNow.AddSeconds(30) || observed.Value < utcNow.AddMinutes(-10)) return false;
            string pageEmail = AccountSubscription.Text(body, "email");
            if (pageEmail == null || !String.Equals(pageEmail.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
            Dictionary<string, object> match = null;
            foreach (object item in JsonValue.AsItems(AccountSubscription.Get(body, "organizations")))
            {
                Dictionary<string, object> entry = AccountSubscription.Map(item);
                Guid id;
                if (Guid.TryParse(AccountSubscription.Text(entry, "uuid"), out id) && id == expected) { match = entry; break; }
            }
            if (match == null) return false;
            string key = Key(account, organization), path = PathFor(root);
            lock (Gate)
            {
                WebSubscriptionRecord previous = WebSubscriptionStore.LoadFile(path, key);
                // An older page result never replaces a newer one.
                if (previous != null && previous.Subscription != null && previous.Subscription.CheckedAt.ToUniversalTime() > observed.Value) return false;
                WebSubscriptionStore.Save(path, root, new WebSubscriptionRecord { AccountId = key, Subscription = Parse(match, observed.Value),
                    LastAttemptUtc = observed.Value, Source = "chrome" });
            }
            return true;
        }

        // Null until Chrome has delivered this owner's data; otherwise the same freshness rules as the ChatGPT Chrome date.
        internal static AccountSubscriptionInfo Read(string root, string account, string organization, DateTime now)
        {
            if (String.IsNullOrWhiteSpace(root) || String.IsNullOrWhiteSpace(account) || String.IsNullOrWhiteSpace(organization)) return null;
            return WebSubscriptionStore.Fresh(WebSubscriptionStore.LoadFile(PathFor(root), Key(account, organization)), now);
        }

        // A scheduled ending (plan_ending_*) wins over the next charge; a canceled plan has no renewal.
        internal static AccountSubscriptionInfo Parse(Dictionary<string, object> fields, DateTime nowUtc)
        {
            nowUtc = nowUtc.ToUniversalTime();
            string status = AccountSubscription.Text(fields, "status");
            if (status != "active" && status != "trialing" && status != "canceled")
                return AccountSubscription.Unavailable("claude.ai 구독 상태를 확인하지 못했습니다" + (String.IsNullOrWhiteSpace(status) ? "." : "(" + status + ")."), nowUtc);
            DateTime? end, renewal = null;
            bool valid = BillingDate(fields, "plan_ending_at", out end) && (end.HasValue || BillingDate(fields, "plan_ending_before", out end));
            if (valid && !end.HasValue && status != "canceled")
                valid = BillingDate(fields, "next_charge_at", out renewal) && (renewal.HasValue || BillingDate(fields, "next_charge_date", out renewal));
            if (!valid) return AccountSubscription.Unavailable("claude.ai 구독 날짜 형식을 확인하지 못했습니다.", nowUtc);
            DateTime? date = end ?? renewal;
            if (!date.HasValue || date.Value.ToUniversalTime() <= nowUtc && date.Value.Date < nowUtc.ToLocalTime().Date)
                return AccountSubscription.Unavailable("claude.ai 응답에 현재 유효한 다음 결제일이나 종료일이 없습니다.", nowUtc);
            return new AccountSubscriptionInfo { Date = date, Kind = end.HasValue ? "end" : "renewal", CheckedAt = nowUtc };
        }

        // A calendar-only date stays on its own day in local time; a timestamp needs an explicit zone.
        private static bool BillingDate(Dictionary<string, object> fields, string key, out DateTime? date)
        {
            date = null;
            object value = AccountSubscription.Get(fields, key);
            if (value == null) return true;
            string text = value as string;
            if (text == null || text.Length > 64) return false;
            DateTime day;
            if (text.Length == 10)
            {
                if (!DateTime.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out day)) return false;
                date = DateTime.SpecifyKind(day, DateTimeKind.Local);
                return true;
            }
            DateTimeOffset parsed;
            if (!(text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(text, @"[+-]\d{2}:?\d{2}$")) ||
                !DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out parsed)) return false;
            date = parsed.UtcDateTime.ToLocalTime();
            return true;
        }
    }
}
