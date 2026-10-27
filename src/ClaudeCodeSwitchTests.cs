using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexUsageMeter
{
    // Claude Code switching in an isolated temporary home folder; the real user profile is never read or written.
    internal static class ClaudeCodeSwitchTests
    {
        private const string AccountA = "11111111-1111-4111-8111-111111111111", AccountB = "22222222-2222-4222-8222-222222222222";
        private const string Organization = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";

        private sealed class Journal : IAccountSwitchJournal
        {
            internal readonly List<string> Events = new List<string>();
            public void Write(string eventName, string details) { Events.Add(eventName); }
        }

        internal static void Run(Action<string> report)
        {
            string root = Path.Combine(Path.GetTempPath(), "codex-meter-claude-switch-" + Guid.NewGuid().ToString("N"));
            string realProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            try
            {
                string home = Path.Combine(root, "home");
                Require(!String.Equals(Path.GetFullPath(home).TrimEnd('\\'), Path.GetFullPath(realProfile).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase),
                    "Test home must be isolated from the real profile.");
                Directory.CreateDirectory(home);
                List<KeyValuePair<int, string>> slots = new List<KeyValuePair<int, string>> {
                    new KeyValuePair<int, string>(1, Slot(root, 1, AccountA, "a@example.test", "access-a", "refresh-a", 1000)),
                    new KeyValuePair<int, string>(2, Slot(root, 2, AccountB, "b@example.test", "access-b", "refresh-b", 2000)) };
                Journal journal = new Journal();
                ClaudeCodeSwitcher switcher = new ClaudeCodeSwitcher(home, journal);
                string credentials = Path.Combine(home, ".claude", ".credentials.json"), config = Path.Combine(home, ".claude.json");

                // Not installed: like Codex's switch without Codex, the switch fails and nothing is created.
                AccountSwitchResult missing = switcher.SwitchTo(2, slots);
                Require(!missing.Success && missing.Message.Contains("Claude Code를 찾지 못해") && !Directory.Exists(Path.Combine(home, ".claude")) &&
                    !File.Exists(config), "A missing Claude Code was not reported or files were created.");
                report("PASS Claude switch without Claude Code changes nothing and reports it");

                // Installed with its own sign-in for account A, an MCP sign-in and an oauthAccount block.
                Directory.CreateDirectory(Path.GetDirectoryName(credentials));
                File.WriteAllText(credentials, Serialize(new Dictionary<string, object> { { "mcpOAuth", new Dictionary<string, object> { { "server", "keep" } } },
                    { "claudeAiOauth", OAuth("access-a", "refresh-a", 1000) } }));
                File.WriteAllText(config, Serialize(new Dictionary<string, object> { { "numStartups", 7 }, { "theme", "dark" },
                    { "oauthAccount", new Dictionary<string, object> { { "accountUuid", AccountA }, { "emailAddress", "a@example.test" },
                        { "organizationUuid", Organization }, { "displayName", "A" } } } }));
                Require(switcher.IsInstalled && switcher.DetectActiveAccountNumber(slots) == 1, "Claude Code's current account was not detected.");

                AccountSwitchResult switched = switcher.SwitchTo(2, slots);
                Dictionary<string, object> stored = Read(credentials), storedConfig = Read(config);
                Dictionary<string, object> oauth = (Dictionary<string, object>)stored["claudeAiOauth"], account = (Dictionary<string, object>)storedConfig["oauthAccount"];
                Require(switched.Success && !switched.AlreadyActive && switched.Message.Contains("Claude Code") && (string)oauth["accessToken"] == "access-b" &&
                    (string)oauth["refreshToken"] == "refresh-b" && ((Dictionary<string, object>)stored["mcpOAuth"])["server"] as string == "keep",
                    "The slot's sign-in was not applied or other Claude Code sign-ins were lost.");
                Require((string)account["accountUuid"] == AccountB && (string)account["emailAddress"] == "b@example.test" && !account.ContainsKey("displayName") &&
                    (int)storedConfig["numStartups"] == 7 && (string)storedConfig["theme"] == "dark", "oauthAccount was not replaced or other settings changed.");
                Require(switcher.DetectActiveAccountNumber(slots) == 2 && Directory.GetFiles(home, "*.tmp", SearchOption.AllDirectories).Length == 0 &&
                    journal.Events.Contains("claude-switch-complete"), "Current-account indication, atomic writes or journal failed.");
                AccountSwitchResult again = switcher.SwitchTo(2, slots);
                Require(again.Success && again.AlreadyActive, "Re-applying the current account was not reported as 다시 연결.");
                report("PASS Claude switch writes only Claude Code's sign-in and oauthAccount, keeps other entries and marks the current account");

                // A slot whose credential text is not bound to a verified owner is never applied.
                File.AppendAllText(Path.Combine(slots[0].Value, ".credentials.json"), " ");
                string before = File.ReadAllText(credentials);
                AccountSwitchResult unbound = switcher.SwitchTo(1, slots);
                Require(!unbound.Success && File.ReadAllText(credentials) == before, "An unverified slot sign-in was applied.");
                Slot(root, 1, AccountA, "a@example.test", "access-a", "refresh-a", 1000);
                Require(!switcher.SwitchTo(3, slots).Success && File.ReadAllText(credentials) == before, "An unknown slot was switched.");

                // A failure after the credential write restores Claude Code's original files.
                File.WriteAllText(config, "{ not json");
                AccountSwitchResult failed = switcher.SwitchTo(1, slots);
                Require(!failed.Success && failed.RolledBack && failed.Message.Contains("복원") && File.ReadAllText(credentials) == before &&
                    File.ReadAllText(config) == "{ not json", "A failed switch did not restore Claude Code's sign-in.");
                // Without oauthAccount the config is left alone and the token identifies the current account.
                File.WriteAllText(config, "{\"theme\":\"light\"}");
                Require(switcher.SwitchTo(1, slots).Success && File.ReadAllText(config) == "{\"theme\":\"light\"}" &&
                    switcher.DetectActiveAccountNumber(slots) == 1, "A config without oauthAccount was changed or detection failed.");
                report("PASS Claude switch rejects unverified/unknown slots and restores the original sign-in on failure");

                // Shared rotating sign-in: Claude Code's newer renewal reaches the slot, and the slot's renewal reaches Claude Code.
                File.WriteAllText(config, Serialize(new Dictionary<string, object> { { "oauthAccount", new Dictionary<string, object> {
                    { "accountUuid", AccountA }, { "organizationUuid", Organization } } } }));
                File.WriteAllText(credentials, Serialize(new Dictionary<string, object> { { "mcpOAuth", "keep" },
                    { "claudeAiOauth", OAuth("access-a2", "refresh-a2", 5000) } }));
                string slotPath = Path.Combine(slots[0].Value, ".credentials.json");
                ClaudeCodeSwitcher.PollShare share = switcher.BeforePoll(slots);
                Dictionary<string, object> slotOAuth = (Dictionary<string, object>)Read(slotPath)["claudeAiOauth"];
                // The copied credential is not trusted until the slot reads its profile again, so nothing is shared yet.
                Require(share == null && (string)slotOAuth["refreshToken"] == "refresh-a2" &&
                    ClaudeUsageAccount.BoundOwner(slots[0].Value, File.ReadAllText(slotPath)) == null, "Claude Code's renewal was not taken into the slot for re-verification.");
                ClaudeUsageAccount.WriteBinding(slots[0].Value, File.ReadAllText(slotPath), AccountA, Organization, "a@example.test");
                share = switcher.BeforePoll(slots);
                Require(share != null && share.Number == 1 && share.Refresh == "refresh-a2", "The verified shared sign-in was not tracked.");
                // A meter renewal during the poll.
                string renewed = Serialize(new Dictionary<string, object> { { "claudeAiOauth", OAuth("access-a3", "refresh-a3", 9000) } });
                ClaudeUsageAccount.AtomicWrite(Path.Combine(slots[0].Value, ".credentials.json"), renewed);
                ClaudeUsageAccount.WriteBinding(slots[0].Value, renewed, AccountA, Organization, "a@example.test");
                switcher.AfterPoll(share, slots);
                stored = Read(credentials);
                Require((string)((Dictionary<string, object>)stored["claudeAiOauth"])["refreshToken"] == "refresh-a3" && (string)stored["mcpOAuth"] == "keep",
                    "The meter's renewal was not handed to Claude Code.");
                // When Claude Code changed its own sign-in meanwhile, it is never overwritten.
                share = switcher.BeforePoll(slots);
                File.WriteAllText(credentials, Serialize(new Dictionary<string, object> { { "claudeAiOauth", OAuth("other", "other-refresh", 100) } }));
                string own = File.ReadAllText(credentials);
                renewed = Serialize(new Dictionary<string, object> { { "claudeAiOauth", OAuth("access-a4", "refresh-a4", 12000) } });
                ClaudeUsageAccount.AtomicWrite(Path.Combine(slots[0].Value, ".credentials.json"), renewed);
                ClaudeUsageAccount.WriteBinding(slots[0].Value, renewed, AccountA, Organization, "a@example.test");
                switcher.AfterPoll(share, slots);
                Require(File.ReadAllText(credentials) == own, "A Claude Code sign-in changed by Claude Code itself was overwritten.");
                report("PASS Claude Code and the switched slot share renewals of one rotating sign-in without overwriting other sign-ins");
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        private static string Slot(string root, int number, string account, string email, string access, string refresh, double expires)
        {
            string slot = Path.Combine(root, "accounts", "claude", "account-" + number);
            Directory.CreateDirectory(slot);
            string raw = Serialize(new Dictionary<string, object> { { "claudeAiOauth", OAuth(access, refresh, expires) } });
            File.WriteAllText(Path.Combine(slot, ".credentials.json"), raw, new UTF8Encoding(false));
            ClaudeUsageAccount.WriteBinding(slot, raw, account, Organization, email);
            return slot;
        }

        private static Dictionary<string, object> OAuth(string access, string refresh, double expires)
        {
            return new Dictionary<string, object> { { "accessToken", access }, { "refreshToken", refresh }, { "expiresAt", expires },
                { "scopes", new[] { "user:inference", "user:profile" } }, { "subscriptionType", "max" } };
        }

        private static string Serialize(object value) { return new JavaScriptSerializer().Serialize(value); }
        private static Dictionary<string, object> Read(string path)
        { return (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)); }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
