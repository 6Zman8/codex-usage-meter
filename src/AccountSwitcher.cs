using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

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
            bool desktopStopped = false;
            bool targetApplied = false;
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
                if (detected.HasValue && detected.Value == targetAccountNumber)
                {
                    _journal.Write("switch-already-active", "target-account=" + targetAccountNumber.ToString());
                    return AccountSwitchResult.Completed(true, "이미 선택한 계정이 Codex에서 사용 중입니다.");
                }

                string stopError;
                if (!_lifecycle.TryStop(TimeSpan.FromSeconds(20), out stopError))
                {
                    _journal.Write("codex-stop-failed", "target-account=" + targetAccountNumber.ToString());
                    return AccountSwitchResult.Failed(false, String.IsNullOrWhiteSpace(stopError)
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
                    if (currentAccountNumber.HasValue)
                    {
                        _store.SaveCurrentAccountIfMatching(currentAccountNumber.Value, current);
                    }
                }

                _store.WriteDefault(target.Bytes);
                targetApplied = true;
                _journal.Write("auth-applied", "target-account=" + targetAccountNumber.ToString());
                CodexAuthRecord applied = _store.ReadDefault();
                if (!String.Equals(applied.AccountId, target.AccountId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("적용된 계정 인증을 확인하지 못했습니다.");
                }

                string startError;
                if (!_lifecycle.TryStart(TimeSpan.FromSeconds(20), out startError))
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
                bool restored = false;
                if (targetApplied)
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

                if (desktopStopped)
                {
                    string ignored;
                    if (!_lifecycle.TryStart(TimeSpan.FromSeconds(20), out ignored)) restored = false;
                }

                _journal.Write("switch-failed", "target-account=" + targetAccountNumber.ToString() +
                    " target-applied=" + targetApplied.ToString() + " restored=" + restored.ToString() +
                    " error-type=" + ex.GetType().Name);

                string message = SafeFailureMessage(ex);
                if (targetApplied)
                {
                    message = restored
                        ? "계정 전환에 실패하여 기존 계정으로 복원하고 Codex를 다시 열었습니다. " + message
                        : "계정 전환과 기존 계정 복원에 실패했습니다. Codex 로그인 상태를 확인해 주세요. " + message;
                }
                return AccountSwitchResult.Failed(targetApplied && restored, message);
            }
            finally
            {
                Interlocked.Exchange(ref _switching, 0);
            }
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
            AtomicWrite(AccountPath(accountNumber), current.Bytes);
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
            return new CodexAuthRecord { Bytes = bytes, AccountId = accountId };
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

        public CodexDesktopLifecycle()
            : this(new SystemCodexDesktopProcessSource(), new SystemCodexDesktopStarter())
        {
        }

        internal CodexDesktopLifecycle(ICodexDesktopProcessSource processSource, ICodexDesktopStarter starter)
        {
            if (processSource == null) throw new ArgumentNullException("processSource");
            if (starter == null) throw new ArgumentNullException("starter");
            _processSource = processSource;
            _starter = starter;
        }

        public bool TryStop(TimeSpan timeout, out string error)
        {
            error = null;
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                ICodexDesktopProcess[] processes = _processSource.FindCodexProcesses() ??
                    new ICodexDesktopProcess[0];
                try
                {
                    ICodexDesktopProcess[] active = processes.Where(delegate(ICodexDesktopProcess process)
                    {
                        return process != null && !process.HasExited;
                    }).ToArray();
                    if (active.Length == 0) return true;

                    foreach (ICodexDesktopProcess process in active)
                    {
                        if (process.HasMainWindow) process.TryCloseMainWindow();
                    }
                    foreach (ICodexDesktopProcess process in active)
                    {
                        if (!process.HasExited) process.TryTerminate();
                    }
                }
                finally
                {
                    foreach (ICodexDesktopProcess process in processes)
                    {
                        if (process != null) process.Dispose();
                    }
                }
                Thread.Sleep(100);
            }
            error = "Codex 프로세스를 제한 시간 안에 완전히 종료하지 못해 계정을 변경하지 않았습니다.";
            return false;
        }

        public bool TryStart(TimeSpan timeout, out string error)
        {
            error = null;
            if (!_starter.TryLaunch(out error)) return false;

            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                ICodexDesktopProcess[] processes = _processSource.FindCodexProcesses() ??
                    new ICodexDesktopProcess[0];
                try
                {
                    if (processes.Any(delegate(ICodexDesktopProcess process)
                    {
                        return process != null && !process.HasExited && process.HasMainWindow;
                    })) return true;
                }
                finally
                {
                    foreach (ICodexDesktopProcess process in processes)
                    {
                        if (process != null) process.Dispose();
                    }
                }
                Thread.Sleep(150);
            }
            error = "Codex를 실행했지만 제한 시간 안에 새 창이 나타나지 않았습니다.";
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
            IncludeDescendants(ids);

            List<ICodexDesktopProcess> matches = new List<ICodexDesktopProcess>();
            foreach (int id in ids)
            {
                try
                {
                    using (Process process = Process.GetProcessById(id))
                    {
                        matches.Add(new SystemCodexDesktopProcess(process));
                    }
                }
                catch
                {
                }
            }
            return matches.ToArray();
        }

        private static HashSet<int> FindCodexChatGptProcessIds()
        {
            HashSet<int> ids = new HashSet<int>();
            foreach (Process process in Process.GetProcessesByName("ChatGPT"))
            {
                try
                {
                    string path = process.MainModule == null ? null : process.MainModule.FileName;
                    if (IsCodexDesktopPath(path)) ids.Add(process.Id);
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
            return ids;
        }

        internal static bool IsCodexDesktopPath(string path)
        {
            return !String.IsNullOrWhiteSpace(path) &&
                path.EndsWith("\\ChatGPT.exe", StringComparison.OrdinalIgnoreCase) &&
                path.IndexOf("\\WindowsApps\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void IncludeDescendants(HashSet<int> ids)
        {
            try
            {
                List<KeyValuePair<int, int>> processes = new List<KeyValuePair<int, int>>();
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, ParentProcessId FROM Win32_Process"))
                {
                    foreach (ManagementObject item in searcher.Get())
                    {
                        using (item)
                        {
                            processes.Add(new KeyValuePair<int, int>(
                                Convert.ToInt32((UInt32)item["ProcessId"]),
                                Convert.ToInt32((UInt32)item["ParentProcessId"])));
                        }
                    }
                }

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
            catch
            {
            }
        }
    }

    internal sealed class SystemCodexDesktopProcess : ICodexDesktopProcess
    {
        private readonly int _processId;
        private readonly long _startTimeFileTimeUtc;

        public SystemCodexDesktopProcess(Process process)
        {
            if (process == null) throw new ArgumentNullException("process");
            _processId = process.Id;
            _startTimeFileTimeUtc = process.StartTime.ToUniversalTime().ToFileTimeUtc();
        }

        public int Id { get { return _processId; } }

        public bool HasMainWindow
        {
            get
            {
                try
                {
                    using (Process process = OpenCurrentProcess())
                    {
                        return process != null && process.MainWindowHandle != IntPtr.Zero;
                    }
                }
                catch { return false; }
            }
        }

        public bool HasExited
        {
            get
            {
                try
                {
                    using (Process process = OpenCurrentProcess()) return process == null;
                }
                catch { return true; }
            }
        }

        public bool TryCloseMainWindow()
        {
            try
            {
                using (Process process = OpenCurrentProcess()) return process == null || process.CloseMainWindow();
            }
            catch { return false; }
        }

        public bool TryTerminate()
        {
            try
            {
                using (Process process = OpenCurrentProcess())
                {
                    if (process == null) return true;
                    process.Kill();
                    return true;
                }
            }
            catch { return false; }
        }

        public void Dispose() { }

        private Process OpenCurrentProcess()
        {
            Process process;
            try { process = Process.GetProcessById(_processId); }
            catch (ArgumentException) { return null; }
            try
            {
                if (process.StartTime.ToUniversalTime().ToFileTimeUtc() != _startTimeFileTimeUtc)
                {
                    process.Dispose();
                    return null;
                }
                return process;
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }
    }
}
