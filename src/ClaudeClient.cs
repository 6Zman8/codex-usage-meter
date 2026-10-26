using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexUsageMeter
{
    internal sealed class ClaudeRequest
    {
        internal string Url, Method = "GET", AccessToken, Body, ContentType = "application/x-www-form-urlencoded";
    }

    internal sealed class ClaudeResponse
    {
        internal int Status;
        internal string Body, RetryAfter;
    }

    // Claude Code's own browser sign-in (OAuth PKCE with a loopback callback), run by the meter itself
    // so no Claude Code installation is needed. Same request shape as Claude Code and Mixdog.
    internal sealed class ClaudeLoopbackLogin : IDisposable
    {
        internal const string AuthorizeEndpoint = "https://claude.com/cai/oauth/authorize";
        internal const string Scope = "org:create_api_key user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";
        private const string CallbackPath = "/callback";
        private readonly List<TcpListener> _listeners = new List<TcpListener>();
        internal readonly string State = RandomText(), Verifier = RandomText(), RedirectUri, AuthorizeUrl;

        internal ClaudeLoopbackLogin(string clientId)
        {
            TcpListener first = new TcpListener(IPAddress.Loopback, 0);
            first.Start(); _listeners.Add(first);
            int port = ((IPEndPoint)first.LocalEndpoint).Port;
            // Browsers may try IPv6 localhost first; IPv4 alone stays a working fallback.
            try { TcpListener v6 = new TcpListener(IPAddress.IPv6Loopback, port); v6.Start(); _listeners.Add(v6); }
            catch (SocketException) { }
            RedirectUri = "http://localhost:" + port.ToString(CultureInfo.InvariantCulture) + CallbackPath;
            string challenge;
            using (SHA256 hash = SHA256.Create()) challenge = Base64Url(hash.ComputeHash(Encoding.ASCII.GetBytes(Verifier)));
            AuthorizeUrl = AuthorizeEndpoint + "?code=true&client_id=" + Uri.EscapeDataString(clientId) + "&response_type=code&redirect_uri=" +
                Uri.EscapeDataString(RedirectUri) + "&scope=" + Uri.EscapeDataString(Scope) + "&code_challenge=" + challenge +
                "&code_challenge_method=S256&state=" + State;
        }

        // Waits for the matching browser callback. The page answers after the exchange so it shows the true outcome.
        internal async Task ReceiveAsync(Func<string, Task> exchange, CancellationToken cancellation)
        {
            using (cancellation.Register(Dispose))
            {
                Task<TcpClient>[] pending = _listeners.Select(listener => listener.AcceptTcpClientAsync()).ToArray();
                while (true)
                {
                    Task<TcpClient> accepted = await Task.WhenAny(pending).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    int index = Array.IndexOf(pending, accepted);
                    TcpClient client = await accepted.ConfigureAwait(false);
                    pending[index] = _listeners[index].AcceptTcpClientAsync();
                    using (client)
                    {
                        Dictionary<string, string> query = await ReadQueryAsync(client, cancellation).ConfigureAwait(false);
                        if (query == null) { await RespondAsync(client, 404, "찾을 수 없는 주소입니다.").ConfigureAwait(false); continue; }
                        string state, code;
                        query.TryGetValue("state", out state); query.TryGetValue("code", out code);
                        if (state != State)
                        {
                            await RespondAsync(client, 400, "이 로그인 요청과 맞지 않는 응답입니다. 미터기에서 다시 연결해 주세요.").ConfigureAwait(false);
                            continue;
                        }
                        Exception failure = null;
                        if (String.IsNullOrEmpty(code))
                            failure = new AccountProviderException(AccountErrorKind.LoginRequired, "브라우저에서 Claude 로그인을 승인하지 않았습니다. 다시 연결해 주세요.");
                        else
                        {
                            try { await exchange(code).ConfigureAwait(false); }
                            catch (Exception error) { failure = error; }
                        }
                        await RespondAsync(client, failure == null ? 200 : 500, failure == null
                            ? "Claude 연결을 마쳤습니다. 이 창을 닫고 미터기로 돌아가세요."
                            : "Claude 연결을 마치지 못했습니다. 미터기의 안내를 확인해 주세요.").ConfigureAwait(false);
                        if (failure != null) throw failure;
                        return;
                    }
                }
            }
        }

        private static async Task<Dictionary<string, string>> ReadQueryAsync(TcpClient client, CancellationToken cancellation)
        {
            byte[] buffer = new byte[8192];
            int length = 0;
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                // A browser's idle speculative connection must not hold up the real callback.
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using (timeout.Token.Register(client.Close))
                try
                {
                    NetworkStream stream = client.GetStream();
                    while (length < buffer.Length && Encoding.ASCII.GetString(buffer, 0, length).IndexOf("\r\n", StringComparison.Ordinal) < 0)
                    {
                        int count = await stream.ReadAsync(buffer, length, buffer.Length - length).ConfigureAwait(false);
                        if (count == 0) break;
                        length += count;
                    }
                }
                catch (IOException) { return null; }
                catch (ObjectDisposedException) { return null; }
                catch (InvalidOperationException) { return null; }
            }
            string[] parts = Encoding.ASCII.GetString(buffer, 0, length).Split(new[] { "\r\n" }, StringSplitOptions.None)[0].Split(' ');
            if (parts.Length != 3 || parts[0] != "GET") return null;
            string target = parts[1];
            int mark = target.IndexOf('?');
            if ((mark < 0 ? target : target.Substring(0, mark)) != CallbackPath) return null;
            Dictionary<string, string> query = new Dictionary<string, string>(StringComparer.Ordinal);
            if (mark >= 0)
                foreach (string pair in target.Substring(mark + 1).Split('&'))
                {
                    int equals = pair.IndexOf('=');
                    if (equals > 0) query[Decode(pair.Substring(0, equals))] = Decode(pair.Substring(equals + 1));
                }
            return query;
        }

        private static async Task RespondAsync(TcpClient client, int status, string message)
        {
            byte[] body = Encoding.UTF8.GetBytes("<!doctype html><meta charset=\"utf-8\"><title>코덱스 미터기</title>" +
                "<body style=\"font-family:sans-serif;padding:48px\"><h2>" + WebUtility.HtmlEncode(message) + "</h2></body>");
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + (status == 200 ? " OK" : " Error") +
                "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) +
                "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            // A browser that already left does not change the sign-in result.
            try
            {
                NetworkStream stream = client.GetStream();
                await stream.WriteAsync(head, 0, head.Length).ConfigureAwait(false);
                await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private static string Decode(string value) { return Uri.UnescapeDataString(value.Replace('+', ' ')); }
        private static string Base64Url(byte[] bytes) { return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        private static string RandomText()
        {
            byte[] bytes = new byte[32];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return Base64Url(bytes);
        }

        public void Dispose() { foreach (TcpListener listener in _listeners) listener.Stop(); }
    }

    internal sealed class ClaudeUsageAccount : IUsageAccount
    {
        // Public implementation reference (not an official quota API):
        // steipete/CodexBar, f66d18fc6fbb85c7df056bdeef56992de0619038,
        // Sources/CodexBarCore/Providers/Claude/ClaudeOAuth/ClaudeOAuthCredentials.swift.
        internal const string TokenEndpoint = "https://platform.claude.com/v1/oauth/token";
        internal const string OAuthClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
        internal const string ProfileEndpoint = "https://api.anthropic.com/api/oauth/profile";
        internal const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
        private const int MaximumBytes = 1024 * 1024;
        // One usage request per minute tick; under the 60-second timer so tick jitter never skips a poll.
        internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(50);
        // The usage endpoint throttles per account with 429 and Retry-After: 0.
        private static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(5), MaximumBackoff = TimeSpan.FromMinutes(60);
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private readonly Func<ClaudeRequest, CancellationToken, Task<ClaudeResponse>> _transport;
        private readonly Func<DateTime> _now;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private CancellationTokenSource _loginCancellation;
        private Task _login;
        private string _loginUrl;
        private DateTime _retryAt, _nextPoll;
        private int _rateLimitStreak;
        private AccountSnapshot _lastGood;
        private string _lastGoodCredentials;
        private string _terminalCredentials;
        private AccountProviderException _terminalError;
        private bool _disposed;

        public AccountProvider Provider { get { return AccountProviders.Claude; } }
        public string ProfileRoot { get; private set; }
        public event EventHandler AccountChanged;

        internal ClaudeUsageAccount(string profileRoot) : this(profileRoot, SendAsync, () => DateTime.UtcNow) { }
        internal ClaudeUsageAccount(string profileRoot, Func<ClaudeRequest, CancellationToken, Task<ClaudeResponse>> transport, Func<DateTime> now)
        { ProfileRoot = Path.GetFullPath(profileRoot); _transport = transport; _now = now; }

        private sealed class Credentials
        {
            internal string Raw, Access, Refresh, Plan;
            internal DateTime Expires;
            internal Dictionary<string, object> Document, OAuth;
        }

        private sealed class Identity
        {
            internal string Account, Organization, Email, Plan;
            internal bool Same(Identity other) { return other != null && Account == other.Account && Organization == other.Organization; }
        }

        private string CredentialPath { get { return Path.Combine(ProfileRoot, ".credentials.json"); } }
        private string BindingPath { get { return Path.Combine(ProfileRoot, "meter-identity.json"); } }

        public async Task<AccountSnapshot> RefreshAsync()
        {
            AccountSnapshot snapshot = new AccountSnapshot { ProviderId = Provider.Id, UpdatedAt = _now().ToLocalTime() };
            bool entered = false;
            string raw = null;
            try
            {
                await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false); entered = true;
                if (_login != null && !_login.IsCompleted)
                    throw Fault(AccountErrorKind.LoginRequired, "브라우저에서 Claude 로그인을 마쳐 주세요. 다른 계정은 계속 갱신됩니다.");
                raw = ReadOptional(CredentialPath);
                if (raw == null)
                    throw Fault(AccountErrorKind.LoginRequired, "연결에서 Claude 구독 계정으로 로그인해 주세요.");
                if (raw == _terminalCredentials && _terminalError != null) throw _terminalError;
                if (_now() < _retryAt) throw Fault(AccountErrorKind.RateLimited, "Claude 요청이 많아 잠시 대기합니다. " + _retryAt.ToLocalTime().ToString("HH:mm") + " 이후 자동으로 다시 확인합니다.");
                // Between polls the last verified reading is reused; a changed sign-in is read at once.
                if (_lastGood != null && raw == _lastGoodCredentials && _now() < _nextPoll) return _lastGood;
                Credentials credentials = ParseCredentials(raw);
                snapshot.IsAuthenticated = true;
                if (credentials.Expires <= _now().AddMinutes(1))
                    credentials = await RenewAsync(credentials, _lifetime.Token).ConfigureAwait(false);
                raw = credentials.Raw;
                // A token belongs to one account: its identity is read once per credential and kept in the binding.
                Identity bound = BoundIdentity(credentials.Raw);
                Identity after = bound ?? await ReadIdentityAsync(credentials.Access, _lifetime.Token).ConfigureAwait(false);
                ClaudeResponse usage = await RequestAsync(new ClaudeRequest { Url = UsageEndpoint, AccessToken = credentials.Access }, _lifetime.Token).ConfigureAwait(false);
                DateTime observed = _now();
                if (ReadOptional(CredentialPath) != credentials.Raw)
                    throw Fault(AccountErrorKind.IdentityMismatch, "조회 중 계정이 바뀌어 이번 사용량을 저장하지 않았습니다. 다시 연결 상태를 확인해 주세요.");
                Dictionary<string, object> body = ParseObject(usage.Body);
                snapshot.Primary = ParseWindow(JsonValue.AsObject(JsonValue.Get(body, "five_hour")), 300, "5시간 한도");
                snapshot.Secondary = ParseWindow(JsonValue.AsObject(JsonValue.Get(body, "seven_day")), 10080, "주간 한도");
                if (bound == null) SaveBinding(credentials, after);
                snapshot.Email = after.Email;
                snapshot.PlanType = credentials.Plan;
                snapshot.RemoteAccountId = after.Account;
                snapshot.RemoteOrganizationId = after.Organization;
                snapshot.IdentityVerified = true;
                snapshot.HistoryKey = UsageHistoryStore.ProviderAccountKey(Provider.Id, after.Account, after.Organization);
                snapshot.RateLimitsObservedAtUtc = observed;
                if (snapshot.Primary == null && snapshot.Secondary == null)
                    snapshot.UsageError = "Claude가 유효한 단기·주간 한도를 제공하지 않았습니다.";
                _lastGood = snapshot; _lastGoodCredentials = raw; _nextPoll = observed + PollInterval; _rateLimitStreak = 0;
            }
            catch (Exception error)
            {
                AccountProviderException safe = SafeError(error, _lifetime.IsCancellationRequested);
                snapshot.ErrorKind = safe.Kind; snapshot.Error = safe.Message;
                snapshot.Primary = null; snapshot.Secondary = null; snapshot.HistoryKey = null;
                snapshot.IdentityVerified = false; snapshot.RateLimitsObservedAtUtc = default(DateTime);
                if (safe.Kind == AccountErrorKind.LoginRequired || safe.Kind == AccountErrorKind.UnsupportedAuthentication ||
                    safe.Kind == AccountErrorKind.InstallNeeded) snapshot.IsAuthenticated = false;
                if (raw != null && (safe.Kind == AccountErrorKind.LoginRequired || safe.Kind == AccountErrorKind.ScopeDenied ||
                    safe.Kind == AccountErrorKind.RenewalBlocked || safe.Kind == AccountErrorKind.IdentityMismatch))
                { _terminalCredentials = raw; _terminalError = safe; }
                snapshot.RetryAfterUtc = _retryAt > _now() ? (DateTime?)_retryAt : null;
                if (safe.Kind != AccountErrorKind.RateLimited || raw != _lastGoodCredentials) _lastGood = null;
                else if (_lastGood != null)
                {
                    // Throttling does not make the last verified reading wrong; keep showing it with its own time.
                    snapshot.IsAuthenticated = true; snapshot.Email = _lastGood.Email; snapshot.PlanType = _lastGood.PlanType;
                    snapshot.Primary = _lastGood.Primary; snapshot.Secondary = _lastGood.Secondary;
                    snapshot.RateLimitsObservedAtUtc = _lastGood.RateLimitsObservedAtUtc;
                    snapshot.Error = "Claude 요청 제한으로 " + _lastGood.RateLimitsObservedAtUtc.ToLocalTime().ToString("HH:mm") +
                        "에 받은 값을 표시합니다. " + _retryAt.ToLocalTime().ToString("HH:mm") + " 이후 다시 확인합니다.";
                }
            }
            finally { if (entered) _gate.Release(); }
            return snapshot;
        }

        public async Task<AccountLoginPrompt> StartLoginAsync()
        {
            await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                if (_login != null && !_login.IsCompleted)
                    return LoginPrompt();
                Directory.CreateDirectory(ProfileRoot);
                ClaudeLoopbackLogin browser = new ClaudeLoopbackLogin(OAuthClientId);
                _loginCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _loginCancellation.CancelAfter(TimeSpan.FromMinutes(10));
                _terminalCredentials = null; _terminalError = null; _retryAt = default(DateTime);
                _loginUrl = browser.AuthorizeUrl;
                _login = CompleteLoginAsync(browser, _loginCancellation.Token);
                return LoginPrompt();
            }
            catch (Exception error) { throw SafeError(error, _lifetime.IsCancellationRequested); }
            finally { _gate.Release(); }
        }

        private AccountLoginPrompt LoginPrompt()
        {
            CancellationTokenSource cancellation = _loginCancellation;
            return new AccountLoginPrompt { Completion = _login, Cancel = () => cancellation.Cancel(), VerificationUrl = _loginUrl,
                Message = "브라우저에서 Claude 구독 계정으로 로그인해 주세요. 로그인이 끝나면 브라우저를 닫아도 사용량과 초기화 시간이 자동 갱신됩니다.\n\nAPI 키·Console 계정은 지원하지 않습니다." };
        }

        private async Task CompleteLoginAsync(ClaudeLoopbackLogin browser, CancellationToken cancellation)
        {
            try
            {
                using (browser)
                    await browser.ReceiveAsync(code => ExchangeAsync(browser, code, cancellation), cancellation).ConfigureAwait(false);
            }
            catch (Exception error) { throw SafeError(error, cancellation.IsCancellationRequested); }
            finally
            {
                EventHandler changed = AccountChanged;
                if (!_disposed && changed != null) changed(this, EventArgs.Empty);
            }
        }

        private async Task ExchangeAsync(ClaudeLoopbackLogin browser, string code, CancellationToken cancellation)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            ClaudeResponse response = await RequestAsync(new ClaudeRequest { Url = TokenEndpoint, Method = "POST", ContentType = "application/json",
                Body = serializer.Serialize(new Dictionary<string, object> { { "grant_type", "authorization_code" }, { "code", code },
                    { "redirect_uri", browser.RedirectUri }, { "client_id", OAuthClientId }, { "code_verifier", browser.Verifier }, { "state", browser.State } }) },
                cancellation).ConfigureAwait(false);
            Dictionary<string, object> token = ParseObject(response.Body);
            double seconds = TokenLifetime(token, AccountErrorKind.InvalidResponse, "Claude 로그인 응답을 확인하지 못했습니다. 다시 연결해 주세요.");
            string access = Text(token, "access_token");
            Identity identity = await ReadIdentityAsync(access, cancellation).ConfigureAwait(false);
            // OAuth omits scope when it equals the requested scope.
            string scope = Text(token, "scope") ?? ClaudeLoopbackLogin.Scope;
            string raw = serializer.Serialize(new Dictionary<string, object> { { "claudeAiOauth", new Dictionary<string, object> {
                { "accessToken", access }, { "refreshToken", Text(token, "refresh_token") },
                { "expiresAt", (_now().AddSeconds(seconds) - Epoch).TotalMilliseconds },
                { "scopes", scope.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries) }, { "subscriptionType", identity.Plan } } } });
            Credentials credentials = ParseCredentials(raw);
            cancellation.ThrowIfCancellationRequested();
            AtomicWrite(CredentialPath, raw);
            SaveBinding(credentials, identity);
        }

        private double TokenLifetime(Dictionary<string, object> token, AccountErrorKind kind, string message)
        {
            string access = Text(token, "access_token"), type = Text(token, "token_type");
            double seconds = Number(JsonValue.Get(token, "expires_in"));
            if (String.IsNullOrWhiteSpace(access) || !Finite(seconds) || seconds <= 0 ||
                seconds > (DateTime.MaxValue - _now()).TotalSeconds ||
                (type != null && !String.Equals(type, "Bearer", StringComparison.OrdinalIgnoreCase)))
                throw Fault(kind, message);
            return seconds;
        }

        public async Task LogoutAsync()
        {
            if (_loginCancellation != null && _login != null && !_login.IsCompleted) _loginCancellation.Cancel();
            if (_login != null) { try { await _login.ConfigureAwait(false); } catch (AccountProviderException) { } }
            await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                // Only this slot's own sign-in is removed; Claude Code and other apps keep theirs.
                if (File.Exists(CredentialPath)) File.Delete(CredentialPath);
                if (File.Exists(BindingPath)) File.Delete(BindingPath);
                _terminalCredentials = null; _terminalError = null; _retryAt = default(DateTime);
            }
            catch (Exception error) { throw SafeError(error, _lifetime.IsCancellationRequested); }
            finally { _gate.Release(); }
        }

        private async Task<Credentials> RenewAsync(Credentials old, CancellationToken cancellation)
        {
            Dictionary<string, object> binding = ParseObject(ReadOptional(BindingPath) ?? "{}");
            Identity expected = new Identity { Account = Text(binding, "account"), Organization = Text(binding, "organization") };
            if (String.IsNullOrEmpty(old.Refresh) || Text(binding, "credentialHash") != Hash(old.Raw) ||
                String.IsNullOrEmpty(expected.Account) || String.IsNullOrEmpty(expected.Organization))
                throw Fault(AccountErrorKind.RenewalBlocked, "만료된 Claude 인증의 계정 연결을 확인할 수 없어 자동 갱신을 중단했습니다. 연결에서 다시 로그인해 주세요.");
            ClaudeResponse response = await RequestAsync(new ClaudeRequest { Url = TokenEndpoint, Method = "POST",
                Body = "grant_type=refresh_token&refresh_token=" + Uri.EscapeDataString(old.Refresh) + "&client_id=" + Uri.EscapeDataString(OAuthClientId) }, cancellation).ConfigureAwait(false);
            Dictionary<string, object> token = ParseObject(response.Body);
            double seconds = TokenLifetime(token, AccountErrorKind.RenewalBlocked, "Claude 인증 갱신 응답을 확인하지 못했습니다. 연결에서 다시 로그인해 주세요.");
            string access = Text(token, "access_token"), refresh = Text(token, "refresh_token");
            Identity actual = await ReadIdentityAsync(access, cancellation).ConfigureAwait(false);
            if (!expected.Same(actual))
                throw Fault(AccountErrorKind.IdentityMismatch, "Claude 인증 갱신 계정이 일치하지 않아 저장하지 않았습니다. 다시 연결해 주세요.");
            old.OAuth["accessToken"] = access;
            old.OAuth["refreshToken"] = String.IsNullOrEmpty(refresh) ? old.Refresh : refresh;
            old.OAuth["expiresAt"] = (_now().AddSeconds(seconds) - Epoch).TotalMilliseconds;
            // OAuth allows scope to be omitted when unchanged. If returned, honor scope reduction.
            string scope = Text(token, "scope");
            if (scope != null) old.OAuth["scopes"] = scope.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string updated = new JavaScriptSerializer().Serialize(old.Document);
            Credentials current = ParseCredentials(updated);
            cancellation.ThrowIfCancellationRequested();
            if (ReadOptional(CredentialPath) != old.Raw)
                throw Fault(AccountErrorKind.IdentityMismatch, "다른 로그인에서 인증을 변경하여 갱신 저장을 취소했습니다.");
            AtomicWrite(CredentialPath, updated);
            SaveBinding(current, actual);
            return current;
        }

        private void SaveBinding(Credentials credentials, Identity identity)
        {
            AtomicWrite(BindingPath, new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                { "credentialHash", Hash(credentials.Raw) }, { "account", identity.Account }, { "organization", identity.Organization },
                { "email", identity.Email ?? "" } }));
        }

        private Identity BoundIdentity(string raw)
        {
            Dictionary<string, object> binding = ParseObject(ReadOptional(BindingPath) ?? "{}");
            Identity identity = new Identity { Account = Text(binding, "account"), Organization = Text(binding, "organization"), Email = Text(binding, "email") };
            // Bindings from older versions carry no email and are re-read once.
            return Text(binding, "credentialHash") == Hash(raw) && !String.IsNullOrEmpty(identity.Account) &&
                !String.IsNullOrEmpty(identity.Organization) && identity.Email != null ? identity : null;
        }

        private static Credentials ParseCredentials(string raw)
        {
            if (raw == null) throw Fault(AccountErrorKind.LoginRequired, "이 칸의 Claude 인증이 없습니다. 다시 연결해 주세요.");
            Dictionary<string, object> document = ParseObject(raw);
            Dictionary<string, object> oauth = JsonValue.AsObject(JsonValue.Get(document, "claudeAiOauth"));
            string access = Text(oauth, "accessToken"), plan = Text(oauth, "subscriptionType");
            if (String.IsNullOrWhiteSpace(access) || !new[] { "pro", "max", "team", "enterprise" }.Contains(plan))
                throw Fault(AccountErrorKind.UnsupportedAuthentication, "Claude 구독 로그인만 지원합니다. API 키·Console·setup-token 대신 연결에서 구독 계정으로 로그인해 주세요.");
            if (!JsonValue.AsItems(JsonValue.Get(oauth, "scopes")).Any(value => value as string == "user:profile"))
                throw Fault(AccountErrorKind.ScopeDenied, "Claude 사용량 조회 권한(user:profile)이 없습니다. 연결에서 구독 계정으로 다시 로그인해 주세요.");
            double expiry = Number(JsonValue.Get(oauth, "expiresAt"));
            if (!Finite(expiry) || expiry <= 0 || expiry >= (DateTime.MaxValue - Epoch).TotalMilliseconds)
                throw Fault(AccountErrorKind.RenewalBlocked, "Claude 인증 만료 시간을 확인하지 못했습니다. 다시 연결해 주세요.");
            return new Credentials { Raw = raw, Document = document, OAuth = oauth, Access = access,
                Refresh = Text(oauth, "refreshToken"), Expires = Epoch.AddMilliseconds(expiry), Plan = plan };
        }

        private async Task<Identity> ReadIdentityAsync(string access, CancellationToken cancellation)
        {
            ClaudeResponse response = await RequestAsync(new ClaudeRequest { Url = ProfileEndpoint, AccessToken = access }, cancellation).ConfigureAwait(false);
            Dictionary<string, object> body = ParseObject(response.Body);
            Dictionary<string, object> account = JsonValue.AsObject(JsonValue.Get(body, "account"));
            Dictionary<string, object> organization = JsonValue.AsObject(JsonValue.Get(body, "organization"));
            Guid accountId, organizationId;
            if (!Guid.TryParse(Text(account, "uuid"), out accountId) || accountId == Guid.Empty ||
                !Guid.TryParse(Text(organization, "uuid"), out organizationId) || organizationId == Guid.Empty)
                throw Fault(AccountErrorKind.IdentityUnavailable, "Claude 계정·조직 식별을 확인하지 못해 사용량을 저장하지 않았습니다.");
            return new Identity { Account = accountId.ToString("D"), Organization = organizationId.ToString("D"),
                Email = Text(account, "email_address"), Plan = PlanOf(account, organization) };
        }

        // The token response carries no plan; Claude Code derives it from the profile the same way.
        internal static string PlanOf(Dictionary<string, object> account, Dictionary<string, object> organization)
        {
            switch (Text(organization, "organization_type"))
            {
                case "claude_max": return "max";
                case "claude_pro": return "pro";
                case "claude_team": return "team";
                case "claude_enterprise": return "enterprise";
            }
            if (Object.Equals(JsonValue.Get(account, "has_claude_max"), true)) return "max";
            if (Object.Equals(JsonValue.Get(account, "has_claude_pro"), true)) return "pro";
            return null;
        }

        internal static RateWindow ParseWindow(Dictionary<string, object> body, int minutes, string name)
        {
            double used = Number(JsonValue.Get(body, "utilization"));
            if (!Finite(used) || used < 0 || used > 100) return null;
            DateTimeOffset reset;
            string raw = Text(body, "resets_at");
            DateTime? resets = DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out reset) &&
                // A reset without a timezone is not a confirmed instant.
                raw != null && (raw.EndsWith("Z", StringComparison.OrdinalIgnoreCase) ||
                System.Text.RegularExpressions.Regex.IsMatch(raw, @"[+-]\d{2}:\d{2}$")) ? (DateTime?)reset.UtcDateTime : null;
            return new RateWindow { Name = name, UsedPercent = used, RemainingPercent = 100 - used, DurationMinutes = minutes, ResetsAt = resets };
        }

        private async Task<ClaudeResponse> RequestAsync(ClaudeRequest request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            ClaudeResponse response = await _transport(request, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (response.Status == 401 || (request.Url == TokenEndpoint && response.Status == 400))
                throw Fault(AccountErrorKind.LoginRequired, "Claude 인증이 만료되었거나 취소되었습니다. 연결에서 다시 로그인해 주세요.");
            if (response.Status == 403)
                throw Fault(AccountErrorKind.ScopeDenied, "Claude 사용량 조회 권한이 거부되었습니다. 구독 계정과 user:profile 권한을 확인하고 다시 연결해 주세요.");
            if (response.Status == 429)
            {
                double seconds;
                DateTimeOffset date;
                if (!Double.TryParse(response.RetryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) || !Finite(seconds))
                    seconds = DateTimeOffset.TryParse(response.RetryAfter, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date)
                        ? (date.UtcDateTime - _now()).TotalSeconds : 0;
                // Retry-After: 0 or none is not a usable wait here; back off 5, 10, 20... up to 60 minutes.
                if (seconds <= 0)
                    seconds = Math.Min(MaximumBackoff.TotalSeconds, FirstBackoff.TotalSeconds * Math.Pow(2, Math.Min(_rateLimitStreak, 10)));
                _rateLimitStreak++;
                _retryAt = _now().AddSeconds(Math.Max(1, Math.Min(86400, seconds)));
                throw Fault(AccountErrorKind.RateLimited, "Claude 요청이 많아 잠시 대기합니다. 잠시 후 자동으로 다시 확인합니다.");
            }
            if (response.Status != 200)
                throw Fault(AccountErrorKind.Network, "Claude 서비스에서 사용량을 확인하지 못했습니다. 잠시 후 다시 시도해 주세요.");
            return response;
        }

        internal static HttpWebRequest CreateRequest(ClaudeRequest input)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(input.Url);
            request.Method = input.Method; request.Accept = "application/json";
            request.AllowAutoRedirect = false; request.Timeout = 15000; request.ReadWriteTimeout = 15000;
            if (input.AccessToken != null)
            {
                request.Headers["Authorization"] = "Bearer " + input.AccessToken;
                request.Headers["anthropic-beta"] = "oauth-2025-04-20";
            }
            if (input.Body != null) request.ContentType = input.ContentType;
            return request;
        }

        internal static async Task<ClaudeResponse> SendAsync(ClaudeRequest input, CancellationToken cancellation)
        {
            HttpWebRequest request = CreateRequest(input);
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using (timeout.Token.Register(request.Abort))
                try
                {
                    if (input.Body != null)
                    {
                        byte[] data = Encoding.UTF8.GetBytes(input.Body);
                        request.ContentLength = data.Length;
                        using (Stream stream = await request.GetRequestStreamAsync().ConfigureAwait(false))
                            await stream.WriteAsync(data, 0, data.Length, timeout.Token).ConfigureAwait(false);
                    }
                    HttpWebResponse response;
                    try { response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false); }
                    catch (WebException error)
                    {
                        response = error.Response as HttpWebResponse;
                        if (response == null) throw;
                    }
                    using (response)
                    using (Stream stream = response.GetResponseStream())
                    using (MemoryStream body = new MemoryStream())
                    {
                        byte[] bytes = new byte[4096]; int count;
                        while ((count = await stream.ReadAsync(bytes, 0, bytes.Length, timeout.Token).ConfigureAwait(false)) > 0)
                        {
                            if (body.Length + count > MaximumBytes) throw Fault(AccountErrorKind.InvalidResponse, "Claude 응답 크기가 예상 범위를 벗어났습니다.");
                            body.Write(bytes, 0, count);
                        }
                        return new ClaudeResponse { Status = (int)response.StatusCode, RetryAfter = response.Headers["Retry-After"],
                            Body = Encoding.UTF8.GetString(body.ToArray()) };
                    }
                }
                catch (Exception)
                {
                    if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
                    if (timeout.IsCancellationRequested) throw new TimeoutException();
                    throw;
                }
            }
        }

        private static string ReadOptional(string path)
        {
            if (!File.Exists(path)) return null;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > MaximumBytes) throw Fault(AccountErrorKind.InvalidResponse, "Claude 인증 파일 크기가 예상 범위를 벗어났습니다.");
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
        }

        internal static void AtomicWrite(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(text);
                    stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static Dictionary<string, object> ParseObject(string text)
        {
            try
            {
                Dictionary<string, object> value = new JavaScriptSerializer { MaxJsonLength = MaximumBytes }.DeserializeObject(text) as Dictionary<string, object>;
                if (value != null) return value;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            throw Fault(AccountErrorKind.InvalidResponse, "Claude 응답 또는 인증 형식을 확인하지 못했습니다. 다시 연결해 주세요.");
        }
        private static string Text(Dictionary<string, object> value, string key) { return JsonValue.Get(value, key) as string; }
        private static double Number(object value)
        { return value is int || value is long || value is double || value is decimal ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : Double.NaN; }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static string Hash(string text)
        { using (SHA256 hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(text))); }
        private static AccountProviderException Fault(AccountErrorKind kind, string message) { return new AccountProviderException(kind, message); }
        private static AccountProviderException SafeError(Exception error, bool canceled)
        {
            if (error is AccountProviderException) return (AccountProviderException)error;
            if (canceled || error is OperationCanceledException) return Fault(AccountErrorKind.Canceled, "Claude 작업을 취소했습니다.");
            if (error is TimeoutException || (error is WebException && ((WebException)error).Status == WebExceptionStatus.Timeout))
                return Fault(AccountErrorKind.Timeout, "Claude 응답 시간이 초과되었습니다. 다음 갱신에서 다시 확인합니다.");
            if (error is IOException || error is UnauthorizedAccessException)
                return Fault(AccountErrorKind.Storage, "Claude 전용 인증을 안전하게 읽거나 저장하지 못했습니다. 다른 로그인 작업을 마치고 다시 연결해 주세요.");
            return Fault(AccountErrorKind.Network, "Claude 연결을 확인하지 못했습니다. 네트워크 상태를 확인해 주세요.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _lifetime.Cancel();
        }
    }
}
