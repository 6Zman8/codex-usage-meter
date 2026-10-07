using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUsageMeter
{
    internal static class UsageHistoryRegressionTests
    {
        internal static void Run(Action<string> report, string evidenceRoot)
        {
            string root = Path.Combine(evidenceRoot, "history-fixtures-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            UsageHistoryStore store = new UsageHistoryStore(root);
            string key = new String('a', 64), other = new String('b', 64);
            DateTime time = new DateTime(2026, 10, 31, 23, 57, 0, DateTimeKind.Utc);
            DateTime reset = time.AddMinutes(3);
            store.Record(Snapshot(key, time, 80, reset));
            store.Record(Snapshot(key, time.AddMinutes(1), 37, reset));
            store.Record(Snapshot(key, time.AddMinutes(2), 37, reset));
            var saved = new UsageHistoryStore(root).Read(key);
            Require(saved.Count == 2 && saved[1].LastUtc == time.AddMinutes(2) && saved[1].Primary.RemainingPercent == 37,
                "persisted history must retain changes and the latest unchanged observation");
            report("PASS history: restart persistence and unchanged samples retain the last observation");

            store.Record(Snapshot(other, time, 12, reset));
            Require(store.Read(other).Single().Primary.RemainingPercent == 12 && store.Read(key).Count == 2,
                "accounts mixed their history");
            Require(UsageHistoryStore.AccountKey("account-a", "A@example.test") == UsageHistoryStore.AccountKey("account-a", "a@example.test") &&
                UsageHistoryStore.AccountKey("account-b", "a@example.test") != UsageHistoryStore.AccountKey("account-a", "a@example.test"), "unstable account identity");
            Require(UsageHistoryStore.AccountKey(null, "a@example.test") == null, "missing identity became a shared account");
            report("PASS history: stable account identity separates accounts independently of slots and plans");

            store.Record(Snapshot(key, reset.AddSeconds(5), 100, reset.AddHours(5)));
            saved = store.Read(key);
            var periods = UsageHistoryStore.Periods(saved).Where(p => p.Slot == "primary").ToList();
            Require(periods.Count == 2 && periods[0].Quota.RemainingPercent == 37 && periods[0].LastUtc == time.AddMinutes(2) &&
                periods[0].EndKind == "scheduled" && periods[1].EndKind == null, "reset overwrote the pre-reset remaining quota");
            Require(Directory.GetFiles(Path.Combine(root, key), "????-??.json").Length == 2, "month rollover lost history");
            report("PASS history: monthly rollover and scheduled reset preserve 37 percent before reset");

            var invalid = Snapshot(key, reset.AddMinutes(1), 0, reset.AddHours(5));
            invalid.Error = "offline"; store.Record(invalid);
            invalid.Error = null; invalid.IsAuthenticated = false; store.Record(invalid);
            invalid.IsAuthenticated = true; invalid.Primary.RemainingPercent = Double.NaN; store.Record(invalid);
            invalid.Primary = null; store.Record(invalid);
            store.Record(Snapshot(key, time, 0, reset));
            Require(store.Read(key).Count == 3, "failed, missing, invalid or out-of-order observations altered history");
            report("PASS history: failed, missing, invalid and old responses cannot create usage");

            var stale = Snapshot(other, reset.AddMinutes(1), 0, reset);
            store.Record(stale);
            Require(store.Read(other).Single().Primary.RemainingPercent == 12, "expired server data replaced a pre-reset value");
            periods = UsageHistoryStore.Periods(store.Read(other));
            Require(periods.Single().EndKind == null, "time alone invented a confirmed reset");
            report("PASS history: stale quota and offline gaps do not invent resets or zero remaining");

            string changedKey = new String('c', 64);
            store.Record(Snapshot(changedKey, time, 20, reset));
            store.Record(Snapshot(changedKey, time.AddMinutes(1), 100, reset));
            var changed = Snapshot(changedKey, time.AddMinutes(2), 90, reset); changed.PlanType = "pro"; store.Record(changed);
            periods = UsageHistoryStore.Periods(store.Read(changedKey));
            Require(periods.Count == 3 && periods[0].EndKind == "changed" && periods[1].EndKind == "changed", "manual or plan change was called a scheduled reset");
            report("PASS history: increases and plan changes are separate from scheduled resets");

            string weeklyKey = new String('d', 64);
            var weekly = Snapshot(weeklyKey, time, 60, reset); weekly.Secondary = weekly.Primary; weekly.Primary = null;
            weekly.Secondary.DurationMinutes = 10080; weekly.Secondary.ResetsAt = time.AddDays(7); store.Record(weekly);
            Require(store.Read(weeklyKey).Single().Primary == null && UsageHistoryStore.Periods(store.Read(weeklyKey)).Single().Slot == "secondary",
                "weekly-only account gained fabricated short quota");
            report("PASS history: weekly-only accounts retain unknown short quota");

            var beforePlanChange = Snapshot(key, time, 37, reset);
            var afterPlanChange = Snapshot(key, time.AddMinutes(1), 65, time.AddDays(7));
            afterPlanChange.Primary.DurationMinutes = 10080;
            afterPlanChange.Secondary = afterPlanChange.Primary; afterPlanChange.Primary = null; afterPlanChange.PlanType = "prolite";
            var transitionStore = new UsageHistoryStore(Path.Combine(root, "plan-transition"));
            transitionStore.Record(beforePlanChange); transitionStore.Record(afterPlanChange);
            Require(UsageHistoryStore.Periods(transitionStore.Read(key)).Single(p => p.Slot == "primary").EndKind == "changed",
                "removed short quota remains in progress after a weekly-only plan change");
            afterPlanChange.PlanType = "plus";
            transitionStore = new UsageHistoryStore(Path.Combine(root, "temporary-missing"));
            transitionStore.Record(beforePlanChange); transitionStore.Record(afterPlanChange);
            Require(UsageHistoryStore.Periods(transitionStore.Read(key)).Single(p => p.Slot == "primary").EndKind == null,
                "a temporarily missing quota was incorrectly closed as a plan change");
            report("PASS history: weekly-only plan changes end removed quotas while temporary gaps do not");

            var unknownReset = Snapshot(weeklyKey, time.AddMinutes(1), 0, reset); unknownReset.Primary.ResetsAt = null;
            store.Record(unknownReset);
            Require(store.Read(weeklyKey).Last().Primary.RemainingPercent == 0 &&
                UsageHistoryStore.Periods(store.Read(weeklyKey)).Last().Quota.ResetsAtUtc == null, "zero quota or unknown reset time was fabricated");
            report("PASS history: real zero remaining is distinct from missing quota and missing reset time");

            string file = Path.Combine(root, key, "2026-11.json");
            File.WriteAllText(file, "broken original");
            bool rejected = false;
            try { store.Record(Snapshot(key, reset.AddMinutes(2), 90, reset.AddHours(5))); } catch (InvalidDataException) { rejected = true; }
            Require(rejected && File.ReadAllText(file) == "broken original", "corrupt original was silently overwritten");
            report("PASS history: corrupt original blocks writes and is preserved");

            string damagedAccount = Path.Combine(root, other, "account.json");
            File.WriteAllText(damagedAccount, "broken account metadata");
            var accounts = store.Accounts();
            Require(accounts.Any(a => a.Key == key && a.Label == "fixture@example.test") &&
                accounts.Any(a => a.Key == other && a.MetadataUnavailable) && File.ReadAllText(damagedAccount) == "broken account metadata",
                "one damaged account metadata file blocked healthy accounts or was overwritten");
            report("PASS history: damaged account metadata is isolated from healthy account history");
            foreach (string invalidDate in new[] { "broken-date", "/Date(broken)/" })
            {
                string damagedJson = "{\"Key\":\"" + other + "\",\"Label\":\"fixture\",\"LastUtc\":\"" + invalidDate + "\"}";
                File.WriteAllText(damagedAccount, damagedJson);
                Require(store.Accounts().Any(a => a.Key == key && !a.MetadataUnavailable) &&
                    store.Accounts().Any(a => a.Key == other && a.MetadataUnavailable) && File.ReadAllText(damagedAccount) == damagedJson,
                    "invalid metadata timestamp blocked healthy accounts or changed the damaged file");
            }
            report("PASS history: malformed metadata timestamps are isolated without modifying their originals");

            string failureRoot = Path.Combine(root, "write-failure");
            string blockingPath = Path.Combine(failureRoot, key, "2026-10.json");
            Directory.CreateDirectory(blockingPath);
            var failureStore = new UsageHistoryStore(failureRoot);
            for (int i = 0; i < 3; i++)
            {
                bool failed = false;
                try { failureStore.Record(Snapshot(key, time.AddSeconds(i), 25, reset)); } catch (IOException) { failed = true; }
                Require(failed, "blocked publication did not report failure");
            }
            Require(Directory.GetFiles(Path.GetDirectoryName(blockingPath), "*.pending*").Length == 1 && Directory.Exists(blockingPath),
                "failed publication accumulated duplicate staging files or changed the original target");
            report("PASS history: repeated publication failures retain only one staging file and preserve the target");

            string content = String.Join("\n", Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Select(File.ReadAllText));
            Require(!content.Contains("access_token") && !content.Contains("id_token") && !content.Contains("refresh_token"), "authentication data leaked to history");

            Window dashboard = DashboardController.LoadWindow();
            try
            {
                foreach (string prefix in new[] { "Account1", "Account2", "CompactAccount1", "CompactAccount2" })
                    Require(dashboard.FindName(prefix + "HistoryButton") is Button, "history entry missing from " + prefix);
                report("PASS history: accessible from expanded and compact account cards");
            }
            finally { dashboard.Close(); }

            TestWindow(root, evidenceRoot, report);
        }

        private static void TestWindow(string root, string evidenceRoot, Action<string> report)
        {
            var store = new UsageHistoryStore(Path.Combine(root, "ui"));
            string key = new String('f', 64);
            DateTime now = DateTime.UtcNow;
            DateTime reset = now.AddMinutes(-10);
            for (int i = 0; i < 70; i++) store.Record(Snapshot(key, reset.AddMinutes(i - 70), 80 - i, reset));
            store.Record(Snapshot(key, reset.AddMinutes(1), 100, reset.AddHours(5)));
            string damagedKey = new String('e', 64);
            store.Record(Snapshot(damagedKey, now.AddMinutes(-1), 12, now.AddHours(1)));
            File.WriteAllText(Path.Combine(root, "ui", damagedKey, "account.json"), "broken other account");
            UsageHistoryWindow window = new UsageHistoryWindow(store, key);
            Grid rootView = (Grid)window.Content; window.Content = null;
            rootView.Resources.MergedDictionaries.Add(window.Resources);
            rootView.SetValue(Control.FontFamilyProperty, window.FontFamily); rootView.SetValue(Control.ForegroundProperty, window.Foreground);
            rootView.Background = window.Background;
            DataGrid table = (DataGrid)typeof(UsageHistoryWindow).GetField("_table", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
            Button resets = (Button)typeof(UsageHistoryWindow).GetField("_resetsButton", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
            Require(table.Items.Count == 71 && ((UsageHistorySampleRow)table.Items[0]).Primary == "100%", "history table did not show recorded remaining quota");
            HwndSourceParameters parameters = new HwndSourceParameters("CodexUsageMeter.HistoryTest") {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080, Width = 1080, Height = 690 };
            using (HwndSource surface = new HwndSource(parameters))
            {
                surface.RootVisual = rootView;
                foreach (bool small in new[] { false, true })
                {
                    if (small) resets.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    int width = small ? 720 : 1080, height = small ? 480 : 690;
                    rootView.Measure(new Size(width, height)); rootView.Arrange(new Rect(0, 0, width, height)); rootView.UpdateLayout();
                    Require(VisibleText(rootView).Contains("fixture@example.test"), "account picker displays an internal type instead of the account label");
                    Require(table.ActualWidth > 500 && table.ActualHeight > 150 && resets.ActualWidth > 90, "history controls lost their usable layout");
                    if (small)
                    {
                        var rows = table.Items.Cast<UsageHistoryResetRow>().ToList();
                        Require(rows.Count == 2 && rows[1].Remaining == "11%" && rows[1].Gap == "1분 전" && rows[1].State.Contains("새 한도"),
                            "reset table lost the last pre-reset observation or observation gap");
                    }
                    RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(rootView);
                    PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (FileStream output = File.Create(Path.Combine(evidenceRoot, small ? "history-reset-small.png" : "history-samples.png"))) encoder.Save(output);
                }
            }
            string corruptUiPath = Directory.GetFiles(Path.Combine(root, "ui", key), "????-??.json").First();
            File.WriteAllText(corruptUiPath, "broken UI fixture");
            window.Reload();
            ComboBox range = (ComboBox)typeof(UsageHistoryWindow).GetField("_range", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
            range.SelectedIndex = 3;
            TextBlock empty = (TextBlock)typeof(UsageHistoryWindow).GetField("_empty", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
            Require(empty.Text.Contains("읽지 못"), "changing a filter disguised an unreadable history file as no records");
            window.Close();
            report("PASS history: hidden WPF tables show quota history and pre-reset remaining at normal and narrow sizes");
            report("PASS history: read errors remain explicit after filtering unreadable history");
        }

        private static string VisibleText(DependencyObject root)
        {
            TextBlock text = root as TextBlock;
            string result = text != null && text.IsVisible ? text.Text + "\n" : "";
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) result += VisibleText(VisualTreeHelper.GetChild(root, i));
            return result;
        }

        private static AccountSnapshot Snapshot(string key, DateTime observed, double remaining, DateTime reset)
        {
            return new AccountSnapshot { IsAuthenticated = true, Email = "fixture@example.test", HistoryKey = key, PlanType = "plus",
                RateLimitsObservedAtUtc = observed, Primary = new RateWindow { RemainingPercent = remaining, UsedPercent = 100 - remaining,
                    DurationMinutes = 300, ResetsAt = reset } };
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
