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
            if (checkedAt > now || now - checkedAt >= TimeSpan.FromHours(24) || (value.Date.HasValue && value.Date.Value.ToUniversalTime() <= now)) return null;
            AccountSubscriptionInfo result = value.Copy();
            // JavaScriptSerializer restores persisted timestamps as UTC.
            // Dashboard dates/countdowns are expressed in the PC's local calendar.
            if (result.Date.HasValue) result.Date = result.Date.Value.ToLocalTime();
            result.Error = "웹 ChatGPT에서 " + checkedAt.ToLocalTime().ToString("M/d HH:mm") + " 확인" +
                (record.WebProfileLinked ? "" : "\n자동 확인을 위해 날짜를 눌러 웹 구독을 연결해 주세요.") +
                (String.IsNullOrWhiteSpace(value.Error) ? "" : "\n" + value.Error) +
                (String.IsNullOrWhiteSpace(record.LastError) ? "" : "\n최근 자동 확인 실패: " + record.LastError);
            return result;
        }

        internal static bool IsDue(WebSubscriptionRecord record, DateTime now, string currentPlan = null)
        {
            if (record == null || !record.WebProfileLinked) return false;
            DateTime attempt = record.LastAttemptUtc.ToUniversalTime(); now = now.ToUniversalTime();
            if (String.IsNullOrWhiteSpace(record.LastError) && ((currentPlan != null && AccountSubscription.Plan(currentPlan) != AccountSubscription.Plan(record.Plan)) ||
                (record.Subscription != null && record.Subscription.Date.HasValue && record.Subscription.Date.Value.ToUniversalTime() <= now))) return true;
            double hours = String.IsNullOrWhiteSpace(record.LastError) ? 6 : 1;
            return now < attempt || now - attempt >= TimeSpan.FromHours(hours);
        }

        internal static bool TryAccept(string root, string expectedAccount, string expectedEmail, string plan, string json, DateTime now, out AccountSubscriptionInfo value)
        {
            value = null;
            var accounts = AccountSubscription.Map(AccountSubscription.Get(AccountSubscription.ParseJson(json), "accounts"));
            var details = AccountSubscription.Map(AccountSubscription.Get(accounts, expectedAccount));
            var account = AccountSubscription.Map(AccountSubscription.Get(details, "account"));
            if (String.IsNullOrWhiteSpace(expectedAccount) || AccountId(root, expectedEmail) != expectedAccount ||
                AccountSubscription.Text(account, "account_id") != expectedAccount ||
                AccountSubscription.Plan(AccountSubscription.Text(account, "plan_type")) != AccountSubscription.Plan(plan)) return false;
            value = AccountSubscription.ParseAccountResponse(json, expectedAccount, plan, now);
            Save(root, new WebSubscriptionRecord { AccountId = expectedAccount, Plan = plan, Subscription = value, LastAttemptUtc = now.ToUniversalTime(), WebProfileLinked = true });
            return true;
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

        internal static void Save(string root, WebSubscriptionRecord record)
        {
            lock (Gate)
            {
                Directory.CreateDirectory(root);
                string path = PathFor(root), temp = path + ".new";
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(record), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
        }
    }
}
