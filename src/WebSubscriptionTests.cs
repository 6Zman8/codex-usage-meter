using System;
using System.IO;
using System.Text;
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

        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
