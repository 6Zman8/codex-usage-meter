using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexUsageMeter
{
    internal static class AccountSwitcherSelfTest
    {
        public static void Run()
        {
            Require(SystemCodexDesktopProcessSource.IsCodexDesktopPath(
                    @"C:\Program Files\WindowsApps\OpenAI.Codex_26.825.6671.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe"),
                "Codex 데스크톱 프로세스를 식별해야 합니다.");
            Require(!SystemCodexDesktopProcessSource.IsCodexDesktopPath(
                    @"C:\Users\example\CodexUsageMeter\bin\CodexUsageMeter.exe"),
                "미터기는 Codex 종료 대상으로 식별하면 안 됩니다.");

            string root = Path.Combine(Path.GetTempPath(), "codex-meter-switch-order-" + Guid.NewGuid().ToString("N"));
            string defaultHome = Path.Combine(root, "default");
            string accountsRoot = Path.Combine(root, "accounts");
            string defaultAuth = Path.Combine(defaultHome, "auth.json");
            try
            {
                WriteAuth(defaultAuth, "account-old", "old");
                WriteAuth(Path.Combine(accountsRoot, "account-1", "auth.json"), "account-old", "old");
                WriteAuth(Path.Combine(accountsRoot, "account-2", "auth.json"), "account-target", "target");

                RecordingLifecycle lifecycle = new RecordingLifecycle(defaultAuth);
                AccountSwitcher switcher = new AccountSwitcher(defaultHome, accountsRoot, lifecycle);
                AccountSwitchResult result = switcher.SwitchTo(2, 1);

                Require(result.Success, "계정 전환이 성공해야 합니다. " + result.Message);
                Require(lifecycle.AuthenticationSeenAtStop == "account-old",
                    "Codex가 완전히 종료되기 전에 인증을 바꾸면 안 됩니다.");
                Require(lifecycle.AuthenticationSeenAtStart == "account-target",
                    "선택 계정 인증을 적용한 뒤 Codex를 다시 실행해야 합니다.");
                Require(ReadAccountId(defaultAuth) == "account-target",
                    "전환 완료 후 기본 Codex 인증이 선택 계정이어야 합니다.");
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        private static void WriteAuth(string path, string accountId, string marker)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string json = "{\"auth_mode\":\"chatgpt\",\"tokens\":{" +
                "\"account_id\":\"" + accountId + "\"," +
                "\"id_token\":\"id-" + marker + "\"," +
                "\"access_token\":\"access-" + marker + "\"," +
                "\"refresh_token\":\"refresh-" + marker + "\"}}";
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        private static string ReadAccountId(string path)
        {
            JavaScriptSerializer json = new JavaScriptSerializer();
            Dictionary<string, object> root = json.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
            Dictionary<string, object> tokens = root["tokens"] as Dictionary<string, object>;
            return tokens["account_id"] as string;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class RecordingLifecycle : ICodexDesktopLifecycle
        {
            private readonly string _authPath;

            public RecordingLifecycle(string authPath)
            {
                _authPath = authPath;
            }

            public string AuthenticationSeenAtStop { get; private set; }
            public string AuthenticationSeenAtStart { get; private set; }

            public bool TryStop(TimeSpan timeout, out string error)
            {
                AuthenticationSeenAtStop = ReadAccountId(_authPath);
                error = null;
                return true;
            }

            public bool TryStart(TimeSpan timeout, out string error)
            {
                AuthenticationSeenAtStart = ReadAccountId(_authPath);
                error = null;
                return true;
            }
        }
    }
}
