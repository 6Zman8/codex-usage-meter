using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexUsageMeter
{
    internal static class AccountSwitchRegressionTests
    {
        public static void Run(Action<string> report)
        {
            TestQueuedAccountTarget("BeginLoginAsync");
            TestQueuedAccountTarget("LogoutConfirmed");
            report("PASS queued login and logout retain the clicked account after page changes");
            HashSet<int> ids = new HashSet<int> { 10 };
            List<KeyValuePair<int, int>> tree = new List<KeyValuePair<int, int>> {
                new KeyValuePair<int, int>(11, 10), new KeyValuePair<int, int>(12, 11),
                new KeyValuePair<int, int>(20, 11), new KeyValuePair<int, int>(21, 20),
                new KeyValuePair<int, int>(22, 21), new KeyValuePair<int, int>(30, 99)
            };
            SystemCodexDesktopProcessSource.IncludeDescendantsExceptMeter(ids, tree, 20);
            Require(ids.SetEquals(new int[] { 10, 11, 12 }), "Exclude meter, its descendants, and unrelated apps.");
            report("PASS process scope excludes meter and its children");
            TestStop();
            report("PASS orphaned backend exit, stop timeout, and inventory failure");
            TestStart();
            report("PASS delayed startup, window-only rejection, backend restart stability");
            TestAuth();
            report("PASS same-account reconnect, fresh auth preservation, exit-time auth refresh");
            TestRollback();
            report("PASS failed stop, partial-start cleanup, and safe rollback");
            TestRealHandle();
            report("PASS real isolated child exit through retained native process handle");
            TestExitedProcessSnapshot();
            report("PASS terminated process in a captured desktop snapshot is treated as exited");
        }

        private static CodexDesktopLifecycle Lifecycle(Source source, Clock clock)
        { return new CodexDesktopLifecycle(source, new Starter(), clock, NullAccountSwitchJournal.Instance); }

        private static void TestQueuedAccountTarget(string operation)
        {
            WithFiles(delegate(string home, string accounts, string auth) {
                int attemptedAccount = 0;
                // Stop at executable resolution: no real login, logout, browser, or credentials are used.
                using (CodexRpcClient first = new CodexRpcClient(delegate {
                    attemptedAccount = 1; throw new InvalidOperationException("isolated target probe");
                }, Path.Combine(home, "queued-account-1")))
                using (CodexRpcClient nextPage = new CodexRpcClient(delegate {
                    attemptedAccount = 3; throw new InvalidOperationException("isolated target probe");
                }, Path.Combine(home, "queued-account-3")))
                using (SemaphoreSlim gate = new SemaphoreSlim(0, 1))
                {
                    // Avoid the live dashboard constructor, which opens the user's account profiles.
                    DashboardController controller = (DashboardController)FormatterServices.GetUninitializedObject(
                        typeof(DashboardController));
                    BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                    typeof(DashboardController).GetField("_accountOperationGate", fields).SetValue(controller, gate);
                    typeof(DashboardController).GetField("_refreshing", fields).SetValue(controller, true);
                    AccountView view = new AccountView { Client = first, Label = "계정 1",
                        LoginButton = new Button(), LogoutButton = new Button(), Status = new TextBlock() };
                    SynchronizationContext previous = SynchronizationContext.Current;
                    DispatcherTimer timer = new DispatcherTimer();
                    DispatcherFrame frame = new DispatcherFrame();
                    Stopwatch elapsed = Stopwatch.StartNew();
                    try
                    {
                        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                        typeof(DashboardController).GetMethod(operation, fields).Invoke(controller, new object[] { view });
                        Require(attemptedAccount == 0, "Operation must wait behind the existing account refresh.");
                        view.Client = nextPage;
                        view.Label = "계정 3";
                        view.Status.Text = "next page status";
                        gate.Release();
                        timer.Interval = TimeSpan.FromMilliseconds(10);
                        timer.Tick += delegate {
                            if ((attemptedAccount != 0 && gate.CurrentCount == 1) || elapsed.ElapsedMilliseconds > 5000)
                                frame.Continue = false;
                        };
                        timer.Start();
                        Dispatcher.PushFrame(frame);
                        Require(attemptedAccount == 1, operation + " must retain account 1 instead of using the rebound account 3.");
                        Require(view.Status.Text == "next page status", "Completion must not overwrite another account's status.");
                    }
                    finally
                    {
                        timer.Stop();
                        SynchronizationContext.SetSynchronizationContext(previous);
                    }
                }
            });
        }

        private static void TestStop()
        {
            Clock clock = new Clock();
            State root = new State(clock, 10, false, true, 0);
            State backend = new State(clock, 11, true, false, 1800);
            Source source = new Source(delegate { return root.Exited ? new State[0] : new State[] { root, backend }; });
            string error;
            Require(Lifecycle(source, clock).TryStop(TimeSpan.FromSeconds(10), out error), error);
            Require(backend.Exited && clock.Milliseconds >= 3100, "Track backend after parent disappears, until exit + quiet interval.");
            Require(source.OpenHandles == 0, "Release all retained handles.");
            clock = new Clock();
            State blocked = new State(clock, 30, false, true, Int32.MaxValue);
            source = new Source(delegate { return new State[] { blocked }; });
            Require(!Lifecycle(source, clock).TryStop(TimeSpan.FromSeconds(2), out error), "A live process must time out.");
            Require(source.OpenHandles == 0, "Release handles on timeout.");
            Source denied = new Source(delegate { throw new UnauthorizedAccessException(); });
            Require(!Lifecycle(denied, new Clock()).TryStop(TimeSpan.FromSeconds(2), out error), "Inventory failure is not an empty list.");
        }

        private static void TestStart()
        {
            Clock clock = new Clock();
            State window = new State(clock, 10, false, true, 0);
            State first = new State(clock, 11, true, false, 0);
            State replacement = new State(clock, 12, true, false, 0);
            Source source = new Source(delegate {
                if (clock.Milliseconds < 25000) return new State[] { window };
                return new State[] { window, clock.Milliseconds < 27000 ? first : replacement };
            });
            string error;
            Require(Lifecycle(source, clock).TryStart(TimeSpan.FromSeconds(60), out error), error);
            Require(clock.Milliseconds >= 30000, "The replacement backend needs a new stability interval.");
            Require(source.OpenHandles == 0, "Release startup snapshot handles.");
            source = new Source(delegate { return new State[] { window }; });
            Require(!Lifecycle(source, new Clock()).TryStart(TimeSpan.FromSeconds(5), out error), "Window alone cannot mean startup success.");
        }

        private static void TestAuth()
        {
            WithFiles(delegate(string home, string accounts, string auth) {
                Recorder recorder = new Recorder(auth);
                AccountSwitcher switcher = new AccountSwitcher(home, accounts, recorder);
                Require(switcher.SwitchTo(2, 1).Success, "Switch to target.");
                recorder.Events.Clear();
                Require(switcher.SwitchTo(2, 2).Success && recorder.Events.Count == 2, "Do not skip a same-account repair.");
            });
            WithFiles(delegate(string home, string accounts, string auth) {
                string registered = Path.Combine(accounts, "account-1", "auth.json");
                WriteAuth(registered, "old", "newer", "2026-09-13T02:00:00Z");
                AccountSwitcher switcher = new AccountSwitcher(home, accounts, new Recorder(auth));
                switcher.SynchronizeCurrentCredentials(2);
                Require(switcher.SwitchTo(2, 1).Success, "Switch with fresher meter auth.");
                Require(File.ReadAllText(registered).Contains("id-newer"), "Old desktop auth must not overwrite newer meter auth.");
            });
            WithFiles(delegate(string home, string accounts, string auth) {
                Recorder recorder = new Recorder(auth);
                recorder.BeforeStop = delegate { WriteAuth(auth, "old", "on-exit", "2026-09-13T03:00:00Z"); };
                Require(new AccountSwitcher(home, accounts, recorder).SwitchTo(1, 1).Success, "Same-account restart.");
                Require(File.ReadAllText(auth).Contains("id-on-exit"), "Re-read auth refreshed during desktop shutdown.");
            });
        }

        private static void TestRollback()
        {
            WithFiles(delegate(string home, string accounts, string auth) {
                Recorder recorder = new Recorder(auth) { StopAllowed = false };
                byte[] before = File.ReadAllBytes(auth);
                Require(!new AccountSwitcher(home, accounts, recorder).SwitchTo(2, 1).Success, "Stop failure.");
                Require(before.SequenceEqual(File.ReadAllBytes(auth)), "No auth write after stop failure.");
                Require(String.Join(",", recorder.Events) == "stop:old,start:old",
                    "If shutdown fails after closing the window, reopen Codex with the unchanged account.");
            });
            WithFiles(delegate(string home, string accounts, string auth) {
                Recorder recorder = new Recorder(auth) { ThrowOnStop = true };
                byte[] before = File.ReadAllBytes(auth);
                AccountSwitchResult result = new AccountSwitcher(home, accounts, recorder).SwitchTo(2, 1);
                Require(!result.Success && !result.RolledBack && before.SequenceEqual(File.ReadAllBytes(auth)),
                    "An unexpected stop error must leave existing authentication untouched.");
                Require(String.Join(",", recorder.Events) == "stop:old,start:old", "Recover even if shutdown throws.");
            });
            WithFiles(delegate(string home, string accounts, string auth) {
                Recorder recorder = new Recorder(auth) { StopAllowed = false, FailFirstStart = true };
                byte[] before = File.ReadAllBytes(auth);
                AccountSwitchResult result = new AccountSwitcher(home, accounts, recorder).SwitchTo(2, 1);
                Require(!result.Success && !result.RolledBack && before.SequenceEqual(File.ReadAllBytes(auth)),
                    "Recovery launch failure must not claim a switch or replace auth.");
                Require(recorder.Events.Count == 2, "Recovery launch is bounded and does not repeat shutdown.");
            });
            WithFiles(delegate(string home, string accounts, string auth) {
                Recorder recorder = new Recorder(auth) { FailFirstStart = true };
                AccountSwitchResult result = new AccountSwitcher(home, accounts, recorder).SwitchTo(2, 1);
                Require(!result.Success && result.RolledBack && ReadAccount(auth) == "old", "Restore previous account.");
                Require(String.Join(",", recorder.Events) == "stop:old,start:target,stop:target,start:old", "Stop partial launch BEFORE rollback.");
            });
            WithFiles(delegate(string home, string accounts, string auth) {
                Recorder recorder = new Recorder(auth) { FailFirstStart = true, FailSecondStop = true };
                AccountSwitchResult result = new AccountSwitcher(home, accounts, recorder).SwitchTo(2, 1);
                Require(!result.Success && !result.RolledBack && ReadAccount(auth) == "target", "Keep target auth if cleanup fails.");
                Require(recorder.Events.Count == 3, "No extra launch while previous instance is unconfirmed.");
            });
        }

        private static void TestRealHandle()
        {
            string executable;
            using (Process self = Process.GetCurrentProcess()) executable = self.MainModule.FileName;
            Process child = Process.Start(new ProcessStartInfo {
                FileName = executable, Arguments = "--process-exit-test-child", UseShellExecute = false,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            });
            SystemCodexDesktopProcess wrapper = null;
            try
            {
                wrapper = new SystemCodexDesktopProcess(child, false);
                Require(!wrapper.HasExited, "Owned test child initially alive.");
                Require(wrapper.TryTerminate(), "Terminate only our isolated test child.");
                Stopwatch wait = Stopwatch.StartNew();
                while (!wrapper.HasExited && wait.ElapsedMilliseconds < 5000) Thread.Sleep(20);
                Require(wrapper.HasExited, "Retained handle must confirm actual exit.");
            }
            finally
            {
                try { if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); } } catch { }
                if (wrapper != null) wrapper.Dispose(); else child.Dispose();
            }
        }

        private static void TestExitedProcessSnapshot()
        {
            string executable;
            using (Process self = Process.GetCurrentProcess()) executable = self.MainModule.FileName;
            using (Process child = Process.Start(new ProcessStartInfo {
                FileName = executable, Arguments = "--process-exit-test-child", UseShellExecute = false,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            }))
            {
                Process snapshot = Process.GetProcessById(child.Id);
                try
                {
                    child.Kill();
                    Require(child.WaitForExit(5000), "Isolated child must exit before its snapshot is inspected.");
                    HashSet<int> ids = SystemCodexDesktopProcessSource.FindCodexChatGptProcessIds(
                        new Process[] { snapshot });
                    Require(ids.Count == 0, "An exited snapshot must not block reconnect or become a termination target.");
                }
                finally
                {
                    snapshot.Dispose();
                    if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); }
                }
            }
        }

        private static void WithFiles(Action<string, string, string> test)
        {
            string root = Path.Combine(Path.GetTempPath(), "codex-meter-regression-" + Guid.NewGuid().ToString("N"));
            string home = Path.Combine(root, "default"), accounts = Path.Combine(root, "accounts"), auth = Path.Combine(home, "auth.json");
            try
            {
                WriteAuth(auth, "old", "old", "2026-09-12T01:00:00Z");
                WriteAuth(Path.Combine(accounts, "account-1", "auth.json"), "old", "old", "2026-09-12T01:00:00Z");
                WriteAuth(Path.Combine(accounts, "account-2", "auth.json"), "target", "target", "2026-09-12T01:00:00Z");
                test(home, accounts, auth);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static void WriteAuth(string path, string accountId, string marker, string timestamp)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string json = "{\"auth_mode\":\"chatgpt\",\"last_refresh\":\"" + timestamp + "\",\"tokens\":{" +
                "\"account_id\":\"" + accountId + "\",\"id_token\":\"id-" + marker + "\"," +
                "\"access_token\":\"access-" + marker + "\",\"refresh_token\":\"refresh-" + marker + "\"}}";
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }
        private static string ReadAccount(string path)
        {
            return new CodexAuthFileStore(Path.GetDirectoryName(path), Path.GetDirectoryName(path)).ReadDefault().AccountId;
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        private sealed class Recorder : ICodexDesktopLifecycle
        {
            private readonly string _auth;
            private int _stops, _starts;
            public readonly List<string> Events = new List<string>();
            public bool StopAllowed = true, FailFirstStart, FailSecondStop, ThrowOnStop;
            public Action BeforeStop;
            public Recorder(string auth) { _auth = auth; }
            public bool TryStop(TimeSpan timeout, out string error)
            {
                Events.Add("stop:" + ReadAccount(_auth));
                if (BeforeStop != null) BeforeStop();
                if (ThrowOnStop) throw new System.ComponentModel.Win32Exception(299);
                error = "simulated stop failure";
                return StopAllowed && !(++_stops == 2 && FailSecondStop);
            }
            public bool TryStart(TimeSpan timeout, out string error)
            { Events.Add("start:" + ReadAccount(_auth)); error = "simulated start failure"; return !(++_starts == 1 && FailFirstStart); }
        }
        private sealed class Clock : IDesktopLifecycleClock
        { public long Milliseconds { get; private set; } public void Delay(int milliseconds) { Milliseconds += milliseconds; } }
        private sealed class Starter : ICodexDesktopStarter
        { public bool TryLaunch(out string error) { error = null; return true; } }
        private sealed class State
        {
            private readonly Clock _clock;
            private readonly int _exitDelay;
            private long? _exitAt;
            public int Id;
            public bool Backend, Window;
            public State(Clock clock, int id, bool backend, bool window, int delay)
            { _clock = clock; Id = id; Backend = backend; Window = window; _exitDelay = delay; }
            public bool Exited { get { return _exitAt.HasValue && _clock.Milliseconds >= _exitAt.Value; } }
            public void Terminate() { if (!_exitAt.HasValue) _exitAt = _clock.Milliseconds + _exitDelay; }
        }
        private sealed class Source : ICodexDesktopProcessSource
        {
            private readonly Func<State[]> _snapshot;
            public int OpenHandles;
            public Source(Func<State[]> snapshot) { _snapshot = snapshot; }
            public ICodexDesktopProcess[] FindCodexProcesses()
            { return _snapshot().Select(state => (ICodexDesktopProcess)new FakeProcess(state, this)).ToArray(); }
        }
        private sealed class FakeProcess : ICodexDesktopProcess
        {
            private readonly State _state;
            private readonly Source _source;
            private bool _disposed;
            public FakeProcess(State state, Source source) { _state = state; _source = source; source.OpenHandles++; }
            public int Id { get { return _state.Id; } }
            public bool IsAppServer { get { return _state.Backend; } }
            public bool HasMainWindow { get { return _state.Window; } }
            public bool HasExited { get { return _state.Exited; } }
            public bool TryCloseMainWindow() { return true; }
            public bool TryTerminate() { _state.Terminate(); return true; }
            public void Dispose() { if (!_disposed) { _disposed = true; _source.OpenHandles--; } }
        }
    }
}
