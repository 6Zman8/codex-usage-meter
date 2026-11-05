using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace CodexUsageMeter
{
    internal sealed class SingleInstanceActivation : IDisposable
    {
        private const int ActivateMessage = 0x8000 + 73;
        private const int RefreshMessage = 0x8000 + 74;
        private const int InstallUpdateMessage = 0x8000 + 75;
        private static readonly IntPtr MessageOnly = new IntPtr(-3);
        private readonly HwndSource _source;
        private readonly Action _open;
        private readonly Action _refresh;
        private readonly Action _installUpdate;

        internal static string DefaultScope { get { return WindowsIdentity.GetCurrent().User.Value; } }

        internal SingleInstanceActivation(string scope, Action open, Action refresh = null, Action installUpdate = null)
        {
            _open = open;
            _refresh = refresh;
            _installUpdate = installUpdate;
            HwndSourceParameters parameters = new HwndSourceParameters(EndpointName(scope));
            parameters.ParentWindow = MessageOnly;
            parameters.WindowStyle = 0;
            parameters.Width = parameters.Height = 0;
            _source = new HwndSource(parameters);
            _source.AddHook(Receive);
        }

        private static string EndpointName(string scope) { return "CodexUsageMeter.Activation." + scope; }

        internal static bool Request(string scope, int timeoutMilliseconds, bool refreshOnly = false)
        { return Request(scope, timeoutMilliseconds, refreshOnly ? RefreshMessage : ActivateMessage); }

        // Asks the running meter to install the latest release without dialogs; versions without this handler refuse it.
        internal static bool RequestInstallUpdate(string scope, int timeoutMilliseconds)
        { return Request(scope, timeoutMilliseconds, InstallUpdateMessage); }

        private static bool Request(string scope, int timeoutMilliseconds, int message)
        {
            bool refreshOnly = message != ActivateMessage;
            Stopwatch elapsed = Stopwatch.StartNew();
            do
            {
                IntPtr receiver = FindWindowEx(MessageOnly, IntPtr.Zero, null, EndpointName(scope));
                if (receiver != IntPtr.Zero)
                {
                    uint processId;
                    GetWindowThreadProcessId(receiver, out processId);
                    // Transfer foreground permission from the user's shortcut launch to the owner only.
                    if (!refreshOnly) AllowSetForegroundWindow(processId);
                    IntPtr result;
                    if (SendMessageTimeout(receiver, message, IntPtr.Zero, IntPtr.Zero,
                        0x0002, 1000, out result) != IntPtr.Zero && result == new IntPtr(1)) return true;
                }
                if (elapsed.ElapsedMilliseconds >= timeoutMilliseconds) break;
                Thread.Sleep(40);
            } while (true);
            return false;
        }

        private IntPtr Receive(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != ActivateMessage && message != RefreshMessage && message != InstallUpdateMessage) return IntPtr.Zero;
            handled = true;
            Action action = message == ActivateMessage ? _open : message == RefreshMessage ? _refresh : _installUpdate;
            if (action == null) return IntPtr.Zero;
            action();
            return new IntPtr(1);
        }
        internal static void Restore(Window window, bool activate)
        {
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            if (activate) window.Activate();
        }
        public void Dispose() { _source.RemoveHook(Receive); _source.Dispose(); }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, int message, IntPtr wParam, IntPtr lParam,
            uint flags, uint timeout, out IntPtr result);
    }
}
