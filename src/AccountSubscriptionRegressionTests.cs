using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexUsageMeter
{
    internal static class AccountSubscriptionRegressionTests
    {
        public static void Run(Action<string> report)
        {
            List<string> failures = new List<string>();
            Check("scheduled plan change is not mislabeled as renewal", delegate {
                string body = Body("account-a", "plus", true, "2026-10-31T00:00:00Z", null, null, null)
                    .Replace("\"renews_at\":", "\"scheduled_plan_change\":{\"changes_at\":\"2026-10-30T00:00:00Z\",\"plan_type\":\"prolite\"},\"renews_at\":");
                AccountSubscriptionInfo change = Parse(body, Now());
                Require(change.Kind == "change" && IsDate(change, 2026, 10, 30), "A scheduled plan change was displayed as renewal of the current plan.");
                Require(AccountSubscription.Format(change, new DateTime(2026, 10, 6)).Contains("→ Pro 100"), "The destination plan was omitted.");
            }, report, failures);
            Check("live renewal, cancellation and period semantics", delegate {
                DateTime now = Now();
                AccountSubscriptionInfo renewal = Parse(Body("account-a", "plus", true, "2026-10-31T00:00:00Z", null, null, null), now);
                Require(renewal.Kind == "renewal" && IsDate(renewal, 2026, 10, 31), "A live renewal was not read from entitlement.renews_at.");
                Require(AccountSubscription.Format(renewal, new DateTime(2026, 10, 6)) == "구독 갱신 10/31 · 25일 남음", "The account renewal countdown is incorrect.");
                AccountSubscriptionInfo cancelled = Parse(Body("account-a", "plus", false, "2026-10-31T00:00:00Z", null, null, null), now);
                Require(cancelled.Kind == "end" && IsDate(cancelled, 2026, 10, 31), "A canceled subscription was mislabeled as renewing.");
                AccountSubscriptionInfo expiry = Parse(Body("account-a", "plus", false, null, null, "2026-11-02T00:00:00Z", null), now);
                Require(expiry.Kind == "end" && IsDate(expiry, 2026, 11, 2), "A confirmed expiry was not displayed as an end.");
                AccountSubscriptionInfo period = Parse(Body("account-a", "plus", null, null, null, null, "2026-11-03T00:00:00Z"), now);
                Require(period.Kind == "period" && IsDate(period, 2026, 11, 3) && AccountSubscription.Format(period, now).StartsWith("이용기간 "), "An access period was inferred as automatic renewal.");
                AccountSubscriptionInfo overdue = Parse(Body("account-a", "plus", true, "2026-09-30T00:00:00Z", null, null, null), now);
                Require(!overdue.Date.HasValue && AccountSubscription.Format(overdue, now) == "구독 날짜 조회 불가", "A past date was advanced or presented as current.");
            }, report, failures);
            Check("response account and current-plan binding", delegate {
                DateTime now = Now();
                Require(!Parse(Body("another-account", "plus", true, "2026-10-31T00:00:00Z", null, null, null), now).Date.HasValue, "The default or first account was used instead of the requested account.");
                Require(!Parse(Body("account-a", "pro", true, "2026-10-31T00:00:00Z", null, null, null), now).Date.HasValue, "A date from a different live plan was reused.");
                string mismatch = Body("account-a", "plus", true, "2026-10-31T00:00:00Z", null, null, null).Replace("\"account_id\":\"account-a\"", "\"account_id\":\"another-account\"");
                Require(!Parse(mismatch, now).Date.HasValue, "Conflicting account identity metadata was ignored.");
                Require(!Parse("{\"accounts\":[]}", now).Date.HasValue && !Parse("broken", now).Date.HasValue, "Unsupported or malformed response data produced a date.");
                Require(!Parse(Body("account-a", "plus", null, null, null, null, null), now).Date.HasValue, "A missing date was synthesized.");
            }, report, failures);
            Check("fresh claim fallback excludes stale, expired and mismatched dates", delegate {
                DateTime now = Now();
                string fresh = Auth("account-a", "user-a", "plus", "2026-10-31T00:00:00Z", "2026-10-06T00:00:00Z", "one");
                DateTime? actual = AccountSubscription.ParseFreshClaim(fresh, "plus", now);
                Require(actual.HasValue && actual.Value.ToUniversalTime() == new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc), "A fresh matching claim was not preserved as a local timestamp.");
                Require(!AccountSubscription.ParseFreshClaim(Auth("account-a", "user-a", "plus", "2026-10-31T00:00:00Z", "2026-10-05T11:59:59Z", "one"), "plus", now).HasValue, "A claim older than 24 hours appears fresh.");
                Require(!AccountSubscription.ParseFreshClaim(Auth("account-a", "user-a", "plus", "2026-10-31T00:00:00Z", "2026-10-06T12:00:01Z", "one"), "plus", now).HasValue, "A future last-checked timestamp bypassed freshness validation.");
                Require(!AccountSubscription.ParseFreshClaim(Auth("account-a", "user-a", "plus", "2026-09-30T00:00:00Z", "2026-10-06T00:00:00Z", "one"), "plus", now).HasValue, "A past claim was advanced into the future.");
                Require(!AccountSubscription.ParseFreshClaim(fresh, "pro", now).HasValue, "A cached plan was substituted for the live plan.");
                Require(!AccountSubscription.ParseFreshClaim(Auth("account-a", "user-a", "plus", null, "2026-10-06T00:00:00Z", "one"), "plus", now).HasValue, "JWT exp was substituted for the subscription date.");
                Require(!AccountSubscription.ParseFreshClaim(Auth("account-a", "user-a", "plus", "2026-10-31T00:00:00Z", null, "one"), "plus", now).HasValue, "A claim without a freshness timestamp was accepted.");
                Require(!AccountSubscription.ParseFreshClaim("broken", "plus", now).HasValue && !AccountSubscription.ReadFreshClaim(null, "plus", now).HasValue, "Invalid or missing local data did not fail closed.");
            }, report, failures);
            Check("normal read request and success cache", delegate {
                DateTime now = Now(); int requests = 0;
                string auth = Auth("account-a", "user-a", "plus", null, null, "one");
                AccountSubscriptionReader reader = new AccountSubscriptionReader(() => auth, delegate(HttpWebRequest request) {
                    requests++;
                    Require(request.Method == "GET" && request.RequestUri.AbsoluteUri == "https://chatgpt.com/backend-api/accounts/check/v4-2023-04-27", "The reader is not using the evidenced read-only account endpoint.");
                    Require(!request.AllowAutoRedirect && request.CookieContainer == null && request.Timeout <= 6000 && request.ReadWriteTimeout <= 6000, "The request can follow credentials to redirects, use browser cookies, or wait too long.");
                    Require(request.Headers["ChatGPT-Account-Id"] == "account-a" && request.Headers["Authorization"].StartsWith("Bearer "), "The normal account authorization headers are missing.");
                    return Task.FromResult(Response(200, Body("account-a", "plus", true, "2026-10-31T00:00:00Z", null, null, null)));
                }, () => now);
                AccountSubscriptionInfo first = Read(reader, "plus");
                Require(first.Date.HasValue && requests == 1, "The automatic reader did not retrieve a live date.");
                first.Date = null; now = now.AddMinutes(59);
                Require(Read(reader, "plus").Date.HasValue && requests == 1, "A cache hit refetched data or leaked mutable cache state.");
                now = now.AddMinutes(2); Read(reader, "plus");
                Require(requests == 2, "Successful dates were not refreshed after one hour.");
            }, report, failures);
            Check("failure cooldown and identity invalidation", delegate {
                DateTime now = Now(); int requests = 0;
                string auth = Auth("account-a", "user-a", "plus", null, null, "one");
                AccountSubscriptionReader reader = new AccountSubscriptionReader(() => auth, delegate(HttpWebRequest request) {
                    requests++; return Task.FromResult(Response(403, "<html>Just a moment... challenge-platform fake-sensitive-value</html>"));
                }, () => now);
                AccountSubscriptionInfo failure = Read(reader, "plus");
                Require(!failure.Date.HasValue && failure.Error != null && failure.Error.Contains("보안 확인") && !failure.Error.Contains("fake-sensitive"), "A security challenge was hidden or its raw body leaked into the UI.");
                now = now.AddMinutes(14); Read(reader, "plus"); Require(requests == 1, "An unchanged failure retried before the cooldown ended.");
                now = now.AddMinutes(2); Read(reader, "plus"); Require(requests == 2, "Failure retry did not resume after 15 minutes.");
                auth = Auth("account-b", "user-a", "plus", null, null, "one"); Read(reader, "plus"); Require(requests == 3, "An account change reused the previous cache.");
                auth = Auth("account-b", "user-b", "plus", null, null, "one"); Read(reader, "plus", "user-b@example.invalid"); Require(requests == 4, "A user change reused the previous cache.");
                Read(reader, "pro", "user-b@example.invalid"); Require(requests == 5, "A live plan change reused the previous cache.");
                auth = Auth("account-b", "user-b", "plus", null, null, "two"); Read(reader, "pro", "user-b@example.invalid"); Require(requests == 6, "Replaced credentials reused a failed cache entry.");
            }, report, failures);
            Check("concurrent reads share one request and account changes discard results", delegate {
                DateTime now = Now(); int requests = 0;
                string auth = Auth("account-a", "user-a", "plus", null, null, "one");
                TaskCompletionSource<AccountSubscriptionResponse> pending = new TaskCompletionSource<AccountSubscriptionResponse>();
                AccountSubscriptionReader reader = new AccountSubscriptionReader(() => auth, delegate(HttpWebRequest request) { requests++; return pending.Task; }, () => now);
                Task<AccountSubscriptionInfo> first = reader.ReadAsync("plus", "user-a@example.invalid"), second = reader.ReadAsync("plus", "user-a@example.invalid");
                Require(requests == 1 && !first.IsCompleted && !second.IsCompleted, "Concurrent refreshes started duplicate requests.");
                pending.SetResult(Response(200, Body("account-a", "plus", true, "2026-10-31T00:00:00Z", null, null, null)));
                Task.WaitAll(first, second);
                Require(first.Result.Date.HasValue && second.Result.Date.HasValue && requests == 1, "Concurrent callers did not share the cached result.");
                pending = new TaskCompletionSource<AccountSubscriptionResponse>(); now = now.AddHours(2);
                Task<AccountSubscriptionInfo> changing = reader.ReadAsync("plus", "user-a@example.invalid");
                auth = Auth("account-b", "user-b", "plus", null, null, "one");
                pending.SetResult(Response(200, Body("account-a", "plus", true, "2026-10-31T00:00:00Z", null, null, null)));
                AccountSubscriptionInfo discarded = changing.GetAwaiter().GetResult();
                Require(!discarded.Date.HasValue && discarded.Error != null && discarded.Error.Contains("변경"), "A response was applied after its account changed.");
            }, report, failures);
            Check("fallback and unavailable states never fabricate dates", delegate {
                DateTime now = Now(); int requests = 0;
                string auth = Auth("account-a", "user-a", "plus", "2026-10-31T00:00:00Z", "2026-10-06T00:00:00Z", "one");
                AccountSubscriptionReader reader = new AccountSubscriptionReader(() => auth, delegate(HttpWebRequest request) { requests++; return Task.FromResult(Response(403, "challenge-platform")); }, () => now);
                AccountSubscriptionInfo period = Read(reader, "plus");
                Require(period.Kind == "period" && period.Date.HasValue, "A verified fresh local access period is not available as a fallback.");
                Require(period.Error != null && period.Error.Contains("403") && period.Error.Contains("24시간"), "A fallback hid the live-query failure or claim freshness limit.");
                now = now.AddHours(13); AccountSubscriptionInfo stale = Read(reader, "plus");
                Require(!stale.Date.HasValue, "A claim fallback outlived its 24-hour freshness boundary.");
                string savedAuth = auth; auth = "broken"; int before = requests;
                Require(!Read(reader, "plus").Date.HasValue && requests == before, "Malformed credentials were sent to the network or reused an old result.");
                auth = savedAuth;
                AccountSubscriptionReader networkFailure = new AccountSubscriptionReader(() => auth, request => { throw new InvalidOperationException("sensitive exception detail"); }, () => now);
                AccountSubscriptionInfo unavailable = Read(networkFailure, "plus");
                Require(!unavailable.Date.HasValue && unavailable.Error != null && !unavailable.Error.Contains("sensitive exception"), "An exception exposed credentials or produced a date.");
            }, report, failures);
            Check("current server data outranks cached claims and native periods tolerate missing ID claims", delegate {
                DateTime now = Now();
                string auth = Auth("account-a", "user-a", "plus", "2026-10-31T00:00:00Z", "2026-10-06T00:00:00Z", "one");
                AccountSubscriptionReader mismatched = new AccountSubscriptionReader(() => auth,
                    request => Task.FromResult(Response(200, Body("account-a", "pro", true, "2026-11-30T00:00:00Z", null, null, null))), () => now);
                Require(!Read(mismatched, "plus").Date.HasValue, "A cached claim overrode a newer server response with a different plan.");
                JavaScriptSerializer json = new JavaScriptSerializer();
                Dictionary<string, object> stored = json.Deserialize<Dictionary<string, object>>(auth);
                ((Dictionary<string, object>)stored["tokens"])["id_token"] = "invalid.#.data"; auth = json.Serialize(stored);
                AccountSubscriptionReader period = new AccountSubscriptionReader(() => auth,
                    request => Task.FromResult(Response(200, Body("account-a", "plus", null, null, null, null, "2026-11-03T00:00:00Z"))), () => now);
                Require(Read(period, "plus").Kind == "period", "A valid live period depends on an unrelated cached ID claim.");
                int requests = 0;
                auth = Auth("account-a", "user-a", "plus", "2026-09-30T00:00:00Z", "2026-08-29T00:00:00Z", "one");
                AccountSubscriptionReader cachedPeriod = new AccountSubscriptionReader(() => auth, delegate(HttpWebRequest request) {
                    requests++; return Task.FromResult(Response(200, Body("account-a", "plus", null, null, null, null, "2026-11-03T00:00:00Z")));
                }, () => now);
                Require(Read(cachedPeriod, "plus").Kind == "period", "A live period was replaced by an older JWT date.");
                now = now.AddMinutes(1); Read(cachedPeriod, "plus");
                Require(requests == 1, "An old JWT freshness date expired a successful live period cache.");
            }, report, failures);
            Check("snapshot identity must match the billing credentials", delegate {
                DateTime now = Now(); int requests = 0;
                string auth = Auth("account-a", "user-a", "plus", null, null, "one");
                AccountSubscriptionReader reader = new AccountSubscriptionReader(() => auth, delegate(HttpWebRequest request) {
                    requests++; return Task.FromResult(Response(200, Body("account-a", "plus", true, "2026-10-31T00:00:00Z", null, null, null)));
                }, () => now);
                Require(reader.ReadAsync("plus", "USER-A@example.invalid").GetAwaiter().GetResult().Date.HasValue, "Equivalent snapshot identity was rejected.");
                AccountSubscriptionInfo mismatch = reader.ReadAsync("plus", "user-b@example.invalid").GetAwaiter().GetResult();
                Require(!mismatch.Date.HasValue && requests == 1, "A date from another credential identity appeared beside the existing usage snapshot.");
                Require(!reader.ReadAsync("plus", null).GetAwaiter().GetResult().Date.HasValue && requests == 1, "Missing snapshot identity silently disabled the binding check.");
            }, report, failures);
            if (failures.Count != 0) throw new InvalidOperationException(failures.Count + " automatic subscription checks failed.");
        }
        private static DateTime Now() { return new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc); }
        private static AccountSubscriptionInfo Read(AccountSubscriptionReader reader, string plan, string email = "user-a@example.invalid")
        { return reader.ReadAsync(plan, email).GetAwaiter().GetResult(); }
        private static AccountSubscriptionInfo Parse(string json, DateTime now) { return AccountSubscription.ParseAccountResponse(json, "account-a", "plus", now); }
        private static bool IsDate(AccountSubscriptionInfo value, int year, int month, int day)
        { return value.Date.HasValue && value.Date.Value.ToUniversalTime() == new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc); }
        private static AccountSubscriptionResponse Response(int status, string body)
        { return new AccountSubscriptionResponse { StatusCode = status, Body = body, ContentType = status == 200 ? "application/json" : "text/html" }; }
        internal static string Body(string id, string plan, bool? renew, string renews, string cancels, string expires, string until)
        {
            return new JavaScriptSerializer().Serialize(new { accounts = new Dictionary<string, object> { { id, new {
                account = new { account_id = id, plan_type = plan },
                entitlement = new { renews_at = renews, cancels_at = cancels, expires_at = expires },
                last_active_subscription = new { will_renew = renew, active_until = until }
            } } } });
        }
        internal static string Auth(string account, string user, string plan, string until, string checkedAt, string signature)
        {
            // Unsigned synthetic fixtures contain no real account or credential data.
            Dictionary<string, object> subscription = new Dictionary<string, object> {
                { "chatgpt_account_id", account }, { "chatgpt_user_id", user }, { "chatgpt_plan_type", plan } };
            if (until != null) subscription["chatgpt_subscription_active_until"] = until;
            if (checkedAt != null) subscription["chatgpt_subscription_last_checked"] = checkedAt;
            JavaScriptSerializer json = new JavaScriptSerializer();
            string payload = json.Serialize(new Dictionary<string, object> { { "https://api.openai.com/auth", subscription },
                { "https://api.openai.com/profile", new { email = user + "@example.invalid" } }, { "email", user + "@example.invalid" }, { "exp", 2208988800L } });
            string token = "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".synthetic-" + signature;
            return json.Serialize(new { tokens = new { account_id = account, id_token = token, access_token = token } });
        }
        private static void Check(string name, Action action, Action<string> report, List<string> failures)
        { try { action(); report("PASS " + name); } catch (Exception error) { failures.Add(name); report("FAIL " + name + ": " + error.Message); } }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
