using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace CodexUsageMeter
{
    internal static class SubscriptionRegressionTests
    {
        public static void Run(Action<string> report)
        {
            SubscriptionEntry monthly = Entry("월말 서비스", "2025-01-31", "monthly");
            CheckDate(monthly, "2025-02-01", "2025-02-28");
            CheckDate(monthly, "2025-02-28", "2025-02-28");
            CheckDate(monthly, "2025-03-01", "2025-03-31");
            CheckDate(monthly, "2025-12-31", "2025-12-31");
            CheckDate(monthly, "2026-01-01", "2026-01-31");
            CheckDate(Entry("미래 시작", "2027-01-31", "monthly"), "2026-10-06", "2027-01-31");
            report("PASS monthly renewal keeps its original day across short months, today and year boundaries");

            SubscriptionEntry yearly = Entry("윤년 서비스", "2024-02-29", "yearly");
            CheckDate(yearly, "2025-02-01", "2025-02-28");
            CheckDate(yearly, "2025-03-01", "2026-02-28");
            CheckDate(yearly, "2028-02-01", "2028-02-29");
            CheckDate(yearly, "2028-02-29", "2028-02-29");
            CheckDate(Entry("한 번", "2026-01-01", "once"), "2026-10-06", "2026-01-01");
            report("PASS yearly leap-day renewal recovers February 29 and one-off dates stay overdue");

            bool invalidDate = false, invalidCycle = false;
            try { Entry("잘못된 날짜", "2026-02-30", "monthly").NextRenewal(DateTime.Today); }
            catch (FormatException) { invalidDate = true; }
            try { Entry("잘못된 주기", "2026-10-06", "weekly").NextRenewal(DateTime.Today); }
            catch (ArgumentException) { invalidCycle = true; }
            Require(invalidDate && invalidCycle, "Invalid dates or unsupported cycles were silently accepted.");

            SubscriptionEntry original = Entry("  서비스 이름  ", "2026-10-06", "  YEARLY ");
            original.Id = " duplicate ";
            SubscriptionEntry duplicate = Entry("다른 서비스", "2026-11-06", "monthly"); duplicate.Id = "duplicate";
            List<SubscriptionEntry> normalized = SubscriptionEntry.Normalize(new[] {
                original, duplicate, null, Entry("", "2026-10-06", "monthly"),
                Entry("불가능한 날짜", "2026-02-30", "monthly"), Entry("잘못된 주기", "2026-10-06", "weekly") });
            Require(normalized.Count == 2 && normalized[0].Name == "서비스 이름" && normalized[0].Cycle == "yearly" &&
                normalized[0].Id == "duplicate" && normalized[1].Id != normalized[0].Id, "Normalization lost valid values or retained invalid rows/duplicate IDs.");
            Require(original.Name == "  서비스 이름  " && original.Cycle == "  YEARLY " && original.Id == " duplicate ", "Normalization mutated the original entry.");
            List<SubscriptionEntry> many = SubscriptionEntry.Normalize(Enumerable.Range(0, 105).Select(index => Entry("서비스 " + index, "2026-10-06", "monthly")));
            Require(many.Count == 100 && many.Select(item => item.Id).Distinct().Count() == 100, "Subscription storage is unbounded or IDs are not unique.");
            report("PASS normalization validates dates/cycles, preserves originals, repairs IDs and bounds stored rows");

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Require(SubscriptionEntry.Normalize(serializer.Deserialize<List<SubscriptionEntry>>("[{\"Name\":\"날짜 누락\",\"Cycle\":\"monthly\"}]")).Count == 0,
                "A missing stored date silently became today's renewal date.");
            List<SubscriptionEntry> reloaded = SubscriptionEntry.Normalize(serializer.Deserialize<List<SubscriptionEntry>>(serializer.Serialize(normalized)));
            Require(reloaded.Count == 2 && reloaded[0].Id == "duplicate" && reloaded[0].Name == "서비스 이름" &&
                reloaded[0].AnchorDate == "2026-10-06" && reloaded[0].Cycle == "yearly", "Subscription JSON did not round-trip.");
            SubscriptionEntry copy = reloaded[0].Copy(); copy.Name = "수정";
            Require(reloaded[0].Name == "서비스 이름", "Editing a copy changed the stored entry.");
            report("PASS subscription JSON round-trip and copy isolation");


        }

        private static SubscriptionEntry Entry(string name, string date, string cycle)
        {
            return new SubscriptionEntry { Name = name, AnchorDate = date, Cycle = cycle };
        }
        private static void CheckDate(SubscriptionEntry entry, string today, string expected)
        {
            DateTime date = DateTime.ParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            string actual = entry.NextRenewal(date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Require(actual == expected, entry.Cycle + " renewal for " + entry.AnchorDate + " on " + today + ": expected " + expected + ", got " + actual);
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
