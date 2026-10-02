using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexUsageMeter
{
    internal static class IndependentProcess
    {
        internal static bool NeedsIsolation
        {
            get
            {
                using (Process self = Process.GetCurrentProcess())
                {
                    int shellId;
                    IntPtr shellWindow = GetShellWindow();
                    if (shellWindow == IntPtr.Zero) return true;
                    GetWindowThreadProcessId(shellWindow, out shellId);
                    using (SafeWaitHandle snapshot = CreateToolhelp32Snapshot(2, 0))
                    {
                        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                        PROCESSENTRY32 entry = new PROCESSENTRY32(); entry.size = (uint)Marshal.SizeOf(entry);
                        if (!Process32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        do { if (entry.pid == self.Id) return entry.parent != shellId; }
                        while (Process32Next(snapshot, ref entry));
                    }
                    throw new InvalidOperationException("미터기의 상위 실행 상태를 확인하지 못했습니다.");
                }
            }
        }

        internal static Process Start(string executable, string arguments)
        {
            // A normal Process.Start inherits a Codex terminal's job. Excluding the meter's
            // PID cannot save it when that job closes. Windows' explicit shell parent gives
            // this ordinary user application the Explorer lifetime instead; no elevation.
            int shellId;
            if (GetShellWindow() == IntPtr.Zero) throw new InvalidOperationException("Windows 바탕 화면 실행 환경을 찾지 못했습니다.");
            GetWindowThreadProcessId(GetShellWindow(), out shellId);
            using (Process shell = Process.GetProcessById(shellId))
            using (Process self = Process.GetCurrentProcess())
            {
                if (shell.SessionId != self.SessionId || !String.Equals(shell.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("같은 Windows 세션의 탐색기를 확인하지 못했습니다.");
            }
            using (SafeWaitHandle parent = OpenProcess(0x0080 | 0x1000, false, shellId))
            {
                if (parent.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                // Explorer may itself belong to an OS job. What matters is inheriting the
                // shell's lifetime rather than this caller's additional terminal/job chain.
                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                IntPtr list = Marshal.AllocHGlobal(size);
                IntPtr parentValue = Marshal.AllocHGlobal(IntPtr.Size);
                bool initialized = false;
                try
                {
                    if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    initialized = true;
                    Marshal.WriteIntPtr(parentValue, parent.DangerousGetHandle());
                    if (!UpdateProcThreadAttribute(list, 0, new IntPtr(0x00020000), parentValue, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    STARTUPINFOEX si = new STARTUPINFOEX();
                    si.startup.cb = Marshal.SizeOf(si);
                    si.startup.flags = 1; si.startup.show = 4; // SW_SHOWNOACTIVATE
                    si.attributes = list;
                    PROCESS_INFORMATION pi;
                    if (!CreateProcess(Path.GetFullPath(executable), new StringBuilder("\"" + executable + "\" " + arguments),
                        IntPtr.Zero, IntPtr.Zero, false, 0x00080000 | 0x08000000, IntPtr.Zero,
                        Path.GetDirectoryName(Path.GetFullPath(executable)), ref si, out pi))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    try
                    {
                        Process child = Process.GetProcessById(pi.pid);
                        try
                        {
                            // Retain the actual process before closing the creation handle;
                            // a fast exit must not let a reused PID pass the updater check.
                            if (child.Handle == IntPtr.Zero) throw new InvalidOperationException("실행한 프로세스를 확인하지 못했습니다.");
                            return child;
                        }
                        catch { child.Dispose(); throw; }
                    }
                    finally { CloseHandle(pi.process); CloseHandle(pi.thread); }
                }
                finally
                {
                    if (initialized) DeleteProcThreadAttributeList(list);
                    Marshal.FreeHGlobal(list);
                    Marshal.FreeHGlobal(parentValue);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb; public string reserved, desktop, title;
            public int x, y, xSize, ySize, xCount, yCount, fill, flags;
            public short show, reserved2; public IntPtr reservedPtr, input, output, error;
        }
        [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX { public STARTUPINFO startup; public IntPtr attributes; }
        [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr process, thread; public int pid, tid; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint size, usage, pid; public IntPtr heap; public uint module, threads, parent;
            public int priority; public uint flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string file;
        }
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeWaitHandle OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeWaitHandle CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32First(SafeWaitHandle snapshot, ref PROCESSENTRY32 entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32Next(SafeWaitHandle snapshot, ref PROCESSENTRY32 entry);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string app, StringBuilder command, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr environment, string cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
