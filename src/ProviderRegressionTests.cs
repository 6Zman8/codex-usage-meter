using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace CodexUsageMeter
{
    internal static class ProviderRegressionTests
    {
        private const string AccountA = "11111111-1111-4111-8111-111111111111";
        private const string AccountB = "22222222-2222-4222-8222-222222222222";
        private const string Organization = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        private static readonly DateTime Initial = new DateTime(2030, 1, 2, 12, 0, 0, DateTimeKind.Utc);
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        // No real process, auth profile, API or inference request is used by these tests.
        internal static void Run(Action<string> report, string testRoot)
        {
            RunCore(report, testRoot);
            Views();
            report("PASS providers: full/compact names, symbols, identity, post-layout capabilities, switching and billing guards");
        }

        internal static void RunCore(Action<string> report, string testRoot)
        {
            string root = Path.Combine(testRoot, "p-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Migration(root); report("PASS providers: legacy paths/keys, mixed slots, metadata restart, retained profiles, unknown IDs and no Gemini");
            Quotas(); report("PASS providers: missing/invalid/zero quotas, independent windows and timezone-correct resets");
            IdentityAndHistory(root); report("PASS providers: verified identity, replacement, token rotation, isolation and truthful observations");
            Renewal(root); report("PASS providers: encoded OAuth refresh contract, isolated atomic persistence and renewal blockers");
            Errors(root); report("PASS providers: per-account failure isolation, 401, scope denial, 429 backoff with usage-only minute polling and kept last reading, timeout, cancellation and safe errors");
            RefreshRetry(root); report("PASS providers: parsed quota recovery, five-second Codex-only delay, per-invocation cap, concurrent accounts and preserved credentials/exclusions");
            LoginAndEnvironment(root); report("PASS providers: browser PKCE login, forged callback rejection, code exchange, plan detection, logout and nonblocking login");
            SwitchFiltering(root); report("PASS providers: Codex detection, synchronization and switching exclude retained non-Codex slots");
        }

        private static void Migration(string root)
        {
            Require(AccountProviders.Registered.Select(p => p.Id).SequenceEqual(new[] { "codex", "claude" }), "Only Codex and Claude are registered.");
            Require(AccountProviders.Resolve(null) == AccountProviders.Codex &&
                AccountProviders.Resolve("gemini").Create == null && AccountProviders.Resolve("").Id == "", "Unknown IDs must not silently migrate.");
            Require(AccountProviders.Codex.ProfilePath(root, 3) == Path.Combine(root, "account-3") &&
                AccountProviders.Claude.ProfilePath(root, 3) == Path.Combine(root, "claude", "account-3"), "Legacy/isolated paths changed.");
            string oldKey;
            using (SHA256 sha = SHA256.Create())
                oldKey = String.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes("account-id\nperson@example.test")).Select(b => b.ToString("x2")));
            Require(UsageHistoryStore.AccountKey("account-id", " Person@EXAMPLE.test ") == oldKey, "Legacy AccountKey bytes changed.");
            Require(UsageHistoryStore.ProviderAccountKey("claude", AccountA, Organization).StartsWith("claude-") &&
                UsageHistoryStore.ProviderAccountKey("claude", null, Organization) == null, "New history keys need remote account and organization.");
            string keyPath = "Software\\CodexUsageMeter-ProviderTests-" + Guid.NewGuid().ToString("N");
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath))
                {
                    Require(AccountProviders.LoadSlot(key, 4) == "codex", "Absent metadata must preserve Codex.");
                    AccountProviders.SaveSlot(key, 1, "codex"); AccountProviders.SaveSlot(key, 2, "claude");
                    AccountProviders.SaveSlot(key, 4, "claude");
                    key.SetValue("AccountCount", 1); // Count reduction must not delete other slot IDs.
                    key.SetValue("AccountProvider3", "future-unknown");
                }
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath))
                {
                    Require(AccountProviders.LoadSlot(key, 1) == "codex" && AccountProviders.LoadSlot(key, 2) == "claude" &&
                        AccountProviders.LoadSlot(key, 4) == "claude" && AccountProviders.LoadSlot(key, 3) == "future-unknown", "Slot metadata/restart changed.");
                }
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath); }
            string codexPath = AccountProviders.Codex.ProfilePath(root, 2);
            Directory.CreateDirectory(codexPath);
            File.WriteAllText(Path.Combine(codexPath, "auth.json"), "retained-codex-credential-fixture");
            using (IUsageAccount claude = AccountProviders.Create("claude", root, 2))
                Require(claude.ProfileRoot != codexPath && !AccountProviders.HasRetainedCredentials(claude), "Providers share a profile.");
            using (IUsageAccount codex = AccountProviders.Create("codex", root, 2))
                Require(AccountProviders.HasRetainedCredentials(codex), "Reassignment lost retained credentials.");
            Require(File.ReadAllText(Path.Combine(codexPath, "auth.json")) == "retained-codex-credential-fixture", "Reassignment overwrote credentials.");
        }

        private static void Quotas()
        {
            foreach (string value in new[] { "{}", "{\"utilization\":null}", "{\"utilization\":\"0\"}", "{\"utilization\":true}",
                "{\"utilization\":-1}", "{\"utilization\":101}" })
                Require(ClaudeUsageAccount.ParseWindow(Object(value), 300, "5시간 한도") == null, "Absent/invalid quota became free quota.");
            RateWindow zero = ClaudeUsageAccount.ParseWindow(Object("{\"utilization\":100,\"resets_at\":\"2030-01-02T21:00:00+09:00\"}"), 300, "5시간 한도");
            Require(zero.RemainingPercent == 0 && zero.ResetsAt.Value.ToUniversalTime() == Initial && zero.ResetsAt.Value.Kind == DateTimeKind.Local &&
                zero.ResetsAt.Value - Initial.ToLocalTime() == TimeSpan.Zero, "Zero or timezone reset changed, or would not count down against local time.");
            RateWindow full = ClaudeUsageAccount.ParseWindow(Object("{\"utilization\":0,\"resets_at\":\"not-a-date\"}"), 10080, "주간 한도");
            Require(full.RemainingPercent == 100 && full.ResetsAt == null && full.DurationMinutes == 10080, "Valid 0% usage or unavailable reset changed.");
            RateWindow unzoned = ClaudeUsageAccount.ParseWindow(Object("{\"utilization\":12,\"resets_at\":\"2030-01-02T12:00:00\"}"), 300, "5시간 한도");
            Require(unzoned.ResetsAt == null, "A local timestamp was invented as a remote reset.");
        }

        private static void IdentityAndHistory(string root)
        {
            Fixture fixture = new Fixture(Path.Combine(root, "identity")); fixture.Save("token-a", Initial.AddHours(2));
            using (ClaudeUsageAccount account = fixture.Create())
            {
                AccountSnapshot first = account.RefreshAsync().GetAwaiter().GetResult();
                Require(first.IdentityVerified && first.ProviderId == "claude" && first.RemoteAccountId == AccountA &&
                    first.RemoteOrganizationId == Organization && first.Primary.RemainingPercent == 75 &&
                    first.Secondary.RemainingPercent == 60 && first.RateLimitsObservedAtUtc == Initial, "Verified quota mapping failed.");
                Require(fixture.Requests.Count == 2 && fixture.Requests.All(r => r.AccessToken == "token-a") &&
                    fixture.Requests.Select(r => r.Url).SequenceEqual(new[] { ClaudeUsageAccount.ProfileEndpoint, ClaudeUsageAccount.UsageEndpoint }),
                    "Quota and identity must use the same isolated credential and exact endpoints.");
                fixture.Now = Initial.AddSeconds(55);
                AccountSnapshot polled = account.RefreshAsync().GetAwaiter().GetResult();
                Require(polled.IdentityVerified && polled.RemoteAccountId == AccountA && polled.Email == first.Email && fixture.Requests.Count == 3 &&
                    fixture.Requests.Last().Url == ClaudeUsageAccount.UsageEndpoint, "A minute poll of an unchanged sign-in must send only the usage request.");
                UsageHistoryStore store = new UsageHistoryStore(Path.Combine(root, "h")); store.Record(first);
                fixture.Now = Initial.AddMinutes(1); fixture.Save("rotated-token", Initial.AddHours(3));
                AccountSnapshot rotated = account.RefreshAsync().GetAwaiter().GetResult(); store.Record(rotated);
                Require(rotated.HistoryKey == first.HistoryKey && store.Read(first.HistoryKey).Last().LastUtc == fixture.Now, "Token rotation changed account identity or observation time.");
                fixture.Account = AccountB; fixture.Now = Initial.AddMinutes(2); fixture.Save("replacement", Initial.AddHours(4));
                AccountSnapshot replaced = account.RefreshAsync().GetAwaiter().GetResult(); store.Record(replaced);
                Require(replaced.HistoryKey != first.HistoryKey && store.Read(replaced.HistoryKey).Single().FirstUtc == fixture.Now &&
                    store.Read(first.HistoryKey).Last().LastUtc == Initial.AddMinutes(1), "Account replacement inherited old history.");
                AccountSnapshot codex = new AccountSnapshot { IsAuthenticated = true, ProviderId = "codex",
                    RemoteAccountId = replaced.RemoteAccountId, Email = replaced.Email, IdentityVerified = true };
                Require(AccountProviders.DuplicateKey(codex) != AccountProviders.DuplicateKey(replaced), "Cross-provider email/identity marked duplicate.");
                fixture.Now = Initial.AddMinutes(7); fixture.Profile = "{}"; File.Delete(Path.Combine(fixture.Root, "meter-identity.json"));
                AccountSnapshot missing = account.RefreshAsync().GetAwaiter().GetResult();
                Require(missing.ErrorKind == AccountErrorKind.IdentityUnavailable && missing.HistoryKey == null &&
                    missing.RateLimitsObservedAtUtc == default(DateTime), "Missing identity created a sample.");
                fixture.Profile = null;
                fixture.OnUsage = () => fixture.Save("changed-during-query", Initial.AddHours(3));
                fixture.Save("before-concurrent-change", Initial.AddHours(3));
                Require(account.RefreshAsync().GetAwaiter().GetResult().ErrorKind == AccountErrorKind.IdentityMismatch, "Concurrent credential replacement accepted.");
                replaced.IdentityVerified = false; replaced.RateLimitsObservedAtUtc = Initial.AddMinutes(3); store.Record(replaced);
                Require(store.Read(replaced.HistoryKey).Single().LastUtc == Initial.AddMinutes(2), "Unverified snapshot wrote history.");
            }
            Fixture other = new Fixture(Path.Combine(root, "other-profile")); other.Save("other-profile-token", Initial.AddHours(2));
            using (ClaudeUsageAccount account = other.Create())
                Require(account.RefreshAsync().GetAwaiter().GetResult().IdentityVerified && other.Requests.All(r => r.AccessToken == "other-profile-token"),
                    "Separate profile borrowed credentials.");
            Fixture missingWindow = new Fixture(Path.Combine(root, "missing-window")); missingWindow.Save("token", Initial.AddHours(1));
            missingWindow.Usage = "{\"seven_day\":{\"utilization\":30},\"seven_day_opus\":{\"utilization\":90}}";
            using (ClaudeUsageAccount account = missingWindow.Create())
            {
                AccountSnapshot value = account.RefreshAsync().GetAwaiter().GetResult();
                Require(value.Primary == null && value.Secondary.RemainingPercent == 70, "Weekly/model-specific quota became primary.");
            }
        }

        private static void Renewal(string root)
        {
            Fixture fixture = new Fixture(Path.Combine(root, "renewal")); fixture.Save("old-access", Initial.AddMinutes(3), "refresh+a&b=c");
            using (ClaudeUsageAccount account = fixture.Create())
            {
                string originalKey = account.RefreshAsync().GetAwaiter().GetResult().HistoryKey;
                fixture.Now = Initial.AddMinutes(5);
                AccountSnapshot refreshed = account.RefreshAsync().GetAwaiter().GetResult();
                ClaudeRequest request = fixture.Requests.Single(r => r.Method == "POST");
                Require(request.Url == "https://platform.claude.com/v1/oauth/token" && request.AccessToken == null &&
                    request.Body == "grant_type=refresh_token&refresh_token=refresh%2Ba%26b%3Dc&client_id=9d1c250a-e61b-44d9-88ed-5944d1962f5e",
                    "OAuth contract or form encoding changed.");
                Dictionary<string, object> document = Object(File.ReadAllText(fixture.CredentialPath));
                Dictionary<string, object> oauth = JsonValue.AsObject(document["claudeAiOauth"]);
                Require((string)oauth["accessToken"] == "renewed-access" && (string)oauth["refreshToken"] == "renewed-refresh" &&
                    (string)document["fixture-preserved"] == "keep" && refreshed.IdentityVerified && refreshed.HistoryKey == originalKey,
                    "Renewal lost unrelated metadata, binding or rotated credentials.");
                Require(Directory.GetFiles(fixture.Root, "*.tmp").Length == 0, "Atomic persistence left temporary credentials.");
            }
            using (ClaudeUsageAccount restart = fixture.Create())
                Require(restart.RefreshAsync().GetAwaiter().GetResult().IdentityVerified, "Renewed isolated credentials failed after restart.");
            Fixture expired = new Fixture(Path.Combine(root, "unbound-expired")); expired.Save("expired", Initial.AddHours(-1));
            using (ClaudeUsageAccount account = expired.Create())
            {
                Require(account.RefreshAsync().GetAwaiter().GetResult().ErrorKind == AccountErrorKind.RenewalBlocked &&
                    expired.Requests.Count == 0, "Expired unbound credentials must expose a blocker, not invent identity.");
            }
            Fixture mismatch = new Fixture(Path.Combine(root, "renewal-mismatch")); mismatch.Save("old", Initial.AddMinutes(3));
            using (ClaudeUsageAccount account = mismatch.Create())
            {
                account.RefreshAsync().GetAwaiter().GetResult();
                string saved = File.ReadAllText(mismatch.CredentialPath);
                mismatch.Account = AccountB; mismatch.Now = Initial.AddMinutes(5);
                Require(account.RefreshAsync().GetAwaiter().GetResult().ErrorKind == AccountErrorKind.IdentityMismatch &&
                    File.ReadAllText(mismatch.CredentialPath) == saved, "Renewal overwrote a mismatched identity.");
            }
        }

        private static void Errors(string root)
        {
            foreach (int status in new[] { 401, 403, 429, 500 })
            {
                Fixture fixture = new Fixture(Path.Combine(root, "status-" + status)); fixture.Save("never-display-token", Initial.AddHours(2));
                fixture.Status = status;
                using (ClaudeUsageAccount account = fixture.Create())
                {
                    AccountSnapshot error = account.RefreshAsync().GetAwaiter().GetResult();
                    Require(error.ErrorKind == (status == 401 ? AccountErrorKind.LoginRequired : status == 403 ? AccountErrorKind.ScopeDenied :
                        status == 429 ? AccountErrorKind.RateLimited : AccountErrorKind.Network) && error.HistoryKey == null &&
                        !error.Error.Contains("never-display-token") && !error.Error.Contains("server-secret"), "HTTP failure was not safely classified.");
                    fixture.Status = 200;
                    if (status == 429)
                    {
                        Require(error.RetryAfterUtc == Initial.AddMinutes(2), "Retry-After not honored.");
                        account.RefreshAsync().GetAwaiter().GetResult();
                        Require(fixture.Requests.Count == 1, "Backoff issued another request.");
                        fixture.Now = Initial.AddMinutes(2);
                        Require(account.RefreshAsync().GetAwaiter().GetResult().IdentityVerified, "Backoff never recovered.");
                    }
                    else if (status == 401 || status == 403)
                    {
                        account.RefreshAsync().GetAwaiter().GetResult();
                        Require(fixture.Requests.Count == 1, "Terminal auth denial retried unchanged credentials.");
                        fixture.Save("new-auth", Initial.AddHours(2));
                        Require(account.RefreshAsync().GetAwaiter().GetResult().IdentityVerified, "Changed login failed to recover.");
                    }
                }
            }
            Fixture busy = new Fixture(Path.Combine(root, "throttle")); busy.Save("token", Initial.AddHours(2));
            using (ClaudeUsageAccount account = busy.Create())
            {
                AccountSnapshot good = account.RefreshAsync().GetAwaiter().GetResult();
                busy.Now = Initial.AddSeconds(30);
                Require(good.IdentityVerified && System.Object.ReferenceEquals(account.RefreshAsync().GetAwaiter().GetResult(), good) &&
                    busy.Requests.Count == 2, "Claude was polled again before the poll interval.");
                busy.Now = Initial.AddMinutes(5); busy.Status = 429; busy.RetryAfter = "0";
                AccountSnapshot limited = account.RefreshAsync().GetAwaiter().GetResult();
                Require(limited.ErrorKind == AccountErrorKind.RateLimited && limited.RetryAfterUtc == busy.Now.AddMinutes(5) &&
                    limited.Primary.RemainingPercent == 75 && limited.RateLimitsObservedAtUtc == Initial && limited.HistoryKey == null,
                    "Throttling hid the last reading, recorded it again or ignored Retry-After: 0.");
                busy.Now = Initial.AddMinutes(10);
                Require(account.RefreshAsync().GetAwaiter().GetResult().RetryAfterUtc == busy.Now.AddMinutes(10), "Repeated throttling did not back off.");
                busy.Status = 200; busy.Now = Initial.AddMinutes(20);
                Require(account.RefreshAsync().GetAwaiter().GetResult().RateLimitsObservedAtUtc == busy.Now, "Throttled account never recovered.");
            }
            Fixture timed = new Fixture(Path.Combine(root, "timeout")); timed.Save("token", Initial.AddHours(2));
            using (ClaudeUsageAccount account = timed.Create((request, cancellation) => { throw new TimeoutException("secret"); }))
                Require(account.RefreshAsync().GetAwaiter().GetResult().ErrorKind == AccountErrorKind.Timeout, "Timeout became auth denial.");
            using (ClaudeUsageAccount account = timed.Create((request, cancellation) => {
                var pending = new TaskCompletionSource<ClaudeResponse>();
                cancellation.Register(() => pending.TrySetCanceled());
                return pending.Task;
            }))
            {
                Task<AccountSnapshot> pending = account.RefreshAsync(); account.Dispose();
                Require(pending.GetAwaiter().GetResult().ErrorKind == AccountErrorKind.Canceled, "Dispose did not cancel pending work.");
            }
            using (IUsageAccount broken = new FailingAccount())
            using (ClaudeUsageAccount healthy = timed.Create())
            {
                var results = Task.WhenAll(AccountProviders.RefreshSafelyAsync(broken), AccountProviders.RefreshSafelyAsync(healthy)).GetAwaiter().GetResult();
                Require(results[0].Error != null && !results[0].Error.Contains("secret") && results[1].IdentityVerified, "One account failure prevented another refresh.");
            }
        }

        private static void LoginAndEnvironment(string root)
        {
            var request = ClaudeUsageAccount.CreateRequest(new ClaudeRequest { Url = ClaudeUsageAccount.UsageEndpoint, AccessToken = "fixture-bearer" });
            Require(request.Address.AbsoluteUri == "https://api.anthropic.com/api/oauth/usage" && request.Method == "GET" &&
                request.Headers["Authorization"] == "Bearer fixture-bearer" && request.Headers["anthropic-beta"] == "oauth-2025-04-20" &&
                !request.AllowAutoRedirect && request.Timeout == 15000 && request.ReadWriteTimeout == 15000 &&
                request.CookieContainer == null && String.IsNullOrEmpty(request.UserAgent), "Auth headers, request bounds or fingerprint isolation changed.");
            var renewal = ClaudeUsageAccount.CreateRequest(new ClaudeRequest { Url = ClaudeUsageAccount.TokenEndpoint, Method = "POST", Body = "fixture" });
            Require(renewal.Headers["Authorization"] == null && renewal.ContentType == "application/x-www-form-urlencoded",
                "Renewal leaked a bearer or changed the evidenced content type.");
            Require(ClaudeUsageAccount.CreateRequest(new ClaudeRequest { Url = ClaudeUsageAccount.TokenEndpoint, Method = "POST",
                ContentType = "application/json", Body = "{}" }).ContentType == "application/json", "Code exchange content type changed.");
            Require(ClaudeUsageAccount.PlanOf(null, Object("{\"organization_type\":\"claude_team\"}")) == "team" &&
                ClaudeUsageAccount.PlanOf(Object("{\"has_claude_pro\":true}"), null) == "pro" &&
                ClaudeUsageAccount.PlanOf(Object("{}"), Object("{\"organization_type\":\"api\"}")) == null, "Plan detection changed.");
            Fixture login = new Fixture(Path.Combine(root, "browser-login"));
            login.Profile = "{\"account\":{\"uuid\":\"" + AccountA + "\",\"email_address\":\"private@example.test\"},\"organization\":{\"uuid\":\"" +
                Organization + "\",\"organization_type\":\"claude_max\"}}";
            using (ClaudeUsageAccount account = login.Create())
            {
                Task<AccountLoginPrompt> promptTask = account.StartLoginAsync();
                Require(promptTask.IsCompleted, "Login waited for the person instead of returning a prompt.");
                AccountLoginPrompt prompt = promptTask.GetAwaiter().GetResult();
                Dictionary<string, string> query = new Uri(prompt.VerificationUrl).Query.TrimStart('?').Split('&')
                    .Select(pair => pair.Split(new[] { '=' }, 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
                string redirect = query["redirect_uri"];
                Require(prompt.VerificationUrl.StartsWith(ClaudeLoopbackLogin.AuthorizeEndpoint + "?") && query["code"] == "true" &&
                    query["client_id"] == ClaudeUsageAccount.OAuthClientId && query["response_type"] == "code" &&
                    query["code_challenge_method"] == "S256" && query["scope"].Split(' ').Contains("user:profile") &&
                    redirect.StartsWith("http://localhost:") && redirect.EndsWith("/callback"), "Browser login request changed.");
                Require(!prompt.Completion.IsCompleted && account.RefreshAsync().GetAwaiter().GetResult().HistoryKey == null,
                    "Pending login blocked refresh or fabricated usage.");
                Require(Callback(redirect + "?code=fixture-code&state=forged") == 400 && Callback(redirect.Replace("/callback", "/favicon.ico")) == 404 &&
                    !prompt.Completion.IsCompleted && login.Requests.Count == 0, "Forged callback was accepted.");
                Require(Callback(redirect + "?code=fixture-code&state=" + Uri.EscapeDataString(query["state"])) == 200, "Valid callback failed.");
                prompt.Completion.GetAwaiter().GetResult();
                ClaudeRequest exchange = login.Requests.Single(sentRequest => sentRequest.Url == ClaudeUsageAccount.TokenEndpoint);
                Dictionary<string, object> sent = Object(exchange.Body);
                string challenge;
                using (SHA256 sha = SHA256.Create())
                    challenge = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes((string)sent["code_verifier"])))
                        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                Require(exchange.ContentType == "application/json" && exchange.AccessToken == null &&
                    (string)sent["grant_type"] == "authorization_code" && (string)sent["code"] == "fixture-code" &&
                    (string)sent["redirect_uri"] == redirect && (string)sent["state"] == query["state"] &&
                    (string)sent["client_id"] == ClaudeUsageAccount.OAuthClientId && challenge == query["code_challenge"], "Code exchange contract changed.");
                var oauth = (Dictionary<string, object>)Object(File.ReadAllText(login.CredentialPath))["claudeAiOauth"];
                Require((string)oauth["subscriptionType"] == "max" && (string)oauth["accessToken"] == "renewed-access" &&
                    (string)oauth["refreshToken"] == "renewed-refresh", "Login did not store the subscription sign-in.");
                Require(account.RefreshAsync().GetAwaiter().GetResult().IdentityVerified, "Completed browser login did not become usable.");
                account.LogoutAsync().GetAwaiter().GetResult();
                Require(!File.Exists(login.CredentialPath) &&
                    account.RefreshAsync().GetAwaiter().GetResult().ErrorKind == AccountErrorKind.LoginRequired, "Logout kept the sign-in.");
            }
            using (ClaudeUsageAccount account = login.Create())
            {
                AccountLoginPrompt prompt = account.StartLoginAsync().GetAwaiter().GetResult(); prompt.Cancel();
                try { prompt.Completion.GetAwaiter().GetResult(); throw new Exception("Cancel unexpectedly succeeded."); }
                catch (AccountProviderException error) { Require(error.Kind == AccountErrorKind.Canceled, "Login cancellation not classified."); }
            }
            using (ClaudeUsageAccount absent = new ClaudeUsageAccount(Path.Combine(root, "unconnected"),
                (r, c) => { throw new Exception("Network forbidden"); }, () => Initial))
                Require(absent.RefreshAsync().GetAwaiter().GetResult().ErrorKind == AccountErrorKind.LoginRequired, "Unconnected slot did not ask for login.");
            Fixture nonSubscription = new Fixture(Path.Combine(root, "api-login"));
            nonSubscription.Save("api-auth", Initial.AddHours(2), "refresh", "api");
            using (ClaudeUsageAccount account = nonSubscription.Create())
                Require(account.RefreshAsync().GetAwaiter().GetResult().ErrorKind == AccountErrorKind.UnsupportedAuthentication &&
                    nonSubscription.Requests.Count == 0, "Non-subscription auth was used.");
            nonSubscription.Save("inference-only", Initial.AddHours(2), "refresh", "pro", new[] { "user:inference" });
            using (ClaudeUsageAccount account = nonSubscription.Create())
                Require(account.RefreshAsync().GetAwaiter().GetResult().ErrorKind == AccountErrorKind.ScopeDenied &&
                    nonSubscription.Requests.Count == 0, "setup-token/inference-only auth was accepted.");
        }

        private static void SwitchFiltering(string root)
        {
            string home = Path.Combine(root, "switch-home"), accounts = Path.Combine(root, "switch-accounts");
            Directory.CreateDirectory(home);
            string auth = "{\"auth_mode\":\"chatgpt\",\"tokens\":{\"account_id\":\"retained\",\"access_token\":\"fixture-access\",\"refresh_token\":\"fixture-refresh\",\"id_token\":\"fixture-id\"}}";
            File.WriteAllText(Path.Combine(home, "auth.json"), auth);
            foreach (int number in new[] { 1, 2 })
            {
                string profile = Path.Combine(accounts, "account-" + number); Directory.CreateDirectory(profile);
                File.WriteAllText(Path.Combine(profile, "auth.json"), auth);
            }
            var lifecycle = new NeverDesktop();
            var switcher = new AccountSwitcher(home, accounts, lifecycle, NullAccountSwitchJournal.Instance, number => number == 2);
            var store = new CodexAuthFileStore(home, accounts, number => number == 2);
            Require(switcher.DetectActiveAccountNumber(2) == 2 && store.DetectActiveAccountNumber(2) == 2, "Retained Claude-slot Codex auth detected as active.");
            switcher.SynchronizeCurrentCredentials(2);
            Require(!switcher.SwitchTo(1, 2).Success && lifecycle.Calls == 0 &&
                File.ReadAllText(Path.Combine(accounts, "account-1", "auth.json")) == auth, "Non-Codex target reached switching or overwrote retained credentials.");
        }

        private static void Views()
        {
            // Reflection avoids constructing a live dashboard (which would read actual profiles).
            Type dashboard = typeof(ProviderRegressionTests).Assembly.GetType("CodexUsageMeter.DashboardController", true);
            Window window = (Window)dashboard.GetMethod("LoadWindow", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            object controller = FormatterServices.GetUninitializedObject(dashboard);
            dashboard.GetField("_window", Private).SetValue(controller, window);
            object first = dashboard.GetMethod("CreateAccountView", Private).Invoke(controller, new object[] { 1, "계정 1", null });
            object second = dashboard.GetMethod("CreateAccountView", Private).Invoke(controller, new object[] { 2, "계정 2", null });
            dashboard.GetField("_account1", Private).SetValue(controller, first);
            dashboard.GetField("_account2", Private).SetValue(controller, second);
            AccountSnapshot snapshot = new AccountSnapshot { ProviderId = "claude", IsAuthenticated = true, Email = "private@example.test",
                PlanType = "max", Primary = new RateWindow { Name = "5시간 한도", DurationMinutes = 300, RemainingPercent = 75 },
                Secondary = new RateWindow { Name = "주간 한도", DurationMinutes = 10080, RemainingPercent = 60 }, UpdatedAt = Initial };
            dashboard.GetMethod("UpdateAccount", Private).Invoke(controller, new[] { second, snapshot });
            snapshot = new AccountSnapshot { IsAuthenticated = false, ProviderId = "codex" };
            dashboard.GetMethod("UpdateAccount", Private).Invoke(controller, new[] { first, snapshot });
            foreach (string prefix in new[] { "", "Compact" })
            {
                string codex = ((TextBlock)window.FindName(prefix + "Account1TitleText")).Text;
                string claude = ((TextBlock)window.FindName(prefix + "Account2TitleText")).Text;
                Require(codex.Contains("GPT/Codex") && codex.Contains("◎") && claude.Contains("Claude") && claude.Contains("✳") &&
                    claude.Contains("계정 2") && ((TextBlock)window.FindName(prefix + "Account2Identity")).Text.Contains("private@example.test"),
                    "Provider header replaced account identification or lost its distinct symbol/name.");
                Require(((Button)window.FindName(prefix + "Account2CodexLoginButton")).Visibility == Visibility.Collapsed &&
                    ((TextBlock)window.FindName(prefix + "Account2SubscriptionValue")).Visibility == Visibility.Collapsed, "Claude exposes unsupported switches/billing.");
                if (prefix.Length == 0)
                    Require(((Button)window.FindName("Account2LoginButton")).Visibility == Visibility.Visible, "Provider selection/reconnect inaccessible.");
                else
                    Require(window.FindName("CompactAccount2LoginButton") == null, "Compact mode exposes generic provider login.");
            }
            Require(((TextBlock)window.FindName("CompactAccount2ResetValue")).Visibility == Visibility.Collapsed, "Claude exposes reset-credit discovery.");
            Type viewType = second.GetType();
            Type stateType = typeof(ProviderRegressionTests).Assembly.GetType("CodexUsageMeter.AccountState", true);
            foreach (FrameworkElement extra in (FrameworkElement[])viewType.GetField("CodexExtras").GetValue(second))
                Require(extra.Visibility == Visibility.Collapsed, "Claude exposes Codex usage extras.");
            using (IUsageAccount client = new UnsupportedUsageAccount(AccountProviders.Claude))
            {
                viewType.GetField("Client").SetValue(second, client);
                // Authenticated Claude cannot enter ChatGPT billing even if called directly.
                dashboard.GetMethod("ConnectWebSubscription", Private).Invoke(controller, new[] { second });
                object state = Activator.CreateInstance(stateType);
                stateType.GetField("Client").SetValue(state, client); stateType.GetField("Number").SetValue(state, 2);
                viewType.GetField("State").SetValue(second, state);
                dashboard.GetMethod("ConfirmCodexLoginChange", Private).Invoke(controller, new[] { second });
                dashboard.GetMethod("BeginCodexLoginChange", Private).Invoke(controller, new[] { second });
                Require(!(bool)dashboard.GetField("_switchingAccount", Private).GetValue(controller), "Claude entered Codex switching.");
            }
            object firstState = Activator.CreateInstance(stateType);
            stateType.GetField("Number").SetValue(firstState, 1);
            viewType.GetField("State").SetValue(first, firstState);
            Type layoutType = typeof(ProviderRegressionTests).Assembly.GetType("CodexUsageMeter.DashboardLayoutView", true);
            object layout = Activator.CreateInstance(layoutType, Private, null, new object[] { window }, null);
            Type settingsType = typeof(ProviderRegressionTests).Assembly.GetType("CodexUsageMeter.LayoutSettings", true);
            object settings = settingsType.GetMethod("Defaults", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, null);
            Array views = Array.CreateInstance(viewType, 2);
            views.SetValue(first, 0); views.SetValue(second, 1);
            foreach (double scale in new[] { 1.0, 2.0 })
            {
                layoutType.GetMethod("Apply", Private).Invoke(layout, new object[] { settings, views, scale });
                foreach (FrameworkElement extra in (FrameworkElement[])viewType.GetField("CodexExtras").GetValue(second))
                    Require(extra.Visibility == Visibility.Collapsed, "Layout restored unsupported Claude content.");
                Require(((TextBlock)window.FindName("CompactAccount2ResetValue")).Visibility == Visibility.Collapsed &&
                    ((TextBlock)window.FindName("CompactAccount2SubscriptionValue")).Visibility == Visibility.Collapsed,
                    "Layout restored Claude reset credits or ChatGPT billing.");
                Require(((TextBlock)window.FindName("CompactAccount1ResetValue")).Visibility == Visibility.Visible,
                    "Claude capability filtering also hid supported Codex content.");
            }
            window.Close();
        }

        private static void RefreshRetry(string root)
        {
            string profile = Path.Combine(root, "refresh-retry-" + Guid.NewGuid().ToString("N"));
            string otherProfile = Path.Combine(root, "refresh-retry-other-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            Directory.CreateDirectory(otherProfile);
            foreach (AccountProvider provider in AccountProviders.Registered)
                File.WriteAllText(Path.Combine(profile, provider.CredentialFile), "retained-fixture-" + provider.Id);
            File.WriteAllText(Path.Combine(otherProfile, AccountProviders.Codex.CredentialFile), "retained-other-codex-fixture");

            AccountSnapshot success = ParseCodexQuota(@"{""rateLimits"":{""primary"":{""usedPercent"":25,""windowDurationMins"":300}}}");
            AccountSnapshot quotaUnavailable = ParseCodexQuota(@"{""rateLimits"":{}}");
            AccountSnapshot network = new AccountSnapshot { ProviderId = "codex", ErrorKind = AccountErrorKind.Network, Error = "offline" };
            TaskCompletionSource<int> delay = new TaskCompletionSource<int>();
            TaskCompletionSource<int> otherDelay = new TaskCompletionSource<int>();
            int delays = 0, otherDelays = 0;
            RefreshAccount recovering = new RefreshAccount(AccountProviders.Codex, profile,
                call => Task.FromResult(call == 1 ? quotaUnavailable : success));
            Task<AccountSnapshot> pending = AccountProviders.RefreshSafelyAsync(recovering, milliseconds => {
                Require(milliseconds == 5000, "Refresh recovery did not wait five seconds.");
                delays++; return delay.Task;
            });
            Require(!pending.IsCompleted && recovering.RefreshCalls == 1 && delays == 1, "Retry did not wait before the second read.");
            AccountSnapshot otherQuotaUnavailable = ParseCodexQuota(@"{}");
            otherQuotaUnavailable.Email = "other@example.test";
            RefreshAccount other = new RefreshAccount(AccountProviders.Codex, otherProfile, call => Task.FromResult(otherQuotaUnavailable));
            Task<AccountSnapshot> otherPending = AccountProviders.RefreshSafelyAsync(other, milliseconds => {
                Require(milliseconds == 5000, "Second account recovery did not wait five seconds.");
                otherDelays++; return otherDelay.Task;
            });
            Require(!otherPending.IsCompleted && other.RefreshCalls == 1 && otherDelays == 1, "Second account did not have its own retry delay.");
            RefreshAccount healthy = new RefreshAccount(AccountProviders.Codex, profile, call => Task.FromResult(success));
            Require(System.Object.ReferenceEquals(RefreshForTest(healthy, 1), success), "Healthy account changed while another account waited.");
            delay.SetResult(0);
            Require(System.Object.ReferenceEquals(pending.GetAwaiter().GetResult(), success) && recovering.RefreshCalls == 2 &&
                delays == 1 && recovering.LoginCalls == 0 && recovering.LogoutCalls == 0, "Delayed recovery did not return the second snapshot.");
            Require(!otherPending.IsCompleted && other.RefreshCalls == 1, "One account's completion released another account's retry.");
            otherDelay.SetResult(0);
            Require(System.Object.ReferenceEquals(otherPending.GetAwaiter().GetResult(), otherQuotaUnavailable) && other.RefreshCalls == 2 &&
                otherDelays == 1 && other.LoginCalls == 0 && other.LogoutCalls == 0, "Second account did not stop after its own single retry.");

            foreach (string json in new[] {
                @"{""rateLimits"":{""primary"":{""usedPercent"":0,""windowDurationMins"":300}}}",
                @"{""rateLimits"":{""primary"":{""usedPercent"":100,""windowDurationMins"":300}}}",
                @"{""rateLimits"":{""secondary"":{""usedPercent"":0,""windowDurationMins"":10080}}}",
                @"{""rateLimits"":{""secondary"":{""usedPercent"":100,""windowDurationMins"":10080}}}",
                @"{""rateLimits"":{""primary"":{""usedPercent"":0,""windowDurationMins"":10080}}}",
                @"{""rateLimits"":{""primary"":{""usedPercent"":100,""windowDurationMins"":10080}}}",
                @"{""rateLimits"":{""primary"":{""usedPercent"":25,""windowDurationMins"":300},""secondary"":{""usedPercent"":75,""windowDurationMins"":10080}}}",
                @"{""rateLimits"":{""primary"":{""usedPercent"":75,""windowDurationMins"":10080},""secondary"":{""usedPercent"":25,""windowDurationMins"":300}}}",
                @"{""rateLimits"":{""primary"":{},""secondary"":{""usedPercent"":0}}}",
                @"{""rateLimits"":{""primary"":{""usedPercent"":100},""secondary"":{""usedPercent"":""invalid""}}}",
                @"{""rateLimitsByLimitId"":{""codex"":{""primary"":{""usedPercent"":0}}}}",
                @"{""rateLimitsByLimitId"":{""codex"":{""secondary"":{""usedPercent"":100,""windowDurationMins"":10080}}},""rateLimits"":{""limitId"":""other""}}"
            })
            {
                AccountSnapshot parsed = ParseCodexQuota(json);
                Require((parsed.Primary != null || parsed.Secondary != null) && parsed.ErrorKind == AccountErrorKind.None &&
                    String.IsNullOrEmpty(parsed.Error), "Valid quota was classified as unavailable: " + json);
                RefreshAccount account = new RefreshAccount(AccountProviders.Codex, profile, call => Task.FromResult(parsed));
                Require(System.Object.ReferenceEquals(RefreshForTest(account, 1), parsed), "Valid quota was replaced or retried.");
                parsed.UsageError = "optional usage unavailable";
                Require(System.Object.ReferenceEquals(RefreshForTest(account, 1), parsed), "Optional usage error retried valid quota.");
            }

            foreach (string json in new[] {
                @"{}", @"{""rateLimits"":null}", @"{""rateLimits"":{}}",
                @"{""rateLimits"":{""primary"":null,""secondary"":null}}",
                @"{""rateLimitsByLimitId"":{""codex"":{}}}",
                @"{""rateLimits"":{""limitId"":""other"",""primary"":{""usedPercent"":25}}}",
                @"{""rateLimits"":{""primary"":{},""secondary"":{""usedPercent"":""invalid""}}}"
            })
            {
                AccountSnapshot parsed = ParseCodexQuota(json);
                Require(parsed.Primary == null && parsed.Secondary == null && parsed.ErrorKind == AccountErrorKind.QuotaUnavailable &&
                    !String.IsNullOrWhiteSpace(parsed.Error), "Parsed empty quota lacked its explicit outcome: " + json);
                RefreshAccount account = new RefreshAccount(AccountProviders.Codex, profile, call => Task.FromResult(call == 1 ? parsed : success));
                Require(System.Object.ReferenceEquals(RefreshForTest(account, 2), success), "Parsed empty quota did not recover.");
                RefreshAccount repeated = new RefreshAccount(AccountProviders.Codex, profile, call => Task.FromResult(parsed));
                foreach (string invocation in new[] { "startup", "poll", "manual refresh" })
                    Require(System.Object.ReferenceEquals(RefreshForTest(repeated, 2), parsed),
                        "Quota retry allowance was not bounded/reset for " + invocation + ".");
                parsed.RetryAfterUtc = Initial.AddMinutes(2);
                Require(System.Object.ReferenceEquals(RefreshForTest(repeated, 1), parsed), "Quota recovery ignored server backoff.");
            }

            foreach (AccountSnapshot failed in new[] {
                network,
                quotaUnavailable,
                new AccountSnapshot { ProviderId = "codex", IsAuthenticated = true, ErrorKind = AccountErrorKind.Timeout, Error = "timeout" },
                new AccountSnapshot { ProviderId = "codex" }
            })
            {
                RefreshAccount account = new RefreshAccount(AccountProviders.Codex, profile, call => Task.FromResult(call == 1 ? failed : success));
                Require(System.Object.ReferenceEquals(RefreshForTest(account, 2), success), "Transient or empty retained Codex account did not recover.");
                RefreshAccount unconnected = new RefreshAccount(AccountProviders.Codex, Path.Combine(profile, "unconnected"),
                    call => Task.FromResult(new AccountSnapshot { ProviderId = "codex", ErrorKind = failed.ErrorKind, Error = failed.Error }));
                RefreshForTest(unconnected, 1);
                AccountSnapshot claudeFailure = new AccountSnapshot { ProviderId = "claude", IsAuthenticated = failed.IsAuthenticated,
                    ErrorKind = failed.ErrorKind, Error = failed.Error };
                RefreshAccount claude = new RefreshAccount(AccountProviders.Claude, profile, call => Task.FromResult(claudeFailure));
                Require(System.Object.ReferenceEquals(RefreshForTest(claude, 1), claudeFailure), "Claude refresh was repeated or replaced.");
            }
            AccountSnapshot last = new AccountSnapshot { ProviderId = "codex", ErrorKind = AccountErrorKind.Timeout, Error = "last failure" };
            RefreshAccount persistent = new RefreshAccount(AccountProviders.Codex, profile, call => Task.FromResult(call % 2 == 1 ? network : last));
            Require(System.Object.ReferenceEquals(RefreshForTest(persistent, 2), last), "Retry did not return the final failure.");
            Require(System.Object.ReferenceEquals(RefreshForTest(persistent, 2), last), "Retry allowance did not reset for the next refresh.");
            RefreshAccount empty = new RefreshAccount(AccountProviders.Codex, profile, call => Task.FromResult(new AccountSnapshot { ProviderId = "codex" }));
            RefreshForTest(empty, 2);
            AccountSnapshot optionalUsage = ParseCodexQuota(@"{""rateLimits"":{""secondary"":{""usedPercent"":100,""windowDurationMins"":10080}}}");
            optionalUsage.UsageError = "optional usage unavailable";
            RefreshAccount usageOnly = new RefreshAccount(AccountProviders.Codex, profile, call => Task.FromResult(optionalUsage));
            RefreshForTest(usageOnly, 1);
            RefreshAccount backoff = new RefreshAccount(AccountProviders.Codex, profile, call => Task.FromResult(
                new AccountSnapshot { ProviderId = "codex", ErrorKind = AccountErrorKind.Network, Error = "offline", RetryAfterUtc = Initial.AddMinutes(2) }));
            RefreshForTest(backoff, 1);

            foreach (AccountErrorKind kind in new[] { AccountErrorKind.InstallNeeded, AccountErrorKind.LoginRequired,
                AccountErrorKind.UnsupportedAuthentication, AccountErrorKind.ScopeDenied, AccountErrorKind.RateLimited,
                AccountErrorKind.Canceled, AccountErrorKind.IdentityUnavailable, AccountErrorKind.IdentityMismatch,
                AccountErrorKind.RenewalBlocked, AccountErrorKind.Storage, AccountErrorKind.InvalidResponse, AccountErrorKind.UnknownProvider })
            {
                AccountSnapshot blocked = new AccountSnapshot { ProviderId = "codex", IsAuthenticated = true, ErrorKind = kind, Error = "connection timed out" };
                RefreshAccount account = new RefreshAccount(AccountProviders.Codex, profile, call => Task.FromResult(blocked));
                Require(System.Object.ReferenceEquals(RefreshForTest(account, 1), blocked), "Permanent snapshot was replaced or retried: " + kind);
                RefreshAccount throwing = new RefreshAccount(AccountProviders.Codex, profile, call => { throw new AccountProviderException(kind, "blocked"); });
                Require(RefreshForTest(throwing, 1).ErrorKind == kind, "Permanent exception classification changed: " + kind);
            }
            foreach (string message in new[] { "account/read 응답 시간이 초과되었습니다.", "Codex app-server 연결이 종료되었습니다.",
                "error sending request for url (https://example.test/usage)", "HTTP 503 Service Unavailable",
                "계정 연결이 필요합니다.", "authentication required: request timed out",
                "401 Unauthorized: connection reset", "403 Forbidden: temporarily unavailable", "scope denied: timeout",
                "429 Too Many Requests: service unavailable", "rate limit exceeded: timeout", "token expired: timeout",
                "request canceled: timed out", "credential file unavailable: timeout", "identity mismatch: network error",
                "저장 실패: 응답 시간이 초과되었습니다.", "알 수 없는 오류", "Codex 요청이 실패했습니다." })
            {
                AccountSnapshot blocked = new AccountSnapshot { ProviderId = "codex", IsAuthenticated = true, Error = message };
                RefreshAccount account = new RefreshAccount(AccountProviders.Codex, profile, call => Task.FromResult(blocked));
                Require(System.Object.ReferenceEquals(RefreshForTest(account, 1), blocked), "Untyped RPC failure was retried: " + message);
            }

            foreach (Exception error in new Exception[] { new TimeoutException("secret"),
                new AccountProviderException(AccountErrorKind.Network, "offline"), new AccountProviderException(AccountErrorKind.Timeout, "timeout") })
            {
                RefreshAccount account = new RefreshAccount(AccountProviders.Codex, profile, call => {
                    if (call == 1) throw error;
                    return Task.FromResult(success);
                });
                Require(System.Object.ReferenceEquals(RefreshForTest(account, 2), success), "Thrown transient failure did not recover: " + error.GetType().Name);
                TaskCompletionSource<AccountSnapshot> fault = new TaskCompletionSource<AccountSnapshot>(); fault.SetException(error);
                RefreshAccount faulted = new RefreshAccount(AccountProviders.Codex, profile, call => call == 1 ? fault.Task : Task.FromResult(success));
                Require(System.Object.ReferenceEquals(RefreshForTest(faulted, 2), success), "Faulted transient task did not recover.");
                RefreshAccount repeated = new RefreshAccount(AccountProviders.Codex, profile, call => { throw error; });
                Require(RefreshForTest(repeated, 2).Error != null, "Persistent thrown failure was lost.");
                RefreshAccount unconnected = new RefreshAccount(AccountProviders.Codex, Path.Combine(profile, "unconnected"), call => { throw error; });
                RefreshForTest(unconnected, 1);
                RefreshAccount claude = new RefreshAccount(AccountProviders.Claude, profile, call => { throw error; });
                RefreshForTest(claude, 1);
            }
            foreach (Exception error in new Exception[] { new OperationCanceledException("secret"), new TaskCanceledException("secret"),
                new IOException("secret connection reset"), new UnauthorizedAccessException("secret"), new FileNotFoundException("secret"),
                new DirectoryNotFoundException("secret"), new InvalidOperationException("secret"), new ObjectDisposedException("secret"),
                new RpcException("connection reset by peer"), new RpcException("authentication required: timeout"), new Exception("secret") })
            {
                RefreshAccount account = new RefreshAccount(AccountProviders.Codex, profile, call => { throw error; });
                AccountSnapshot failed = RefreshForTest(account, 1);
                Require(!String.IsNullOrWhiteSpace(failed.Error) && !failed.Error.Contains("secret"), "Thrown error was not safely reported.");
                if (error is OperationCanceledException || error is ObjectDisposedException)
                    Require(failed.ErrorKind == AccountErrorKind.Canceled, "Cancellation was reported as a network failure.");
                if (error is IOException || error is UnauthorizedAccessException)
                    Require(failed.ErrorKind == AccountErrorKind.Storage, "Storage error was reported as a network failure.");
            }
            Require(CodexRpcClient.RefreshErrorKind(new IOException("pipe closed")) == AccountErrorKind.Network &&
                CodexRpcClient.RefreshErrorKind(new TimeoutException("request expired")) == AccountErrorKind.Timeout,
                "Codex transport failures lost their type before friendly formatting.");
            foreach (Exception error in new Exception[] { new RpcException("connection reset by peer"), new RpcException("authentication required"),
                new FileNotFoundException(), new DirectoryNotFoundException(), new PathTooLongException(), new InvalidDataException(),
                new UnauthorizedAccessException(), new OperationCanceledException(), new ObjectDisposedException("fixture"), new Exception("unknown") })
            {
                Require(CodexRpcClient.RefreshErrorKind(error) == AccountErrorKind.None, "Codex classified a permanent/unknown error as transient.");
                AccountSnapshot blocked = new AccountSnapshot { ProviderId = "codex", IsAuthenticated = true,
                    ErrorKind = CodexRpcClient.RefreshErrorKind(error), Error = error.Message };
                RefreshAccount account = new RefreshAccount(AccountProviders.Codex, profile, call => Task.FromResult(blocked));
                Require(System.Object.ReferenceEquals(RefreshForTest(account, 1), blocked), "Codex error without quota outcome was retried.");
            }
            foreach (AccountProvider provider in AccountProviders.Registered)
                Require(File.ReadAllText(Path.Combine(profile, provider.CredentialFile)) == "retained-fixture-" + provider.Id, "Retry changed retained credentials.");
            Require(Directory.GetFiles(profile).Length == AccountProviders.Registered.Length && Directory.GetDirectories(profile).Length == 0,
                "Retry created credential/profile artifacts.");
            Require(File.ReadAllText(Path.Combine(otherProfile, AccountProviders.Codex.CredentialFile)) == "retained-other-codex-fixture" &&
                Directory.GetFiles(otherProfile).Length == 1 && Directory.GetDirectories(otherProfile).Length == 0,
                "Concurrent retry changed the other account's credentials/profile.");
        }

        private static AccountSnapshot ParseCodexQuota(string json)
        {
            AccountSnapshot snapshot = new AccountSnapshot { ProviderId = "codex", IsAuthenticated = true,
                Email = "fixture@example.test", ResetCredits = new List<ResetCreditInfo>() };
            // The parser uses no process/client state; exercise it without launching Codex or accessing real credentials.
            typeof(CodexRpcClient).GetMethod("ParseRateLimits", Private).Invoke(
                FormatterServices.GetUninitializedObject(typeof(CodexRpcClient)), new object[] { Object(json), snapshot });
            return snapshot;
        }

        private static AccountSnapshot RefreshForTest(RefreshAccount account, int expectedCalls)
        {
            int before = account.RefreshCalls, delays = 0;
            AccountSnapshot result = AccountProviders.RefreshSafelyAsync(account, milliseconds => {
                Require(milliseconds == 5000, "Refresh recovery did not wait five seconds.");
                delays++; return Task.FromResult(0);
            }).GetAwaiter().GetResult();
            Require(account.RefreshCalls - before == expectedCalls && delays == expectedCalls - 1,
                "Unexpected refresh/delay count for " + account.Provider.Id + ".");
            Require(account.LoginCalls == 0 && account.LogoutCalls == 0, "Refresh invoked login/logout.");
            return result;
        }

        private sealed class RefreshAccount : IUsageAccount
        {
            private readonly Func<int, Task<AccountSnapshot>> _refresh;
            internal int RefreshCalls, LoginCalls, LogoutCalls;
            internal RefreshAccount(AccountProvider provider, string profile, Func<int, Task<AccountSnapshot>> refresh)
            { Provider = provider; ProfileRoot = profile; _refresh = refresh; }
            public AccountProvider Provider { get; private set; }
            public string ProfileRoot { get; private set; }
            public event EventHandler AccountChanged { add { } remove { } }
            public Task<AccountSnapshot> RefreshAsync() { return _refresh(++RefreshCalls); }
            public Task<AccountLoginPrompt> StartLoginAsync() { LoginCalls++; throw new InvalidOperationException("Unexpected login."); }
            public Task LogoutAsync() { LogoutCalls++; throw new InvalidOperationException("Unexpected logout."); }
            public void Dispose() { }
        }

        private sealed class Fixture
        {
            internal readonly string Root;
            internal string CredentialPath { get { return Path.Combine(Root, ".credentials.json"); } }
            internal DateTime Now = Initial;
            internal string Account = AccountA, Profile, Usage;
            internal int Status = 200;
            internal string RetryAfter = "120";
            internal Action OnUsage;
            internal readonly List<ClaudeRequest> Requests = new List<ClaudeRequest>();
            internal Fixture(string root) { Root = Path.GetFullPath(root); Directory.CreateDirectory(Root); }
            internal void Save(string token, DateTime expires, string refresh = "fixture-refresh", string plan = "pro", string[] scopes = null)
            {
                File.WriteAllText(CredentialPath, new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                    { "fixture-preserved", "keep" }, { "claudeAiOauth", new Dictionary<string, object> {
                        { "accessToken", token }, { "refreshToken", refresh }, { "subscriptionType", plan },
                        { "expiresAt", (expires - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds },
                        { "scopes", scopes ?? new[] { "user:profile", "user:inference" } } } } }));
            }
            internal ClaudeUsageAccount Create(Func<ClaudeRequest, CancellationToken, Task<ClaudeResponse>> transport = null)
            {
                return new ClaudeUsageAccount(Root, transport ?? Send, () => Now);
            }
            private Task<ClaudeResponse> Send(ClaudeRequest request, CancellationToken cancellation)
            {
                Requests.Add(request);
                if (Status != 200) return Task.FromResult(new ClaudeResponse { Status = Status, Body = "server-secret", RetryAfter = RetryAfter });
                string body;
                if (request.Url == ClaudeUsageAccount.ProfileEndpoint)
                    body = Profile ?? "{\"account\":{\"uuid\":\"" + Account + "\",\"email_address\":\"private@example.test\"},\"organization\":{\"uuid\":\"" + Organization + "\"}}";
                else if (request.Url == ClaudeUsageAccount.UsageEndpoint)
                {
                    if (OnUsage != null) OnUsage();
                    body = Usage ?? "{\"five_hour\":{\"utilization\":25,\"resets_at\":\"2030-01-02T16:00:00Z\"},\"seven_day\":{\"utilization\":40,\"resets_at\":\"2030-01-09T12:00:00Z\"}}";
                }
                else if (request.Url == ClaudeUsageAccount.TokenEndpoint)
                    body = "{\"access_token\":\"renewed-access\",\"refresh_token\":\"renewed-refresh\",\"expires_in\":3600,\"token_type\":\"Bearer\"}";
                else throw new Exception("Unexpected endpoint.");
                return Task.FromResult(new ClaudeResponse { Status = 200, Body = body });
            }
        }

        private sealed class NeverDesktop : ICodexDesktopLifecycle
        {
            internal int Calls;
            public bool TryStop(TimeSpan timeout, out string error) { Calls++; error = "Forbidden"; return false; }
            public bool TryStart(TimeSpan timeout, out string error) { Calls++; error = "Forbidden"; return false; }
        }

        private sealed class FailingAccount : IUsageAccount
        {
            public AccountProvider Provider { get { return AccountProviders.Codex; } }
            public string ProfileRoot { get { return "unused"; } }
            public event EventHandler AccountChanged { add { } remove { } }
            public Task<AccountSnapshot> RefreshAsync() { throw new Exception("secret"); }
            public Task<AccountLoginPrompt> StartLoginAsync() { throw new NotSupportedException(); }
            public Task LogoutAsync() { throw new NotSupportedException(); }
            public void Dispose() { }
        }

        private static Dictionary<string, object> Object(string json)
        { return (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json); }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        // Plays the browser returning to the meter's loopback address.
        private static int Callback(string url)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Proxy = null; request.Timeout = 15000;
            try { using (HttpWebResponse response = (HttpWebResponse)request.GetResponse()) return (int)response.StatusCode; }
            catch (WebException error)
            {
                HttpWebResponse response = error.Response as HttpWebResponse;
                if (response == null) throw;
                using (response) return (int)response.StatusCode;
            }
        }
    }

#if PROVIDER_CORE_TEST
    // Focused runner: Codex billing is an inert boundary; no dashboard/global profile is constructed.
    // Production builds do not define PROVIDER_CORE_TEST and use the real billing implementation.
    internal sealed class AccountSubscriptionInfo { }
    internal sealed class AccountSubscriptionReader
    {
        internal AccountSubscriptionReader(string profile) { }
        internal Task<AccountSubscriptionInfo> ReadAsync(string plan, string email) { throw new InvalidOperationException("Billing is outside this test boundary."); }
    }
    internal static class WebSubscriptionStore
    {
        internal static string AccountId(string profile, string email) { throw new InvalidOperationException("Codex credentials are outside this test boundary."); }
    }
    internal static class ProviderCoreTestRunner
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 1) ProviderRegressionTests.RunCore(Console.WriteLine, args[0]);
                else foreach (string test in args.Skip(1))
                {
                    typeof(ProviderRegressionTests).GetMethod(test, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { args[0] });
                    Console.WriteLine("PASS providers: " + test);
                }
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
#endif
}
