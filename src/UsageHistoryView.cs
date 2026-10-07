using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexUsageMeter
{
    internal sealed class UsageHistoryView : UserControl
    {
        private readonly UsageHistoryStore _store;
        private readonly ComboBox _accounts = new ComboBox { MinWidth = 90, Margin = new Thickness(6, 0, 8, 0) };
        private readonly ComboBox _range = new ComboBox { Width = 100, Margin = new Thickness(0, 0, 6, 0) };
        private readonly ComboBox _mode = new ComboBox { Width = 130, Margin = new Thickness(0, 0, 6, 0) };
        private readonly TextBlock _status = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 9, Margin = new Thickness(0, 4, 0, 0) };
        private readonly TextBlock _empty = new TextBlock { TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, MaxWidth = 400, TextAlignment = TextAlignment.Center, Margin = new Thickness(8), FontSize = 11 };
        private readonly DataGrid _table;
        private readonly Button _backButton;
        private readonly UsageHistoryChart _chart = new UsageHistoryChart();
        private List<UsageHistorySample> _samples = new List<UsageHistorySample>();
        private string _selectedKey;
        private bool _loading, _readError, _metadataError;

        internal UsageHistoryView(UsageHistoryStore store, string selectedKey, Action back)
        {
            _store = store; _selectedKey = selectedKey;
            FrameworkElementFactory accountLabel = new FrameworkElementFactory(typeof(TextBlock));
            accountLabel.SetBinding(TextBlock.TextProperty, new Binding("Label"));
            accountLabel.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            _accounts.ItemTemplate = new DataTemplate { VisualTree = accountLabel };
            Foreground = Brush("#F4F4F5"); FontFamily = new FontFamily("Malgun Gothic"); FontSize = 11;
            Grid root = new Grid(); Content = root;
            for (int i = 0; i < 4; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            DockPanel heading = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            _backButton = Button("← 현재", back); DockPanel.SetDock(_backButton, Dock.Right); heading.Children.Add(_backButton);
            heading.Children.Add(new TextBlock { Text = "이력", FontSize = 13, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            heading.Children.Add(_accounts);
            root.Children.Add(heading);

            StackPanel filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) }; Grid.SetRow(filters, 1); root.Children.Add(filters);
            filters.Children.Add(_range); filters.Children.Add(_mode);
            foreach (string label in new[] { "최근 7일", "최근 30일", "최근 90일", "전체 기간" }) _range.Items.Add(label);
            _range.SelectedIndex = 1;
            foreach (string label in new[] { "잔여량 그래프", "기록 표", "초기화 전" }) _mode.Items.Add(label);
            _mode.SelectedIndex = 0;
            _mode.ToolTip = "그래프는 연속 조회 구간만 연결합니다. 초기화 전 값은 각 주기의 마지막 확인값입니다.";
            Button refresh = Button("↻", delegate { Reload(); }); refresh.ToolTip = "이력 새로고침"; filters.Children.Add(refresh);
            _accounts.SelectionChanged += delegate { if (!_loading) { var account = _accounts.SelectedItem as UsageHistoryAccount; _selectedKey = account == null ? null : account.Key; ReadSelected(); } };
            _range.SelectionChanged += delegate { if (!_loading) Render(); };
            _mode.SelectionChanged += delegate { if (!_loading) Render(); };
            SizeChanged += delegate {
                bool singleRow = ActualWidth >= 560 && ActualHeight < 260;
                Grid.SetRow(filters, singleRow ? 0 : 1);
                filters.HorizontalAlignment = singleRow ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                heading.HorizontalAlignment = singleRow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
                heading.Width = singleRow ? ActualWidth - 280 : Double.NaN;
            };

            Grid body = new Grid { ClipToBounds = true }; Grid.SetRow(body, 2); root.Children.Add(body);
            _table = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
                CanUserReorderColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = Brush("#35353C"), Background = Brush("#1C1C20"), Foreground = Foreground,
                RowBackground = Brush("#1C1C20"), AlternatingRowBackground = Brush("#232329"), BorderBrush = Brush("#3B3B42"),
                RowHeight = 28, MinRowHeight = 28, ColumnHeaderHeight = 30, SelectionMode = DataGridSelectionMode.Single,
                EnableRowVirtualization = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            Style header = new Style(typeof(DataGridColumnHeader)); header.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#303039")));
            header.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#E2E8F0"))); header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 6, 10, 6)));
            _table.ColumnHeaderStyle = header;
            Style cell = new Style(typeof(DataGridCell)); cell.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            cell.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
            Trigger selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#255B4C"))); selected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            cell.Triggers.Add(selected); _table.CellStyle = cell;
            body.Children.Add(_chart); body.Children.Add(_table); body.Children.Add(_empty);
            Grid.SetRow(_status, 3); root.Children.Add(_status);
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { e.Handled = true; back(); } };
            Reload();
        }

        internal void SelectAccount(string key) { _selectedKey = key; Reload(); }

        internal void Reload()
        {
            _loading = true;
            try
            {
                var accounts = _store.Accounts();
                _metadataError = accounts.Any(a => a.MetadataUnavailable);
                _accounts.ItemsSource = accounts;
                _accounts.SelectedItem = accounts.FirstOrDefault(a => a.Key == _selectedKey);
                _loading = false; ReadSelected();
            }
            catch (Exception) { ShowError(); }
            finally { _loading = false; }
        }

        private void ReadSelected()
        {
            try { _samples = _selectedKey == null ? new List<UsageHistorySample>() : _store.Read(_selectedKey); _readError = false; Render(); }
            catch (Exception) { ShowError(); }
        }

        private void ShowError()
        {
            _readError = true;
            _samples.Clear(); _chart.SetSamples(_samples, DateTime.MinValue);
            _chart.Visibility = _table.Visibility = Visibility.Collapsed; _table.ItemsSource = null; _empty.Visibility = Visibility.Visible;
            _empty.Text = "이력 파일을 읽지 못했습니다. 기존 파일은 그대로 보존되어 있습니다.";
            _status.Text = "기록을 확인한 뒤 다시 열어 주세요. 현재 사용량 조회는 계속됩니다."; _status.Foreground = Brush("#FBBF24");
        }

        private void Render()
        {
            if (_table == null) return;
            if (_readError) { ShowError(); return; }
            DateTime now = DateTime.UtcNow;
            int days = new[] { 7, 30, 90, 0 }[Math.Max(0, _range.SelectedIndex)];
            DateTime since = days == 0 ? DateTime.MinValue : now.AddDays(-days);
            _chart.Visibility = _mode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            _table.Visibility = _mode.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
            _table.Columns.Clear();
            int count;
            if (_mode.SelectedIndex == 0)
            {
                _table.ItemsSource = null; _chart.SetSamples(_samples, since);
                count = _samples.Count(s => s.LastUtc >= since);
            }
            else if (_mode.SelectedIndex == 2)
            {
                Column("한도", "Window", 72); Column("마지막 잔여", "Remaining", 88); Column("예정 초기화", "Reset", 136);
                Column("마지막 확인", "Observed", 136); Column("확인 간격", "Gap", 110); Column("상태", "State", 210); Column("플랜", "Plan", 80);
                var rows = UsageHistoryStore.Periods(_samples).Where(p => p.LastUtc >= since).OrderByDescending(p => p.LastUtc)
                    .Select(p => UsageHistoryResetRow.From(p, now)).ToList();
                _table.ItemsSource = rows; count = rows.Count;
            }
            else
            {
                Column("마지막 확인", "Observed", 148); Column("단기 잔여", "Primary", 88); Column("단기 초기화", "PrimaryReset", 136);
                Column("주간 잔여", "Secondary", 88); Column("주간 초기화", "SecondaryReset", 136); Column("플랜", "Plan", 88);
                Column("같은 값 처음 확인", "First", 148);
                var rows = _samples.Where(s => s.LastUtc >= since).OrderByDescending(s => s.LastUtc).Select(s => new UsageHistorySampleRow {
                    Observed = Stamp(s.LastUtc), First = Stamp(s.FirstUtc), Plan = s.Plan,
                    Primary = Percent(s.Primary), Secondary = Percent(s.Secondary),
                    PrimaryReset = Stamp(s.Primary == null ? null : s.Primary.ResetsAtUtc), SecondaryReset = Stamp(s.Secondary == null ? null : s.Secondary.ResetsAtUtc) }).ToList();
                _table.ItemsSource = rows; count = rows.Count;
            }
            _empty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _empty.Text = _samples.Count == 0 ? "아직 이력이 없습니다.\n사용량 조회 성공 후 자동으로 기록됩니다.\n저장된 다른 계정도 위에서 선택할 수 있습니다." : "선택한 기간에 기록이 없습니다.\n전체 기간을 선택해 주세요.";
            DateTime? latest = _samples.Count == 0 ? null : (DateTime?)_samples.Max(s => s.LastUtc);
            _status.Foreground = Brush("#B2BAC5");
            _status.Text = count + "개 · " + (latest.HasValue ? "최근 " + Stamp(latest) : "조회 후 기록됩니다") +
                (latest.HasValue && now - latest.Value > TimeSpan.FromMinutes(3) ? " · 오래된 기록" : "") + (_metadataError ? " · 이름 읽기 오류" : "");
            _status.ToolTip = "PC 현지 시각 · 60초 조회 시 기록 · 그래프는 기록이 있는 시간 범위에 맞춥니다.\n" +
                "2분을 넘는 공백·초기화·플랜 변경은 연결하지 않습니다. 선 위에 마우스를 올리면 확인 시각과 수치가 표시됩니다.\n" +
                "초기화 전 값은 마지막 확인값이며 미관측 시간의 값은 알 수 없습니다." +
                (_metadataError ? "\n일부 계정 이름을 읽지 못했습니다. 정상 계정의 이력과 원본은 보존했습니다." : "");
        }

        private void Column(string title, string property, double width)
        {
            Style text = new Style(typeof(TextBlock)); text.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(8, 0, 8, 0)));
            text.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            _table.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(property), Width = width, ElementStyle = text });
        }

        private static Button Button(string label, Action action)
        {
            Button button = new Button { Content = label, Padding = new Thickness(8, 4, 8, 4), FontSize = 11 };
            button.Click += delegate { action(); }; return button;
        }

        internal static string Stamp(DateTime? value) { return value.HasValue ? value.Value.ToLocalTime().ToString("yy/MM/dd HH:mm:ss") : "—"; }
        internal static string Percent(UsageHistoryQuota quota) { return quota == null ? "미확인" : quota.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%"; }
        private static Brush Brush(string color) { return (Brush)new BrushConverter().ConvertFromString(color); }
    }

    internal sealed class UsageHistorySampleRow
    {
        public string Observed { get; set; }
        public string First { get; set; }
        public string Plan { get; set; }
        public string Primary { get; set; }
        public string Secondary { get; set; }
        public string PrimaryReset { get; set; }
        public string SecondaryReset { get; set; }
    }

    internal sealed class UsageHistoryResetRow
    {
        public string Window { get; set; }
        public string Remaining { get; set; }
        public string Reset { get; set; }
        public string Observed { get; set; }
        public string Gap { get; set; }
        public string State { get; set; }
        public string Plan { get; set; }

        internal static UsageHistoryResetRow From(UsageHistoryPeriod period, DateTime now)
        {
            DateTime? reset = period.Quota.ResetsAtUtc;
            string state = period.EndKind == "scheduled" ? "초기화 후 새 한도 확인" : period.EndKind == "changed" ? "잔여량·플랜·한도 변경" :
                !reset.HasValue ? "초기화 시각 미제공" : reset.Value <= now ? "예정 시각 지남 · 갱신 미확인" : "진행 중";
            string gap = "—";
            if (period.EndKind == "changed") gap = "변경 시각 미확인";
            else if (reset.HasValue)
            {
                TimeSpan distance = reset.Value - period.LastUtc;
                gap = distance.TotalDays >= 1 ? distance.TotalDays.ToString("0.#") + "일 전" :
                    distance.TotalHours >= 1 ? distance.TotalHours.ToString("0.#") + "시간 전" :
                    distance.TotalMinutes >= 1 ? Math.Ceiling(distance.TotalMinutes) + "분 전" : Math.Ceiling(distance.TotalSeconds) + "초 전";
            }
            int minutes = period.Quota.DurationMinutes;
            return new UsageHistoryResetRow { Window = minutes >= 8640 ? "주간" : minutes % 60 == 0 ? (minutes / 60) + "시간" : minutes + "분",
                Remaining = UsageHistoryView.Percent(period.Quota), Reset = UsageHistoryView.Stamp(reset), Observed = UsageHistoryView.Stamp(period.LastUtc),
                Gap = gap, State = state, Plan = period.Plan };
        }
    }
}
