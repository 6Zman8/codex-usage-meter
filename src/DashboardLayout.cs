using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CodexUsageMeter
{
    internal sealed class LayoutTile
    {
        public Border Card;
        public Viewbox Host;
        public Grid Content;
        public Grid Surface;
        public UsageHistoryView History;
        public bool ShowingHistory { get { return History != null && History.Visibility == Visibility.Visible; } }
        public Func<List<LayoutContentItem>> ContentItems;
        public readonly Dictionary<FrameworkElement, Visibility> SuppressedItems = new Dictionary<FrameworkElement, Visibility>();
        public LayoutCardSettings Settings;
        public double MinimumHeight;
        public bool KeepNaturalHeight;
        public Rect Bounds;
    }

    // Fit complete rows to the viewport; preserve card edges while scaling their contents.
    internal sealed class CardLayoutPanel : Panel
    {
        internal readonly List<LayoutTile> Tiles = new List<LayoutTile>();
        internal int Columns = 1;
        internal double MinimumCardWidth = 360;
        private const double Gap = 10;

        internal LayoutTile Add(Border card)
        {
            card.Margin = new Thickness(0);
            Grid content = (Grid)card.Child;
            card.Child = null;
            Grid surface = new Grid(); surface.Children.Add(content);
            Viewbox contentHost = new Viewbox { Child = surface, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
            card.Child = contentHost;
            Viewbox host = new Viewbox { Child = card, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
            LayoutTile tile = new LayoutTile { Card = card, Host = host, Content = content, Surface = surface };
            surface.LayoutUpdated += delegate { CardContentLayout.Apply(tile); };
            Tiles.Add(tile); Children.Add(host); return tile;
        }
        protected override Size MeasureOverride(Size available)
        {
            double width = Double.IsInfinity(available.Width) ? MinimumCardWidth : Math.Max(1, available.Width);
            List<LayoutTile> visible = Tiles.Where(tile => tile.Settings != null && tile.Settings.Visible && tile.Card.Visibility == Visibility.Visible).ToList();
            int columns = Math.Max(1, Columns);
            columns = Math.Max(1, Math.Min(columns, visible.Sum(tile => Math.Min(columns, tile.Settings.Span))));
            double horizontalGap = Math.Min(Gap, width / (columns * 4));
            double unit = (width - horizontalGap * (columns - 1)) / columns;
            List<List<LayoutTile>> rows = new List<List<LayoutTile>>();
            List<LayoutTile> row = null;
            int occupied = columns;
            foreach (LayoutTile tile in visible)
            {
                int span = Math.Min(columns, tile.Settings.Span);
                if (occupied + span > columns) { row = new List<LayoutTile>(); rows.Add(row); occupied = 0; }
                double cardWidth = unit * span + horizontalGap * (span - 1);
                double logicalWidth = Math.Max(MinimumCardWidth, cardWidth);
                double scale = cardWidth / logicalWidth;
                double height = tile.MinimumHeight * (tile.Settings.Size == 0 ? 1 : tile.Settings.Size == 2 ? 1.5 : 1.15) * scale;
                tile.Bounds = new Rect(occupied * (unit + horizontalGap), 0, cardWidth, height);
                tile.Card.Width = logicalWidth;
                row.Add(tile); occupied += span;
            }
            foreach (List<LayoutTile> items in rows)
            {
                foreach (IGrouping<int, LayoutTile> group in items.Where(tile => !tile.KeepNaturalHeight).GroupBy(tile => tile.Settings.Size))
                {
                    double alignedHeight = group.Max(tile => tile.Bounds.Height);
                    foreach (LayoutTile tile in group) tile.Bounds = new Rect(tile.Bounds.X, 0, tile.Bounds.Width, alignedHeight);
                }
            }
            double naturalHeight = rows.Sum(items => items.Max(tile => tile.Bounds.Height));
            double availableHeight = Double.IsInfinity(available.Height) ? naturalHeight + Gap * Math.Max(0, rows.Count - 1) : Math.Max(1, available.Height);
            double gap = rows.Count < 2 ? 0 : Math.Min(Gap, availableHeight / (rows.Count * 4));
            double fit = naturalHeight == 0 ? 1 : Math.Min(1, Math.Max(1, availableHeight - gap * Math.Max(0, rows.Count - 1)) / naturalHeight);
            double top = 0;
            foreach (List<LayoutTile> items in rows)
            {
                double height = items.Max(tile => tile.Bounds.Height) * fit;
                foreach (LayoutTile tile in items)
                {
                    tile.Bounds = new Rect(tile.Bounds.X, top, tile.Bounds.Width, tile.Bounds.Height * fit);
                    tile.Card.Height = tile.Bounds.Height * tile.Card.Width / tile.Bounds.Width;
                    Thickness padding = tile.Card.Padding, border = tile.Card.BorderThickness;
                    double innerWidth = Math.Max(1, tile.Card.Width - padding.Left - padding.Right - border.Left - border.Right);
                    double innerHeight = Math.Max(1, tile.Card.Height - padding.Top - padding.Bottom - border.Top - border.Bottom);
                    tile.Content.Width = innerWidth;
                    bool custom = tile.Settings.ItemLayouts.Count > 0;
                    tile.Content.VerticalAlignment = custom ? VerticalAlignment.Top : VerticalAlignment.Stretch;
                    tile.Content.Height = Double.NaN;
                    tile.Content.Measure(new Size(innerWidth, Double.PositiveInfinity));
                    tile.Content.Height = Math.Max(innerHeight, tile.Content.DesiredSize.Height);
                    tile.Surface.Width = innerWidth;
                    tile.Surface.Height = tile.ShowingHistory ? Math.Max(170, innerHeight) : custom ? innerHeight : tile.Content.Height;
                    tile.Surface.ClipToBounds = custom;
                    tile.Host.Visibility = Visibility.Visible;
                    tile.Host.Measure(tile.Bounds.Size);
                }
                top += height + gap;
            }
            foreach (LayoutTile tile in Tiles.Except(visible)) { tile.Host.Visibility = Visibility.Collapsed; tile.Host.Measure(new Size(0, 0)); }
            return new Size(width, Math.Max(0, top - (rows.Count > 0 ? gap : 0)));
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            foreach (LayoutTile tile in Tiles.Where(tile => tile.Host.Visibility == Visibility.Visible))
            {
                tile.Host.Arrange(tile.Bounds); CardContentLayout.Apply(tile);
            }
            return finalSize;
        }
    }

    internal sealed class DashboardLayoutView
    {
        private readonly Window _window;
        private readonly CardLayoutPanel[] _panels = new CardLayoutPanel[2];
        private readonly LayoutTile[,] _tiles = new LayoutTile[2, 3];
        internal DateTime RenderDate { get; private set; }
        private readonly TextBlock[] _empty = new TextBlock[2];
        private readonly Grid[] _compactQuotas = new Grid[2];
        private readonly Viewbox[] _compactQuotaHosts = new Viewbox[2];
        private bool _editing;

        internal IEnumerable<LayoutTile> Tiles(bool compact) { return _panels[compact ? 1 : 0].Tiles; }

        internal void ShowHistory(int slot, bool compact, UsageHistoryView history)
        {
            HideHistory(slot, compact);
            LayoutTile tile = _tiles[compact ? 1 : 0, slot];
            tile.History = history; tile.Content.Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
            history.Visibility = _editing ? Visibility.Collapsed : Visibility.Visible;
            tile.Surface.Children.Add(history); _panels[compact ? 1 : 0].InvalidateMeasure();
        }

        internal void HideHistory(int slot, bool compact)
        {
            LayoutTile tile = _tiles[compact ? 1 : 0, slot];
            if (tile.History == null) return;
            tile.Surface.Children.Remove(tile.History); tile.History = null;
            tile.Content.Visibility = Visibility.Visible; _panels[compact ? 1 : 0].InvalidateMeasure();
        }

        internal void ReloadHistory()
        {
            foreach (LayoutTile tile in _tiles) if (tile.History != null) tile.History.Reload();
        }

        internal void SetEditing(bool editing)
        {
            _editing = editing;
            foreach (LayoutTile tile in _tiles)
                if (tile.History != null)
                {
                    tile.History.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
                    tile.Content.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
                }
            foreach (CardLayoutPanel panel in _panels) panel.InvalidateMeasure();
        }

        internal DashboardLayoutView(Window window)
        {
            _window = window;
            for (int mode = 0; mode < 2; mode++)
            {
                string prefix = mode == 1 ? "Compact" : "";
                Border first = Find<Border>(prefix + "Account1Card"), second = Find<Border>(prefix + "Account2Card");
                Grid body = (Grid)first.Parent;
                Border pc = body.Children.OfType<Border>().Single(item => item != first && item != second);
                body.Children.Clear(); body.RowDefinitions.Clear(); body.ColumnDefinitions.Clear();
                CardLayoutPanel panel = new CardLayoutPanel { MinimumCardWidth = mode == 1 ? 390 : 400 };
                _panels[mode] = panel;
                _tiles[mode, 0] = panel.Add(first); _tiles[mode, 1] = panel.Add(second); _tiles[mode, 2] = panel.Add(pc);
                body.Children.Add(panel);
                _empty[mode] = new TextBlock { Text = "표시할 카드가 없습니다.\n⚙ 설정 → 배치 편집에서 카드를 켜 주세요.", TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 175)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20), Visibility = Visibility.Collapsed };
                body.Children.Add(_empty[mode]);
            }
            for (int slot = 0; slot < 2; slot++)
            {
                Grid card = _tiles[1, slot].Content;
                Grid quota = card.Children.OfType<Grid>().Single(item => Grid.GetRow(item) == 1);
                card.Children.Remove(quota);
                Viewbox host = new Viewbox { Child = quota, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
                Grid.SetRow(host, 1); card.Children.Add(host);
                _compactQuotas[slot] = quota; _compactQuotaHosts[slot] = host;
                quota.SetBinding(FrameworkElement.WidthProperty, new System.Windows.Data.Binding("Width") { Source = card });
            }
            foreach (LayoutTile tile in _tiles) tile.ContentItems = delegate { return DescribeItems(tile); };
            _tiles[1, 2].Content.LayoutUpdated += delegate { UpdatePcChartGuides(_tiles[1, 2]); };
        }

        private List<LayoutContentItem> DescribeItems(LayoutTile tile)
        {
            var result = new List<LayoutContentItem>();
            Action<string, string, FrameworkElement> add = (id, label, element) => {
                if (element != null) result.Add(new LayoutContentItem { Id = id, Label = label, Element = element });
            };
            bool compact = tile == _tiles[1, 0] || tile == _tiles[1, 1] || tile == _tiles[1, 2];
            bool pc = tile == _tiles[0, 2] || tile == _tiles[1, 2];
            Grid body = tile.Content;
            add("header", pc ? "제목·상태" : "제목·계정 버튼", body.Children.OfType<Grid>().FirstOrDefault(child => Grid.GetRow(child) == 0));
            if (!pc && !compact)
            {
                string[] keys = { "header", "short", "weekly", "credits", "stats", "calendar", "status" };
                string[] names = { "제목", "5시간 한도", "주간 한도", "초기화권·구독", "사용 통계", "달력·7일", "상태 안내" };
                foreach (FrameworkElement child in body.Children)
                    if (Grid.GetRow(child) > 0) add(keys[Grid.GetRow(child)], names[Grid.GetRow(child)], child);
            }
            else if (!pc)
            {
                int slot = tile == _tiles[1, 0] ? 0 : 1;
                foreach (FrameworkElement child in _compactQuotas[slot].Children)
                {
                    int column = Grid.GetColumn(child);
                    add(column == 0 ? "weekly" : column == 2 ? "short" : "countdown", column == 0 ? "주간 원형 게이지" : column == 2 ? "5시간 원형 게이지" : "갱신까지", child);
                }
                string prefix = "CompactAccount" + (slot + 1);
                add("credits", "초기화권", Find<TextBlock>(prefix + "ResetValue"));
                add("subscription", "구독 날짜", Find<TextBlock>(prefix + "SubscriptionValue"));
            }
            else
            {
                UniformGrid items = body.Children.OfType<UniformGrid>().Single();
                string[] keys = { "cpu", "gpu", "ram", "disk" }, names = { "CPU", "GPU", "RAM", "디스크" };
                for (int i = 0; i < items.Children.Count; i++)
                {
                    FrameworkElement child = (FrameworkElement)items.Children[i];
                    string key = compact ? keys[i] : Convert.ToString(child.Tag);
                    string label = compact ? names[i] : System.Windows.Automation.AutomationProperties.GetName(child);
                    if (String.IsNullOrWhiteSpace(label)) label = key == "memory" ? "RAM" : key.ToUpperInvariant();
                    add(key, label, child);
                }
                add(compact ? "network" : "status", compact ? "네트워크" : "상태 안내",
                    body.Children.OfType<FrameworkElement>().FirstOrDefault(child => Grid.GetRow(child) == 2));
            }
            return result;
        }

        internal void Apply(LayoutSettings settings, AccountView[] views, double fontScale)
        {
            RenderDate = DateTime.Today;
            for (int mode = 0; mode < 2; mode++)
            {
                LayoutModeSettings layout = settings.Mode(mode == 1);
                CardLayoutPanel panel = _panels[mode]; panel.Columns = layout.Columns;
                panel.MinimumCardWidth = (mode == 1 ? 390 : 400) * Math.Max(1, fontScale / 1.5);
                for (int slot = 0; slot < 2; slot++)
                {
                    AccountView view = views[slot];
                    LayoutTile tile = _tiles[mode, slot];
                    CardContentLayout.RestoreVisibility(tile);
                    tile.Settings = layout.Card("account" + (view.State == null ? slot + 1 : view.State.Number));
                    tile.Card.Visibility = view.State != null && tile.Settings.Visible ? Visibility.Visible : Visibility.Collapsed;
                    bool shortQuota = tile.Settings.Shows("short"), weekly = tile.Settings.Shows("weekly");
                    if (mode == 0) ApplyExpandedAccount(tile, shortQuota, weekly, fontScale);
                    else ApplyCompactAccount(slot, tile, shortQuota, weekly, fontScale);
                    CardContentLayout.ApplyVisibility(tile);
                }
                LayoutTile pc = _tiles[mode, 2]; pc.Settings = layout.Card("pc");
                CardContentLayout.RestoreVisibility(pc);
                pc.Card.Visibility = pc.Settings.Visible ? Visibility.Visible : Visibility.Collapsed;
                if (mode == 0) ApplyExpandedPc(pc, fontScale); else ApplyCompactPc(pc, fontScale);
                CardContentLayout.ApplyVisibility(pc);
                panel.Tiles.Sort((a, b) => layout.Cards.IndexOf(a.Settings).CompareTo(layout.Cards.IndexOf(b.Settings)));
                _empty[mode].Visibility = panel.Tiles.Any(tile => tile.Card.Visibility == Visibility.Visible) ? Visibility.Collapsed : Visibility.Visible;
                panel.InvalidateMeasure();
            }
        }
        private static void ApplyExpandedAccount(LayoutTile tile, bool shortQuota, bool weekly, double fontScale)
        {
            Grid body = tile.Content;
            bool[] show = { true, shortQuota, weekly, tile.Settings.Shows("credits"), tile.Settings.Shows("stats"), tile.Settings.Shows("calendar") };
            string[] ids = { "header", "short", "weekly", "credits", "stats", "calendar" };
            for (int row = 1; row <= 5; row++) show[row] = show[row] && tile.Settings.ShowsItem(ids[row]);
            for (int row = 1; row <= 5; row++)
            {
                foreach (FrameworkElement child in body.Children)
                    if (Grid.GetRow(child) == row)
                    {
                        child.Visibility = show[row] ? Visibility.Visible : Visibility.Collapsed;
                        Thickness margin = child.Margin; margin.Top = 14; child.Margin = margin;
                    }
                body.RowDefinitions[row].Height = !show[row] ? new GridLength(0) : row == 5 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            }
            tile.MinimumHeight = (95 + (show[1] ? 82 : 0) + (show[2] ? 82 : 0) + (show[3] ? 82 : 0) + (show[4] ? 62 : 0)) * Math.Max(1, fontScale / 1.3) + (show[5] ? 235 : 0);
        }
        private void ApplyCompactAccount(int slot, LayoutTile tile, bool shortQuota, bool weekly, double fontScale)
        {
            Grid quota = _compactQuotas[slot];
            bool shortRing = shortQuota && tile.Settings.ShowsItem("short"), weeklyRing = weekly && tile.Settings.ShowsItem("weekly");
            bool countdownVisible = (shortQuota || weekly) && tile.Settings.ShowsItem("countdown");
            bool hasQuota = shortRing || weeklyRing || countdownVisible;
            bool single = shortRing != weeklyRing;
            foreach (UIElement item in quota.Children)
            {
                int column = Grid.GetColumn(item);
                item.Visibility = (column == 0 ? weeklyRing : column == 2 ? shortRing : countdownVisible) ? Visibility.Visible : Visibility.Collapsed;
                if (column == 4)
                {
                    // Keep the original content size so saved item layouts do not shrink on upgrade.
                    FrameworkElement countdown = (FrameworkElement)item;
                    countdown.Width = single ? (weeklyRing ? 208 : 212) : 132;
                    countdown.HorizontalAlignment = HorizontalAlignment.Center;
                    quota.ColumnDefinitions[4].MinWidth = countdownVisible ? countdown.Width : 0;
                }
            }
            quota.ColumnDefinitions[0].Width = weeklyRing ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            quota.ColumnDefinitions[0].MinWidth = weeklyRing ? (single ? 144 : 120) : 0;
            quota.ColumnDefinitions[1].Width = new GridLength(weeklyRing && (shortRing || countdownVisible) ? 18 : 0);
            quota.ColumnDefinitions[2].Width = shortRing ? new GridLength(single ? 1 : 0.8, GridUnitType.Star) : new GridLength(0);
            quota.ColumnDefinitions[2].MinWidth = shortRing ? (single ? 144 : 96) : 0;
            quota.ColumnDefinitions[3].Width = new GridLength(shortRing && countdownVisible ? 20 : 0);
            quota.ColumnDefinitions[4].Width = countdownVisible ? new GridLength(1.2, GridUnitType.Star) : new GridLength(0);
            quota.MinWidth = quota.ColumnDefinitions[0].MinWidth + quota.ColumnDefinitions[1].Width.Value +
                quota.ColumnDefinitions[2].MinWidth + quota.ColumnDefinitions[3].Width.Value + quota.ColumnDefinitions[4].MinWidth;
            quota.Height = 136 * Math.Max(1, fontScale / 1.5);
            foreach (Canvas canvas in quota.Children.OfType<Canvas>())
            {
                double ringScale = single ? 132 / canvas.Width : 1;
                canvas.LayoutTransform = new ScaleTransform(ringScale, ringScale);
            }
            _compactQuotaHosts[slot].Visibility = hasQuota ? Visibility.Visible : Visibility.Collapsed;
            string prefix = "CompactAccount" + (slot + 1);
            ((FrameworkElement)Find<ProgressBar>(prefix + "PrimaryTimeBar").Parent).Visibility = shortQuota ? Visibility.Visible : Visibility.Collapsed;
            ((FrameworkElement)Find<ProgressBar>(prefix + "SecondaryTimeBar").Parent).Visibility = weekly ? Visibility.Visible : Visibility.Collapsed;
            bool credits = tile.Settings.Shows("credits") && tile.Settings.ShowsItem("credits");
            Find<TextBlock>(prefix + "ResetValue").Visibility = credits ? Visibility.Visible : Visibility.Collapsed;
            Grid body = tile.Content;
            body.RowDefinitions[1].Height = hasQuota ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            tile.MinimumHeight = (85 + (credits ? 23 : 0)) * Math.Max(1, fontScale / 1.5) + (hasQuota ? 132 : 0);
        }
        private void ApplyExpandedPc(LayoutTile tile, double fontScale)
        {
            UniformGrid items = Find<UniformGrid>("PerformanceItemsPanel");
            foreach (FrameworkElement item in items.Children)
            {
                string key = Convert.ToString(item.Tag).Split(':')[0];
                if (key == "memory") key = "ram";
                item.Visibility = tile.Settings.Shows(key) && tile.Settings.ShowsItem(Convert.ToString(item.Tag)) ? Visibility.Visible : Visibility.Collapsed;
            }
            int count = items.Children.Cast<UIElement>().Count(item => item.Visibility == Visibility.Visible);
            items.Columns = count >= 6 ? 2 : 1;
            tile.MinimumHeight = 100 * Math.Max(1, fontScale / 1.3) + Math.Max(1, (int)Math.Ceiling(count / (double)items.Columns)) * 87 * Math.Max(1, fontScale / 1.5);
        }
        private void ApplyCompactPc(LayoutTile tile, double fontScale)
        {
            Grid body = tile.Content;
            UniformGrid items = body.Children.OfType<UniformGrid>().Single();
            string[] keys = { "cpu", "gpu", "ram", "disk" };
            for (int n = 0; n < keys.Length; n++) items.Children[n].Visibility = tile.Settings.Shows(keys[n]) && tile.Settings.ShowsItem(keys[n]) ? Visibility.Visible : Visibility.Collapsed;
            foreach (StackPanel metric in items.Children)
            {
                TextBlock[] texts = metric.Children.OfType<TextBlock>().ToArray();
                FormattedText name = MeasurePcText(texts[0], "CPU GPU RAM 디스크 0");
                FormattedText value = MeasurePcText(texts[1], "100%");
                // Reserve the complete percentage field, never the current reading's width.
                // CardContentLayout must not rescale a saved item when 9% becomes 10%.
                metric.Width = Math.Max(42, Math.Ceiling(value.WidthIncludingTrailingWhitespace) + 8);
                texts[0].Height = Math.Ceiling(name.Height);
                texts[1].Height = Math.Ceiling(value.Height);
            }
            int count = keys.Count(key => tile.Settings.Shows(key) && tile.Settings.ShowsItem(key));
            items.Rows = 1; items.Columns = Math.Max(1, count);
            TextBlock tick = Find<Canvas>("CompactPcChartGuides").Children.OfType<TextBlock>().First();
            double tickInset = Math.Ceiling(MeasurePcText(tick, "100%").Height / 2);
            items.Margin = new Thickness(32 * Math.Max(1, fontScale / 1.5), 8, 0, tickInset);
            tile.KeepNaturalHeight = true;
            items.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
            body.RowDefinitions[1].Height = count == 0 ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Find<TextBlock>("CompactNetworkValue").Visibility = tile.Settings.Shows("network") ? Visibility.Visible : Visibility.Collapsed;
            items.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity));
            tile.MinimumHeight = (50 + (tile.Settings.Shows("network") ? 25 : 0)) * Math.Max(1, fontScale / 1.5) +
                (count == 0 ? 0 : items.DesiredSize.Height);
        }

        private static FormattedText MeasurePcText(TextBlock text, string sample)
        {
            return new FormattedText(sample, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, text.Foreground);
        }

        private void UpdatePcChartGuides(LayoutTile tile)
        {
            Canvas guides = Find<Canvas>("CompactPcChartGuides");
            Rect[] tracks = new[] { "Cpu", "Gpu", "Memory", "Disk" }.Select(name => Find<Border>("Compact" + name + "Track"))
                .Where(track => track.IsVisible && track.ActualHeight > 0)
                .Select(track => track.TransformToAncestor(tile.Content).TransformBounds(new Rect(track.RenderSize))).ToArray();
            // A shared percentage axis is meaningful only while the visible tracks
            // share a top and baseline. Free placement keeps its saved coordinates.
            bool aligned = tracks.Length > 0 && tracks.All(track =>
                Math.Abs(track.Top - tracks[0].Top) < 0.1 && Math.Abs(track.Bottom - tracks[0].Bottom) < 0.1);
            guides.Visibility = aligned ? Visibility.Visible : Visibility.Hidden;
            if (!aligned) return;
            Point origin = guides.TranslatePoint(new Point(), tile.Content);
            double left = tile.Content.Children.OfType<UniformGrid>().Single().Margin.Left;
            Line[] lines = guides.Children.OfType<Line>().ToArray();
            TextBlock[] labels = guides.Children.OfType<TextBlock>().ToArray();
            for (int i = 0; i < lines.Length; i++)
            {
                double y = tracks[0].Top - origin.Y + tracks[0].Height * i / 4.0;
                lines[i].X1 = left; lines[i].X2 = guides.ActualWidth;
                lines[i].Y1 = lines[i].Y2 = y;
                labels[i].Width = left - 5;
                Canvas.SetLeft(labels[i], 0);
                Canvas.SetTop(labels[i], y - labels[i].ActualHeight / 2);
            }
        }
        private T Find<T>(string name) where T : FrameworkElement { return (T)_window.FindName(name); }
    }
}
