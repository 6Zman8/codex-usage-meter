using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace CodexUsageMeter
{
    internal static class WebSubscriptionTests
    {
        internal static void RunData(Action<string> report, string evidenceRoot)
        {
            string root = Path.Combine(evidenceRoot, "web-subscription-fixture");
            Directory.CreateDirectory(root);
            DateTime now = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
            string auth = AccountSubscriptionRegressionTests.Auth("account-web", "web", "prolite", null, null, "fixture");
            File.WriteAllText(Path.Combine(root, "auth.json"), auth);
            string response = AccountSubscriptionRegressionTests.Body("account-web", "prolite", true, "2026-11-02T11:01:48Z", null, "2026-11-02T17:01:48Z", null)
                .Replace("\"renews_at\":", "\"scheduled_plan_change\":{\"changes_at\":\"2026-11-02T11:01:48Z\",\"plan_type\":\"plus\"},\"renews_at\":");
            AccountSubscriptionInfo info;
            Require(WebSubscriptionStore.TryAccept(root, "account-web", "web@example.invalid", "prolite", response, now, out info), "Matching web response rejected.");
            WebSubscriptionRecord stored = WebSubscriptionStore.Load(root, "account-web", "prolite");
            Require(info.Kind == "change" && info.NextPlan == "plus" && WebSubscriptionStore.Fresh(stored, now).Date.HasValue, "Web plan change was lost.");
            Require(WebSubscriptionStore.Load(root, "other", "prolite") == null && WebSubscriptionStore.Load(root, "account-web", "plus") == null, "Account/plan cache leaked.");
            Require(File.ReadAllText(Path.Combine(root, "auth.json")) == auth && !File.ReadAllText(WebSubscriptionStore.PathFor(root)).Contains("token"), "Auth changed or credentials copied to date cache.");
            string cache = File.ReadAllText(WebSubscriptionStore.PathFor(root));
            Require(!WebSubscriptionStore.TryAccept(root, "account-web", "other@example.invalid", "prolite", response, now, out info), "Wrong snapshot identity accepted.");
            Require(!WebSubscriptionStore.TryAccept(root, "account-web", "web@example.invalid", "plus", response, now, out info), "Different live plan accepted.");
            File.WriteAllText(Path.Combine(root, "auth.json"), AccountSubscriptionRegressionTests.Auth("other", "web", "prolite", null, null, "fixture"));
            Require(!WebSubscriptionStore.TryAccept(root, "account-web", "web@example.invalid", "prolite", response, now, out info) && File.ReadAllText(WebSubscriptionStore.PathFor(root)) == cache, "Account changed during web fetch but old result was saved.");
            File.WriteAllText(Path.Combine(root, "auth.json"), auth);
            report("PASS web response identity, plan-change semantics, minimal persistence and auth preservation");

            Require(!WebSubscriptionStore.IsDue(stored, now.AddHours(5)) && WebSubscriptionStore.IsDue(stored, now.AddHours(6)), "Normal refresh cadence incorrect.");
            Require(WebSubscriptionStore.IsDue(stored, now.AddMinutes(1), "plus") && WebSubscriptionStore.Load(root, "account-web", null) != null, "A plan change silently disabled web refresh.");
            Require(WebSubscriptionStore.Fresh(stored, now.AddHours(23)) != null && WebSubscriptionStore.Fresh(stored, now.AddHours(24)) == null && WebSubscriptionStore.Fresh(stored, now.AddSeconds(-1)) == null, "Stale or future-checked web data displayed as current.");
            WebSubscriptionStore.Failed(root, "account-web", "prolite", "로그인 필요", now.AddHours(6));
            stored = WebSubscriptionStore.Load(root, "account-web", "prolite");
            Require(!WebSubscriptionStore.IsDue(stored, now.AddHours(6.5)) && WebSubscriptionStore.IsDue(stored, now.AddHours(7)), "Failure retry cadence incorrect.");
            Require(WebSubscriptionStore.Fresh(stored, now.AddHours(7)).Error.Contains("로그인 필요") && WebSubscriptionStore.Fresh(stored, now.AddHours(24)) == null, "Failure hid freshness or extended date freshness.");
            string missing = AccountSubscriptionRegressionTests.Body("account-web", "prolite", null, null, null, null, null);
            Require(WebSubscriptionStore.TryAccept(root, "account-web", "web@example.invalid", "prolite", missing, now.AddHours(7), out info) && !WebSubscriptionStore.Fresh(WebSubscriptionStore.Load(root, "account-web", "prolite"), now.AddHours(7)).Date.HasValue, "A newer no-date response retained an old billing date.");
            Require(WebSubscriptionWindow.IsAccountResponse("https://chatgpt.com/backend-api/accounts/check/v4-2023-04-27") &&
                !WebSubscriptionWindow.IsAccountResponse("https://chatgpt.com.evil.invalid/backend-api/accounts/check/v4-2023-04-27") &&
                !WebSubscriptionWindow.IsAccountResponse("http://chatgpt.com/backend-api/accounts/check/v4-2023-04-27") &&
                !WebSubscriptionWindow.IsAccountResponse("https://chatgpt.com:8443/backend-api/accounts/check/v4-2023-04-27") &&
                !WebSubscriptionWindow.IsAccountResponse("https://chatgpt.com/backend-api/payments"), "Unrelated response admitted.");
            report("PASS web date freshness, failure retry, authoritative no-date result and response origin filter");
            DateTime boundary = new DateTime(2026, 11, 2, 23, 30, 0, DateTimeKind.Utc);
            string overnight = AccountSubscriptionRegressionTests.Body("account-web", "prolite", true, boundary.ToString("o"), null, null, null);
            Require(WebSubscriptionStore.TryAccept(root, "account-web", "web@example.invalid", "prolite", overnight, now, out info), "Timezone fixture rejected.");
            AccountSubscriptionInfo reloaded = WebSubscriptionStore.Fresh(WebSubscriptionStore.Load(root, "account-web", "prolite"), now);
            Require(reloaded.Date.Value == boundary.ToLocalTime() && reloaded.Date.Value.Kind == DateTimeKind.Local &&
                AccountSubscription.Format(reloaded, now.ToLocalTime()).Contains(boundary.ToLocalTime().ToString("M/d", System.Globalization.CultureInfo.InvariantCulture)), "A persisted UTC timestamp changed the local subscription day.");
            report("PASS persisted web date retains the local calendar day across UTC midnight");
        }

        internal static int RunOnlineProbe(string output)
        {
            output = Path.GetFullPath(output); string evidence = Path.GetDirectoryName(output); Directory.CreateDirectory(evidence);
            int code = 1; string observation = "No rendered state";
            Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Dispatcher.BeginInvoke(new Action(async delegate {
                try
                {
                    using (WebSubscriptionWindow window = new WebSubscriptionWindow(Path.Combine(evidence, "anonymous-web-probe"), "fixture", "fixture@example.invalid", "plus", false))
                    {
                        window.ConfigureForTest = delegate(CoreWebView2Environment environment, CoreWebView2 web) {
                            web.NavigationCompleted += async delegate {
                                try
                                {
                                    await Task.Delay(1800);
                                    observation = await web.ExecuteScriptAsync("JSON.stringify({title:document.title,login:/로그인|Log in|Sign in/.test(document.body.innerText),challenge:/Just a moment|Verify you are human/.test(document.body.innerText)})");
                                    string state = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<string>(observation);
                                    var fields = AccountSubscription.ParseJson(state);
                                    code = Object.Equals(AccountSubscription.Get(fields, "login"), true) && Object.Equals(AccountSubscription.Get(fields, "challenge"), false) ? 0 : 1;
                                }
                                catch { observation = "Rendered-state observation failed"; }
                                finally { window.FinishProbe(); }
                            };
                        };
                        await window.RunAsync(null);
                    }
                }
                catch (Exception error) { observation = error.Message; }
                finally { File.WriteAllText(output, observation); app.Shutdown(); }
            }));
            app.Run(); return code;
        }

        internal static int RunBrowser(string output)
        {
            output = Path.GetFullPath(output); string evidence = Path.GetDirectoryName(output);
            Directory.CreateDirectory(evidence); StringBuilder report = new StringBuilder(); int code = 1;
            Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Dispatcher.BeginInvoke(new Action(async delegate {
                try
                {
                    RunData(line => report.AppendLine(line), evidence);
                    string root = Path.Combine(evidence, "browser-fixture"); Directory.CreateDirectory(root);
                    File.WriteAllText(Path.Combine(root, "auth.json"), AccountSubscriptionRegressionTests.Auth("account-web", "web", "plus", null, null, "fixture"));
                    string response = AccountSubscriptionRegressionTests.Body("account-web", "plus", true, DateTime.UtcNow.AddDays(20).ToString("o"), null, null, null);
                    for (int iteration = 0; iteration < 2; iteration++)
                    {
                        using (WebSubscriptionWindow window = new WebSubscriptionWindow(root, "account-web", "web@example.invalid", "plus", false))
                        {
                            window.ConfigureForTest = delegate(CoreWebView2Environment environment, CoreWebView2 web) {
                                web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                                web.WebResourceRequested += delegate(object sender, CoreWebView2WebResourceRequestedEventArgs request) {
                                    bool account = WebSubscriptionWindow.IsAccountResponse(request.Request.Uri);
                                    string content = account ? response : "<!doctype html><meta charset='utf-8'><script>fetch('/backend-api/accounts/check/v4-2023-04-27')</script>";
                                    request.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(content)), 200, "OK", "Content-Type: " + (account ? "application/json" : "text/html"));
                                };
                            };
                            Require(await window.RunAsync(null), "Hidden WebView2 did not consume its page's account response.");
                        }
                    }
                    Require(WebSubscriptionStore.Fresh(WebSubscriptionStore.Load(root, "account-web", "plus"), DateTime.UtcNow).Date.HasValue, "Browser result did not reach cache.");
                    report.AppendLine("PASS hidden WebView2 launch, normal response capture, persistence, close and second launch");
                    using (WebSubscriptionWindow canceled = new WebSubscriptionWindow(root, "account-web", "web@example.invalid", "plus", false))
                    {
                        Task<bool> pending = canceled.RunAsync(null); canceled.FinishProbe();
                        Require(!await pending, "Cancel during initialization did not finish.");
                    }
                    string authBefore = File.ReadAllText(Path.Combine(root, "auth.json"));
                    using (WebSubscriptionWindow noMatch = new WebSubscriptionWindow(root, "account-web", "web@example.invalid", "plus", false))
                    {
                        noMatch.ConfigureForTest = delegate(CoreWebView2Environment environment, CoreWebView2 web) {
                            web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                            web.WebResourceRequested += delegate(object sender, CoreWebView2WebResourceRequestedEventArgs request) {
                                request.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes("<p>No matching account response</p>")), 200, "OK", "Content-Type: text/html");
                            };
                        };
                        Require(!await noMatch.RunAsync(null), "Missing account response falsely completed the connection.");
                    }
                    WebSubscriptionRecord afterTimeout = WebSubscriptionStore.Load(root, "account-web", "plus");
                    Require(afterTimeout != null && !String.IsNullOrWhiteSpace(afterTimeout.LastError) && !WebSubscriptionStore.IsDue(afterTimeout, DateTime.UtcNow) &&
                        File.ReadAllText(Path.Combine(root, "auth.json")) == authBefore, "Timeout did not preserve auth and apply retry cooldown.");
                    report.AppendLine("PASS initialization cancellation and silent timeout preserve auth and retry cooldown");
                    await RunClaudeBrowser(Path.Combine(evidence, "claude-browser-fixture"));
                    report.AppendLine("PASS hidden claude.ai window: in-page reads, identity check before storing, mismatch rejection and signed-out timeout");
                    await RunClaudeSignIn(Path.Combine(evidence, "claude-sign-in-fixture"));
                    report.AppendLine("PASS Claude one-window sign-in: exact loopback callback only, OAuth saved, web data read in the same session without a second sign-in, web failure keeps OAuth with a no-login retry, window close cancels the login");
                    code = 0;
                }
                catch (Exception error) { report.AppendLine("FAIL " + error); }
                finally { File.WriteAllText(output, report.ToString()); app.Shutdown(); }
            }));
            app.Run(); return code;
        }

        // Fixture claude.ai served inside the WebView2 profile; no network or real session is used.
        private static async Task RunClaudeBrowser(string root)
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);
            const string org = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
            ClaudeWebIdentity identity = new ClaudeWebIdentity { AccountId = "11111111-1111-4111-8111-111111111111", OrganizationId = org, Email = "web@example.invalid" };
            string ends = DateTime.UtcNow.AddDays(5).ToString("yyyy-MM-ddTHH:mm:ssZ"), charge = DateTime.UtcNow.AddDays(12).ToString("yyyy-MM-ddTHH:mm:ssZ");
            Func<string, int, Action<CoreWebView2Environment, CoreWebView2>> serve = (email, accountStatus) => delegate(CoreWebView2Environment environment, CoreWebView2 web) {
                web.AddWebResourceRequestedFilter("https://claude.ai/*", CoreWebView2WebResourceContext.All);
                web.WebResourceRequested += delegate(object sender, CoreWebView2WebResourceRequestedEventArgs request) {
                    string path = new Uri(request.Request.Uri).PathAndQuery, body = "<!doctype html><meta charset='utf-8'><p>claude fixture</p>", type = "text/html";
                    int status = 200;
                    if (path == "/api/account") { type = "application/json"; status = accountStatus;
                        body = "{\"email_address\":\"" + email + "\",\"memberships\":[{\"organization\":{\"uuid\":\"" + org + "\"}}]}"; }
                    else if (path == "/api/organizations/" + org + "/usage?cedar_ember=1&skip_spend=1") { type = "application/json";
                        body = "{\"cedar_ember\":{\"eligible\":true,\"grants\":[{\"id\":\"g\",\"resets_left\":1,\"paused\":false,\"ends_at\":\"" + ends + "\"}]}}"; }
                    else if (path == "/api/organizations/" + org + "/subscription_details") { type = "application/json";
                        body = "{\"status\":\"active\",\"next_charge_at\":\"" + charge + "\",\"next_charge_date\":null,\"plan_ending_at\":null,\"plan_ending_before\":null}"; }
                    request.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(body)), status, status == 200 ? "OK" : "Error", "Content-Type: " + type);
                };
            };
            using (ClaudeWebWindow mismatch = new ClaudeWebWindow(root, identity, false))
            {
                mismatch.ConfigureForTest = serve("other@example.invalid", 200);
                Require(!await mismatch.RunAsync(null) && !File.Exists(ClaudeWeb.PathFor(root)), "A different claude.ai account was stored.");
            }
            using (ClaudeWebWindow window = new ClaudeWebWindow(root, identity, false))
            {
                window.ConfigureForTest = serve("web@example.invalid", 200);
                Require(await window.RunAsync(null), "Hidden claude.ai window did not read the verified session.");
            }
            AccountSnapshot snapshot = new AccountSnapshot { ProviderId = "claude", IsAuthenticated = true, Email = identity.Email,
                RemoteAccountId = identity.AccountId, RemoteOrganizationId = org };
            ClaudeWeb.Apply(snapshot, root, DateTime.UtcNow);
            Require(snapshot.ResetCreditCount == 1 && snapshot.Subscription.Kind == "renewal" && snapshot.Subscription.Date.HasValue &&
                Directory.Exists(ClaudeWeb.ProfileFolder(root)), "claude.ai data did not reach the slot record.");
            using (ClaudeWebWindow signedOut = new ClaudeWebWindow(root, identity, false))
            {
                signedOut.ConfigureForTest = serve("web@example.invalid", 401);
                Require(!await signedOut.RunAsync(null), "Signed-out session completed.");
            }
            ClaudeWebRecord record = ClaudeWeb.Load(root);
            Require(record != null && !String.IsNullOrWhiteSpace(record.LastError) && record.Grants.Count == 1, "Signed-out timeout lost data or cooldown.");
        }

        // Fixture claude.com/claude.ai and Claude OAuth served locally; the loopback callback is the meter's real listener.
        private static async Task RunClaudeSignIn(string root)
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);
            const string org = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", accountId = "11111111-1111-4111-8111-111111111111";
            Uri callback = new Uri("http://localhost:43123/callback");
            Require(ClaudeWebWindow.IsCallback(new Uri("http://localhost:43123/callback?code=a&state=b"), callback) &&
                !ClaudeWebWindow.IsCallback(new Uri("http://127.0.0.1:43123/callback?code=a"), callback) &&
                !ClaudeWebWindow.IsCallback(new Uri("http://localhost:43124/callback"), callback) &&
                !ClaudeWebWindow.IsCallback(new Uri("http://localhost:43123/other"), callback) &&
                !ClaudeWebWindow.IsCallback(new Uri("http://user@localhost:43123/callback"), callback) &&
                !ClaudeWebWindow.IsCallback(new Uri("ftp://localhost:43123/callback"), callback) &&
                !ClaudeWebWindow.IsCallback(new Uri("http://localhost:43123/callback"), null), "A non-callback address was admitted.");
            List<string> sent = new List<string>();
            Func<ClaudeRequest, CancellationToken, Task<ClaudeResponse>> transport = delegate(ClaudeRequest request, CancellationToken cancellation) {
                lock (sent) sent.Add(request.Url);
                string body;
                if (request.Url == ClaudeUsageAccount.TokenEndpoint)
                    body = "{\"access_token\":\"fixture-access\",\"refresh_token\":\"fixture-refresh\",\"expires_in\":3600,\"token_type\":\"Bearer\"}";
                else if (request.Url == ClaudeUsageAccount.ProfileEndpoint)
                    body = "{\"account\":{\"uuid\":\"" + accountId + "\",\"email\":\"web@example.invalid\"},\"organization\":{\"uuid\":\"" + org + "\",\"organization_type\":\"claude_max\"}}";
                else if (request.Url == ClaudeUsageAccount.UsageEndpoint)
                    body = "{\"five_hour\":{\"utilization\":25,\"resets_at\":\"2030-01-02T16:00:00Z\"},\"seven_day\":{\"utilization\":40,\"resets_at\":\"2030-01-09T12:00:00Z\"}}";
                else throw new InvalidOperationException("Unexpected endpoint.");
                return Task.FromResult(new ClaudeResponse { Status = 200, Body = body });
            };
            string ends = DateTime.UtcNow.AddDays(5).ToString("yyyy-MM-ddTHH:mm:ssZ"), charge = DateTime.UtcNow.AddDays(12).ToString("yyyy-MM-ddTHH:mm:ssZ");
            int signInPages = 0;
            List<string> navigations = new List<string>();
            // Forged loopback targets point at this trap; a blocked navigation must never reach it.
            TcpListener trap = new TcpListener(IPAddress.Loopback, 0);
            trap.Start();
            int trapPort = ((IPEndPoint)trap.LocalEndpoint).Port;
            // approve=false leaves the sign-in page waiting, as when the person has not finished.
            Func<string, bool, Action<CoreWebView2Environment, CoreWebView2>> serve = (email, approve) => delegate(CoreWebView2Environment environment, CoreWebView2 web) {
                web.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e) {
                    lock (navigations) navigations.Add((e.Cancel ? "blocked " : "allowed ") + e.Uri);
                };
                web.NavigationCompleted += delegate(object sender, CoreWebView2NavigationCompletedEventArgs e) {
                    lock (navigations) navigations.Add("completed " + e.IsSuccess + " " + e.WebErrorStatus + " " + web.Source);
                };
                web.AddWebResourceRequestedFilter("https://claude.com/*", CoreWebView2WebResourceContext.All);
                web.AddWebResourceRequestedFilter("https://claude.ai/*", CoreWebView2WebResourceContext.All);
                web.WebResourceRequested += async delegate(object sender, CoreWebView2WebResourceRequestedEventArgs request) {
                    // The window's own http filter raises this event too; the loopback callback must reach the real listener.
                    Uri uri = new Uri(request.Request.Uri);
                    if (uri.Scheme != "https" || (uri.Host != "claude.ai" && uri.Host != "claude.com")) return;
                    CoreWebView2Deferral deferral = request.GetDeferral();
                    try
                    {
                        string path = uri.PathAndQuery, body = "<!doctype html><meta charset='utf-8'><p>claude fixture</p>", type = "text/html";
                        int status = 200;
                        if (uri.Host == "claude.com" && uri.AbsolutePath == "/cai/oauth/authorize")
                            body = "<!doctype html><script>location.replace('https://claude.ai/oauth/authorize'+location.search)</script>";
                        else if (uri.AbsolutePath == "/oauth/authorize")
                        {
                            // The person's one sign-in: claude.ai keeps its session cookie, then approval returns to the loopback address.
                            signInPages++;
                            body = "<!doctype html><meta charset='utf-8'><p>sign in</p><script>document.cookie='fixtureSession=1; path=/; max-age=86400; secure';" +
                                (approve ? "const q=new URLSearchParams(location.search),r=new URL(q.get('redirect_uri')),tail='?code=fixture-code&state='+encodeURIComponent(q.get('state'));" +
                                "const forged=['http://127.0.0.1:" + trapPort + "/callback'+tail,'http://localhost:" + trapPort + "/callback'+tail,'http://evil.invalid/callback'+tail];" +
                                "forged.forEach((u,i)=>setTimeout(()=>{location.href=u;},300*i));" +
                                "setTimeout(()=>{location.href=q.get('redirect_uri')+tail;},300*forged.length);" : "") + "</script>";
                        }
                        else if (path == "/api/account")
                        {
                            type = "application/json";
                            bool session = (await web.CookieManager.GetCookiesAsync("https://claude.ai")).Any(cookie => cookie.Name == "fixtureSession");
                            status = session ? 200 : 401;
                            body = "{\"email_address\":\"" + email + "\",\"memberships\":[{\"organization\":{\"uuid\":\"" + org + "\"}}]}";
                        }
                        else if (path == "/api/organizations/" + org + "/usage?cedar_ember=1&skip_spend=1") { type = "application/json";
                            body = "{\"cedar_ember\":{\"eligible\":true,\"grants\":[{\"id\":\"g\",\"resets_left\":1,\"paused\":false,\"ends_at\":\"" + ends + "\"}]}}"; }
                        else if (path == "/api/organizations/" + org + "/subscription_details") { type = "application/json";
                            body = "{\"status\":\"active\",\"next_charge_at\":\"" + charge + "\",\"next_charge_date\":null,\"plan_ending_at\":null,\"plan_ending_before\":null}"; }
                        request.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(body)), status, status == 200 ? "OK" : "Error", "Content-Type: " + type);
                    }
                    finally { deferral.Complete(); }
                };
            };

            // One window: sign-in, exact callback, saved OAuth, then the same session's web data with no second sign-in page.
            string slot = Path.Combine(root, "single");
            using (ClaudeUsageAccount account = new ClaudeUsageAccount(slot, transport, () => DateTime.UtcNow))
            {
                AccountLoginPrompt login = await account.StartLoginAsync();
                string error;
                using (ClaudeWebWindow window = new ClaudeWebWindow(slot, login, async () => ClaudeWeb.IdentityOf(await account.RefreshAsync()), false))
                {
                    window.ConfigureForTest = serve("web@example.invalid", true);
                    error = await window.SignInAsync(null);
                }
                Require(error == null, "One-window sign-in did not finish: " + error + " / " + login.Completion.Status + " / " + String.Join(" | ", navigations));
                await login.Completion;
                string redirect = login.CallbackUrl;
                Require(navigations.Any(n => n.StartsWith("blocked http://127.0.0.1:" + trapPort + "/")) &&
                    navigations.Any(n => n.StartsWith("blocked http://localhost:" + trapPort + "/")) &&
                    navigations.Any(n => n.StartsWith("blocked http://evil.invalid/")),
                    "A forged non-https callback was not blocked: " + String.Join(" | ", navigations));
                string trapped = TrapRequests(trap);
                Require(trapped.Length == 0, "A blocked navigation still sent its request: " + trapped);
                Require(navigations.Any(n => n.StartsWith("allowed " + redirect + "?")) && !navigations.Any(n => n.StartsWith("allowed http://") && !n.StartsWith("allowed " + redirect + "?")),
                    "The exact loopback callback was not the only admitted http address.");
                Require(signInPages == 1 && sent.SequenceEqual(new[] { ClaudeUsageAccount.TokenEndpoint, ClaudeUsageAccount.ProfileEndpoint, ClaudeUsageAccount.UsageEndpoint }),
                    "The sign-in needed a second sign-in page or extra OAuth requests: " + String.Join(", ", sent));
                ClaudeWebIdentity identity = new ClaudeWebIdentity { AccountId = accountId, OrganizationId = org, Email = "web@example.invalid" };
                AccountSnapshot snapshot = await account.RefreshAsync();
                ClaudeWeb.Apply(snapshot, slot, DateTime.UtcNow);
                Require(File.Exists(Path.Combine(slot, ".credentials.json")) && ClaudeWeb.Matches(ClaudeWeb.Load(slot), identity) &&
                    snapshot.ResetCreditCount == 1 && snapshot.Subscription.Date.HasValue && !snapshot.WebConnectionNeeded &&
                    Directory.Exists(ClaudeWeb.ProfileFolder(slot)), "OAuth or claude.ai data did not reach the slot.");
            }

            // Web failure after OAuth: the OAuth connection stays, nothing web is saved, and the retry needs no new OAuth sign-in.
            sent.Clear(); signInPages = 0; navigations.Clear();
            slot = Path.Combine(root, "web-failure");
            using (ClaudeUsageAccount account = new ClaudeUsageAccount(slot, transport, () => DateTime.UtcNow))
            {
                AccountLoginPrompt login = await account.StartLoginAsync();
                string error;
                using (ClaudeWebWindow window = new ClaudeWebWindow(slot, login, async () => ClaudeWeb.IdentityOf(await account.RefreshAsync()), false))
                {
                    window.ConfigureForTest = serve("other@example.invalid", true);
                    error = await window.SignInAsync(null);
                }
                await login.Completion;
                AccountSnapshot snapshot = await account.RefreshAsync();
                ClaudeWebIdentity identity = ClaudeWeb.IdentityOf(snapshot);
                ClaudeWeb.Apply(snapshot, slot, DateTime.UtcNow);
                Require(error != null && error.Contains("다릅니다") && snapshot.IdentityVerified && identity != null &&
                    File.Exists(Path.Combine(slot, ".credentials.json")) && !File.Exists(ClaudeWeb.PathFor(slot)) && snapshot.WebConnectionNeeded,
                    "A web failure lost the OAuth connection, saved web data or hid the retry: " + error);
                int oauthRequests = sent.Count;
                using (ClaudeWebWindow retry = new ClaudeWebWindow(slot, identity, false))
                {
                    retry.ConfigureForTest = serve("web@example.invalid", false);
                    Require(await retry.RunAsync(null), "The web-only retry did not reuse the slot's claude.ai session.");
                }
                Require(sent.Count == oauthRequests && signInPages == 1 && ClaudeWeb.Matches(ClaudeWeb.Load(slot), identity),
                    "The web-only retry asked for a new OAuth sign-in.");
            }

            // Closing the window while the sign-in page waits cancels the pending login; nothing is saved and the callback stops listening.
            sent.Clear();
            slot = Path.Combine(root, "closed");
            using (ClaudeUsageAccount account = new ClaudeUsageAccount(slot, transport, () => DateTime.UtcNow))
            {
                AccountLoginPrompt login = await account.StartLoginAsync();
                string error;
                using (ClaudeWebWindow window = new ClaudeWebWindow(slot, login, async () => ClaudeWeb.IdentityOf(await account.RefreshAsync()), false))
                {
                    Action<CoreWebView2Environment, CoreWebView2> fixture = serve("web@example.invalid", false);
                    window.ConfigureForTest = delegate(CoreWebView2Environment environment, CoreWebView2 web) {
                        fixture(environment, web);
                        web.NavigationCompleted += delegate { if (web.Source.StartsWith("https://claude.ai/oauth/authorize")) window.FinishProbe(); };
                    };
                    error = await window.SignInAsync(null);
                }
                bool canceled = false;
                try { await login.Completion; }
                catch (AccountProviderException failure) { canceled = failure.Kind == AccountErrorKind.Canceled; }
                bool listening = true;
                using (TcpClient probe = new TcpClient())
                {
                    try { probe.Connect(IPAddress.Loopback, new Uri(login.CallbackUrl).Port); }
                    catch (SocketException) { listening = false; }
                }
                Require(error != null && canceled && !listening && sent.Count == 0 && !File.Exists(Path.Combine(slot, ".credentials.json")) &&
                    !File.Exists(ClaudeWeb.PathFor(slot)), "Closing the sign-in window did not cancel the login cleanly.");
            }
            trap.Stop();
        }

        // Chromium may open an idle speculative connection to a navigation's origin; only request bytes count as a sent request.
        private static string TrapRequests(TcpListener trap)
        {
            StringBuilder received = new StringBuilder();
            while (trap.Pending())
                using (TcpClient client = trap.AcceptTcpClient())
                {
                    client.ReceiveTimeout = 1000;
                    byte[] buffer = new byte[1024];
                    try { int count = client.GetStream().Read(buffer, 0, buffer.Length); received.Append(Encoding.ASCII.GetString(buffer, 0, count)); }
                    catch (IOException) { }
                }
            return received.ToString();
        }

        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
