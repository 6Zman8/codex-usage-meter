using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace CodexUsageMeter
{
    internal sealed class SubscriptionEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string AnchorDate { get; set; }
        public string Cycle { get; set; }
        public SubscriptionEntry()
        {
            Id = Guid.NewGuid().ToString("N"); Name = "";
            // A missing date in saved data must remain invalid; only the add form supplies today's date.
            AnchorDate = ""; Cycle = "monthly";
        }
        public DateTime NextRenewal(DateTime today)
        {
            DateTime anchor;
            if (!TryParseDate(AnchorDate, out anchor)) throw new FormatException("기준 날짜를 YYYY-MM-DD 형식의 올바른 날짜로 입력해 주세요.");
            string cycle = NormalizeCycle(Cycle);
            if (cycle == null) throw new ArgumentException("갱신 주기는 매월, 매년 또는 한 번이어야 합니다.", "Cycle");
            today = today.Date;
            if (cycle == "once" || anchor >= today) return anchor;
            if (cycle == "monthly")
            {
                int months = (today.Year - anchor.Year) * 12 + today.Month - anchor.Month;
                DateTime next = anchor.AddMonths(months);
                // Always calculate from the original anchor, so February clamping does not move March's date.
                return next < today ? anchor.AddMonths(months + 1) : next;
            }
            int years = today.Year - anchor.Year;
            DateTime yearly = anchor.AddYears(years);
            return yearly < today ? anchor.AddYears(years + 1) : yearly;
        }
        public SubscriptionEntry Copy() { return new SubscriptionEntry { Id = Id, Name = Name, AnchorDate = AnchorDate, Cycle = Cycle }; }
        public static List<SubscriptionEntry> Normalize(IEnumerable<SubscriptionEntry> entries)
        {
            List<SubscriptionEntry> result = new List<SubscriptionEntry>();
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            if (entries == null) return result;
            foreach (SubscriptionEntry entry in entries)
            {
                if (entry == null) continue;
                DateTime anchor;
                string name = String.Join(" ", (entry.Name ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
                string cycle = NormalizeCycle(entry.Cycle);
                if (name.Length == 0 || cycle == null || !TryParseDate(entry.AnchorDate, out anchor)) continue;
                string id = (entry.Id ?? "").Trim();
                if (id.Length == 0 || id.Length > 64 || id.Any(Char.IsControl) || ids.Contains(id)) id = Guid.NewGuid().ToString("N");
                ids.Add(id);
                result.Add(new SubscriptionEntry { Id = id, Name = name.Length > 80 ? name.Substring(0, 80) : name,
                    AnchorDate = anchor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Cycle = cycle });
                if (result.Count == 100) break;
            }
            return result;
        }
        internal static bool TryParseDate(string value, out DateTime date)
        {
            return DateTime.TryParseExact((value ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }
        private static string NormalizeCycle(string cycle)
        {
            string value = String.IsNullOrWhiteSpace(cycle) ? "monthly" : cycle.Trim().ToLowerInvariant();
            return value == "monthly" || value == "yearly" || value == "once" ? value : null;
        }
        internal static string CycleLabel(string cycle) { return cycle == "monthly" ? "매월" : cycle == "yearly" ? "매년" : "한 번"; }
    }

}
