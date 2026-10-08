using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexUsageMeter
{
    internal sealed class WindowsWidgetBridge
    {
        private readonly string _root;
        internal WindowsWidgetBridge(string root) { _root = root; }
        internal static string DefaultRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageMeter", "windows-widget"); } }

        internal static string BuildJson(IEnumerable<AccountState> accounts, bool running, DateTime writtenAtUtc)
        {
            List<object> items = new List<object>();
            foreach (AccountState account in accounts)
            {
                AccountSnapshot snapshot = account.LastSnapshot;
                string status = snapshot == null ? "waiting" : !String.IsNullOrEmpty(snapshot.Error) ? "error" :
                    !snapshot.IsAuthenticated ? "unlinked" : snapshot.RateLimitsObservedAtUtc == default(DateTime) ? "waiting" : "ok";
                items.Add(new Dictionary<string, object> {
                    { "number", account.Number }, { "label", "계정 " + account.Number },
                    { "plan", snapshot == null ? String.Empty : snapshot.PlanType ?? String.Empty },
                    { "status", status },
                    { "observedAtUtc", status == "ok" ? Utc(snapshot.RateLimitsObservedAtUtc) : null },
                    { "primary", status == "ok" ? Limit(snapshot.Primary) : null },
                    { "secondary", status == "ok" ? Limit(snapshot.Secondary) : null }
                });
            }
            return new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                { "schemaVersion", 1 }, { "writtenAtUtc", Utc(writtenAtUtc) }, { "running", running }, { "accounts", items }
            });
        }

        private static object Limit(RateWindow window)
        {
            if (window == null || Double.IsNaN(window.RemainingPercent) || Double.IsInfinity(window.RemainingPercent)) return null;
            return new Dictionary<string, object> {
                { "remainingPercent", Math.Max(0, Math.Min(100, window.RemainingPercent)) },
                { "durationMinutes", window.DurationMinutes },
                { "resetsAtUtc", window.ResetsAt.HasValue ? Utc(window.ResetsAt.Value) : null }
            };
        }

        private static string Utc(DateTime value) { return value.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture); }

        internal void Publish(IEnumerable<AccountState> accounts, bool running)
        {
            Directory.CreateDirectory(_root);
            WriteAtomic(Path.Combine(_root, "snapshot.json"), BuildJson(accounts, running, DateTime.UtcNow));
        }

        internal void SetApplicationPath(string executable)
        {
            Directory.CreateDirectory(_root);
            WriteAtomic(Path.Combine(_root, "app-path.txt"), Path.GetFullPath(executable));
        }

        private static void WriteAtomic(string path, string value)
        {
            string temporary = path + ".new-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, value, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
    }
}
