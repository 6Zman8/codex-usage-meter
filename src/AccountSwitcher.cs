using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace CodexUsageMeter
{
    internal interface ICodexDesktopLifecycle
    {
        bool TryStop(TimeSpan timeout, out string error);
        bool TryStart(TimeSpan timeout, out string error);
    }

    internal interface ICodexDesktopProcess : IDisposable
    {
        int Id { get; }
        bool IsAppServer { get; }
        bool HasMainWindow { get; }
        bool HasExited { get; }
        bool TryCloseMainWindow();
        bool TryTerminate();
    }

    internal interface ICodexDesktopProcessSource
    {
        ICodexDesktopProcess[] FindCodexProcesses();
    }

    internal interface ICodexDesktopStarter
    {
        bool TryLaunch(out string error);
    }

    internal interface IDesktopLifecycleClock
    {
        long Milliseconds { get; }
        void Delay(int milliseconds);
    }

    internal sealed class DesktopLifecycleClock : IDesktopLifecycleClock
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        public long Milliseconds { get { return _watch.ElapsedMilliseconds; } }
        public void Delay(int milliseconds) { Thread.Sleep(milliseconds); }
    }

    internal interface IAccountSwitchJournal
    {
        void Write(string stage, string detail);
    }

    internal sealed class NullAccountSwitchJournal : IAccountSwitchJournal
    {
        public static readonly NullAccountSwitchJournal Instance = new NullAccountSwitchJournal();
        private NullAccountSwitchJournal() { }
        public void Write(string stage, string detail) { }
    }

    internal sealed class FileAccountSwitchJournal : IAccountSwitchJournal
    {
        private readonly string _path;
        private readonly object _sync = new object();

        public FileAccountSwitchJournal()
            : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexUsageMeter", "account-switch.log"))
        {
        }

        internal FileAccountSwitchJournal(string path)
        {
            _path = Path.GetFullPath(path);
        }

        public void Write(string stage, string detail)
        {
            try
            {
                string directory = Path.GetDirectoryName(_path);
                if (!String.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                string line = DateTimeOffset.Now.ToString("o") + " " + (stage ?? "unknown") + " " +
                    (detail ?? String.Empty) + Environment.NewLine;
                lock (_sync)
                {
                    File.AppendAllText(_path, line, new UTF8Encoding(false));
                }
            }
            catch
            {
            }
        }
    }

    internal sealed class AccountSwitchResult
    {
        public bool Success { get; private set; }
        public bool RolledBack { get; private set; }
        public bool AlreadyActive { get; private set; }
        public string Message { get; private set; }

        public static AccountSwitchResult Completed(bool alreadyActive, string message)
        {
            return new AccountSwitchResult { Success = true, AlreadyActive = alreadyActive, Message = message };
        }

        public static AccountSwitchResult Failed(bool rolledBack, string message)
        {
            return new AccountSwitchResult { Success = false, RolledBack = rolledBack, Message = message };
        }
    }

    internal sealed class AccountSwitcher
    {
        private readonly CodexAuthFileStore _store;
        private readonly ICodexDesktopLifecycle _lifecycle;
        private readonly IAccountSwitchJournal _journal;
        private int _switching;

        public AccountSwitcher(string defaultCodexHome, string accountsRoot, ICodexDesktopLifecycle lifecycle)
            : this(defaultCodexHome, accountsRoot, lifecycle, NullAccountSwitchJournal.Instance)
        {
        }

        internal AccountSwitcher(string defaultCodexHome, string accountsRoot, ICodexDesktopLifecycle lifecycle,
            IAccountSwitchJournal journal)
        {
            if (String.IsNullOrWhiteSpace(defaultCodexHome)) throw new ArgumentNullException("defaultCodexHome");
            if (String.IsNullOrWhiteSpace(accountsRoot)) throw new ArgumentNullException("accountsRoot");
            if (lifecycle == null) throw new ArgumentNullException("lifecycle");
            if (journal == null) throw new ArgumentNullException("journal");
            _store = new CodexAuthFileStore(defaultCodexHome, accountsRoot);
            _lifecycle = lifecycle;
            _journal = journal;
        }

        public bool IsSwitching
        {
            get { return Interlocked.CompareExchange(ref _switching, 0, 0) != 0; }
        }

        public int? DetectActiveAccountNumber(int accountCount)
        {
            return _store.DetectActiveAccountNumber(accountCount);
        }

        public void SynchronizeCurrentCredentials(int accountCount)
        {
            int? current = _store.DetectActiveAccountNumber(accountCount);
            if (current.HasValue) _store.SaveCurrentAccountIfMatching(current.Value, _store.ReadDefault());
        }

        public AccountSwitchResult SwitchTo(int targetAccountNumber, int? currentAccountNumber)
        {
            if (targetAccountNumber < 1)
            {
                return AccountSwitchResult.Failed(false, "전환할 계정 번호가 올바르지 않습니다.");
            }
            if (Interlocked.CompareExchange(ref _switching, 1, 0) != 0)
            {
                return AccountSwitchResult.Failed(false, "다른 계정 전환이 진행 중입니다.");
            }

            byte[] originalAuth = null;
            bool originalExisted = false;
            bool stopAttempted = false;
            bool desktopStopped = false;
            bool targetApplied = false;
            bool startAttempted = false;
            try
            {
                int meterProcessId;
                using (Process meter = Process.GetCurrentProcess()) meterProcessId = meter.Id;
                _journal.Write("switch-start", "meter-pid=" + meterProcessId.ToString() +
                    " target-account=" + targetAccountNumber.ToString());

                CodexAuthRecord target = _store.ReadAccount(targetAccountNumber);
                _journal.Write("target-validated", "target-account=" + targetAccountNumber.ToString());
                int? detected = _store.DetectActiveAccountNumber(Math.Max(targetAccountNumber,
                    currentAccountNumber.HasValue ? currentAccountNumber.Value : 0));
                string stopError;
                stopAttempted = true;
                if (!_lifecycle.TryStop(TimeSpan.FromSeconds(20), out stopError))
                {
                    _journal.Write("codex-stop-failed", "target-account=" + targetAccountNumber.ToString());
                    return RecoverAfterStopFailure(String.IsNullOrWhiteSpace(stopError)
                        ? "Codex를 완전히 종료하지 못해 계정을 변경하지 않았습니다."
                        : stopError);
                }
                desktopStopped = true;
                _journal.Write("codex-stop-confirmed", "target-account=" + targetAccountNumber.ToString());

                if (_store.DefaultExists)
                {
                    CodexAuthRecord current = _store.ReadDefault();
                    originalAuth = current.Bytes;
                    originalExisted = true;
                    if (detected.HasValue)
                    {
                        _store.SaveCurrentAccountIfMatching(detected.Value, current);
                    }
                }

                // Read again after all writers have stopped; the desktop may have refreshed auth on exit.
                target = _store.ReadAccount(targetAccountNumber);
                _store.WriteDefault(target.Bytes);
                targetApplied = true;
                _journal.Write("auth-applied", "target-account=" + targetAccountNumber.ToString());
                CodexAuthRecord applied = _store.ReadDefault();
                if (!String.Equals(applied.AccountId, target.AccountId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("적용된 계정 인증을 확인하지 못했습니다.");
                }

                string startError;
                startAttempted = true;
                if (!_lifecycle.TryStart(TimeSpan.FromSeconds(60), out startError))
                {
                    throw new InvalidOperationException(String.IsNullOrWhiteSpace(startError)
                        ? "Codex를 다시 실행하지 못했습니다."
                        : startError);
                }

                _journal.Write("codex-start-confirmed", "target-account=" + targetAccountNumber.ToString());
                _journal.Write("switch-complete", "target-account=" + targetAccountNumber.ToString());
                return AccountSwitchResult.Completed(false,
                    "선택한 계정으로 전환하고 Codex를 다시 열었습니다. 미터기는 계속 실행 중입니다.");
            }
            catch (Exception ex)
            {
                if (stopAttempted && !desktopStopped)
                {
                    _journal.Write("codex-stop-failed", "error-type=" + ex.GetType().Name);
                    return RecoverAfterStopFailure(SafeFailureMessage(ex));
                }
                bool restored = false;
                bool safeToRestore = desktopStopped;
                if (startAttempted)
                {
                    // A failed launch can still leave a live backend. Never change its auth underneath it.
                    string cleanupError;
                    try { safeToRestore = _lifecycle.TryStop(TimeSpan.FromSeconds(20), out cleanupError); }
                    catch { safeToRestore = false; }
                    _journal.Write("failed-start-cleanup", "stopped=" + safeToRestore.ToString());
                }
                if (targetApplied && safeToRestore)
                {
                    try
                    {
                        if (originalExisted) _store.WriteDefault(originalAuth);
                        else _store.RemoveDefaultCreatedBySwitch();
                        restored = true;
                    }
                    catch
                    {
                        restored = false;
                    }
                }

                if (safeToRestore && (!targetApplied || restored))
                {
                    string ignored;
                    try { if (!_lifecycle.TryStart(TimeSpan.FromSeconds(60), out ignored)) restored = false; }
                    catch { restored = false; }
                }

                _journal.Write("switch-failed", "target-account=" + targetAccountNumber.ToString() +
                    " target-applied=" + targetApplied.ToString() + " restored=" + restored.ToString() +
                    " error-type=" + ex.GetType().Name);

                string message = SafeFailureMessage(ex);
                if (targetApplied)
                {
                    message = restored
                        ? "계정 전환에 실패하여 기존 계정으로 복원하고 Codex를 다시 열었습니다. " + message
                        : !safeToRestore
                            ? "Codex의 종료를 확인하지 못해 계정 정보가 섞이지 않도록 선택 계정의 인증을 유지했습니다. " + message
                            : "계정 전환과 기존 계정 복원에 실패했습니다. Codex 로그인 상태를 확인해 주세요. " + message;
                }
                return AccountSwitchResult.Failed(targetApplied && restored, message);
            }
            finally
            {
                Interlocked.Exchange(ref _switching, 0);
            }
        }

        private AccountSwitchResult RecoverAfterStopFailure(string stopError)
        {
            // Shutdown may have already closed the desktop window. Authentication has not
            // been written yet, so reopening is safe even if an old backend is still alive.
            bool reopened = false;
            try
            {
                string startError;
                reopened = _lifecycle.TryStart(TimeSpan.FromSeconds(60), out startError);
            }
            catch (Exception ex)
            {
                _journal.Write("stop-failure-recovery-error", "error-type=" + ex.GetType().Name);
            }
            _journal.Write("stop-failure-recovery", "started=" + reopened.ToString());
            return AccountSwitchResult.Failed(false, (reopened
                ? "종료 확인에 실패하여 계정 전환을 취소하고 기존 계정으로 Codex를 다시 열었습니다. "
                : "계정 정보는 변경하지 않았지만 Codex를 다시 열지 못했습니다. 시작 메뉴에서 Codex를 열어 주세요. ") + stopError);
        }

        private static string SafeFailureMessage(Exception exception)
        {
            if (exception is FileNotFoundException) return "등록된 계정의 인증정보를 찾지 못했습니다.";
            if (exception is UnauthorizedAccessException) return "인증정보 파일을 읽거나 쓸 권한이 없습니다.";
            if (exception is InvalidDataException) return exception.Message;
            if (exception is IOException) return "인증정보 파일을 안전하게 교체하지 못했습니다.";
            return String.IsNullOrWhiteSpace(exception.Message) ? "알 수 없는 오류가 발생했습니다." : exception.Message;
        }
    }

    internal sealed class CodexAuthRecord
    {
        public byte[] Bytes { get; set; }
        public string AccountId { get; set; }
        public DateTimeOffset? LastRefresh { get; set; }
    }

    internal sealed class CodexAuthFileStore
    {
        private const int MaximumAuthBytes = 1024 * 1024;
        private readonly string _defaultAuthPath;
        private readonly string _accountsRoot;
        private readonly JavaScriptSerializer _json;

        public CodexAuthFileStore(string defaultCodexHome, string accountsRoot)
        {
            _defaultAuthPath = Path.Combine(defaultCodexHome, "auth.json");
            _accountsRoot = accountsRoot;
            _json = new JavaScriptSerializer { MaxJsonLength = MaximumAuthBytes };
        }

        public bool DefaultExists { get { return File.Exists(_defaultAuthPath); } }

        public CodexAuthRecord ReadDefault() { return ReadValidated(_defaultAuthPath); }

        public CodexAuthRecord ReadAccount(int accountNumber) { return ReadValidated(AccountPath(accountNumber)); }

        public int? DetectActiveAccountNumber(int accountCount)
        {
            if (accountCount < 1 || !DefaultExists) return null;
            CodexAuthRecord current;
            try { current = ReadDefault(); }
            catch { return null; }

            for (int number = 1; number <= accountCount; number++)
            {
                try
                {
                    CodexAuthRecord candidate = ReadAccount(number);
                    if (String.Equals(current.AccountId, candidate.AccountId, StringComparison.Ordinal)) return number;
                }
                catch (FileNotFoundException) { }
                catch (InvalidDataException) { }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            return null;
        }

        public void SaveCurrentAccountIfMatching(int accountNumber, CodexAuthRecord current)
        {
            CodexAuthRecord registered = ReadAccount(accountNumber);
            if (!String.Equals(registered.AccountId, current.AccountId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("현재 Codex 계정과 미터기의 활성 계정이 일치하지 않아 전환을 중단했습니다.");
            }
            // The meter and desktop refresh independently. Do not overwrite newer registered credentials.
            if (!registered.LastRefresh.HasValue ||
                (current.LastRefresh.HasValue && current.LastRefresh.Value > registered.LastRefresh.Value))
            {
                AtomicWrite(AccountPath(accountNumber), current.Bytes);
            }
        }

        public void WriteDefault(byte[] bytes)
        {
            ValidateBytes(bytes);
            AtomicWrite(_defaultAuthPath, bytes);
        }

        public void RemoveDefaultCreatedBySwitch()
        {
            if (File.Exists(_defaultAuthPath)) File.Delete(_defaultAuthPath);
        }

        private string AccountPath(int accountNumber)
        {
            if (accountNumber < 1) throw new ArgumentOutOfRangeException("accountNumber");
            return Path.Combine(_accountsRoot, "account-" + accountNumber.ToString(), "auth.json");
        }

        private CodexAuthRecord ReadValidated(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("인증정보 파일을 찾지 못했습니다.", path);
            return ValidateBytes(File.ReadAllBytes(path));
        }

        private CodexAuthRecord ValidateBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumAuthBytes)
            {
                throw new InvalidDataException("인증정보 파일의 크기가 올바르지 않습니다.");
            }

            Dictionary<string, object> root;
            try { root = _json.DeserializeObject(Encoding.UTF8.GetString(bytes)) as Dictionary<string, object>; }
            catch { throw new InvalidDataException("인증정보 파일 형식이 올바르지 않습니다."); }
            Dictionary<string, object> tokens = root == null ? null : JsonValue.AsObject(JsonValue.Get(root, "tokens"));
            string mode = root == null ? null : JsonValue.AsString(JsonValue.Get(root, "auth_mode"));
            if (tokens == null || String.IsNullOrWhiteSpace(mode))
            {
                throw new InvalidDataException("Codex 로그인 인증정보가 완전하지 않습니다.");
            }

            string accountId = RequiredToken(tokens, "account_id");
            RequiredToken(tokens, "id_token");
            RequiredToken(tokens, "access_token");
            RequiredToken(tokens, "refresh_token");
            DateTimeOffset lastRefresh;
            bool hasRefresh = DateTimeOffset.TryParse(JsonValue.AsString(JsonValue.Get(root, "last_refresh")),
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out lastRefresh);
            return new CodexAuthRecord { Bytes = bytes, AccountId = accountId,
                LastRefresh = hasRefresh ? (DateTimeOffset?)lastRefresh : null };
        }

        private static string RequiredToken(Dictionary<string, object> tokens, string key)
        {
            string value = JsonValue.AsString(JsonValue.Get(tokens, key));
            if (String.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException("Codex 로그인 인증정보가 완전하지 않습니다.");
            }
            return value;
        }

        private static void AtomicWrite(string path, byte[] bytes)
        {
            string directory = Path.GetDirectoryName(path);
            if (String.IsNullOrWhiteSpace(directory)) throw new IOException("인증정보 저장 위치가 올바르지 않습니다.");
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." +
                Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
                if (File.Exists(path)) File.Replace(temporary, path, null, true);
                else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
    }

    internal sealed class CodexDesktopLifecycle : ICodexDesktopLifecycle
    {
        private readonly ICodexDesktopProcessSource _processSource;
        private readonly ICodexDesktopStarter _starter;
        private readonly IDesktopLifecycleClock _clock;
        private readonly IAccountSwitchJournal _journal;

        public CodexDesktopLifecycle()
            : this(new FileAccountSwitchJournal())
        {
        }

        internal CodexDesktopLifecycle(IAccountSwitchJournal journal)
            : this(new SystemCodexDesktopProcessSource(), new SystemCodexDesktopStarter(),
                new DesktopLifecycleClock(), journal)
        {
        }

        internal CodexDesktopLifecycle(ICodexDesktopProcessSource processSource, ICodexDesktopStarter starter,
            IDesktopLifecycleClock clock, IAccountSwitchJournal journal)
        {
            if (processSource == null) throw new ArgumentNullException("processSource");
            if (starter == null) throw new ArgumentNullException("starter");
            _processSource = processSource;
            _starter = starter;
            _clock = clock;
            _journal = journal;
        }

        public bool TryStop(TimeSpan timeout, out string error)
        {
            error = null;
            long started = _clock.Milliseconds;
            long? emptySince = null;
            Dictionary<int, ICodexDesktopProcess> observed = new Dictionary<int, ICodexDesktopProcess>();
            try
            {
                while (_clock.Milliseconds - started < timeout.TotalMilliseconds)
                {
                    // Retain handles to descendants even after their parent disappears from discovery.
                    foreach (ICodexDesktopProcess process in _processSource.FindCodexProcesses())
                    {
                        ICodexDesktopProcess previous;
                        if (observed.TryGetValue(process.Id, out previous))
                        {
                            if (!previous.HasExited) { process.Dispose(); continue; }
                            previous.Dispose();
                        }
                        observed[process.Id] = process;
                        _journal.Write("stop-observed", "pid=" + process.Id.ToString());
                        if (process.HasMainWindow) process.TryCloseMainWindow();
                    }
                    ICodexDesktopProcess[] active = observed.Values.Where(p => !p.HasExited).ToArray();
                    if (active.Length == 0)
                    {
                        if (!emptySince.HasValue) emptySince = _clock.Milliseconds;
                        // Also catch children spawned while shutdown was already in progress.
                        if (_clock.Milliseconds - emptySince.Value >= 500) return true;
                    }
                    else
                    {
                        emptySince = null;
                        if (_clock.Milliseconds - started >= 750)
                        {
                            foreach (ICodexDesktopProcess process in active) process.TryTerminate();
                        }
                    }
                    _clock.Delay(100);
                }
            }
            catch (Exception ex)
            {
                _journal.Write("stop-inspection-failed", "error-type=" + ex.GetType().Name);
                error = "Codex 내부 프로세스의 종료 상태를 확인하지 못해 계정을 변경하지 않았습니다.";
                return false;
            }
            finally
            {
                foreach (ICodexDesktopProcess process in observed.Values) process.Dispose();
            }
            error = "Codex 프로세스를 제한 시간 안에 완전히 종료하지 못해 계정을 변경하지 않았습니다.";
            return false;
        }

        public bool TryStart(TimeSpan timeout, out string error)
        {
            error = null;
            if (!_starter.TryLaunch(out error)) return false;

            long started = _clock.Milliseconds;
            long? stableSince = null;
            string stableProcesses = null;
            try
            {
                while (_clock.Milliseconds - started < timeout.TotalMilliseconds)
                {
                    ICodexDesktopProcess[] processes = _processSource.FindCodexProcesses();
                    try
                    {
                        ICodexDesktopProcess[] active = processes.Where(p => !p.HasExited).ToArray();
                        bool windowReady = active.Any(p => p.HasMainWindow);
                        int[] backends = active.Where(p => p.IsAppServer).Select(p => p.Id).OrderBy(id => id).ToArray();
                        string identity = windowReady && backends.Length > 0
                            ? String.Join(",", active.Where(p => p.HasMainWindow || p.IsAppServer)
                                .Select(p => p.Id).OrderBy(id => id)) : null;
                        if (identity == null || identity != stableProcesses)
                        {
                            stableSince = identity == null ? (long?)null : _clock.Milliseconds;
                            stableProcesses = identity;
                        }
                        // A splash/error window alone is not a successful restart. Observe a stable
                        // desktop + desktop-owned backend, including across delayed runtime extraction.
                        if (stableSince.HasValue && _clock.Milliseconds - stableSince.Value >= 3000)
                        {
                            _journal.Write("start-processes-stable", "pids=" + identity);
                            return true;
                        }
                    }
                    finally
                    {
                        foreach (ICodexDesktopProcess process in processes) process.Dispose();
                    }
                    _clock.Delay(250);
                }
            }
            catch (Exception ex)
            {
                _journal.Write("start-inspection-failed", "error-type=" + ex.GetType().Name);
            }
            error = "Codex의 창과 내부 실행 프로세스가 제한 시간 안에 안정적으로 시작되지 않았습니다.";
            return false;
        }
    }

    internal sealed class SystemCodexDesktopStarter : ICodexDesktopStarter
    {
        internal const string CodexApplicationUserModelId = "OpenAI.Codex_2p2nqsd0c76g0!App";

        public bool TryLaunch(out string error)
        {
            error = null;
            try
            {
                using (Process process = Process.Start(new ProcessStartInfo {
                    FileName = "explorer.exe",
                    Arguments = "shell:AppsFolder\\" + CodexApplicationUserModelId,
                    UseShellExecute = true
                }))
                {
                }
                return true;
            }
            catch
            {
                error = "Codex를 자동으로 다시 열지 못했습니다.";
                return false;
            }
        }
    }

    internal sealed class SystemCodexDesktopProcessSource : ICodexDesktopProcessSource
    {
        public ICodexDesktopProcess[] FindCodexProcesses()
        {
            HashSet<int> ids = FindCodexChatGptProcessIds();
            if (ids.Count == 0) return new ICodexDesktopProcess[0];
            HashSet<int> roots = new HashSet<int>(ids);
            List<KeyValuePair<int, int>> tree = ReadProcessTree();
            int meterId;
            using (Process meter = Process.GetCurrentProcess()) meterId = meter.Id;
            IncludeDescendantsExceptMeter(ids, tree, meterId);

            List<ICodexDesktopProcess> matches = new List<ICodexDesktopProcess>();
            try
            {
                foreach (int id in ids)
                {
                    Process process = null;
                    try
                    {
                        process = Process.GetProcessById(id);
                        bool desktopChild = tree.Any(edge => edge.Key == id && roots.Contains(edge.Value));
                        matches.Add(new SystemCodexDesktopProcess(process, desktopChild));
                        process = null; // The wrapper owns the process handle until Dispose.
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception)
                    {
                        // A process may finish between the system snapshot and opening its handle.
                        // Only skip it after confirming exit; access errors on a live process still fail.
                        if (process == null || !process.HasExited) throw;
                    }
                    finally { if (process != null) process.Dispose(); }
                }
                return matches.ToArray();
            }
            catch
            {
                foreach (ICodexDesktopProcess process in matches) process.Dispose();
                throw;
            }
        }

        private static HashSet<int> FindCodexChatGptProcessIds()
        {
            return FindCodexChatGptProcessIds(Process.GetProcessesByName("ChatGPT"));
        }

        internal static HashSet<int> FindCodexChatGptProcessIds(Process[] processes)
        {
            HashSet<int> ids = new HashSet<int>();
            try
            {
                foreach (Process process in processes)
                {
                    try
                    {
                        if (process.HasExited) continue;
                        string path = process.MainModule == null ? null : process.MainModule.FileName;
                        if (IsCodexDesktopPath(path)) ids.Add(process.Id);
                    }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception)
                    {
                        // MainModule can throw ERROR_PARTIAL_COPY (299) once the process exits.
                        // Recheck exit instead of aborting a shutdown that has already succeeded.
                        if (!process.HasExited) throw;
                    }
                }
            }
            finally
            {
                foreach (Process process in processes) process.Dispose();
            }
            return ids;
        }

        internal static bool IsCodexDesktopPath(string path)
        {
            return !String.IsNullOrWhiteSpace(path) &&
                path.EndsWith("\\ChatGPT.exe", StringComparison.OrdinalIgnoreCase) &&
                path.IndexOf("\\WindowsApps\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static List<KeyValuePair<int, int>> ReadProcessTree()
        {
            List<KeyValuePair<int, int>> processes = new List<KeyValuePair<int, int>>();
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId FROM Win32_Process"))
            {
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject item in results)
                    {
                        using (item)
                        {
                            processes.Add(new KeyValuePair<int, int>(
                                Convert.ToInt32((UInt32)item["ProcessId"]),
                                Convert.ToInt32((UInt32)item["ParentProcessId"])));
                        }
                    }
                }

            }
            return processes;
        }

        internal static void IncludeDescendantsExceptMeter(HashSet<int> ids,
            List<KeyValuePair<int, int>> processes, int meterId)
        {
            HashSet<int> protectedIds = new HashSet<int> { meterId };
            IncludeDescendants(protectedIds, processes);
            IncludeDescendants(ids, processes);
            ids.ExceptWith(protectedIds);
        }

        private static void IncludeDescendants(HashSet<int> ids, List<KeyValuePair<int, int>> processes)
        {
            bool changed;
            do
            {
                changed = false;
                foreach (KeyValuePair<int, int> process in processes)
                {
                    if (ids.Contains(process.Value) && ids.Add(process.Key)) changed = true;
                }
            }
            while (changed);
        }
    }

    internal sealed class SystemCodexDesktopProcess : ICodexDesktopProcess
    {
        private readonly int _processId;
        private readonly Process _process;
        private readonly SafeWaitHandle _exitHandle;
        private readonly bool _isAppServer;

        public SystemCodexDesktopProcess(Process process, bool desktopChild)
        {
            if (process == null) throw new ArgumentNullException("process");
            _processId = process.Id;
            _isAppServer = desktopChild && String.Equals(process.ProcessName, "codex", StringComparison.OrdinalIgnoreCase);
            // Hold the native process handle: an exit request is not the same as exit completion,
            // and a later process reusing this PID must never become a termination target.
            _exitHandle = OpenProcess(0x00100000, false, _processId); // SYNCHRONIZE only, not PROCESS_ALL_ACCESS
            if (_exitHandle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                _exitHandle.Dispose();
                if (error == 87) throw new InvalidOperationException("Process already exited.");
                throw new Win32Exception(error);
            }
            _process = process;
        }

        public int Id { get { return _processId; } }
        public bool IsAppServer { get { return _isAppServer; } }

        public bool HasMainWindow
        {
            get
            {
                try
                {
                    _process.Refresh();
                    return !HasExited && _process.MainWindowHandle != IntPtr.Zero && _process.Responding;
                }
                catch { return false; }
            }
        }

        public bool HasExited
        {
            get
            {
                uint result = WaitForSingleObject(_exitHandle, 0);
                if (result == 0xFFFFFFFF) throw new Win32Exception(Marshal.GetLastWin32Error());
                return result == 0;
            }
        }

        public bool TryCloseMainWindow()
        {
            try
            {
                return HasExited || _process.CloseMainWindow();
            }
            catch { return false; }
        }

        public bool TryTerminate()
        {
            try
            {
                if (!HasExited) _process.Kill();
                return true;
            }
            catch { return false; }
        }

        public void Dispose() { _exitHandle.Dispose(); _process.Dispose(); }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeWaitHandle OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    }
}
