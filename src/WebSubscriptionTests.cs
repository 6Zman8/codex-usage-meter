using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                    await RunUserScript(Path.Combine(evidence, "userscript-fixture"));
                    report.AppendLine("PASS Tampermonkey script 1.4.0: claude.ai same-origin reads post only email/organization/status/dates and route to the matching Claude slot; ChatGPT payload unchanged");
                    code = 0;
                }
                catch (Exception error) { report.AppendLine("FAIL " + error); }
                finally { File.WriteAllText(output, report.ToString()); app.Shutdown(); }
            }));
            app.Run(); return code;
        }

        // Runs the repository's userscript (embedded at build) in a hidden WebView2 with fixture claude.ai/chatgpt.com pages.
        // GM_* are recorded stubs, so nothing is posted to a real meter and no real site is contacted.
        private static async Task RunUserScript(string root)
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);
            string script;
            using (Stream resource = typeof(WebSubscriptionTests).Assembly.GetManifestResourceStream("CodexUsageMeter.UserScript.js"))
            using (StreamReader reader = new StreamReader(resource, Encoding.UTF8)) script = reader.ReadToEnd();
            Require(script.Contains("// @version      1.4.0") && script.Contains("// @match        https://chatgpt.com/*") &&
                script.Contains("// @match        https://claude.ai/*") && script.Contains("// @updateURL    " + ChromeSubscriptionBridge.ScriptUrl) &&
                script.Contains("// @downloadURL  " + ChromeSubscriptionBridge.ScriptUrl) && script.Contains("// @name         Codex 미터기 · ChatGPT 구독 연결"),
                "Userscript header changed in a way Tampermonkey cannot auto-update.");
            const string org = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", apiOrg = "dddddddd-dddd-4ddd-8ddd-dddddddddddd", accountId = "11111111-1111-4111-8111-111111111111";
            string key = String.Concat(Enumerable.Repeat("0f", 32)), charge = DateTime.UtcNow.AddDays(9).ToString("yyyy-MM-ddTHH:mm:ssZ");
            string prelude = "window.__posted=[];var unsafeWindow=window;var __store={};" +
                "function GM_getValue(k,d){return Object.prototype.hasOwnProperty.call(__store,k)?__store[k]:d}function GM_setValue(k,v){__store[k]=v}" +
                "function GM_xmlhttpRequest(o){window.__posted.push({url:o.url,method:o.method,headers:o.headers,data:o.data,anonymous:o.anonymous});" +
                "setTimeout(function(){o.onload({status:200})},0)}\n";
            List<string> requests = new List<string>();
            Window host = new Window { Width = 320, Height = 240, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -20000, Top = -20000 };
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(host).EnsureHandle();
            try { CoreWebView2Environment.SetLoaderDllFolderPath(WebViewRuntime.LoaderFolder()); } catch (Exception) { /* Already set by an earlier window. */ }
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "profile"));
            CoreWebView2Controller controller = await environment.CreateCoreWebView2ControllerAsync(handle);
            try
            {
                CoreWebView2 web = controller.CoreWebView2;
                web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                web.WebResourceRequested += delegate(object sender, CoreWebView2WebResourceRequestedEventArgs e) {
                    Uri uri = new Uri(e.Request.Uri);
                    lock (requests) requests.Add(uri.Host + uri.AbsolutePath);
                    string body = "<!doctype html><meta charset='utf-8'><p>fixture</p>", type = "text/html"; int status = 200;
                    if (uri.Host == "claude.ai" && uri.AbsolutePath == "/api/account")
                    {
                        type = "application/json";
                        body = "{\"uuid\":\"" + accountId + "\",\"email_address\":\"web@example.test\",\"full_name\":\"Private Name\",\"memberships\":[" +
                            "{\"organization\":{\"uuid\":\"" + org + "\",\"name\":\"Private Org\"}},{\"organization\":{\"uuid\":\"" + apiOrg + "\",\"name\":\"API\"}},{\"organization\":{\"uuid\":\"not-a-uuid\"}}]}";
                    }
                    else if (uri.Host == "claude.ai" && uri.AbsolutePath == "/api/organizations/" + org + "/subscription_details")
                    {
                        type = "application/json";
                        body = "{\"status\":\"active\",\"next_charge_at\":\"" + charge + "\",\"next_charge_date\":null,\"plan_ending_at\":null,\"plan_ending_before\":null," +
                            "\"billing_email\":\"bill@example.test\",\"payment_method\":{\"last4\":\"4242\"}}";
                    }
                    else if (uri.Host == "claude.ai" && uri.AbsolutePath.StartsWith("/api/")) { type = "application/json"; status = 403; body = "{}"; }
                    else if (uri.Host == "chatgpt.com" && WebSubscriptionWindow.IsAccountResponse(e.Request.Uri))
                    { type = "application/json"; body = AccountSubscriptionRegressionTests.Body("account-web", "plus", true, DateTime.UtcNow.AddDays(20).ToString("o"), null, null, null); }
                    else if (uri.Host == "chatgpt.com")
                        body = "<!doctype html><meta charset='utf-8'><script>setTimeout(()=>fetch('/backend-api/accounts/check/v4-2023-04-27'),300)</script>";
                    e.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(body)), status, status == 200 ? "OK" : "Forbidden", "Content-Type: " + type);
                };
                await web.AddScriptToExecuteOnDocumentCreatedAsync(prelude + script);

                Dictionary<string, object> claude = await NextPost(web, "https://claude.ai/settings/billing#codex-meter-connect=" + key);
                Dictionary<string, object> headers = (Dictionary<string, object>)claude["headers"];
                Require((string)claude["url"] == "http://127.0.0.1:" + ChromeSubscriptionBridge.Port + "/subscription" && (string)claude["method"] == "POST" &&
                    Object.Equals(claude["anonymous"], true) && (string)headers["X-Codex-Meter-Key"] == key && headers.Count == 2, "claude.ai post target or headers changed.");
                string data = (string)claude["data"];
                Dictionary<string, object> payload = AccountSubscription.ParseJson(data);
                Dictionary<string, object> entry = (Dictionary<string, object>)JsonValue.AsItems(payload["organizations"]).Single();
                Require(payload.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(new[] { "email", "kind", "observedAt", "organizations", "version" }) && (string)payload["kind"] == "claude" &&
                    (string)payload["email"] == "web@example.test" && (string)entry["uuid"] == org && (string)entry["status"] == "active" && (string)entry["next_charge_at"] == charge &&
                    entry.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(new[] { "next_charge_at", "next_charge_date", "plan_ending_at", "plan_ending_before", "status", "uuid" }) &&
                    !data.Contains("Private") && !data.Contains("bill@") && !data.Contains("4242") && !data.Contains(accountId), "claude.ai payload carries more than the needed fields: " + data);
                Require(new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<string>(await web.ExecuteScriptAsync("location.hash")) == "" &&
                    requests.Contains("claude.ai/api/account") && requests.Contains("claude.ai/api/organizations/" + apiOrg + "/subscription_details"),
                    "Connection key stayed in the address or organizations were not read same-origin.");
                string slot = Path.Combine(root, "claude-slot");
                Directory.CreateDirectory(slot);
                ChromeSubscriptionBridge.Stop();
                ChromeSubscriptionBridge.RegisterClaude(slot, accountId, org, "web@example.test");
                Require(ChromeSubscriptionBridge.Route(data, DateTime.UtcNow) == 1 && ClaudeSubscriptionStore.Read(slot, accountId, org, DateTime.UtcNow).Kind == "renewal",
                    "The script's claude.ai payload is not accepted by the meter.");
                ChromeSubscriptionBridge.Stop();

                Dictionary<string, object> chatGpt = await NextPost(web, "https://chatgpt.com/settings/billing#codex-meter-connect=" + key);
                Dictionary<string, object> old = AccountSubscription.ParseJson((string)chatGpt["data"]);
                Require(!old.ContainsKey("kind") && Convert.ToString(old["version"]) == "1" && old.ContainsKey("accounts") &&
                    (string)((Dictionary<string, object>)((Dictionary<string, object>)((Dictionary<string, object>)old["accounts"])["account-web"])["account"])["plan_type"] == "plus",
                    "The ChatGPT payload changed shape.");
            }
            finally { controller.Close(); host.Close(); }
        }

        private static async Task<Dictionary<string, object>> NextPost(CoreWebView2 web, string url)
        {
            web.Navigate(url);
            System.Web.Script.Serialization.JavaScriptSerializer json = new System.Web.Script.Serialization.JavaScriptSerializer();
            for (int attempt = 0; attempt < 80; attempt++)
            {
                await Task.Delay(250);
                string posted = json.Deserialize<string>(await web.ExecuteScriptAsync("JSON.stringify(window.__posted||[])"));
                object[] items = json.Deserialize<object[]>(posted);
                if (items != null && items.Length > 0) return (Dictionary<string, object>)items[0];
            }
            throw new InvalidOperationException("The userscript posted nothing for " + new Uri(url).Host + ".");
        }

        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
