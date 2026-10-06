using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace CodexUsageMeter
{
    internal sealed class LayoutTile
    {
        public Border Card;
        public Viewbox Host;
        public LayoutCardSettings Settings;
        public double MinimumHeight;
        public Rect Bounds;
    }

    // The content keeps its readable design width when a window is extremely narrow.
    // Normal windows reflow; very small windows scale the card and scroll vertically.
    internal sealed class CardLayoutPanel : Panel
    {
        internal readonly List<LayoutTile> Tiles = new List<LayoutTile>();
        internal int Columns = 1;
        internal double MinimumCardWidth = 360;
        private const double Gap = 10;

        internal LayoutTile Add(Border card)
        {
            card.Margin = new Thickness(0);
            Viewbox host = new Viewbox { Child = card, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
            LayoutTile tile = new LayoutTile { Card = card, Host = host };
            Tiles.Add(tile); Children.Add(host); return tile;
        }
        protected override Size MeasureOverride(Size available)
        {
            double width = Double.IsInfinity(available.Width) ? MinimumCardWidth : Math.Max(1, available.Width);
            List<LayoutTile> visible = Tiles.Where(tile => tile.Settings != null && tile.Settings.Visible && tile.Card.Visibility == Visibility.Visible).ToList();
            int columns = Math.Max(1, Math.Min(Columns, (int)Math.Floor((width + Gap) / (MinimumCardWidth + Gap))));
            columns = Math.Max(1, Math.Min(columns, visible.Sum(tile => Math.Min(columns, tile.Settings.Span))));
            double unit = Math.Max(1, (width - Gap * (columns - 1)) / columns);
            List<List<LayoutTile>> rows = new List<List<LayoutTile>>();
            List<LayoutTile> row = null;
            int occupied = columns;
            foreach (LayoutTile tile in visible)
            {
                int span = Math.Min(columns, tile.Settings.Span);
                if (occupied + span > columns) { row = new List<LayoutTile>(); rows.Add(row); occupied = 0; }
                double cardWidth = unit * span + Gap * (span - 1);
                double logicalWidth = Math.Max(MinimumCardWidth, cardWidth);
                double scale = cardWidth / logicalWidth;
                double height = tile.MinimumHeight * (tile.Settings.Size == 0 ? 1 : tile.Settings.Size == 2 ? 1.5 : 1.15) * scale;
                tile.Bounds = new Rect(occupied * (unit + Gap), 0, cardWidth, height);
                tile.Card.Width = logicalWidth;
                row.Add(tile); occupied += span;
            }
            double top = 0;
            foreach (List<LayoutTile> items in rows)
            {
                foreach (IGrouping<int, LayoutTile> group in items.GroupBy(tile => tile.Settings.Size))
                {
                    double alignedHeight = group.Max(tile => tile.Bounds.Height);
                    foreach (LayoutTile tile in group) tile.Bounds = new Rect(tile.Bounds.X, 0, tile.Bounds.Width, alignedHeight);
                }
                double height = items.Max(tile => tile.Bounds.Height);
                foreach (LayoutTile tile in items)
                {
                    tile.Bounds = new Rect(tile.Bounds.X, top, tile.Bounds.Width, tile.Bounds.Height);
                    tile.Card.Height = tile.Bounds.Height * tile.Card.Width / tile.Bounds.Width;
                    tile.Host.Visibility = Visibility.Visible;
                    tile.Host.Measure(tile.Bounds.Size);
                }
                top += height + Gap;
            }
            foreach (LayoutTile tile in Tiles.Except(visible)) { tile.Host.Visibility = Visibility.Collapsed; tile.Host.Measure(new Size(0, 0)); }
            return new Size(width, Math.Max(0, top - (rows.Count > 0 ? Gap : 0)));
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            foreach (LayoutTile tile in Tiles.Where(tile => tile.Host.Visibility == Visibility.Visible)) tile.Host.Arrange(tile.Bounds);
            return finalSize;
        }
    }

    internal sealed class DashboardLayoutView
    {
        private readonly Window _window;
        private readonly CardLayoutPanel[] _panels = new CardLayoutPanel[2];
        private readonly LayoutTile[,] _tiles = new LayoutTile[2, 4];
        private readonly SubscriptionCardView[] _subscriptions = new SubscriptionCardView[2];
        internal event Action ManageSubscriptions;
        internal DateTime RenderDate { get; private set; }
        private readonly TextBlock[] _empty = new TextBlock[2];
        private readonly Grid[] _compactQuotas = new Grid[2];
        private readonly Viewbox[] _compactQuotaHosts = new Viewbox[2];

        internal IEnumerable<LayoutTile> Tiles(bool compact) { return _panels[compact ? 1 : 0].Tiles; }

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
                _subscriptions[mode] = new SubscriptionCardView(mode == 1, delegate { if (ManageSubscriptions != null) ManageSubscriptions(); });
                _tiles[mode, 3] = panel.Add(_subscriptions[mode].Card);
                ScrollViewer scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false, Padding = new Thickness(0) };
                body.Children.Add(scroll);
                _empty[mode] = new TextBlock { Text = "표시할 카드가 없습니다.\n⚙ 설정 → 배치 편집에서 카드를 켜 주세요.", TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 175)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20), Visibility = Visibility.Collapsed };
                body.Children.Add(_empty[mode]);
            }
            for (int slot = 0; slot < 2; slot++)
            {
                Grid card = (Grid)_tiles[1, slot].Card.Child;
                Grid quota = card.Children.OfType<Grid>().Single(item => Grid.GetRow(item) == 1);
                card.Children.Remove(quota);
                Viewbox host = new Viewbox { Child = quota, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
                Grid.SetRow(host, 1); card.Children.Add(host);
                _compactQuotas[slot] = quota; _compactQuotaHosts[slot] = host;
            }
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
                    tile.Settings = layout.Card("account" + (view.State == null ? slot + 1 : view.State.Number));
                    tile.Card.Visibility = view.State != null && tile.Settings.Visible ? Visibility.Visible : Visibility.Collapsed;
                    bool shortQuota = tile.Settings.Shows("short"), weekly = tile.Settings.Shows("weekly");
                    AccountSnapshot snapshot = view.LastSnapshot;
                    if (layout.HideUnavailable && snapshot != null && snapshot.IsAuthenticated && String.IsNullOrEmpty(snapshot.Error))
                    {
                        if (snapshot.Secondary != null && snapshot.Primary == null) shortQuota = false;
                        if (snapshot.Primary != null && snapshot.Secondary == null) weekly = false;
                    }
                    if (mode == 0) ApplyExpandedAccount(tile, shortQuota, weekly, fontScale);
                    else ApplyCompactAccount(slot, tile, shortQuota, weekly, fontScale);
                }
                LayoutTile pc = _tiles[mode, 2]; pc.Settings = layout.Card("pc");
                pc.Card.Visibility = pc.Settings.Visible ? Visibility.Visible : Visibility.Collapsed;
                if (mode == 0) ApplyExpandedPc(pc, fontScale); else ApplyCompactPc(pc, fontScale);
                LayoutTile subscription = _tiles[mode, 3]; subscription.Settings = layout.Card("subscriptions");
                subscription.Card.Visibility = subscription.Settings.Visible ? Visibility.Visible : Visibility.Collapsed;
                subscription.MinimumHeight = (mode == 1 ? 245 : 320) * Math.Max(1, fontScale / 1.5);
                _subscriptions[mode].Update(settings.Subscriptions, RenderDate, fontScale);
                panel.Tiles.Sort((a, b) => layout.Cards.IndexOf(a.Settings).CompareTo(layout.Cards.IndexOf(b.Settings)));
                _empty[mode].Visibility = panel.Tiles.Any(tile => tile.Card.Visibility == Visibility.Visible) ? Visibility.Collapsed : Visibility.Visible;
                panel.InvalidateMeasure();
            }
        }
        private static void ApplyExpandedAccount(LayoutTile tile, bool shortQuota, bool weekly, double fontScale)
        {
            Grid body = (Grid)tile.Card.Child;
            bool[] show = { true, shortQuota, weekly, tile.Settings.Shows("credits"), tile.Settings.Shows("stats"), tile.Settings.Shows("calendar") };
            for (int row = 1; row <= 5; row++)
            {
                foreach (UIElement child in body.Children) if (Grid.GetRow(child) == row) child.Visibility = show[row] ? Visibility.Visible : Visibility.Collapsed;
                body.RowDefinitions[row].Height = !show[row] ? new GridLength(0) : row == 5 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            }
            tile.MinimumHeight = (95 + (shortQuota ? 82 : 0) + (weekly ? 82 : 0) + (show[3] ? 60 : 0) + (show[4] ? 62 : 0)) * Math.Max(1, fontScale / 1.3) + (show[5] ? 235 : 0);
        }
        private void ApplyCompactAccount(int slot, LayoutTile tile, bool shortQuota, bool weekly, double fontScale)
        {
            Grid quota = _compactQuotas[slot];
            bool single = shortQuota != weekly;
            foreach (UIElement item in quota.Children)
            {
                int column = Grid.GetColumn(item);
                item.Visibility = (column == 0 ? weekly : column == 2 ? shortQuota : shortQuota || weekly) ? Visibility.Visible : Visibility.Collapsed;
            }
            quota.ColumnDefinitions[0].Width = new GridLength(weekly ? (single ? 144 : 120) : 0);
            quota.ColumnDefinitions[1].Width = new GridLength(weekly ? (single ? 16 : 8) : 0);
            quota.ColumnDefinitions[2].Width = new GridLength(shortQuota ? (single ? 144 : 96) : 0);
            quota.ColumnDefinitions[3].Width = new GridLength(shortQuota ? 12 : 0);
            quota.Width = 368;
            quota.Height = 136 * Math.Max(1, fontScale / 1.5);
            foreach (Canvas canvas in quota.Children.OfType<Canvas>())
            {
                double ringScale = single ? 132 / canvas.Width : 1;
                canvas.LayoutTransform = new ScaleTransform(ringScale, ringScale);
                StackPanel labels = canvas.Children.OfType<StackPanel>().FirstOrDefault();
                if (labels != null)
                {
                    labels.Measure(new Size(labels.Width, Double.PositiveInfinity));
                    Canvas.SetTop(labels, Math.Max(0, (canvas.Height - labels.DesiredSize.Height) / 2));
                }
            }
            _compactQuotaHosts[slot].Visibility = shortQuota || weekly ? Visibility.Visible : Visibility.Collapsed;
            string prefix = "CompactAccount" + (slot + 1);
            ((FrameworkElement)Find<ProgressBar>(prefix + "PrimaryTimeBar").Parent).Visibility = shortQuota ? Visibility.Visible : Visibility.Collapsed;
            ((FrameworkElement)Find<ProgressBar>(prefix + "SecondaryTimeBar").Parent).Visibility = weekly ? Visibility.Visible : Visibility.Collapsed;
            Find<TextBlock>(prefix + "ResetValue").Visibility = tile.Settings.Shows("credits") ? Visibility.Visible : Visibility.Collapsed;
            Grid body = (Grid)tile.Card.Child;
            body.RowDefinitions[1].Height = shortQuota || weekly ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            tile.MinimumHeight = (65 + (tile.Settings.Shows("credits") ? 23 : 0)) * Math.Max(1, fontScale / 1.5) + (shortQuota || weekly ? 132 : 0);
        }
        private void ApplyExpandedPc(LayoutTile tile, double fontScale)
        {
            UniformGrid items = Find<UniformGrid>("PerformanceItemsPanel");
            foreach (FrameworkElement item in items.Children)
            {
                string key = Convert.ToString(item.Tag).Split(':')[0];
                if (key == "memory") key = "ram";
                item.Visibility = tile.Settings.Shows(key) ? Visibility.Visible : Visibility.Collapsed;
            }
            int count = items.Children.Cast<UIElement>().Count(item => item.Visibility == Visibility.Visible);
            items.Columns = count >= 6 ? 2 : 1;
            tile.MinimumHeight = 100 * Math.Max(1, fontScale / 1.3) + Math.Max(1, (int)Math.Ceiling(count / (double)items.Columns)) * 87 * Math.Max(1, fontScale / 1.5);
        }
        private void ApplyCompactPc(LayoutTile tile, double fontScale)
        {
            Grid body = (Grid)tile.Card.Child;
            UniformGrid items = body.Children.OfType<UniformGrid>().Single();
            string[] keys = { "cpu", "gpu", "ram", "disk" };
            for (int n = 0; n < keys.Length; n++) items.Children[n].Visibility = tile.Settings.Shows(keys[n]) ? Visibility.Visible : Visibility.Collapsed;
            int count = keys.Count(tile.Settings.Shows);
            items.Rows = Math.Max(1, (count + 1) / 2); items.Columns = count == 1 ? 1 : 2;
            items.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
            body.RowDefinitions[1].Height = count == 0 ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Find<TextBlock>("CompactNetworkValue").Visibility = tile.Settings.Shows("network") ? Visibility.Visible : Visibility.Collapsed;
            tile.MinimumHeight = (50 + (tile.Settings.Shows("network") ? 25 : 0)) * Math.Max(1, fontScale / 1.5) + ((count + 1) / 2) * 108;
        }
        private T Find<T>(string name) where T : FrameworkElement { return (T)_window.FindName(name); }
    }
}
