using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CodexUsageMeterUpdater
{
    internal static class Program
    {
        private const int MoveFileDelayUntilReboot = 0x4;

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length < 4) return 2;
            int oldProcessId;
            if (!Int32.TryParse(args[0], out oldProcessId)) return 2;
            string stagedPath = Path.GetFullPath(args[1]);
            string targetPath = Path.GetFullPath(args[2]);
            string expectedHash = args[3];
            bool noRestart = args.Length > 4 && String.Equals(args[4], "--no-restart", StringComparison.OrdinalIgnoreCase);
            string newPath = targetPath + ".new";
            string backupPath = targetPath + ".old";

            try
            {
                WaitForExit(oldProcessId);
                if (!File.Exists(stagedPath)) throw new FileNotFoundException("다운로드한 업데이트 파일이 없습니다.", stagedPath);
                if (!String.Equals(ComputeSha256(stagedPath), expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("업데이트 파일의 SHA-256 검증에 실패했습니다.");
                }

                if (File.Exists(newPath)) File.Delete(newPath);
                if (File.Exists(backupPath)) File.Delete(backupPath);
                File.Copy(stagedPath, newPath, true);
                File.Replace(newPath, targetPath, backupPath, true);

                if (noRestart)
                {
                    File.Delete(backupPath);
                    File.Delete(stagedPath);
                    return 0;
                }

                Process updated = CodexUsageMeter.IndependentProcess.Start(targetPath, String.Empty);
                if (updated == null) throw new InvalidOperationException("새 버전을 시작하지 못했습니다.");
                Thread.Sleep(5000);
                updated.Refresh();
                if (updated.HasExited)
                {
                    throw new InvalidOperationException("새 버전이 시작 직후 종료되어 기존 버전으로 복구합니다.");
                }

                File.Delete(backupPath);
                File.Delete(stagedPath);
                MoveFileEx(Process.GetCurrentProcess().MainModule.FileName, null, MoveFileDelayUntilReboot);
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    if (File.Exists(backupPath))
                    {
                        if (File.Exists(targetPath)) File.Delete(targetPath);
                        File.Move(backupPath, targetPath);
                        if (!noRestart) CodexUsageMeter.IndependentProcess.Start(targetPath, String.Empty);
                    }
                }
                catch { }
                if (!noRestart)
                {
                    MessageBox.Show("업데이트를 적용하지 못했습니다. 기존 실행 파일은 복구했습니다.\n\n" + ex.Message,
                        "Codex Meter 업데이트", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return 1;
            }
            finally
            {
                try { if (File.Exists(newPath)) File.Delete(newPath); } catch { }
            }
        }

        private static void WaitForExit(int processId)
        {
            if (processId <= 0) return;
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    if (!process.WaitForExit(30000)) throw new TimeoutException("기존 프로그램이 30초 안에 종료되지 않았습니다.");
                }
            }
            catch (ArgumentException) { }
        }

        private static string ComputeSha256(string path)
        {
            using (SHA256 hash = SHA256.Create())
            using (FileStream input = File.OpenRead(path))
            {
                byte[] bytes = hash.ComputeHash(input);
                StringBuilder text = new StringBuilder(bytes.Length * 2);
                foreach (byte value in bytes) text.Append(value.ToString("x2"));
                return text.ToString();
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);
    }
}
