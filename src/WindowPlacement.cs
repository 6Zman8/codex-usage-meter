using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CodexUsageMeter
{
    // Keep monitor and window coordinates in the same native DPI context.
    internal static class WindowPlacement
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeRect
        {
            public int Left, Top, Right, Bottom;
            internal Rect Bounds { get { return new Rect(Left, Top, Right - Left, Bottom - Top); } }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor, Work;
            public uint Flags;
        }

        [DllImport("user32.dll")] internal static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] internal static extern IntPtr GetThreadDpiAwarenessContext();
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        private sealed class DpiScope : IDisposable
        {
            private readonly IntPtr _previous;
            internal DpiScope(IntPtr window)
            {
                _previous = SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(window));
                if (_previous == IntPtr.Zero) throw new Win32Exception();
            }
            public void Dispose() { SetThreadDpiAwarenessContext(_previous); }
        }

        internal static Rect Bounds(Window window)
        {
            IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
            using (new DpiScope(handle))
            {
                NativeRect bounds;
                if (!GetWindowRect(handle, out bounds)) throw new Win32Exception();
                return bounds.Bounds;
            }
        }

        internal static Rect WorkArea(Window window)
        {
            IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
            using (new DpiScope(handle))
            {
                MonitorInfo info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info)) throw new Win32Exception();
                return info.Work.Bounds;
            }
        }

        internal static void SetBounds(Window window, Rect pixels)
        {
            IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
            using (new DpiScope(handle))
            {
                // No activation, z-order change or visibility change; hidden tests stay hidden.
                if (!SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(pixels.Left), (int)Math.Round(pixels.Top),
                    (int)Math.Round(pixels.Width), (int)Math.Round(pixels.Height), 0x0014))
                    throw new Win32Exception();
            }
        }

        internal static void Maximize(Window window) { SetBounds(window, WorkArea(window)); }
    }
}
