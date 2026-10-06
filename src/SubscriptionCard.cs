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

    internal sealed class SubscriptionCardView
    {
        private readonly bool _compact;
        private readonly StackPanel _items = new StackPanel();
        private readonly TextBlock _title;
        private readonly TextBlock _footer;
        private readonly Button _manage;
        private List<SubscriptionEntry> _lastEntries;
        private DateTime _lastDay;
        private double _lastScale;
        internal Border Card { get; private set; }
        internal SubscriptionCardView(bool compact, Action manage)
        {
            _compact = compact;
            Card = new Border { Background = Brush("#1B1B1B"), BorderBrush = Brush("#343434"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(compact ? 12 : 14) };
            Card.SetValue(TextElement.FontFamilyProperty, new FontFamily("Malgun Gothic"));
            Card.SetValue(TextElement.ForegroundProperty, Brush("#F4F4F5"));
            DarkTheme.Apply(Card);
            Grid body = new Grid(); Card.Child = body;
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition());
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            DockPanel header = new DockPanel { Margin = new Thickness(0, 0, 0, 7) }; body.Children.Add(header);
            _manage = new Button { Content = "관리", Padding = new Thickness(10, 5, 10, 5), VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "구독 서비스와 갱신일 관리" };
            _manage.Click += delegate { if (manage != null) manage(); };
            DockPanel.SetDock(_manage, Dock.Right); header.Children.Add(_manage);
            _title = new TextBlock { Text = "구독 갱신", FontWeight = FontWeights.SemiBold, Foreground = Brush("#ECECEE"), VerticalAlignment = VerticalAlignment.Center };
            header.Children.Add(_title);
            ScrollViewer scroll = new ScrollViewer { Content = _items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false, Padding = new Thickness(0, 0, 4, 0) };
            Grid.SetRow(scroll, 1); body.Children.Add(scroll);
            _footer = new TextBlock { Foreground = Brush("#A1A1AA"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) };
            Grid.SetRow(_footer, 2); body.Children.Add(_footer);
        }
        internal void Update(IList<SubscriptionEntry> entries, DateTime today, double fontScale)
        {
            double scale = Double.IsNaN(fontScale) || Double.IsInfinity(fontScale) ? 1.5 : Math.Max(1, Math.Min(2, fontScale));
            List<SubscriptionEntry> normalized = SubscriptionEntry.Normalize(entries);
            if (_lastEntries != null && _lastDay == today.Date && _lastScale == scale && _lastEntries.Count == normalized.Count &&
                _lastEntries.Zip(normalized, (a, b) => a.Id == b.Id && a.Name == b.Name && a.AnchorDate == b.AnchorDate && a.Cycle == b.Cycle).All(equal => equal)) return;
            _lastEntries = normalized; _lastDay = today.Date; _lastScale = scale;
            double ratio = scale / 1.5;
            _title.FontSize = 16 * ratio; _manage.FontSize = 12 * ratio; _footer.FontSize = 11 * ratio;
            _items.Children.Clear();
            if (normalized.Count == 0)
            {
                _items.Children.Add(new TextBlock { Text = "등록된 구독이 없습니다.\n오른쪽 위 관리에서 서비스를 추가해 주세요.",
                    FontSize = 13 * ratio, Foreground = Brush("#B6B6BF"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 10) });
                _footer.Text = "다음 갱신일과 남은 날짜를 한눈에 확인하세요.";
                return;
            }
            var ordered = normalized.Select(entry => new { Entry = entry, Date = entry.NextRenewal(today) })
                .OrderBy(item => item.Date).ThenBy(item => item.Entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var item in ordered.Take(_compact ? 3 : 100))
            {
                int days = (item.Date - today.Date).Days;
                string countdown = days < 0 ? (-days).ToString(CultureInfo.InvariantCulture) + "일 지남" : days == 0 ? "오늘" : "D-" + days.ToString(CultureInfo.InvariantCulture);
                Border row = new Border { BorderBrush = Brush("#303030"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 6, 0, 8),
                    ToolTip = item.Entry.Name + " · " + item.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " · " + countdown };
                Grid content = new Grid(); row.Child = content;
                content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                StackPanel label = new StackPanel { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                label.Children.Add(new TextBlock { Text = item.Entry.Name, FontSize = 14 * ratio, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                label.Children.Add(new TextBlock { Text = item.Date.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture) + " · " + SubscriptionEntry.CycleLabel(item.Entry.Cycle),
                    FontSize = 11 * ratio, Foreground = Brush("#A1A1AA"), Margin = new Thickness(0, 3, 0, 0) });
                content.Children.Add(label);
                Border badge = new Border { Background = Brush(days < 0 ? "#3B2C23" : days <= 7 ? "#203B33" : "#28282D"), CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(8, 5, 8, 5), VerticalAlignment = VerticalAlignment.Center };
                badge.Child = new TextBlock { Text = countdown, FontSize = 12 * ratio, FontWeight = FontWeights.SemiBold,
                    Foreground = Brush(days < 0 ? "#E8BA78" : days <= 7 ? "#8DE0C4" : "#C6C6CF") };
                Grid.SetColumn(badge, 1); content.Children.Add(badge); _items.Children.Add(row);
            }
            _footer.Text = _compact && normalized.Count > 3 ? "외 " + (normalized.Count - 3) + "개 · 전체 목록은 관리에서 확인하세요." : "기준일과 주기에 따른 예정일입니다.";
        }
        private static SolidColorBrush Brush(string hex) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
    }

    internal sealed class SubscriptionEditor : Window
    {
        private sealed class EditRow
        {
            internal SubscriptionEntry Entry;
            internal TextBox Name;
            internal TextBox Date;
            internal ComboBox Cycle;
            internal Border View;
        }
        private readonly List<EditRow> _rows = new List<EditRow>();
        private readonly Action<List<SubscriptionEntry>> _save;
        private readonly StackPanel _list = new StackPanel();
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _count = new TextBlock();
        private readonly TextBlock _empty;
        private readonly Button _add;
        private readonly ScrollViewer _scroll;
        internal bool Saved { get; private set; }
        internal SubscriptionEditor(IList<SubscriptionEntry> original, Action<List<SubscriptionEntry>> save)
        {
            if (save == null) throw new ArgumentNullException("save");
            _save = save;
            Title = "구독 갱신 관리"; Width = 660; Height = Math.Max(420, Math.Min(740, SystemParameters.WorkArea.Height - 40));
            MinWidth = 540; MinHeight = 400; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResizeWithGrip;
            Background = Brush("#181818"); Foreground = Brush("#F4F4F5"); FontFamily = new FontFamily("Malgun Gothic"); FontSize = 13;
            DarkTheme.Apply(this);
            Grid root = new Grid { Margin = new Thickness(22) }; Content = root;
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            StackPanel top = new StackPanel(); root.Children.Add(top);
            DockPanel title = new DockPanel(); top.Children.Add(title);
            Button close = MakeButton("×", delegate { Close(); }); close.ToolTip = "닫기";
            DockPanel.SetDock(close, Dock.Right); title.Children.Add(close);
            _add = MakeButton("구독 추가", delegate {
                EditRow row = AddRow(new SubscriptionEntry { AnchorDate = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
                UpdateCount(); _status.Text = ""; _scroll.ScrollToEnd();
                if (row != null && IsVisible) row.Name.Focus();
            });
            DockPanel.SetDock(_add, Dock.Right); title.Children.Add(_add);
            TextBlock heading = new TextBlock { Text = Title, FontSize = 23, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
            heading.MouseLeftButtonDown += delegate { DragMove(); }; title.Children.Add(heading);
            top.Children.Add(new TextBlock { Text = "서비스 이름과 기준 갱신일을 등록하세요. 매월·매년 일정은\n기준일의 날짜를 유지하며 다음 갱신일을 계산합니다.",
                Foreground = Brush("#AAAAB3"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 16) });
            _empty = new TextBlock { Text = "아직 등록된 구독이 없습니다.\n구독 추가를 눌러 첫 번째 서비스를 등록해 주세요.",
                Foreground = Brush("#AAAAB3"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(10, 42, 10, 20) };
            _list.Children.Add(_empty);
            _scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false, Padding = new Thickness(0, 0, 5, 0) };
            Grid.SetRow(_scroll, 1); root.Children.Add(_scroll);
            StackPanel bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; Grid.SetRow(bottom, 2); root.Children.Add(bottom);
            _status.Foreground = Brush("#E8BA78"); bottom.Children.Add(_status);
            DockPanel actions = new DockPanel { Margin = new Thickness(0, 8, 0, 0) }; bottom.Children.Add(actions);
            Button cancel = MakeButton("취소", delegate { Close(); }); cancel.IsCancel = true;
            DockPanel.SetDock(cancel, Dock.Right); actions.Children.Add(cancel);
            Button saveButton = MakeButton("구독 저장", delegate { if (TrySave()) Close(); });
            saveButton.Background = Brush("#285844"); saveButton.IsDefault = true;
            DockPanel.SetDock(saveButton, Dock.Right); actions.Children.Add(saveButton);
            _count.Foreground = Brush("#AAAAB3"); _count.VerticalAlignment = VerticalAlignment.Center; actions.Children.Add(_count);
            bottom.Children.Add(new TextBlock { Text = "취소하거나 창을 닫으면 변경 내용이 저장되지 않습니다.", Foreground = Brush("#92929B"),
                FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
            foreach (SubscriptionEntry entry in SubscriptionEntry.Normalize(original)) AddRow(entry);
            UpdateCount();
        }
        internal bool TrySave()
        {
            Saved = false;
            List<SubscriptionEntry> draft = new List<SubscriptionEntry>();
            for (int index = 0; index < _rows.Count; index++)
            {
                EditRow row = _rows[index];
                if (String.IsNullOrWhiteSpace(row.Name.Text)) return Invalid(row.Name, (index + 1) + "번째 서비스 이름을 입력해 주세요.");
                DateTime date;
                if (!SubscriptionEntry.TryParseDate(row.Date.Text, out date))
                    return Invalid(row.Date, (index + 1) + "번째 기준 날짜를 YYYY-MM-DD 형식으로 확인해 주세요. 예: 2026-10-06");
                if (row.Cycle.SelectedIndex < 0 || row.Cycle.SelectedIndex > 2) return Invalid(row.Cycle, (index + 1) + "번째 갱신 주기를 선택해 주세요.");
                SubscriptionEntry entry = row.Entry.Copy();
                entry.Name = row.Name.Text; entry.AnchorDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                entry.Cycle = row.Cycle.SelectedIndex == 0 ? "monthly" : row.Cycle.SelectedIndex == 1 ? "yearly" : "once";
                draft.Add(entry);
            }
            try { _save(SubscriptionEntry.Normalize(draft)); Saved = true; _status.Text = ""; return true; }
            catch (Exception) { _status.Text = "저장하지 못했습니다. 변경 내용은 유지됩니다. 잠시 후 다시 저장해 주세요."; return false; }
        }
        private bool Invalid(Control field, string message)
        {
            _status.Text = message;
            if (IsVisible) { field.BringIntoView(); field.Focus(); }
            return false;
        }
        private EditRow AddRow(SubscriptionEntry entry)
        {
            if (_rows.Count >= 100) return null;
            EditRow row = new EditRow { Entry = entry.Copy() };
            row.View = new Border { Background = Brush("#242424"), BorderBrush = Brush("#3B3B3F"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 10) };
            StackPanel body = new StackPanel(); row.View.Child = body;
            DockPanel nameLine = new DockPanel(); body.Children.Add(nameLine);
            Button remove = MakeButton("삭제", delegate { _rows.Remove(row); _list.Children.Remove(row.View); UpdateCount(); _status.Text = ""; });
            remove.ToolTip = "이 구독을 목록에서 삭제"; remove.VerticalAlignment = VerticalAlignment.Bottom; remove.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(remove, Dock.Right); nameLine.Children.Add(remove);
            row.Name = new TextBox { Text = entry.Name, MaxLength = 80, VerticalContentAlignment = VerticalAlignment.Center };
            nameLine.Children.Add(Field("서비스 이름", row.Name));
            Grid schedule = new Grid { Margin = new Thickness(0, 12, 0, 0) }; body.Children.Add(schedule);
            schedule.ColumnDefinitions.Add(new ColumnDefinition()); schedule.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
            row.Date = new TextBox { Text = entry.AnchorDate, MaxLength = 10, ToolTip = "YYYY-MM-DD 형식, 예: 2026-10-06" };
            StackPanel dateField = Field("기준 갱신일 · YYYY-MM-DD", row.Date); dateField.Margin = new Thickness(0, 0, 14, 0); schedule.Children.Add(dateField);
            row.Cycle = new ComboBox { ItemsSource = new[] { "매월", "매년", "한 번" }, SelectedIndex = entry.Cycle == "monthly" ? 0 : entry.Cycle == "yearly" ? 1 : 2,
                MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center };
            StackPanel cycleField = Field("갱신 주기", row.Cycle); Grid.SetColumn(cycleField, 1); schedule.Children.Add(cycleField);
            _rows.Add(row); _list.Children.Add(row.View); return row;
        }
        private void UpdateCount()
        {
            _empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _count.Text = _rows.Count + "개 등록 · 최대 100개"; _add.IsEnabled = _rows.Count < 100;
        }
        private static StackPanel Field(string label, Control control)
        {
            StackPanel field = new StackPanel();
            field.Children.Add(new TextBlock { Text = label, Foreground = Brush("#B5B5BE"), FontSize = 11, Margin = new Thickness(0, 0, 0, 5) });
            System.Windows.Automation.AutomationProperties.SetName(control, label);
            field.Children.Add(control); return field;
        }
        private static Button MakeButton(string title, Action action)
        {
            Button button = new Button { Content = title, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(7, 0, 0, 0) };
            button.Click += delegate { action(); }; return button;
        }
        private static SolidColorBrush Brush(string hex) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
    }
}
