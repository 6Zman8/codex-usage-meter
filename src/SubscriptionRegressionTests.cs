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
            CheckCard(report);
            CheckEditor(report);
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
        private static void CheckCard(Action<string> report)
        {
            DateTime today = new DateTime(2026, 10, 6);
            List<SubscriptionEntry> entries = new List<SubscriptionEntry> {
                Entry("나중 일정", "2026-10-26", "monthly"), Entry("내일 일정", "2026-10-07", "monthly"),
                Entry("지난 일정", "2026-10-03", "once"), Entry("오늘 일정", "2026-10-06", "yearly") };
            int manageCalls = 0;
            SubscriptionCardView compact = new SubscriptionCardView(true, delegate { manageCalls++; });
            compact.Update(entries, today, 1.5);
            using (HwndSource surface = Surface(400, 260))
            {
                Render(surface, compact.Card, 400, 260);
                string[] displayed = Descendants<TextBlock>(compact.Card).Where(text => entries.Any(entry => entry.Name == text.Text)).Select(text => text.Text).ToArray();
                Require(displayed.SequenceEqual(new[] { "지난 일정", "오늘 일정", "내일 일정" }), "Compact card is not ordered by next date or does not limit the visible rows.");
                string[] labels = Descendants<TextBlock>(compact.Card).Select(text => text.Text).ToArray();
                Require(labels.Contains("3일 지남") && labels.Contains("오늘") && labels.Contains("D-1") && labels.Any(text => text.Contains("1개")), "Card does not distinguish overdue/today/future or show the remaining count.");
                Button manage = Descendants<Button>(compact.Card).Single();
                manage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(manageCalls == 1, "Subscription management action is unreachable from the card.");
            }
            SubscriptionCardView expanded = new SubscriptionCardView(false, delegate { });
            expanded.Update(entries, today, 2.0);
            using (HwndSource surface = Surface(440, 320))
            {
                Render(surface, expanded.Card, 440, 320);
                Require(Descendants<TextBlock>(expanded.Card).Any(text => text.Text == "나중 일정"), "Expanded card lost entries outside the compact limit.");
                Require(Descendants<ScrollViewer>(expanded.Card).Any(scroll => scroll.Style != null), "Expanded subscription scroll does not use the shared theme.");
                expanded.Update(new List<SubscriptionEntry>(), today, 1.0);
                Render(surface, expanded.Card, 440, 320);
                Require(Descendants<TextBlock>(expanded.Card).Any(text => text.Text.Contains("등록") && text.Text.Contains("관리")), "Empty subscription card does not explain how to add an entry.");
            }
            report("PASS subscription card date ordering, overdue/today/D-day labels, compact overflow and management action");
        }
        private static HwndSource Surface(int width, int height)
        {
            HwndSourceParameters parameters = new HwndSourceParameters("CodexUsageMeter.SubscriptionTest");
            parameters.WindowStyle = unchecked((int)0x80000000); parameters.ExtendedWindowStyle = 0x08000080;
            parameters.Width = width; parameters.Height = height; return new HwndSource(parameters);
        }
        private static void CheckEditor(Action<string> report)
        {
            List<SubscriptionEntry> original = new List<SubscriptionEntry> { Entry("서비스 A", "2026-10-06", "monthly") };
            int saves = 0;
            SubscriptionEditor cancelled = new SubscriptionEditor(original, delegate { saves++; });
            using (HwndSource surface = Surface(640, 700))
            {
                FrameworkElement content = EditorContent(cancelled); Render(surface, content, 640, 700);
                Require(Descendants<TextBox>(content).Count() == 2, "Subscription editor does not expose editable service/date fields.");
                Descendants<TextBox>(content).Single(text => text.Text == "서비스 A").Text = "취소할 수정";
                Descendants<TextBox>(content).Single(text => text.Text == "2026-10-06").Text = "2026-02-30";
                Require(!cancelled.TrySave() && !cancelled.Saved && saves == 0, "Invalid editor date was saved.");
                Require(Descendants<TextBlock>(content).Any(text => text.Text.Contains("날짜") && text.Text.Contains("YYYY-MM-DD")), "Invalid date has no useful in-window validation message.");
                cancelled.Close();
                Require(original[0].Name == "서비스 A" && original[0].AnchorDate == "2026-10-06" && saves == 0, "Cancel or invalid save changed original subscriptions.");
            }

            bool failSave = true; List<SubscriptionEntry> persisted = null;
            SubscriptionEditor editor = new SubscriptionEditor(original, delegate(List<SubscriptionEntry> draft) {
                if (failSave) throw new InvalidOperationException("fixture storage unavailable");
                persisted = draft;
            });
            using (HwndSource surface = Surface(640, 700))
            {
                FrameworkElement content = EditorContent(editor); Render(surface, content, 640, 700);
                TextBox name = Descendants<TextBox>(content).Single(text => text.Text == "서비스 A");
                name.Text = "  저장할 서비스  ";
                Require(!editor.TrySave() && !editor.Saved, "Storage failure marked subscriptions saved.");
                Require(Descendants<TextBlock>(content).Any(text => text.Text.Contains("저장하지 못")), "Storage failure is not visible in the editor.");
                Require(name.Text == "  저장할 서비스  " && original[0].Name == "서비스 A", "Storage failure discarded the draft or mutated originals.");
                failSave = false;
                Require(editor.TrySave() && editor.Saved && persisted.Count == 1 && persisted[0].Name == "저장할 서비스", "Retry after storage failure did not save the corrected draft.");
                persisted[0].Name = "외부 수정";
                Require(original[0].Name == "서비스 A", "Saving exposed the original entry to callback mutation.");
                editor.Close();
            }
            SubscriptionEditor empty = new SubscriptionEditor(new List<SubscriptionEntry>(), delegate(List<SubscriptionEntry> draft) { persisted = draft; });
            using (HwndSource surface = Surface(640, 700))
            {
                FrameworkElement content = EditorContent(empty); Render(surface, content, 640, 700);
                Descendants<Button>(content).Single(button => (string)button.Content == "구독 추가").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Render(surface, content, 640, 700);
                Require(!empty.TrySave() && !empty.Saved, "New blank service name was silently saved or dropped.");
                Descendants<Button>(content).Single(button => (string)button.Content == "삭제").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(empty.TrySave() && persisted.Count == 0, "Removing the final row cannot persist an empty subscription list.");
                empty.Close();
            }
            report("PASS editor validation, cancel isolation, save failure/retry, add/remove and empty-list persistence");
        }
        private static FrameworkElement EditorContent(SubscriptionEditor editor)
        {
            FrameworkElement content = (FrameworkElement)editor.Content; editor.Content = null;
            content.Resources.MergedDictionaries.Add(editor.Resources);
            content.SetValue(Control.ForegroundProperty, editor.Foreground);
            content.SetValue(Control.FontFamilyProperty, editor.FontFamily);
            content.SetValue(Control.FontSizeProperty, editor.FontSize);
            return content;
        }
        private static void Render(HwndSource surface, FrameworkElement element, int width, int height)
        {
            surface.RootVisual = element; element.Measure(new Size(width, height));
            element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        }
        private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                if (child is T) yield return (T)child;
                foreach (T nested in Descendants<T>(child)) yield return nested;
            }
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
