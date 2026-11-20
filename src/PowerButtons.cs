using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexUsageMeter
{
    internal enum HoldPhase { Idle, Holding, Draining, Armed, Fired }

    // A full gauge commits the press. The action waits for the release,
    // because the release input itself would wake a display that was already off.
    internal sealed class HoldToConfirm
    {
        private const double Epsilon = 1e-9;
        private readonly double _holdMs, _drainMs;
        private double _start, _from;

        internal HoldToConfirm(double holdMs) { _holdMs = holdMs; _drainMs = holdMs / 4.0; }

        internal HoldPhase Phase { get; private set; }
        internal double Progress { get; private set; }

        internal void Press(double now)
        {
            Advance(now);
            if (Phase == HoldPhase.Idle || Phase == HoldPhase.Draining) Begin(HoldPhase.Holding, now, Progress);
        }

        // The pointer left the button or the press was lost; a full gauge stays committed.
        internal void Cancel(double time) { Stop(time); }

        // True when this release runs the action.
        internal bool Release(double time)
        {
            Stop(time);
            if (Phase != HoldPhase.Armed) return false;
            Phase = HoldPhase.Fired;
            return true;
        }

        internal void Advance(double now)
        {
            if (Phase == HoldPhase.Holding)
            {
                Progress = HeldAt(now);
                if (Progress >= 1.0 - Epsilon) { Progress = 1.0; Phase = HoldPhase.Armed; }
            }
            else if (Phase == HoldPhase.Draining)
            {
                Progress = Math.Max(0.0, _from - Math.Max(0.0, now - _start) / _drainMs);
                if (Progress <= Epsilon) { Progress = 0.0; Phase = HoldPhase.Idle; }
            }
        }

        internal void Reset() { Phase = HoldPhase.Idle; Progress = 0.0; }

        // Judged at the time the press really ended, even if a later timer tick already showed a full gauge.
        private void Stop(double time)
        {
            if (Phase != HoldPhase.Holding && Phase != HoldPhase.Armed) return;
            double held = HeldAt(time);
            if (held >= 1.0 - Epsilon) { Progress = 1.0; Phase = HoldPhase.Armed; }
            else Begin(HoldPhase.Draining, time, held);
        }

        private double HeldAt(double time) { return Math.Min(1.0, _from + Math.Max(0.0, time - _start) / _holdMs); }

        private void Begin(HoldPhase phase, double time, double progress) { Phase = phase; _start = time; _from = progress; Progress = progress; }
    }

    // Drives one PowerButton-styled button: hold to fill the gauge, release a full gauge to run the action.
    internal sealed class PowerHoldButton
    {
        internal const double HoldMilliseconds = 1200.0;
        private const int VkLeftButton = 0x01, VkRightButton = 0x02, VkSpace = 0x20;
        private static readonly Brush IdleBorder = Solid(0xFF, 0x34, 0x34, 0x34), HoverBorder = Solid(0xFF, 0x5A, 0x5A, 0x5A),
            IdleBackground = Solid(0xFF, 0x1B, 0x1B, 0x1B), HoverBackground = Solid(0xFF, 0x22, 0x22, 0x24),
            TextBrush = Solid(0xFF, 0xF4, 0xF4, 0xF5), MutedBrush = Solid(0xFF, 0xA1, 0xA1, 0xAA);
        private readonly HoldToConfirm _hold = new HoldToConfirm(HoldMilliseconds);
        private readonly Button _button;
        private readonly TextBlock _label, _hint;
        private readonly bool _vertical;
        private readonly string _idleLabel, _firedLabel, _armedHint;
        private readonly Brush _tone, _softFill, _strongFill;
        private readonly Action _action;
        private readonly Border _frame, _fill;
        private readonly Grid _surface;
        private readonly DispatcherTimer _timer;
        private int _pressKey;
        private bool _awaitingRelease;
        // Input event timestamps use the same millisecond tick count.
        internal Func<double> Now = () => Environment.TickCount;
        internal Func<int, bool> IsKeyDown = key => (GetAsyncKeyState(key) & 0x8000) != 0;
        internal Action<TimeSpan, Action> After = RunLater;

        internal static PowerHoldButton ScreenOff(Button button, TextBlock label, TextBlock hint, bool vertical, Action action)
        {
            return new PowerHoldButton(button, label, hint, vertical, Color.FromRgb(0x38, 0xBD, 0xF8), "화면 끄는 중…", "손을 떼면 꺼집니다", action);
        }

        internal static PowerHoldButton Sleep(Button button, TextBlock label, TextBlock hint, bool vertical, Action action)
        {
            return new PowerHoldButton(button, label, hint, vertical, Color.FromRgb(0xFB, 0xBF, 0x24), "절전 진입 중…", "손을 떼면 절전합니다", action);
        }

        private PowerHoldButton(Button button, TextBlock label, TextBlock hint, bool vertical, Color tone, string firedLabel, string armedHint, Action action)
        {
            _button = button; _label = label; _hint = hint; _vertical = vertical; _action = action;
            _idleLabel = label.Text; _firedLabel = firedLabel; _armedHint = armedHint;
            _tone = Solid(0xFF, tone.R, tone.G, tone.B);
            _softFill = Solid(61, tone.R, tone.G, tone.B);
            _strongFill = Solid(87, tone.R, tone.G, tone.B);
            button.ApplyTemplate();
            _frame = (Border)button.Template.FindName("Frame", button);
            _surface = (Grid)button.Template.FindName("Surface", button);
            _fill = (Border)button.Template.FindName("Fill", button);
            double radius = vertical ? 16.0 : 12.0;
            _frame.CornerRadius = new CornerRadius(radius);
            _fill.BorderBrush = _tone;
            _fill.BorderThickness = vertical ? new Thickness(0, 3, 0, 0) : new Thickness(0, 0, 3, 0);
            _fill.HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            _fill.VerticalAlignment = vertical ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
            _surface.SizeChanged += delegate
            {
                _surface.Clip = new RectangleGeometry(new Rect(_surface.RenderSize), radius - 1.0, radius - 1.0);
                Refresh();
            };
            // Below input priority, so a pending release is handled before the next tick.
            _timer = new DispatcherTimer(DispatcherPriority.Background, button.Dispatcher) { Interval = TimeSpan.FromMilliseconds(15) };
            _timer.Tick += delegate { Tick(); };
            // Only the input that started the press may end it.
            button.PreviewMouseLeftButtonDown += delegate { if (!button.IsPressed) _pressKey = SystemParameters.SwapButtons ? VkRightButton : VkLeftButton; };
            button.PreviewKeyDown += (sender, e) => { if (e.Key == Key.Space && !e.IsRepeat && !button.IsPressed) _pressKey = VkSpace; };
            DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(Button))
                .AddValueChanged(button, delegate { PressedChanged(button.IsPressed); });
            // ButtonBase keeps mouse capture while pressed, so the release arrives here even outside the button.
            button.PreviewMouseLeftButtonUp += (sender, e) => { if (_pressKey != VkSpace) Released(e.Timestamp); };
            button.PreviewKeyUp += (sender, e) => { if (e.Key == Key.Space && _pressKey == VkSpace) Released(e.Timestamp); };
            button.LostMouseCapture += (sender, e) => Released(e.Timestamp);
            button.MouseEnter += delegate { Refresh(); };
            button.MouseLeave += delegate { Refresh(); };
            Refresh();
        }

        internal HoldPhase Phase { get { return _hold.Phase; } }

        internal void PressedChanged(bool pressed)
        {
            if (pressed) _hold.Press(Now()); else _hold.Cancel(Now());
            Update();
        }

        internal void Released(double time)
        {
            // A committed press whose key or button is still physically down (capture lost, window switched)
            // waits for the real release, which would otherwise wake the display right after it turned off.
            if (_hold.Phase == HoldPhase.Armed && _pressKey != 0 && IsKeyDown(_pressKey)) { _awaitingRelease = true; Update(); return; }
            _awaitingRelease = false;
            bool run = _hold.Release(time);
            Update();
            if (!run) return;
            After(TimeSpan.FromMilliseconds(600), _action);
            After(TimeSpan.FromMilliseconds(2000), delegate { _hold.Reset(); Update(); });
        }

        internal void Tick()
        {
            if (!_awaitingRelease) { _hold.Advance(Now()); Update(); }
            else if (!IsKeyDown(_pressKey)) Released(Now());
        }

        private void Update()
        {
            if (_awaitingRelease || _hold.Phase == HoldPhase.Holding || _hold.Phase == HoldPhase.Draining) _timer.Start(); else _timer.Stop();
            Refresh();
        }

        private void Refresh()
        {
            HoldPhase phase = _hold.Phase;
            bool committed = phase == HoldPhase.Armed || phase == HoldPhase.Fired;
            _frame.BorderBrush = committed || phase == HoldPhase.Holding ? _tone : _button.IsMouseOver ? HoverBorder : IdleBorder;
            _frame.Background = _button.IsMouseOver && phase == HoldPhase.Idle ? HoverBackground : IdleBackground;
            _fill.Background = committed ? _strongFill : _softFill;
            _fill.Visibility = _hold.Progress > 0.0 ? Visibility.Visible : Visibility.Hidden;
            if (_vertical) _fill.Height = _hold.Progress * _surface.ActualHeight;
            else _fill.Width = _hold.Progress * _surface.ActualWidth;
            _label.Text = phase == HoldPhase.Fired ? _firedLabel : _idleLabel;
            _hint.Text = committed ? _armedHint : phase == HoldPhase.Holding ? "계속 누르세요" : "꾹 누르기";
            _hint.Foreground = committed ? TextBrush : MutedBrush;
        }

        internal static void RunLater(TimeSpan delay, Action action)
        {
            DispatcherTimer timer = new DispatcherTimer { Interval = delay };
            timer.Tick += delegate { timer.Stop(); action(); };
            timer.Start();
        }

        private static Brush Solid(byte a, byte r, byte g, byte b)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    }

    internal static class PowerActions
    {
        private const int WmSysCommand = 0x0112, ScMonitorPower = 0xF170, MonitorOff = 2;
        private const uint TokenAdjustPrivileges = 0x0020, TokenQuery = 0x0008, PrivilegeEnabled = 0x0002;

        // DefWindowProc carries out the predefined system command, without WPF's window procedure in between.
        internal static void TurnOffScreen(Window window)
        {
            DefWindowProc(new WindowInteropHelper(window).Handle, WmSysCommand, (IntPtr)ScMonitorPower, (IntPtr)MonitorOff);
        }

        // Sleep (S3), not hibernation. Returns only after the PC resumes, so callers run it off the UI thread.
        internal static void Sleep()
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out token)) throw new Win32Exception();
            try
            {
                TokenPrivilege privilege = new TokenPrivilege { Count = 1, Attributes = PrivilegeEnabled };
                if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out privilege.Luid)) throw new Win32Exception();
                if (!AdjustTokenPrivileges(token, false, ref privilege, 0, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
                // Success without the privilege being assigned still reports ERROR_NOT_ALL_ASSIGNED.
                int error = Marshal.GetLastWin32Error();
                if (error != 0) throw new Win32Exception(error);
            }
            finally { CloseHandle(token); }
            if (!SetSuspendState(false, false, false)) throw new Win32Exception();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct TokenPrivilege { public int Count; public long Luid; public uint Attributes; }

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern IntPtr DefWindowProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValue(string system, string name, out long luid);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivilege state, int length, IntPtr previous, IntPtr returnLength);
        [DllImport("powrprof.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SetSuspendState([MarshalAs(UnmanagedType.I1)] bool hibernate, [MarshalAs(UnmanagedType.I1)] bool force, [MarshalAs(UnmanagedType.I1)] bool disableWakeEvent);
    }
}
