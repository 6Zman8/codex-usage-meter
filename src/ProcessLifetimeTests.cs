using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CodexUsageMeter
{
    internal static class ProcessLifetimeTests
    {
        internal static int Host(string directory)
        {
            try
            {
            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            using (Process normal = Process.Start(new ProcessStartInfo(exe, "--lifetime-test-child") { UseShellExecute = false, CreateNoWindow = true }))
            using (Process independent = IndependentProcess.Start(exe, "--lifetime-test-child"))
                File.WriteAllText(Path.Combine(directory, "children.txt"), normal.Id + "," + independent.Id);
            Thread.Sleep(30000);
            return 0;
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "host-error.txt"), ex.ToString()); return 1; }
        }

        internal static void Run(Action<string> report)
        {
            string directory = Path.Combine(Path.GetTempPath(), "meter-lifetime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            PROCESS_INFORMATION pi = new PROCESS_INFORMATION();
            Process normal = null, independent = null;
            try
            {
                if (job == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                STARTUPINFO si = new STARTUPINFO(); si.cb = Marshal.SizeOf(si);
                if (!CreateProcess(exe, new StringBuilder("\"" + exe + "\" --lifetime-test-host \"" + directory + "\""),
                    IntPtr.Zero, IntPtr.Zero, false, 0x08000004, IntPtr.Zero, directory, ref si, out pi))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                if (!AssignProcessToJobObject(job, pi.process)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                ResumeThread(pi.thread);
                string marker = Path.Combine(directory, "children.txt");
                Stopwatch wait = Stopwatch.StartNew();
                while (!File.Exists(marker) && !File.Exists(Path.Combine(directory, "host-error.txt")) && wait.ElapsedMilliseconds < 10000) Thread.Sleep(50);
                if (!File.Exists(marker)) throw new InvalidOperationException(File.Exists(Path.Combine(directory, "host-error.txt"))
                    ? File.ReadAllText(Path.Combine(directory, "host-error.txt")) : "Isolated host failed to report children");
                string[] ids = File.ReadAllText(marker).Split(',');
                normal = Process.GetProcessById(Int32.Parse(ids[0]));
                independent = Process.GetProcessById(Int32.Parse(ids[1]));
                // Retain handles before termination; do not rely on reusable numeric PIDs.
                IntPtr normalHandle = normal.Handle, independentHandle = independent.Handle;
                if (!TerminateJobObject(job, 0)) throw new System.ComponentModel.Win32Exception();
                if (!normal.WaitForExit(5000)) throw new InvalidOperationException("Control child did not exit with its parent job");
                report("PASS baseline reproduced: an ordinary launched child dies when its parent job is terminated");
                if (independent.WaitForExit(750)) throw new InvalidOperationException("Meter-style independent child died with the parent job");
                bool contained;
                if (!IsProcessInJob(independentHandle, job, out contained) || contained)
                    throw new InvalidOperationException("Independent child still belongs to the parent job");
                report("PASS independent child survives actual parent job termination");
            }
            finally
            {
                if (job != IntPtr.Zero) { TerminateJobObject(job, 0); CloseHandle(job); }
                if (pi.process != IntPtr.Zero) { TerminateProcess(pi.process, 0); WaitForSingleObject(pi.process, 3000); CloseHandle(pi.process); }
                if (pi.thread != IntPtr.Zero) CloseHandle(pi.thread);
                foreach (Process process in new Process[] { normal, independent })
                    if (process != null) { try { if (!process.HasExited) { process.Kill(); process.WaitForExit(3000); } } finally { process.Dispose(); } }
                for (int attempt = 0; attempt < 20 && Directory.Exists(directory); attempt++)
                {
                    try { Directory.Delete(directory, true); }
                    catch (IOException) { Thread.Sleep(100); }
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
        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION { public IntPtr process, thread; public int pid, tid; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(string app, StringBuilder command, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr environment, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
        [DllImport("kernel32.dll")] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
    }
}
