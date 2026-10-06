using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexUsageMeter
{
    internal sealed class RateWindow
    {
        public string Name { get; set; }
        public double RemainingPercent { get; set; }
        public double UsedPercent { get; set; }
        public int DurationMinutes { get; set; }
        public DateTime? ResetsAt { get; set; }
    }

    internal sealed class AccountSnapshot
    {
        public bool IsAuthenticated { get; set; }
        public string Email { get; set; }
        public string PlanType { get; set; }
        public RateWindow Primary { get; set; }
        public RateWindow Secondary { get; set; }
        public int? ResetCreditCount { get; set; }
        public List<ResetCreditInfo> ResetCredits { get; set; }
        public AccountSubscriptionInfo Subscription { get; set; }
        public UsageSummary Usage { get; set; }
        public List<DailyUsageBucket> DailyUsage { get; set; }
        public string Error { get; set; }
        public string UsageError { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    internal sealed class ResetCreditInfo
    {
        public string Status { get; set; }
        public string Title { get; set; }
        public DateTime? GrantedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
    }

    internal sealed class UsageSummary
    {
        public long? LifetimeTokens { get; set; }
        public long? PeakDailyTokens { get; set; }
        public long? LongestRunningTurnSeconds { get; set; }
        public long? CurrentStreakDays { get; set; }
        public long? LongestStreakDays { get; set; }
    }

    internal sealed class DailyUsageBucket
    {
        public DateTime Date { get; set; }
        public long Tokens { get; set; }
    }

    internal sealed class DeviceLoginInfo
    {
        public string LoginId { get; set; }
        public string VerificationUrl { get; set; }
        public string UserCode { get; set; }
    }

    internal sealed class RpcException : Exception
    {
        public RpcException(string message) : base(message) { }
    }

    internal sealed class CodexRpcClient : IDisposable
    {
        private readonly Func<string> _codexPathResolver;
        private readonly string _profileRoot;
        private readonly AccountSubscriptionReader _subscriptionReader;
        private readonly JavaScriptSerializer _json;
        private readonly Dictionary<int, TaskCompletionSource<Dictionary<string, object>>> _pending;
        private readonly object _pendingLock;
        private readonly object _writeLock;
        private readonly SemaphoreSlim _startGate;
        private readonly Queue<string> _stderrLines;
        private Process _process;
        private Task _readerTask;
        private Task _stderrTask;
        private int _nextId;
        private bool _initialized;
        private bool _disposed;

        public event EventHandler AccountChanged;

        public CodexRpcClient(string codexPath, string profileRoot)
            : this(delegate { return codexPath; }, profileRoot)
        {
        }

        public CodexRpcClient(Func<string> codexPathResolver, string profileRoot)
        {
            if (codexPathResolver == null) throw new ArgumentNullException("codexPathResolver");
            _codexPathResolver = codexPathResolver;
            _profileRoot = profileRoot;
            _subscriptionReader = new AccountSubscriptionReader(profileRoot);
            _json = new JavaScriptSerializer();
            _pending = new Dictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
            _pendingLock = new object();
            _writeLock = new object();
            _startGate = new SemaphoreSlim(1, 1);
            _stderrLines = new Queue<string>();
        }

        public async Task ProbeAsync()
        {
            await EnsureStartedAsync();
        }

        public async Task PrepareForSwitchAsync()
        {
            try
            {
                Dictionary<string, object> result = await ReadWithReconnectAsync("account/read",
                    new Dictionary<string, object> { { "refreshToken", false } });
                Dictionary<string, object> account = JsonValue.AsObject(JsonValue.Get(result, "account"));
                if (account == null || JsonValue.AsString(JsonValue.Get(account, "type")) != "chatgpt")
                    throw new InvalidOperationException("ChatGPT 계정 인증이 필요합니다.");
                // This checks authentication, not remaining quota, and never creates a model turn.
                await ReadWithReconnectAsync("account/rateLimits/read", new Dictionary<string, object>());
            }
            catch
            {
                // RPC errors can include server details. Do not expose authentication data.
                throw new InvalidOperationException(
                    "선택 계정의 인증을 갱신·확인하지 못해 Codex를 종료하지 않았습니다. 연결 상태를 확인해 주세요.");
            }
        }

        public async Task<AccountSnapshot> RefreshAsync()
        {
            AccountSnapshot snapshot = new AccountSnapshot();
            snapshot.UpdatedAt = DateTime.Now;
            snapshot.ResetCredits = new List<ResetCreditInfo>();
            snapshot.DailyUsage = new List<DailyUsageBucket>();

            try
            {
                Dictionary<string, object> accountResult = await ReadWithReconnectAsync(
                    "account/read",
                    new Dictionary<string, object> { { "refreshToken", false } });

                Dictionary<string, object> account = JsonValue.AsObject(JsonValue.Get(accountResult, "account"));
                if (account == null)
                {
                    snapshot.IsAuthenticated = false;
                    return snapshot;
                }

                snapshot.IsAuthenticated = true;
                snapshot.Email = JsonValue.AsString(JsonValue.Get(account, "email"));
                snapshot.PlanType = JsonValue.AsString(JsonValue.Get(account, "planType"));

                try
                {
                    Dictionary<string, object> limitResult = await ReadWithReconnectAsync(
                        "account/rateLimits/read",
                        new Dictionary<string, object>());
                    ParseRateLimits(limitResult, snapshot);
                }
                catch (Exception ex)
                {
                    snapshot.Error = FriendlyError(ex);
                }

                Task<AccountSubscriptionInfo> subscription = _subscriptionReader.ReadAsync(snapshot.PlanType, snapshot.Email);
                try
                {
                    Dictionary<string, object> usageResult = await ReadWithReconnectAsync(
                        "account/usage/read",
                        new Dictionary<string, object>());
                    ParseUsage(usageResult, snapshot);
                }
                catch (Exception ex)
                {
                    snapshot.UsageError = FriendlyError(ex);
                }
                snapshot.Subscription = await subscription;
            }
            catch (Exception ex)
            {
                snapshot.Error = FriendlyError(ex);
            }

            return snapshot;
        }

        public async Task<DeviceLoginInfo> StartDeviceLoginAsync()
        {
            await EnsureStartedAsync();
            Dictionary<string, object> result = await RequestRawAsync(
                "account/login/start",
                new Dictionary<string, object> { { "type", "chatgptDeviceCode" } });

            DeviceLoginInfo info = new DeviceLoginInfo();
            info.LoginId = JsonValue.AsString(JsonValue.Get(result, "loginId"));
            info.VerificationUrl = JsonValue.AsString(JsonValue.Get(result, "verificationUrl"));
            info.UserCode = JsonValue.AsString(JsonValue.Get(result, "userCode"));

            if (String.IsNullOrWhiteSpace(info.VerificationUrl) || String.IsNullOrWhiteSpace(info.UserCode))
            {
                throw new InvalidOperationException("기기 로그인 주소나 코드를 받지 못했습니다.");
            }

            return info;
        }

        public async Task LogoutAsync()
        {
            await EnsureStartedAsync();
            await RequestRawAsync("account/logout", new Dictionary<string, object>());
        }

        private async Task EnsureStartedAsync()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException("CodexRpcClient");
            }

            if (_initialized && _process != null && !_process.HasExited)
            {
                return;
            }

            await _startGate.WaitAsync();
            try
            {
                if (_initialized && _process != null && !_process.HasExited)
                {
                    return;
                }

                StopProcess();
                Directory.CreateDirectory(_profileRoot);
                string codexPath = _codexPathResolver();
                if (String.IsNullOrWhiteSpace(codexPath) || !File.Exists(codexPath))
                {
                    throw new FileNotFoundException("현재 설치된 codex.exe를 찾을 수 없습니다.", codexPath);
                }

                ProcessStartInfo start = new ProcessStartInfo();
                start.FileName = codexPath;
                start.Arguments = "app-server --stdio";
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.RedirectStandardInput = true;
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.WorkingDirectory = _profileRoot;
                start.EnvironmentVariables["CODEX_HOME"] = _profileRoot;

                _process = new Process();
                _process.StartInfo = start;
                _process.EnableRaisingEvents = true;
                if (!_process.Start())
                {
                    throw new InvalidOperationException("Codex app-server를 시작하지 못했습니다.");
                }
                _process.StandardInput.AutoFlush = true;
                Process startedProcess = _process;
                _readerTask = Task.Run(delegate { return ReadLoopAsync(startedProcess); });
                _stderrTask = Task.Run(delegate { return ReadStderrLoopAsync(startedProcess); });

                Dictionary<string, object> clientInfo = new Dictionary<string, object>();
                clientInfo["name"] = "codex_usage_meter";
                clientInfo["title"] = "Codex Usage Meter";
                clientInfo["version"] = "1.0.0";
                Dictionary<string, object> parameters = new Dictionary<string, object>();
                parameters["clientInfo"] = clientInfo;

                await RequestRawAsync("initialize", parameters);
                SendNotification("initialized", new Dictionary<string, object>());
                _initialized = true;
            }
            catch
            {
                StopProcess();
                throw;
            }
            finally
            {
                _startGate.Release();
            }
        }

        private async Task<Dictionary<string, object>> RequestRawAsync(string method, Dictionary<string, object> parameters)
        {
            if (_process == null || _process.HasExited)
            {
                throw new IOException("Codex app-server가 실행 중이 아닙니다.");
            }

            int id = Interlocked.Increment(ref _nextId);
            TaskCompletionSource<Dictionary<string, object>> completion =
                new TaskCompletionSource<Dictionary<string, object>>();
            lock (_pendingLock)
            {
                _pending[id] = completion;
            }

            Dictionary<string, object> message = new Dictionary<string, object>();
            message["method"] = method;
            message["id"] = id;
            if (parameters != null)
            {
                message["params"] = parameters;
            }

            try
            {
                WriteMessage(message);
            }
            catch
            {
                lock (_pendingLock)
                {
                    _pending.Remove(id);
                }
                throw;
            }

            Task winner = await Task.WhenAny(completion.Task, Task.Delay(15000));
            if (winner != completion.Task)
            {
                lock (_pendingLock)
                {
                    _pending.Remove(id);
                }
                throw new TimeoutException(method + " 응답 시간이 초과되었습니다.");
            }

            Dictionary<string, object> response = await completion.Task;
            Dictionary<string, object> error = JsonValue.AsObject(JsonValue.Get(response, "error"));
            if (error != null)
            {
                string messageText = JsonValue.AsString(JsonValue.Get(error, "message"));
                throw new RpcException(String.IsNullOrWhiteSpace(messageText) ? "Codex 요청이 실패했습니다." : messageText);
            }

            Dictionary<string, object> result = JsonValue.AsObject(JsonValue.Get(response, "result"));
            return result ?? new Dictionary<string, object>();
        }

        private Task<Dictionary<string, object>> ReadWithReconnectAsync(string method, Dictionary<string, object> parameters)
        {
            return RetryReadAsync(async delegate {
                await EnsureStartedAsync();
                return await RequestRawAsync(method, parameters);
            }, delegate { StopProcess(); });
        }

        internal static async Task<T> RetryReadAsync<T>(Func<Task<T>> read, Action reconnect)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return await read(); }
                catch (Exception ex)
                {
                    // Only idempotent reads use this path. Never repeat login/logout or
                    // force token rotation after an ambiguous/lost response.
                    System.ComponentModel.Win32Exception startError = ex as System.ComponentModel.Win32Exception;
                    bool transientStart = startError != null && (startError.NativeErrorCode == 2 || startError.NativeErrorCode == 3);
                    if (attempt >= 1 || !(ex is IOException || ex is TimeoutException || transientStart)) throw;
                    reconnect();
                }
                await Task.Delay(350);
            }
        }

        private void SendNotification(string method, Dictionary<string, object> parameters)
        {
            Dictionary<string, object> message = new Dictionary<string, object>();
            message["method"] = method;
            message["params"] = parameters;
            WriteMessage(message);
        }

        private void WriteMessage(Dictionary<string, object> message)
        {
            string serialized = _json.Serialize(message);
            lock (_writeLock)
            {
                if (_process == null || _process.HasExited)
                {
                    throw new IOException("Codex app-server 연결이 종료되었습니다.");
                }
                _process.StandardInput.WriteLine(serialized);
            }
        }

        private async Task ReadLoopAsync(Process process)
        {
            try
            {
                while (!process.HasExited)
                {
                    string line = await process.StandardOutput.ReadLineAsync();
                    if (line == null)
                    {
                        break;
                    }

                    Dictionary<string, object> message;
                    try
                    {
                        message = _json.DeserializeObject(line) as Dictionary<string, object>;
                    }
                    catch
                    {
                        continue;
                    }

                    if (message == null)
                    {
                        continue;
                    }

                    object rawId = JsonValue.Get(message, "id");
                    if (rawId != null)
                    {
                        int id;
                        try
                        {
                            id = Convert.ToInt32(rawId);
                        }
                        catch
                        {
                            continue;
                        }

                        TaskCompletionSource<Dictionary<string, object>> completion = null;
                        lock (_pendingLock)
                        {
                            if (_pending.TryGetValue(id, out completion))
                            {
                                _pending.Remove(id);
                            }
                        }
                        if (completion != null)
                        {
                            completion.TrySetResult(message);
                        }
                        continue;
                    }

                    string method = JsonValue.AsString(JsonValue.Get(message, "method"));
                    if (method == "account/login/completed" ||
                        method == "account/updated" ||
                        method == "account/rateLimits/updated")
                    {
                        EventHandler handler = AccountChanged;
                        if (handler != null)
                        {
                            handler(this, EventArgs.Empty);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_disposed && Object.ReferenceEquals(_process, process))
                {
                    FailPending(ex);
                }
            }
            finally
            {
                if (Object.ReferenceEquals(_process, process))
                {
                    _initialized = false;
                }
                if (!_disposed && Object.ReferenceEquals(_process, process))
                {
                    FailPending(new IOException("Codex app-server 연결이 종료되었습니다."));
                }
            }
        }

        private async Task ReadStderrLoopAsync(Process process)
        {
            try
            {
                while (!process.HasExited)
                {
                    string line = await process.StandardError.ReadLineAsync();
                    if (line == null)
                    {
                        break;
                    }
                    lock (_stderrLines)
                    {
                        _stderrLines.Enqueue(line);
                        while (_stderrLines.Count > 12)
                        {
                            _stderrLines.Dequeue();
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private void ParseRateLimits(Dictionary<string, object> result, AccountSnapshot snapshot)
        {
            ParseResetCredits(JsonValue.AsObject(JsonValue.Get(result, "rateLimitResetCredits")), snapshot);
            Dictionary<string, object> allBuckets = JsonValue.AsObject(JsonValue.Get(result, "rateLimitsByLimitId"));
            Dictionary<string, object> bucket = JsonValue.AsObject(JsonValue.Get(allBuckets, "codex"));
            if (bucket == null)
            {
                bucket = JsonValue.AsObject(JsonValue.Get(result, "rateLimits"));
                string limitId = JsonValue.AsString(JsonValue.Get(bucket, "limitId"));
                if (!String.IsNullOrEmpty(limitId) && limitId != "codex") bucket = null;
            }

            if (bucket == null)
            {
                snapshot.Error = "사용량 한도 정보가 비어 있습니다.";
                return;
            }

            // account/read may still reflect the plan cached at login time.
            string currentPlan = JsonValue.AsString(JsonValue.Get(bucket, "planType"));
            if (!String.IsNullOrWhiteSpace(currentPlan))
            {
                snapshot.PlanType = currentPlan;
            }
            snapshot.Primary = ParseWindow(JsonValue.AsObject(JsonValue.Get(bucket, "primary")), "단기 한도");
            snapshot.Secondary = ParseWindow(JsonValue.AsObject(JsonValue.Get(bucket, "secondary")), "장기 한도");
            // These snapshot slots represent the UI's short/long windows, not wire order.
            // Pro Lite currently returns its only (weekly) window as primary.
            if (snapshot.Primary != null && snapshot.Primary.DurationMinutes >= 6 * 24 * 60 &&
                (snapshot.Secondary == null || (snapshot.Secondary.DurationMinutes > 0 &&
                    snapshot.Secondary.DurationMinutes < snapshot.Primary.DurationMinutes)))
            {
                RateWindow weekly = snapshot.Primary;
                snapshot.Primary = snapshot.Secondary;
                snapshot.Secondary = weekly;
            }
            if (snapshot.Primary == null && snapshot.Secondary == null)
                snapshot.Error = "사용량 한도 정보가 비어 있습니다.";
        }

        private static void ParseResetCredits(Dictionary<string, object> source, AccountSnapshot snapshot)
        {
            if (source == null)
            {
                snapshot.ResetCreditCount = null;
                return;
            }

            long? available = JsonValue.AsNullableInt64(JsonValue.Get(source, "availableCount"));
            snapshot.ResetCreditCount = available.HasValue ? (int?)Math.Max(0, Math.Min(Int32.MaxValue, available.Value)) : 0;
            foreach (object rawCredit in JsonValue.AsItems(JsonValue.Get(source, "credits")))
            {
                Dictionary<string, object> credit = JsonValue.AsObject(rawCredit);
                if (credit == null)
                {
                    continue;
                }
                ResetCreditInfo parsed = new ResetCreditInfo();
                parsed.Status = JsonValue.AsString(JsonValue.Get(credit, "status"));
                parsed.Title = JsonValue.AsString(JsonValue.Get(credit, "title"));
                parsed.GrantedAt = ParseUnixTime(JsonValue.Get(credit, "grantedAt"));
                parsed.ExpiresAt = ParseUnixTime(JsonValue.Get(credit, "expiresAt"));
                snapshot.ResetCredits.Add(parsed);
            }
        }

        private static void ParseUsage(Dictionary<string, object> result, AccountSnapshot snapshot)
        {
            Dictionary<string, object> summary = JsonValue.AsObject(JsonValue.Get(result, "summary"));
            if (summary != null)
            {
                UsageSummary parsed = new UsageSummary();
                parsed.LifetimeTokens = JsonValue.AsNullableInt64(JsonValue.Get(summary, "lifetimeTokens"));
                parsed.PeakDailyTokens = JsonValue.AsNullableInt64(JsonValue.Get(summary, "peakDailyTokens"));
                parsed.LongestRunningTurnSeconds = JsonValue.AsNullableInt64(JsonValue.Get(summary, "longestRunningTurnSec"));
                parsed.CurrentStreakDays = JsonValue.AsNullableInt64(JsonValue.Get(summary, "currentStreakDays"));
                parsed.LongestStreakDays = JsonValue.AsNullableInt64(JsonValue.Get(summary, "longestStreakDays"));
                snapshot.Usage = parsed;
            }

            foreach (object rawBucket in JsonValue.AsItems(JsonValue.Get(result, "dailyUsageBuckets")))
            {
                Dictionary<string, object> bucket = JsonValue.AsObject(rawBucket);
                if (bucket == null)
                {
                    continue;
                }
                DateTime date;
                long? tokens = JsonValue.AsNullableInt64(JsonValue.Get(bucket, "tokens"));
                if (DateTime.TryParse(JsonValue.AsString(JsonValue.Get(bucket, "startDate")), out date))
                {
                    DailyUsageBucket parsed = new DailyUsageBucket();
                    parsed.Date = date.Date;
                    parsed.Tokens = Math.Max(0L, tokens ?? 0L);
                    snapshot.DailyUsage.Add(parsed);
                }
            }
            snapshot.DailyUsage = snapshot.DailyUsage.OrderBy(delegate(DailyUsageBucket bucket) { return bucket.Date; }).ToList();
        }

        private static RateWindow ParseWindow(Dictionary<string, object> source, string fallbackName)
        {
            if (source == null)
            {
                return null;
            }

            double used = JsonValue.AsDouble(JsonValue.Get(source, "usedPercent"), Double.NaN);
            if (Double.IsNaN(used) || Double.IsInfinity(used)) return null;
            used = Math.Max(0.0, Math.Min(100.0, used));
            int minutes = (int)JsonValue.AsDouble(JsonValue.Get(source, "windowDurationMins"), 0.0);
            DateTime? resetsAt = ParseUnixTime(JsonValue.Get(source, "resetsAt"));

            RateWindow window = new RateWindow();
            window.DurationMinutes = minutes;
            window.UsedPercent = used;
            window.RemainingPercent = 100.0 - used;
            window.Name = WindowName(minutes, fallbackName);
            window.ResetsAt = resetsAt;
            return window;
        }

        private static DateTime? ParseUnixTime(object value)
        {
            double unixSeconds = JsonValue.AsDouble(value, 0.0);
            if (unixSeconds <= 0.0)
            {
                return null;
            }
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return epoch.AddSeconds(unixSeconds).ToLocalTime();
        }

        private static string WindowName(int minutes, string fallbackName)
        {
            if (minutes >= 6 * 24 * 60)
            {
                return "주간 한도";
            }
            if (minutes > 0 && minutes % 60 == 0)
            {
                return (minutes / 60).ToString() + "시간 한도";
            }
            if (minutes > 0)
            {
                return minutes.ToString() + "분 한도";
            }
            return fallbackName;
        }

        private void FailPending(Exception error)
        {
            List<TaskCompletionSource<Dictionary<string, object>>> waiting;
            lock (_pendingLock)
            {
                waiting = _pending.Values.ToList();
                _pending.Clear();
            }
            foreach (TaskCompletionSource<Dictionary<string, object>> completion in waiting)
            {
                completion.TrySetException(error);
            }
        }

        private void StopProcess()
        {
            _initialized = false;
            Process process = _process;
            _process = null;
            if (process == null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    try { process.StandardInput.Close(); } catch { }
                    if (!process.WaitForExit(750))
                    {
                        process.Kill();
                        process.WaitForExit(2000);
                    }
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        private static string FriendlyError(Exception error)
        {
            if (error == null)
            {
                return null;
            }
            string message = error.Message;
            if (message != null && message.IndexOf("authentication required", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "계정 연결이 필요합니다.";
            }
            return String.IsNullOrWhiteSpace(message) ? "알 수 없는 오류" : message;
        }

        public void Suspend()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException("CodexRpcClient");
            }

            _startGate.Wait();
            try
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException("CodexRpcClient");
                }
                FailPending(new IOException("계정 전환을 위해 미터기 연결을 잠시 멈췄습니다."));
                StopProcess();
            }
            finally
            {
                _startGate.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            FailPending(new ObjectDisposedException("CodexRpcClient"));
            StopProcess();
            _startGate.Dispose();
        }
    }

    internal static class JsonValue
    {
        public static object Get(Dictionary<string, object> source, string key)
        {
            if (source == null)
            {
                return null;
            }
            object value;
            return source.TryGetValue(key, out value) ? value : null;
        }

        public static Dictionary<string, object> AsObject(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static string AsString(object value)
        {
            return value == null ? null : Convert.ToString(value);
        }

        public static double AsDouble(object value, double fallback)
        {
            if (value == null)
            {
                return fallback;
            }
            try
            {
                return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        public static long? AsNullableInt64(object value)
        {
            if (value == null)
            {
                return null;
            }
            try
            {
                return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        public static IEnumerable<object> AsItems(object value)
        {
            if (value == null || value is string)
            {
                yield break;
            }
            IEnumerable items = value as IEnumerable;
            if (items == null)
            {
                yield break;
            }
            foreach (object item in items)
            {
                yield return item;
            }
        }
    }

    internal static class CodexLocator
    {
        public static string Find()
        {
            string pathValue = Environment.GetEnvironmentVariable("PATH") ?? String.Empty;
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string bundledRoot = Path.Combine(localData, "OpenAI", "Codex", "bin");
            return FindFrom(pathValue, bundledRoot);
        }

        internal static string FindFrom(string pathValue, string bundledRoot)
        {
            List<string> candidates = new List<string>();
            if (Directory.Exists(bundledRoot))
            {
                try
                {
                    IEnumerable<string> bundled = Directory.GetDirectories(bundledRoot)
                        .Select(delegate(string directory) { return Path.Combine(directory, "codex.exe"); })
                        .Where(File.Exists)
                        .OrderByDescending(File.GetLastWriteTimeUtc);
                    candidates.AddRange(bundled);
                }
                catch
                {
                }
            }

            pathValue = pathValue ?? String.Empty;
            foreach (string rawPart in pathValue.Split(Path.PathSeparator))
            {
                string part = rawPart.Trim().Trim('"');
                if (!String.IsNullOrWhiteSpace(part))
                {
                    candidates.Add(Path.Combine(part, "codex.exe"));
                }
            }

            foreach (string candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch
                {
                }
            }

            throw new FileNotFoundException("codex.exe를 찾을 수 없습니다. Codex 데스크톱 앱 또는 CLI가 필요합니다.");
        }
    }
}
