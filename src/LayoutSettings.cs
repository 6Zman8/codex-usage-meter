using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace CodexUsageMeter
{
    internal sealed class LayoutItemSettings
    {
        public string Id { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        internal bool Normalize()
        {
            if (String.IsNullOrWhiteSpace(Id) || Id.Length > 80 ||
                new[] { X, Y, Width, Height }.Any(value => Double.IsNaN(value) || Double.IsInfinity(value)) || Width <= 0 || Height <= 0) return false;
            Width = Math.Max(0.025, Math.Min(1, Width)); Height = Math.Max(0.02, Math.Min(1, Height));
            X = Math.Max(0, Math.Min(1 - Width, X)); Y = Math.Max(0, Math.Min(1 - Height, Y));
            return true;
        }
    }

    internal sealed class LayoutCardSettings
    {
        public string Id { get; set; }
        public bool Visible { get; set; }
        public int Span { get; set; }
        public int Size { get; set; }
        public List<string> Sections { get; set; }
        public List<LayoutItemSettings> ItemLayouts { get; set; }
        public List<string> HiddenItems { get; set; }

        public LayoutCardSettings() { Visible = true; Span = 1; Size = 1; Sections = new List<string>(); ItemLayouts = new List<LayoutItemSettings>(); HiddenItems = new List<string>(); }
        public bool Shows(string section) { return Sections.Contains(section); }
        public bool ShowsItem(string id) { return !HiddenItems.Contains(id); }
        public void SetItemVisible(string id, bool visible)
        {
            HiddenItems.RemoveAll(value => value == id);
            if (!visible) HiddenItems.Add(id);
        }
        public void SetSection(string section, bool visible)
        {
            Sections.RemoveAll(value => value == section);
            if (visible) Sections.Add(section);
        }
    }

    internal sealed class LayoutModeSettings
    {
        public int Columns { get; set; }
        public bool HideUnavailable { get; set; }
        public List<LayoutCardSettings> Cards { get; set; }

        public LayoutModeSettings() { Columns = 1; HideUnavailable = true; Cards = new List<LayoutCardSettings>(); }
        public LayoutCardSettings Card(string id) { return Cards.First(card => card.Id == id); }
        public int[] VisibleAccounts(int count)
        {
            return Cards.Where(card => card.Visible && card.Id.StartsWith("account"))
                .Select(card => Int32.Parse(card.Id.Substring(7))).Where(number => number <= count).ToArray();
        }
        public void Move(string id, string targetId)
        {
            int from = Cards.FindIndex(card => card.Id == id), to = Cards.FindIndex(card => card.Id == targetId);
            if (from < 0 || to < 0 || from == to) return;
            LayoutCardSettings moved = Cards[from];
            Cards.RemoveAt(from);
            Cards.Insert(to, moved);
        }
        public void UseSingleAccount(int number, bool compact)
        {
            Columns = compact ? 1 : 2;
            HideUnavailable = true;
            foreach (LayoutCardSettings card in Cards)
            {
                card.Visible = card.Id == "pc" || card.Id == "account" + number;
                card.Span = 1;
                card.Size = 1;
            }
        }
    }

    internal sealed class LayoutSettings
    {
        public int Version { get; set; }
        public LayoutModeSettings Expanded { get; set; }
        public LayoutModeSettings Widget { get; set; }
        public List<SubscriptionEntry> Subscriptions { get; set; }
        public LayoutSettings() { Version = 1; Subscriptions = new List<SubscriptionEntry>(); }
        public LayoutModeSettings Mode(bool compact) { return compact ? Widget : Expanded; }
        public LayoutSettings Copy() { return Parse(ToJson()); }
        public string ToJson() { return new JavaScriptSerializer().Serialize(this); }

        public static LayoutSettings Defaults()
        {
            return new LayoutSettings { Expanded = DefaultMode(false), Widget = DefaultMode(true) };
        }
        public static LayoutModeSettings DefaultMode(bool compact)
        {
            LayoutModeSettings mode = new LayoutModeSettings { Columns = compact ? 1 : 3 };
            for (int number = 1; number <= 4; number++)
                mode.Cards.Add(new LayoutCardSettings { Id = "account" + number,
                    Sections = new List<string>(compact ? new[] { "short", "weekly", "credits" } :
                        new[] { "short", "weekly", "credits", "stats", "calendar" }) });
            mode.Cards.Add(new LayoutCardSettings { Id = "pc", Sections = new List<string>(new[] { "cpu", "gpu", "ram", "disk", "network" }) });
            return mode;
        }
        public static LayoutSettings Parse(string json)
        {
            LayoutSettings parsed = null;
            if (!String.IsNullOrWhiteSpace(json) && json.Length <= 65536)
            {
                try { parsed = new JavaScriptSerializer().Deserialize<LayoutSettings>(json); }
                catch (Exception) { }
            }
            if (parsed == null || parsed.Version != 1) return Defaults();
            parsed.Expanded = Normalize(parsed.Expanded, false);
            parsed.Widget = Normalize(parsed.Widget, true);
            parsed.Subscriptions = SubscriptionEntry.Normalize(parsed.Subscriptions);
            return parsed;
        }
        private static LayoutModeSettings Normalize(LayoutModeSettings input, bool compact)
        {
            if (input == null) return DefaultMode(compact);
            LayoutModeSettings defaults = DefaultMode(compact);
            input.Columns = Math.Max(1, Math.Min(3, input.Columns));
            List<LayoutCardSettings> cards = new List<LayoutCardSettings>();
            foreach (LayoutCardSettings card in input.Cards ?? new List<LayoutCardSettings>())
            {
                if (card == null || !defaults.Cards.Any(value => value.Id == card.Id) || cards.Any(value => value.Id == card.Id)) continue;
                card.Span = Math.Max(1, Math.Min(3, card.Span));
                card.Size = Math.Max(0, Math.Min(2, card.Size));
                card.ItemLayouts = (card.ItemLayouts ?? new List<LayoutItemSettings>()).Where(item => item != null && item.Normalize())
                    .GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => group.First()).Take(128).ToList();
                card.HiddenItems = (card.HiddenItems ?? new List<string>()).Where(id => !String.IsNullOrWhiteSpace(id) && id.Length <= 80)
                    .Distinct(StringComparer.Ordinal).Take(128).ToList();
                List<string> allowed = defaults.Card(card.Id).Sections;
                card.Sections = card.Sections == null ? new List<string>(allowed) : card.Sections.Where(allowed.Contains).Distinct().ToList();
                cards.Add(card);
            }
            foreach (LayoutCardSettings card in defaults.Cards)
                if (!cards.Any(value => value.Id == card.Id)) cards.Add(card);
            input.Cards = cards;
            return input;
        }
    }

    internal static class LayoutSettingsStore
    {
        private const string KeyPath = "Software\\CodexUsageMeter";
        public static LayoutSettings Load()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                return LayoutSettings.Parse(key == null ? null : Convert.ToString(key.GetValue("LayoutV1")));
        }
        public static void Save(LayoutSettings settings)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                if (key == null) throw new InvalidOperationException("배치 설정을 저장할 수 없습니다.");
                key.SetValue("LayoutV1", settings.ToJson(), RegistryValueKind.String);
            }
        }
    }
}
