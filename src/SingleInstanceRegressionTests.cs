using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Interop;

namespace CodexUsageMeter
{
    internal static class SingleInstanceRegressionTests
    {
        internal static int Send(string scope, bool refresh)
        {
            return SingleInstanceActivation.Request(scope, 4000, refresh) ? 0 : 1;
        }

        internal static void Run(Action<string> report)
        {
            string scope = "test-" + Guid.NewGuid().ToString("N");
            int opened = 0;
            int refreshed = 0;
            Window window = new Window { Width = 100, Height = 100, Left = -30000, Top = -30000,
                Opacity = 0, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
            try
            {
                using (SingleInstanceActivation endpoint = new SingleInstanceActivation(scope, delegate {
                    opened++; SingleInstanceActivation.Restore(window, false);
                }, delegate { refreshed++; }))
                {
                    window.Show(); window.Hide();
                    RequestFromChild(scope, delegate { return opened == 1; });
                    if (!window.IsVisible) throw new InvalidOperationException("Hidden window was not restored");
                    report("PASS second process restores the existing hidden WPF window");
                    window.WindowState = WindowState.Minimized;
                    RequestFromChild(scope, delegate { return opened == 2; });
                    if (window.WindowState != WindowState.Normal) throw new InvalidOperationException("Minimized window was not restored");
                    report("PASS minimized window returns to normal without creating another window");
                    RequestFromChild(scope, delegate { return opened == 3; });
                    report("PASS repeated launches reuse the same visible window");
                    window.Hide();
                    using (Process child = StartChild(scope, true)) WaitForChild(child, delegate { return refreshed == 1; });
                    if (window.IsVisible || opened != 3) throw new InvalidOperationException("Widget refresh opened the app window");
                    report("PASS Windows widget refresh reaches the running app without showing its window");
                    if (SingleInstanceActivation.Request(scope + "-other", 50))
                        throw new InvalidOperationException("An unrelated instance received the request");
                    report("PASS isolated endpoint cannot activate the user's live meter");
                }
                if (SingleInstanceActivation.Request(scope, 50)) throw new InvalidOperationException("Disposed endpoint still accepts requests");
                report("PASS closed endpoint times out without a new UI or modal dialog");

                using (Process child = StartChild(scope))
                {
                    // The receiver is intentionally created after the second launch.
                    Thread.Sleep(120);
                    using (SingleInstanceActivation endpoint = new SingleInstanceActivation(scope, delegate { opened++; }))
                        WaitForChild(child, delegate { return opened == 4; });
                }
                report("PASS second launch waits for the first instance's startup endpoint");
                using (Process child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                    "--tray-startup-test") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
                {
                    if (!child.WaitForExit(6000)) { child.Kill(); child.WaitForExit(2000); throw new InvalidOperationException("Tray startup test timed out"); }
                    if (child.ExitCode != 0) throw new InvalidOperationException("Tray startup showed a native window or skipped initialization");
                }
                report("PASS cold tray startup initializes once and runs without a visible native window");
            }
            finally { window.Close(); }
        }

        private static Process StartChild(string scope, bool refresh = false)
        {
            return Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                (refresh ? "--activation-test-refresh " : "--activation-test-send ") + scope) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
        }

        internal static int CheckTrayStartup()
        {
            Application application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            Window window = new Window { Width = 100, Height = 100, Left = -30000, Top = -30000,
                ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            int initialized = 0, loaded = 0, result = 1;
            window.Loaded += delegate { loaded++; };
            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += delegate {
                timer.Stop();
                IntPtr handle = new WindowInteropHelper(window).Handle;
                if (initialized == 1 && loaded == 0 && !window.IsVisible && handle != IntPtr.Zero && !IsWindowVisible(handle)) result = 0;
                window.Close();
            };
            timer.Start();
            Program.RunMainWindow(application, window, true, delegate { initialized++; });
            return result;
        }

        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);

        private static void RequestFromChild(string scope, Func<bool> received)
        {
            using (Process child = StartChild(scope)) WaitForChild(child, received);
        }

        private static void WaitForChild(Process child, Func<bool> received)
        {
            try
            {
                Stopwatch deadline = Stopwatch.StartNew();
                while ((!child.HasExited || !received()) && deadline.ElapsedMilliseconds < 6500)
                {
                    DispatcherFrame frame = new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                        new Action(delegate { frame.Continue = false; }));
                    Dispatcher.PushFrame(frame);
                    Thread.Sleep(10);
                }
                if (!child.HasExited || child.ExitCode != 0 || !received())
                    throw new InvalidOperationException("Second launch did not deliver a window activation request");
            }
            finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(2000); } }
        }
    }
}
