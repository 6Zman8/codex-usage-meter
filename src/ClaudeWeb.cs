using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace CodexUsageMeter
{
    // claude.ai web data for one Claude slot: saved limit resets ("Reset for free") and the subscription's next charge/end date.
    // Neither is available to the OAuth token, so the slot's own WebView2 profile (cookies stay in <slot>\claude-web) reads them.
    // Sources (third-party observations, not a public API): CodexBar docs/claude.md (Web API: /api/account,
    // /api/organizations/{org}/usage?cedar_ember=1, /api/organizations/{org}/subscription_details) and
    // context-panel docs/provider-usage-access.md (Claude Desktop settings usage GET uses cedar_ember=1&skip_spend=1).
    internal sealed class ClaudeWebGrant
    {
        public int Remaining { get; set; }
        public DateTime? StartsAt { get; set; }
        public DateTime? EndsAt { get; set; }
        public bool Paused { get; set; }
        public string Label { get; set; }
    }

    internal sealed class ClaudeWebRecord
    {
        public string AccountId { get; set; }
        public string OrganizationId { get; set; }
        public bool Linked { get; set; }
        public DateTime CheckedAtUtc { get; set; }
        public DateTime LastAttemptUtc { get; set; }
        public string LastError { get; set; }
        // Null: the usage response carried no readable reset inventory.
        public bool? ResetsEligible { get; set; }
        public string ResetsNote { get; set; }
        public List<ClaudeWebGrant> Grants { get; set; }
        public AccountSubscriptionInfo Subscription { get; set; }
    }

    internal sealed class ClaudeWebIdentity
    {
        internal string AccountId, OrganizationId, Email;
    }

    internal static class ClaudeWeb
    {
        internal const string StartUrl = "https://claude.ai/settings/usage";
        internal const int MaximumJsonLength = 524288;
        private const int MaximumResets = 50;
        private static readonly object Gate = new object();

        internal static string PathFor(string root) { return Path.Combine(root, "claude-web.json"); }
        internal static string ProfileFolder(string root) { return Path.Combine(root, "claude-web"); }

        internal static ClaudeWebIdentity IdentityOf(AccountSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.IsAuthenticated || String.IsNullOrWhiteSpace(snapshot.RemoteAccountId) ||
                String.IsNullOrWhiteSpace(snapshot.RemoteOrganizationId) || String.IsNullOrWhiteSpace(snapshot.Email)) return null;
            return new ClaudeWebIdentity { AccountId = snapshot.RemoteAccountId, OrganizationId = snapshot.RemoteOrganizationId, Email = snapshot.Email.Trim() };
        }

        // The web session must be the slot's OAuth account: same email and a membership in the OAuth organization.
        internal static bool VerifyAccount(string json, ClaudeWebIdentity expected, out string error)
        {
            error = "claude.ai 계정 정보를 확인하지 못했습니다. 웹 로그인 상태를 확인해 주세요.";
            Dictionary<string, object> account = Parse(json);
            if (account == null || expected == null) return false;
            string email = AccountSubscription.Text(account, "email_address");
            if (String.IsNullOrWhiteSpace(email)) return false;
            bool member = JsonValue.AsItems(AccountSubscription.Get(account, "memberships")).Any(item =>
            {
                Dictionary<string, object> organization = AccountSubscription.Map(AccountSubscription.Get(AccountSubscription.Map(item), "organization"));
                Guid id, wanted;
                return Guid.TryParse(AccountSubscription.Text(organization, "uuid"), out id) &&
                    Guid.TryParse(expected.OrganizationId, out wanted) && id == wanted;
            });
            if (!String.Equals(email.Trim(), expected.Email.Trim(), StringComparison.OrdinalIgnoreCase) || !member)
            {
                error = "claude.ai에 로그인한 계정이 이 칸의 Claude 계정(" + expected.Email + ")과 다릅니다. 웹 정보는 표시하지 않았습니다.";
                return false;
            }
            return true;
        }

        // cedar_ember block of /api/organizations/{org}/usage?cedar_ember=1&skip_spend=1.
        // Returns null when the inventory is absent or unreadable; an ineligible block is an empty inventory.
        internal static List<ClaudeWebGrant> ParseResets(string usageJson, out bool? eligible, out string reason)
        {
            eligible = null; reason = null;
            Dictionary<string, object> block = AccountSubscription.Map(AccountSubscription.Get(Parse(usageJson), "cedar_ember"));
            object flag = AccountSubscription.Get(block, "eligible");
            if (block == null || !(flag is bool)) return null;
            object rawGrants = AccountSubscription.Get(block, "grants");
            if (rawGrants != null && !(rawGrants is System.Collections.IList)) return null;
            List<ClaudeWebGrant> grants = new List<ClaudeWebGrant>();
            if (!(bool)flag)
            {
                eligible = false; reason = AccountSubscription.Text(block, "ineligible_reason");
                return grants;
            }
            foreach (object item in JsonValue.AsItems(rawGrants))
            {
                ClaudeWebGrant grant = ParseGrant(AccountSubscription.Map(item));
                // An unreadable grant is dropped; other grants stay valid.
                if (grant != null) grants.Add(grant);
            }
            if (grants.Sum(grant => (long)grant.Remaining) > MaximumResets) return null;
            eligible = true;
            return grants;
        }

        private static ClaudeWebGrant ParseGrant(Dictionary<string, object> value)
        {
            if (value == null) return null;
            object left = AccountSubscription.Get(value, "resets_left"), paused = AccountSubscription.Get(value, "paused");
            if (!(left is int) || (int)left < 0 || (paused != null && !(paused is bool))) return null;
            DateTime? starts, ends;
            if (!OptionalInstant(AccountSubscription.Get(value, "starts_at"), out starts) ||
                !OptionalInstant(AccountSubscription.Get(value, "ends_at"), out ends)) return null;
            string label = AccountSubscription.Get(value, "label") as string;
            return new ClaudeWebGrant { Remaining = (int)left, Paused = paused is bool && (bool)paused, StartsAt = starts, EndsAt = ends,
                Label = String.IsNullOrWhiteSpace(label) ? null : label.Trim() };
        }

        private static bool OptionalInstant(object value, out DateTime? instant)
        {
            instant = null;
            if (value == null) return true;
            string text = value as string;
            DateTimeOffset parsed;
            if (text == null || text.Length > 64 || !(text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) ||
                System.Text.RegularExpressions.Regex.IsMatch(text, @"[+-]\d{2}:?\d{2}$")) ||
                !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)) return false;
            instant = parsed.UtcDateTime;
            return true;
        }

        // /api/organizations/{org}/subscription_details. A scheduled ending (plan_ending_*) wins over a next charge.
        internal static AccountSubscriptionInfo ParseSubscription(string json, DateTime nowUtc)
        {
            nowUtc = nowUtc.ToUniversalTime();
            Dictionary<string, object> fields = Parse(json);
            string status = AccountSubscription.Text(fields, "status");
            if (fields == null || !new[] { "active", "trialing", "canceled" }.Contains(status) ||
                !new[] { "next_charge_at", "next_charge_date", "plan_ending_at", "plan_ending_before" }.All(fields.ContainsKey))
                return AccountSubscription.Unavailable("claude.ai 구독 정보 형식을 확인하지 못했습니다.", nowUtc);
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

        // A calendar-only date stays on its own day in local time; a timestamp is converted to local time.
        private static bool BillingDate(Dictionary<string, object> fields, string key, out DateTime? date)
        {
            date = null;
            object value = AccountSubscription.Get(fields, key);
            if (value == null) return true;
            string text = value as string;
            DateTime day;
            if (text != null && text.Length == 10)
            {
                if (!DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) return false;
                date = DateTime.SpecifyKind(day, DateTimeKind.Local);
                return true;
            }
            DateTime? instant;
            if (!OptionalInstant(text, out instant) || !instant.HasValue) return false;
            date = instant.Value.ToLocalTime();
            return true;
        }

        // Saves only data from a session verified (before and after the reads) as the slot's own account.
        internal static bool Accept(string root, ClaudeWebIdentity expected, string accountJson, string accountAfterJson,
            int usageStatus, string usageJson, int subscriptionStatus, string subscriptionJson, DateTime nowUtc, out string error)
        {
            nowUtc = nowUtc.ToUniversalTime();
            if (!VerifyAccount(accountJson, expected, out error) || !VerifyAccount(accountAfterJson, expected, out error)) return false;
            ClaudeWebRecord record = new ClaudeWebRecord { AccountId = expected.AccountId, OrganizationId = expected.OrganizationId,
                Linked = true, CheckedAtUtc = nowUtc, LastAttemptUtc = nowUtc, Grants = new List<ClaudeWebGrant>() };
            if (usageStatus == 200)
            {
                bool? eligible; string reason;
                List<ClaudeWebGrant> grants = ParseResets(usageJson, out eligible, out reason);
                record.ResetsEligible = eligible;
                if (grants != null) record.Grants = grants;
                record.ResetsNote = grants == null ? "claude.ai 응답에 초기화권 정보가 없습니다." :
                    eligible == false ? "claude.ai에서 초기화권을 받을 수 없는 상태입니다" + (String.IsNullOrWhiteSpace(reason) ? "." : "(" + reason + ").") : null;
            }
            else record.ResetsNote = "claude.ai 사용량 정보를 받지 못했습니다(HTTP " + usageStatus.ToString(CultureInfo.InvariantCulture) + ").";
            record.Subscription = subscriptionStatus == 200 ? ParseSubscription(subscriptionJson, nowUtc) :
                AccountSubscription.Unavailable("claude.ai 구독 정보를 받지 못했습니다(HTTP " + subscriptionStatus.ToString(CultureInfo.InvariantCulture) + ").", nowUtc);
            Save(root, record);
            error = null;
            return true;
        }

        internal static void Failed(string root, ClaudeWebIdentity expected, string error, DateTime nowUtc)
        {
            lock (Gate)
            {
                ClaudeWebRecord record = Load(root);
                if (record == null || !Matches(record, expected)) return;
                record.LastAttemptUtc = nowUtc.ToUniversalTime(); record.LastError = error;
                try { Save(root, record); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        internal static ClaudeWebRecord Load(string root)
        {
            if (String.IsNullOrWhiteSpace(root)) return null;
            lock (Gate)
            {
                try
                {
                    string path = PathFor(root);
                    if (!File.Exists(path) || new FileInfo(path).Length > 65536) return null;
                    return new JavaScriptSerializer().Deserialize<ClaudeWebRecord>(File.ReadAllText(path, Encoding.UTF8));
                }
                catch { return null; }
            }
        }

        private static void Save(string root, ClaudeWebRecord record)
        {
            lock (Gate) ClaudeUsageAccount.AtomicWrite(PathFor(root), new JavaScriptSerializer().Serialize(record));
        }

        internal static bool Matches(ClaudeWebRecord record, ClaudeWebIdentity identity)
        {
            return record != null && identity != null && record.Linked &&
                String.Equals(record.AccountId, identity.AccountId, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(record.OrganizationId, identity.OrganizationId, StringComparison.OrdinalIgnoreCase);
        }

        // Background reads are infrequent: every 3 hours, or 1 hour after a failure.
        internal static bool IsDue(ClaudeWebRecord record, ClaudeWebIdentity identity, DateTime nowUtc)
        {
            if (!Matches(record, identity)) return false;
            DateTime attempt = record.LastAttemptUtc.ToUniversalTime(); nowUtc = nowUtc.ToUniversalTime();
            return nowUtc < attempt || nowUtc - attempt >= TimeSpan.FromHours(String.IsNullOrWhiteSpace(record.LastError) ? 3 : 1);
        }

        // Fills the snapshot's reset-credit and subscription fields from this slot's verified web record.
        internal static void Apply(AccountSnapshot snapshot, string root, DateTime nowUtc)
        {
            nowUtc = nowUtc.ToUniversalTime();
            snapshot.ResetCredits = new List<ResetCreditInfo>(); snapshot.ResetCreditCount = null; snapshot.Subscription = null;
            snapshot.ResetCreditsNote = null; snapshot.WebConnectionNeeded = false;
            ClaudeWebIdentity identity = IdentityOf(snapshot);
            if (identity == null) return;
            ClaudeWebRecord record = Load(root);
            if (!Matches(record, identity))
            {
                snapshot.WebConnectionNeeded = true;
                snapshot.ResetCreditsNote = "구독 날짜 줄을 눌러 claude.ai에 로그인하면 초기화권을 표시합니다.";
                snapshot.Subscription = new AccountSubscriptionInfo { Error = "눌러서 claude.ai에 로그인하면 초기화권과 다음 결제일을 자동으로 확인합니다." };
                return;
            }
            DateTime checkedAt = record.CheckedAtUtc.ToUniversalTime();
            string failure = String.IsNullOrWhiteSpace(record.LastError) ? "" : "\n최근 자동 확인 실패: " + record.LastError;
            string source = "claude.ai에서 " + checkedAt.ToLocalTime().ToString("M/d HH:mm", CultureInfo.InvariantCulture) + " 확인";
            if (checkedAt > nowUtc || nowUtc - checkedAt >= TimeSpan.FromHours(24))
            {
                snapshot.ResetCreditsNote = "claude.ai 확인이 24시간 넘게 지났습니다. 구독 날짜 줄을 눌러 다시 연결해 주세요." + failure;
                snapshot.Subscription = new AccountSubscriptionInfo { Error = snapshot.ResetCreditsNote, CheckedAt = checkedAt };
                return;
            }
            if (record.ResetsEligible.HasValue)
            {
                foreach (ClaudeWebGrant grant in record.Grants ?? new List<ClaudeWebGrant>())
                {
                    // Started, unpaused and unexpired grants count; an expiry that passes drops the reset without a new read.
                    if (grant.Paused || grant.Remaining <= 0 || (grant.StartsAt.HasValue && grant.StartsAt.Value.ToUniversalTime() > nowUtc) ||
                        (grant.EndsAt.HasValue && grant.EndsAt.Value.ToUniversalTime() <= nowUtc)) continue;
                    for (int index = 0; index < grant.Remaining; index++)
                        snapshot.ResetCredits.Add(new ResetCreditInfo { Status = "available", Title = grant.Label,
                            GrantedAt = grant.StartsAt.HasValue ? (DateTime?)grant.StartsAt.Value.ToLocalTime() : null,
                            ExpiresAt = grant.EndsAt.HasValue ? (DateTime?)grant.EndsAt.Value.ToLocalTime() : null });
                }
                snapshot.ResetCreditCount = snapshot.ResetCredits.Count;
            }
            snapshot.ResetCreditsNote = (String.IsNullOrWhiteSpace(record.ResetsNote) ? "" : record.ResetsNote + "\n") + source + failure;
            if (record.Subscription != null)
            {
                AccountSubscriptionInfo subscription = record.Subscription.Copy();
                if (subscription.Date.HasValue) subscription.Date = subscription.Date.Value.ToLocalTime();
                subscription.Error = source + (String.IsNullOrWhiteSpace(subscription.Error) ? "" : "\n" + subscription.Error) + failure;
                snapshot.Subscription = subscription;
            }
        }

        private static Dictionary<string, object> Parse(string json)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(json) || json.Length > MaximumJsonLength) return null;
                return new JavaScriptSerializer { MaxJsonLength = MaximumJsonLength, RecursionLimit = 32 }.DeserializeObject(json) as Dictionary<string, object>;
            }
            catch { return null; }
        }
    }

    internal static class ClaudeWebService
    {
        private static readonly HashSet<string> Pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static void QueueRefresh(string root, ClaudeWebIdentity identity)
        {
            Application application = Application.Current;
            if (application == null || application.Dispatcher.HasShutdownStarted || !ClaudeWeb.IsDue(ClaudeWeb.Load(root), identity, DateTime.UtcNow)) return;
            lock (Pending) { if (!Pending.Add(root)) return; }
            application.Dispatcher.BeginInvoke(new Action(async delegate {
                await WebSubscriptionService.Gate.WaitAsync();
                try
                {
                    if (!ClaudeWeb.IsDue(ClaudeWeb.Load(root), identity, DateTime.UtcNow)) return;
                    using (ClaudeWebWindow window = new ClaudeWebWindow(root, identity, false)) await window.RunAsync(null);
                }
                catch { ClaudeWeb.Failed(root, identity, "claude.ai 로그인 또는 연결 상태를 확인해 주세요.", DateTime.UtcNow); }
                finally { WebSubscriptionService.Gate.Release(); lock (Pending) Pending.Remove(root); }
            }));
        }

        internal static async Task<bool> ConnectAsync(Window owner, string root, ClaudeWebIdentity identity)
        {
            if (identity == null) throw new InvalidOperationException("먼저 Claude 계정 연결 상태를 새로고침해 주세요.");
            lock (Pending) { if (!Pending.Add(root)) throw new InvalidOperationException("이 계정의 claude.ai 정보를 확인 중입니다. 잠시 후 다시 눌러 주세요."); }
            await WebSubscriptionService.Gate.WaitAsync();
            try { using (ClaudeWebWindow window = new ClaudeWebWindow(root, identity, true)) return await window.RunAsync(owner); }
            finally { WebSubscriptionService.Gate.Release(); lock (Pending) Pending.Remove(root); }
        }

        // The card's 연결: one window signs in to claude.ai, approves the OAuth request and reads the web data with the same session.
        // Returns null once the web data is saved, otherwise why it is not; the OAuth result is the login's own Completion.
        internal static async Task<string> SignInAsync(Window owner, string root, AccountLoginPrompt login, Func<Task<ClaudeWebIdentity>> identify)
        {
            // Holding the slot keeps a hidden refresh from queueing behind the sign-in; an existing holder only delays the window.
            bool owned;
            lock (Pending) owned = Pending.Add(root);
            try
            {
                await WebSubscriptionService.Gate.WaitAsync();
                try
                {
                    if (login.Completion.IsCompleted) return "Claude 연결 창을 열기 전에 로그인 요청이 끝났습니다.";
                    using (ClaudeWebWindow window = new ClaudeWebWindow(root, login, identify, true)) return await window.SignInAsync(owner);
                }
                finally { WebSubscriptionService.Gate.Release(); }
            }
            finally { if (owned) lock (Pending) Pending.Remove(root); }
        }
    }

    // Reads claude.ai with the page's own session from inside the page (same origin), so cookies never leave the slot's WebView2 profile.
    internal sealed class ClaudeWebWindow : WebLoginWindow
    {
        private readonly string _root;
        private ClaudeWebIdentity _identity;
        private readonly AccountLoginPrompt _login;
        private readonly Func<Task<ClaudeWebIdentity>> _identify;
        private Uri _callback;
        private readonly string _nonce = Guid.NewGuid().ToString("N");
        private CoreWebView2 _web;
        private DispatcherTimer _poll;
        private bool _reading;
        // Last reason the web data was not saved; shown after a sign-in window closes.
        internal string WebError { get; private set; }

        internal ClaudeWebWindow(string root, ClaudeWebIdentity identity, bool interactive)
            : base("claude.ai 연결 · Codex 미터기", identity.Email + " 계정으로 claude.ai에 로그인해 주세요.\n초기화권과 구독 날짜를 확인하면 자동으로 닫힙니다. 로그인 정보는 이 계정 칸에만 보관됩니다.",
                ClaudeWeb.ProfileFolder(root), ClaudeWeb.StartUrl, interactive)
        { _root = root; _identity = identity; }

        // One sign-in: the OAuth approval runs in this slot's claude.ai profile, then the same session's web data is read.
        internal ClaudeWebWindow(string root, AccountLoginPrompt login, Func<Task<ClaudeWebIdentity>> identify, bool interactive)
            : base("Claude 연결 · Codex 미터기", login.Message, ClaudeWeb.ProfileFolder(root), login.VerificationUrl, interactive)
        { _root = root; _login = login; _identify = identify; _callback = new Uri(login.CallbackUrl); }

        protected override TimeSpan BackgroundTimeout { get { return TimeSpan.FromSeconds(40); } }
        protected override string TimeoutError { get { return "claude.ai 로그인이 만료되었거나 응답을 받지 못했습니다. 구독 날짜 줄을 눌러 다시 연결해 주세요."; } }
        protected override void RecordFailure(string error) { if (_identity != null) ClaudeWeb.Failed(_root, _identity, error, DateTime.UtcNow); }
        protected override bool AllowsNavigation(Uri uri) { return base.AllowsNavigation(uri) || IsCallback(uri, _callback); }

        // Exactly this run's loopback callback (scheme, host, port and path); the query carries the code and state.
        internal static bool IsCallback(Uri uri, Uri callback)
        {
            return callback != null && uri != null && uri.Scheme == Uri.UriSchemeHttp && String.IsNullOrEmpty(uri.UserInfo) &&
                String.Equals(uri.Host, callback.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == callback.Port &&
                uri.AbsolutePath == callback.AbsolutePath;
        }

        // Closing the window ends a pending sign-in. Returns null once the web data is saved, otherwise why it is not.
        internal async Task<string> SignInAsync(Window owner)
        {
            try { return await RunAsync(owner) ? null : (WebError ?? "claude.ai 정보를 확인하기 전에 창이 닫혔습니다."); }
            finally { if (!_login.Completion.IsCompleted) _login.Cancel(); }
        }

        protected override void Attach(CoreWebView2 web)
        {
            _web = web;
            web.WebMessageReceived += Received;
            // A navigation discards any script still waiting in the previous page.
            web.NavigationCompleted += delegate { _reading = false; Read(); };
            // Sign-in can finish inside the single-page app without a navigation event.
            _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _poll.Tick += delegate { if (Closed) _poll.Stop(); else Read(); };
            _poll.Start();
            if (_login != null) FinishLogin();
        }

        // After the OAuth callback the verified owner comes from the account refresh; the same window then reads claude.ai.
        private async void FinishLogin()
        {
            try { await _login.Completion; }
            catch (Exception)
            {
                // The sign-in error is reported by the caller; nothing is left for this window.
                _callback = null; Complete(false); return;
            }
            _callback = null;
            if (Closed) return;
            ShowStatus("Claude 로그인을 마쳤습니다. 같은 로그인으로 claude.ai의 초기화권과 구독 날짜를 확인하는 중입니다…");
            ClaudeWebIdentity identity = null;
            try { identity = await _identify(); }
            catch (Exception) { }
            if (Closed) return;
            if (identity == null)
            {
                WebError = "Claude 계정 정보를 확인하지 못해 claude.ai 정보를 읽지 않았습니다.";
                Complete(false); return;
            }
            _identity = identity;
            _web.Navigate(ClaudeWeb.StartUrl);
        }

        private void Report(string text) { WebError = text; ShowStatus(text); }

        internal static bool IsClaudePage(string address)
        {
            Uri uri;
            return Uri.TryCreate(address, UriKind.Absolute, out uri) && uri.Scheme == "https" && uri.Host == "claude.ai" && uri.IsDefaultPort;
        }

        private async void Read()
        {
            // A sign-in run reads only after the OAuth callback has given the verified owner.
            if (Closed || _reading || _web == null || _identity == null || !IsClaudePage(_web.Source)) return;
            _reading = true;
            try { await _web.ExecuteScriptAsync(Script(_identity.OrganizationId, _nonce)); }
            catch { _reading = false; }
        }

        internal static string Script(string organization, string nonce)
        {
            JavaScriptSerializer json = new JavaScriptSerializer();
            return "(async()=>{const nonce=" + json.Serialize(nonce) + ",org=encodeURIComponent(" + json.Serialize(organization) + ");" +
                "const send=m=>window.chrome.webview.postMessage(JSON.stringify(Object.assign({meter:'claude-web',nonce:nonce},m)));" +
                "const get=async p=>{const r=await fetch(p,{credentials:'include',cache:'no-store',headers:{'Accept':'application/json'}});return{status:r.status,body:r.status===200?await r.text():''};};" +
                "try{const account=await get('/api/account');if(account.status!==200){send({stage:'signin',status:account.status});return;}" +
                "const usage=await get('/api/organizations/'+org+'/usage?cedar_ember=1&skip_spend=1');" +
                "const subscription=await get('/api/organizations/'+org+'/subscription_details');" +
                "const after=await get('/api/account');" +
                "send({stage:'data',account:account.body,after:after.body,usageStatus:usage.status,usage:usage.body,subscriptionStatus:subscription.status,subscription:subscription.body});}" +
                "catch(e){send({stage:'error'});}})();";
        }

        private void Received(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (Closed || !IsClaudePage(e.Source)) return;
            Dictionary<string, object> message;
            try
            {
                string text = e.TryGetWebMessageAsString();
                if (text == null || text.Length > 4 * ClaudeWeb.MaximumJsonLength) return;
                message = new JavaScriptSerializer { MaxJsonLength = 4 * ClaudeWeb.MaximumJsonLength, RecursionLimit = 8 }.DeserializeObject(text) as Dictionary<string, object>;
            }
            catch { return; }
            if (AccountSubscription.Text(message, "meter") != "claude-web" || AccountSubscription.Text(message, "nonce") != _nonce) return;
            _reading = false;
            string stage = AccountSubscription.Text(message, "stage");
            if (stage == "signin")
            {
                // Not signed in yet; the person signs in (interactive) or the hidden run times out.
                if (_login != null) Report("Claude 사용량은 연결했지만 claude.ai 로그인 상태를 확인하지 못했습니다. 이 창에서 claude.ai에 로그인하면 이어서 확인합니다.");
                return;
            }
            if (stage != "data") { Report("claude.ai 정보를 읽지 못했습니다. 웹 페이지의 로그인 상태를 확인해 주세요."); return; }
            string error; bool accepted;
            try
            {
                accepted = ClaudeWeb.Accept(_root, _identity, AccountSubscription.Text(message, "account"), AccountSubscription.Text(message, "after"),
                    Status(message, "usageStatus"), AccountSubscription.Text(message, "usage"),
                    Status(message, "subscriptionStatus"), AccountSubscription.Text(message, "subscription"), DateTime.UtcNow, out error);
            }
            catch (Exception ex)
            {
                // A WebView2 event callback must not let a disk/permission failure escape and end the process; keep the previous record.
                Report("claude.ai 정보를 저장하지 못했습니다(" + ex.GetType().Name + "). 디스크 공간이나 폴더 권한을 확인한 뒤 다시 시도해 주세요.");
                if (!Interactive) Complete(false);
                return;
            }
            if (accepted) { Complete(true); return; }
            if (Interactive) { Report(error + "\n웹 화면에서 로그아웃한 뒤 " + _identity.Email + " 계정으로 다시 로그인해 주세요. 미터기 정보는 변경하지 않았습니다."); return; }
            WebError = error; RecordFailure(error); Complete(false);
        }

        private static int Status(Dictionary<string, object> message, string key)
        {
            object value = AccountSubscription.Get(message, key);
            return value is int ? (int)value : 0;
        }
    }
}
