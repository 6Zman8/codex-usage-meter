using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexUsageMeter
{
    internal sealed class ClaudeRequest
    {
        internal string Url, Method = "GET", AccessToken, Body;
    }

    internal sealed class ClaudeResponse
    {
        internal int Status;
        internal string Body, RetryAfter;
    }

    internal static class ClaudeCli
    {
        internal const string InstallMessage = "Claude CLI를 찾지 못했습니다. https://code.claude.com/docs/en/setup 에서 Windows 기본 설치를 진행한 뒤 다시 연결해 주세요. 미터기는 자동 설치하지 않습니다.";

        internal static string Find()
        {
            return FindFrom(Environment.GetEnvironmentVariable("PATH"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        internal static string FindFrom(string path, string home)
        {
            foreach (string directory in (path ?? "").Split(Path.PathSeparator).Concat(new[] { Path.Combine(home, ".local", "bin") }))
            {
                string value = directory.Trim().Trim('"');
                // Never resolve an empty PATH entry against the dashboard's current directory.
                if (!Path.IsPathRooted(value)) continue;
                string candidate = Path.Combine(value, "claude.exe");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            throw new AccountProviderException(AccountErrorKind.InstallNeeded, InstallMessage);
        }

        internal static ProcessStartInfo StartInfo(string executable, string profile, bool login)
        {
            ProcessStartInfo info = new ProcessStartInfo(executable, login ? "auth login" : "auth logout");
            info.UseShellExecute = false;
            info.CreateNoWindow = !login;
            info.WorkingDirectory = Path.GetFullPath(profile);
            // All auth and cloud-provider overrides are removed, not merely the common API key.
            foreach (string key in info.EnvironmentVariables.Keys.Cast<string>().ToArray())
                if (new[] { "ANTHROPIC_", "CLAUDE_", "AWS_", "GOOGLE_", "GCLOUD_", "CLOUDSDK_", "AZURE_", "BEDROCK_", "VERTEX_" }
                    .Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    info.EnvironmentVariables.Remove(key);
            info.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = info.WorkingDirectory;
            info.EnvironmentVariables["DISABLE_AUTOUPDATER"] = "1";
            return info;
        }

        internal static Task<int> RunAsync(ProcessStartInfo info, CancellationToken cancellation)
        {
            // No stdout/stderr is retained: auth output may contain secrets.
            return Task.Run(delegate {
                cancellation.ThrowIfCancellationRequested();
                using (Process process = new Process { StartInfo = info })
                {
                    process.Start();
                    using (cancellation.Register(delegate {
                        try { if (!process.HasExited) process.Kill(); }
                        catch (InvalidOperationException) { }
                    }))
                    {
                        process.WaitForExit();
                        cancellation.ThrowIfCancellationRequested();
                        return process.ExitCode;
                    }
                }
            }, cancellation);
        }
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
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private readonly Func<ClaudeRequest, CancellationToken, Task<ClaudeResponse>> _transport;
        private readonly Func<ProcessStartInfo, CancellationToken, Task<int>> _run;
        private readonly Func<string> _locate;
        private readonly Func<DateTime> _now;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private CancellationTokenSource _loginCancellation;
        private Task _login;
        private DateTime _retryAt;
        private string _terminalCredentials;
        private AccountProviderException _terminalError;
        private bool _disposed;

        public AccountProvider Provider { get { return AccountProviders.Claude; } }
        public string ProfileRoot { get; private set; }
        public event EventHandler AccountChanged;

        internal ClaudeUsageAccount(string profileRoot) : this(profileRoot, SendAsync, ClaudeCli.RunAsync, ClaudeCli.Find, () => DateTime.UtcNow) { }
        internal ClaudeUsageAccount(string profileRoot, Func<ClaudeRequest, CancellationToken, Task<ClaudeResponse>> transport,
            Func<ProcessStartInfo, CancellationToken, Task<int>> run, Func<string> locate, Func<DateTime> now)
        { ProfileRoot = Path.GetFullPath(profileRoot); _transport = transport; _run = run; _locate = locate; _now = now; }

        private sealed class Credentials
        {
            internal string Raw, Access, Refresh, Plan;
            internal DateTime Expires;
            internal Dictionary<string, object> Document, OAuth;
        }

        private sealed class Identity
        {
            internal string Account, Organization, Email;
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
                    throw Fault(AccountErrorKind.LoginRequired, "Claude 로그인 창에서 인증을 마쳐 주세요. 다른 계정은 계속 갱신됩니다.");
                raw = ReadOptional(CredentialPath);
                if (raw == null)
                {
                    _locate(); // Distinguish an unconnected slot from a missing native CLI.
                    throw Fault(AccountErrorKind.LoginRequired, "연결에서 Claude 구독 계정으로 로그인해 주세요.");
                }
                if (raw == _terminalCredentials && _terminalError != null) throw _terminalError;
                if (_now() < _retryAt) throw Fault(AccountErrorKind.RateLimited, "Claude 요청이 많아 잠시 대기합니다. " + _retryAt.ToLocalTime().ToString("HH:mm") + " 이후 자동으로 다시 확인합니다.");
                Credentials credentials = ParseCredentials(raw);
                snapshot.IsAuthenticated = true;
                if (credentials.Expires <= _now().AddMinutes(1))
                    credentials = await RenewAsync(credentials, _lifetime.Token).ConfigureAwait(false);
                raw = credentials.Raw;
                Identity before = await ReadIdentityAsync(credentials.Access, _lifetime.Token).ConfigureAwait(false);
                ClaudeResponse usage = await RequestAsync(new ClaudeRequest { Url = UsageEndpoint, AccessToken = credentials.Access }, _lifetime.Token).ConfigureAwait(false);
                DateTime observed = _now();
                Identity after = await ReadIdentityAsync(credentials.Access, _lifetime.Token).ConfigureAwait(false);
                if (!before.Same(after) || ReadOptional(CredentialPath) != credentials.Raw)
                    throw Fault(AccountErrorKind.IdentityMismatch, "조회 중 계정이 바뀌어 이번 사용량을 저장하지 않았습니다. 다시 연결 상태를 확인해 주세요.");
                Dictionary<string, object> body = ParseObject(usage.Body);
                snapshot.Primary = ParseWindow(JsonValue.AsObject(JsonValue.Get(body, "five_hour")), 300, "5시간 한도");
                snapshot.Secondary = ParseWindow(JsonValue.AsObject(JsonValue.Get(body, "seven_day")), 10080, "주간 한도");
                SaveBinding(credentials, after);
                snapshot.Email = after.Email;
                snapshot.PlanType = credentials.Plan;
                snapshot.RemoteAccountId = after.Account;
                snapshot.RemoteOrganizationId = after.Organization;
                snapshot.IdentityVerified = true;
                snapshot.HistoryKey = UsageHistoryStore.ProviderAccountKey(Provider.Id, after.Account, after.Organization);
                snapshot.RateLimitsObservedAtUtc = observed;
                if (snapshot.Primary == null && snapshot.Secondary == null)
                    snapshot.UsageError = "Claude가 유효한 단기·주간 한도를 제공하지 않았습니다.";
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
                string executable = _locate();
                Directory.CreateDirectory(ProfileRoot);
                _loginCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _loginCancellation.CancelAfter(TimeSpan.FromMinutes(10));
                _terminalCredentials = null; _terminalError = null; _retryAt = default(DateTime);
                _login = CompleteLoginAsync(_run(ClaudeCli.StartInfo(executable, ProfileRoot, true), _loginCancellation.Token));
                return LoginPrompt();
            }
            catch (Exception error) { throw SafeError(error, _lifetime.IsCancellationRequested); }
            finally { _gate.Release(); }
        }

        private AccountLoginPrompt LoginPrompt()
        {
            CancellationTokenSource cancellation = _loginCancellation;
            return new AccountLoginPrompt { Completion = _login, Cancel = () => cancellation.Cancel(),
                Message = "Claude CLI 로그인 창에서 구독 계정 인증을 마쳐 주세요. 인증 후에는 브라우저를 닫아도 사용량과 초기화 시간이 자동 갱신됩니다.\n\nAPI 키·Console 계정·setup-token은 지원하지 않습니다." };
        }

        private async Task CompleteLoginAsync(Task<int> process)
        {
            try
            {
                if (await process.ConfigureAwait(false) != 0)
                    throw Fault(AccountErrorKind.LoginRequired, "Claude 로그인을 마치지 못했습니다. CLI 창의 안내를 확인하고 다시 연결해 주세요.");
                ParseCredentials(ReadOptional(CredentialPath));
            }
            catch (Exception error) { throw SafeError(error, _loginCancellation.IsCancellationRequested); }
            finally
            {
                EventHandler changed = AccountChanged;
                if (!_disposed && changed != null) changed(this, EventArgs.Empty);
            }
        }

        public async Task LogoutAsync()
        {
            if (_loginCancellation != null && _login != null && !_login.IsCompleted) _loginCancellation.Cancel();
            if (_login != null) { try { await _login.ConfigureAwait(false); } catch (AccountProviderException) { } }
            await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                string executable = _locate();
                Directory.CreateDirectory(ProfileRoot);
                using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    if (await _run(ClaudeCli.StartInfo(executable, ProfileRoot, false), timeout.Token).ConfigureAwait(false) != 0)
                        throw Fault(AccountErrorKind.LoginRequired, "Claude 연결 해제를 마치지 못했습니다. 다시 시도해 주세요.");
                }
                // The unmodified CLI owns logout. Never delete a credential file on its behalf.
                _terminalCredentials = null; _terminalError = null; _retryAt = default(DateTime);
                if (File.Exists(BindingPath)) File.Delete(BindingPath);
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
            string access = Text(token, "access_token"), refresh = Text(token, "refresh_token"), type = Text(token, "token_type");
            double seconds = Number(JsonValue.Get(token, "expires_in"));
            if (String.IsNullOrWhiteSpace(access) || !Finite(seconds) || seconds <= 0 ||
                seconds > (DateTime.MaxValue - _now()).TotalSeconds ||
                (type != null && !String.Equals(type, "Bearer", StringComparison.OrdinalIgnoreCase)))
                throw Fault(AccountErrorKind.RenewalBlocked, "Claude 인증 갱신 응답을 확인하지 못했습니다. 연결에서 다시 로그인해 주세요.");
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
                { "credentialHash", Hash(credentials.Raw) }, { "account", identity.Account }, { "organization", identity.Organization } }));
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
                Email = Text(account, "email_address") };
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
                        ? (date.UtcDateTime - _now()).TotalSeconds : 60;
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
            if (input.Body != null) request.ContentType = "application/x-www-form-urlencoded";
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
            return Fault(AccountErrorKind.Network, "Claude 연결을 확인하지 못했습니다. 네트워크와 CLI 설치 상태를 확인해 주세요.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _lifetime.Cancel();
        }
    }
}
