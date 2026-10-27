using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexUsageMeter
{
    internal sealed class UsageHistoryQuota
    {
        public double RemainingPercent { get; set; }
        public int DurationMinutes { get; set; }
        public DateTime? ResetsAtUtc { get; set; }
    }

    internal sealed class UsageHistorySample
    {
        public DateTime FirstUtc { get; set; }
        public DateTime LastUtc { get; set; }
        public string Plan { get; set; }
        public UsageHistoryQuota Primary { get; set; }
        public UsageHistoryQuota Secondary { get; set; }
    }

    internal sealed class UsageHistoryAccount
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public DateTime LastUtc { get; set; }
        [ScriptIgnore]
        public bool MetadataUnavailable { get; set; }
    }

    internal sealed class UsageHistoryPeriod
    {
        public string Slot { get; set; }
        public string Plan { get; set; }
        public DateTime FirstUtc { get; set; }
        public DateTime LastUtc { get; set; }
        public UsageHistoryQuota Quota { get; set; }
        public string EndKind { get; set; }
        public DateTime? NextObservedUtc { get; set; }
    }

    internal sealed class UsageHistoryStore
    {
        private static readonly object Gate = new object();
        private readonly string _root;
        private const int MaxFileBytes = 32 * 1024 * 1024;

        internal UsageHistoryStore(string root) { _root = Path.GetFullPath(root); }

        internal static string AccountKey(string accountId, string email)
        {
            if (String.IsNullOrWhiteSpace(accountId) || String.IsNullOrWhiteSpace(email)) return null;
            using (SHA256 hash = SHA256.Create())
                return String.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(accountId + "\n" + email.Trim().ToLowerInvariant()))
                    .Select(b => b.ToString("x2")));
        }

        internal void Record(AccountSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.IsAuthenticated || !String.IsNullOrEmpty(snapshot.Error) ||
                String.IsNullOrEmpty(snapshot.HistoryKey) || snapshot.RateLimitsObservedAtUtc == default(DateTime)) return;
            if (AccountProviders.For(snapshot).Id != "codex" && (!snapshot.IdentityVerified ||
                snapshot.HistoryKey != ProviderAccountKey(snapshot.ProviderId, snapshot.RemoteAccountId, snapshot.RemoteOrganizationId))) return;
            DateTime observed = snapshot.RateLimitsObservedAtUtc.ToUniversalTime();
            UsageHistorySample sample = new UsageHistorySample { FirstUtc = observed, LastUtc = observed,
                Plan = snapshot.PlanType ?? "", Primary = Capture(snapshot.Primary, observed), Secondary = Capture(snapshot.Secondary, observed) };
            if (sample.Primary == null && sample.Secondary == null) return;
            string directory = AccountDirectory(snapshot.HistoryKey);
            lock (Gate)
            {
                string accountPath = Path.Combine(directory, "account.json");
                UsageHistoryAccount account = File.Exists(accountPath) ? LoadAccount(accountPath, snapshot.HistoryKey) : null;
                if (account != null && observed <= account.LastUtc) return;
                string monthPath = Path.Combine(directory, observed.ToString("yyyy-MM") + ".json");
                UsageHistoryMonth month = File.Exists(monthPath) ? LoadMonth(monthPath, snapshot.HistoryKey) :
                    new UsageHistoryMonth { Schema = 1, Key = snapshot.HistoryKey, Samples = new List<UsageHistorySample>() };
                UsageHistorySample last = month.Samples.LastOrDefault();
                if (last != null && observed <= last.LastUtc) return;
                if (last != null && observed - last.LastUtc <= TimeSpan.FromMinutes(2) && last.Plan == sample.Plan &&
                    Equal(last.Primary, sample.Primary) && Equal(last.Secondary, sample.Secondary)) last.LastUtc = observed;
                else month.Samples.Add(sample);
                Directory.CreateDirectory(directory);
                Save(monthPath, month);
                Save(accountPath, new UsageHistoryAccount { Key = snapshot.HistoryKey,
                    Label = String.IsNullOrWhiteSpace(snapshot.Email) ? AccountProviders.For(snapshot).DisplayName + " 계정" : snapshot.Email, LastUtc = observed });
            }
        }

        internal List<UsageHistorySample> Read(string key)
        {
            string directory = AccountDirectory(key);
            lock (Gate)
            {
                if (!Directory.Exists(directory)) return new List<UsageHistorySample>();
                return Directory.GetFiles(directory, "????-??.json").OrderBy(p => p, StringComparer.Ordinal)
                    .SelectMany(p => LoadMonth(p, key).Samples).OrderBy(s => s.FirstUtc).ToList();
            }
        }

        internal List<UsageHistoryAccount> Accounts()
        {
            lock (Gate)
            {
                if (!Directory.Exists(_root)) return new List<UsageHistoryAccount>();
                List<UsageHistoryAccount> accounts = new List<UsageHistoryAccount>();
                foreach (string directory in Directory.GetDirectories(_root))
                {
                    string key = Path.GetFileName(directory), path = Path.Combine(directory, "account.json");
                    if (ValidKey(key) && File.Exists(path))
                    {
                        try { accounts.Add(LoadAccount(path, key)); }
                        catch (InvalidDataException)
                        {
                            accounts.Add(new UsageHistoryAccount { Key = key, Label = "이름을 읽지 못한 계정", MetadataUnavailable = true });
                        }
                    }
                }
                return accounts.OrderByDescending(a => a.LastUtc).ToList();
            }
        }

        internal static List<UsageHistoryPeriod> Periods(IEnumerable<UsageHistorySample> samples)
        {
            List<UsageHistoryPeriod> result = new List<UsageHistoryPeriod>();
            List<UsageHistorySample> ordered = samples.OrderBy(s => s.FirstUtc).ToList();
            foreach (string slot in new[] { "primary", "secondary" })
            {
                UsageHistoryPeriod previous = null;
                foreach (UsageHistorySample sample in ordered)
                {
                    UsageHistoryQuota quota = slot == "primary" ? sample.Primary : sample.Secondary;
                    if (quota == null)
                    {
                        // A known plan transition can remove a window altogether. A missing
                        // window on the same plan is only an observation gap, not a reset.
                        if (previous != null && !String.IsNullOrEmpty(sample.Plan) && !String.IsNullOrEmpty(previous.Plan) && previous.Plan != sample.Plan)
                        {
                            previous.EndKind = "changed"; previous.NextObservedUtc = sample.FirstUtc; previous = null;
                        }
                        continue;
                    }
                    bool same = previous != null && previous.Plan == sample.Plan &&
                        previous.Quota.DurationMinutes == quota.DurationMinutes && previous.Quota.ResetsAtUtc == quota.ResetsAtUtc;
                    if (same && quota.RemainingPercent <= previous.Quota.RemainingPercent)
                    {
                        previous.LastUtc = sample.LastUtc; previous.Quota = quota;
                        continue;
                    }
                    if (previous != null)
                    {
                        previous.NextObservedUtc = sample.FirstUtc;
                        previous.EndKind = previous.Plan == sample.Plan && previous.Quota.DurationMinutes == quota.DurationMinutes &&
                            previous.Quota.ResetsAtUtc.HasValue && quota.ResetsAtUtc.HasValue &&
                            sample.FirstUtc >= previous.Quota.ResetsAtUtc.Value && quota.ResetsAtUtc.Value > previous.Quota.ResetsAtUtc.Value
                            ? "scheduled" : "changed";
                    }
                    previous = new UsageHistoryPeriod { Slot = slot, Plan = sample.Plan, FirstUtc = sample.FirstUtc,
                        LastUtc = sample.LastUtc, Quota = quota };
                    result.Add(previous);
                }
            }
            return result.OrderBy(p => p.FirstUtc).ToList();
        }

        private static UsageHistoryQuota Capture(RateWindow window, DateTime observed)
        {
            if (window == null || window.DurationMinutes <= 0 || !ValidPercent(window.RemainingPercent)) return null;
            DateTime? reset = window.ResetsAt.HasValue ? (DateTime?)window.ResetsAt.Value.ToUniversalTime() : null;
            // An expired response is not a new observation of the quota before its deadline.
            if (reset.HasValue && reset.Value <= observed) return null;
            return new UsageHistoryQuota { RemainingPercent = window.RemainingPercent,
                DurationMinutes = window.DurationMinutes, ResetsAtUtc = reset };
        }

        private static bool Equal(UsageHistoryQuota a, UsageHistoryQuota b)
        {
            return a == null || b == null ? a == b : a.RemainingPercent == b.RemainingPercent &&
                a.DurationMinutes == b.DurationMinutes && a.ResetsAtUtc == b.ResetsAtUtc;
        }

        private string AccountDirectory(string key)
        {
            if (!ValidKey(key)) throw new InvalidDataException("이력 계정 식별자가 올바르지 않습니다.");
            return Path.Combine(_root, key);
        }

        internal static string ProviderAccountKey(string providerId, string accountId, string organizationId)
        {
            if (String.IsNullOrWhiteSpace(providerId) || providerId == "codex" || !providerId.All(c => c >= 'a' && c <= 'z') ||
                String.IsNullOrWhiteSpace(accountId) || String.IsNullOrWhiteSpace(organizationId)) return null;
            using (SHA256 hash = SHA256.Create())
                return providerId + "-" + String.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(
                    providerId + "\n" + accountId + "\n" + organizationId)).Select(b => b.ToString("x2")));
        }

        private static bool ValidKey(string key)
        {
            if (key == null) return false;
            int split = key.IndexOf('-');
            if (split >= 0)
            {
                if (split == 0 || !key.Substring(0, split).All(c => c >= 'a' && c <= 'z')) return false;
                key = key.Substring(split + 1);
            }
            return key.Length == 64 && key.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        }

        private static bool ValidPercent(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= 0 && value <= 100; }
        private static bool ValidQuota(UsageHistoryQuota value)
        {
            return value == null || (value.DurationMinutes > 0 && ValidPercent(value.RemainingPercent));
        }

        private static UsageHistoryAccount LoadAccount(string path, string key)
        {
            UsageHistoryAccount account = Load<UsageHistoryAccount>(path);
            if (account == null || account.Key != key || String.IsNullOrWhiteSpace(account.Label) || account.LastUtc == default(DateTime))
                throw Damaged();
            return account;
        }

        private static UsageHistoryMonth LoadMonth(string path, string key)
        {
            UsageHistoryMonth month = Load<UsageHistoryMonth>(path);
            if (month == null || month.Schema != 1 || month.Key != key || month.Samples == null) throw Damaged();
            DateTime last = DateTime.MinValue;
            string expectedMonth = Path.GetFileNameWithoutExtension(path);
            foreach (UsageHistorySample sample in month.Samples)
            {
                if (sample == null || sample.FirstUtc <= last || sample.LastUtc < sample.FirstUtc ||
                    sample.FirstUtc.ToString("yyyy-MM") != expectedMonth || sample.LastUtc.ToString("yyyy-MM") != expectedMonth ||
                    !ValidQuota(sample.Primary) || !ValidQuota(sample.Secondary) || (sample.Primary == null && sample.Secondary == null)) throw Damaged();
                last = sample.LastUtc;
            }
            return month;
        }

        private static T Load<T>(string path)
        {
            try
            {
                if (new FileInfo(path).Length > MaxFileBytes) throw Damaged();
                return new JavaScriptSerializer { MaxJsonLength = MaxFileBytes }.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                if (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException ||
                    ex is FormatException || ex is OverflowException || ex is InvalidCastException)
                    throw Damaged();
                throw;
            }
        }

        private static InvalidDataException Damaged()
        {
            return new InvalidDataException("사용량 이력 파일을 읽지 못했습니다. 원본을 보존했으며 덮어쓰지 않습니다.");
        }

        private static void Save(string path, object value)
        {
            string json = new JavaScriptSerializer { MaxJsonLength = MaxFileBytes }.Serialize(value);
            if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes) throw new IOException("월별 이력 파일 크기 한도를 초과했습니다. 기존 기록은 보존합니다.");
            // Reuse the unpublished staging file after a failed replacement. Repeated
            // polling must not accumulate full copies; the target and backup stay intact.
            string pending = path + ".pending";
            using (FileStream stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(json);
                stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(pending, path, path + ".bak", true);
            else File.Move(pending, path);
        }
    }

    // Statistics in % of the weekly limit, derived only from this meter's own samples (no tokens are known).
    internal static class UsageHistoryStats
    {
        internal const string WeeklyPercentUnit = "weekly-percent";

        // Daily consumed weekly-limit %: positive increases of used % within a window, plus the used % of a
        // new window (its reset passed before the reading). A drop within a window (a redeemed reset) only moves the baseline.
        internal static SortedDictionary<DateTime, double> WeeklyPercentByDay(IEnumerable<UsageHistorySample> samples)
        {
            SortedDictionary<DateTime, double> days = new SortedDictionary<DateTime, double>();
            UsageHistoryQuota previous = null;
            foreach (UsageHistorySample sample in samples.OrderBy(s => s.FirstUtc))
            {
                UsageHistoryQuota quota = sample.Secondary;
                if (quota == null || quota.DurationMinutes != 10080) continue;
                double used = 100 - quota.RemainingPercent, consumed = 0;
                if (previous != null)
                {
                    double before = 100 - previous.RemainingPercent;
                    if (previous.ResetsAtUtc.HasValue && sample.FirstUtc.ToUniversalTime() >= previous.ResetsAtUtc.Value.ToUniversalTime()) consumed = used;
                    else if (used > before) consumed = used - before;
                }
                DateTime day = sample.FirstUtc.ToLocalTime().Date;
                double total;
                days.TryGetValue(day, out total);
                days[day] = total + consumed;
                previous = quota;
            }
            return days;
        }

        internal static void ApplyWeeklyPercent(AccountSnapshot snapshot, IEnumerable<UsageHistorySample> samples, DateTime today)
        {
            SortedDictionary<DateTime, double> days = WeeklyPercentByDay(samples);
            today = today.Date;
            snapshot.UsageUnit = WeeklyPercentUnit;
            snapshot.DailyUsage = days.Select(p => new DailyUsageBucket { Date = p.Key, Percent = Math.Round(p.Value, 2) }).ToList();
            if (days.Count == 0) { snapshot.Usage = null; return; }
            double value;
            DateTime cursor = days.TryGetValue(today, out value) && value > 0 ? today : today.AddDays(-1);
            long current = 0, longest = 0, run = 0;
            while (days.TryGetValue(cursor, out value) && value > 0) { current++; cursor = cursor.AddDays(-1); }
            DateTime? last = null;
            foreach (KeyValuePair<DateTime, double> day in days)
            {
                if (day.Value <= 0) { run = 0; last = null; continue; }
                run = last.HasValue && last.Value.AddDays(1) == day.Key ? run + 1 : 1;
                longest = Math.Max(longest, run); last = day.Key;
            }
            snapshot.Usage = new UsageSummary { LifetimePercent = days.Values.Sum(), PeakDailyPercent = days.Values.Max(),
                CurrentStreakDays = current, LongestStreakDays = longest, RecordedSince = days.Keys.First() };
        }
    }

    internal sealed class UsageHistoryMonth
    {
        public int Schema { get; set; }
        public string Key { get; set; }
        public List<UsageHistorySample> Samples { get; set; }
    }
}
