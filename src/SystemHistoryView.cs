using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace CodexUsageMeter
{
    internal sealed class SystemHistoryView : HistoryView
    {
        private readonly SystemHistory _store;
        private readonly Func<DateTime> _clock;
        private readonly ComboBox _metric = new ComboBox { DisplayMemberPath = "Label", Margin = new Thickness(0, 6, 0, 6) };
        internal readonly SystemHistoryChart Chart = new SystemHistoryChart();
        private bool _loading;
        internal SystemHistoryView(SystemHistory store, Action back) : this(store, back, delegate { return DateTime.UtcNow; }) { }
        internal SystemHistoryView(SystemHistory store, Action back, Func<DateTime> clock)
        {
            _store = store; _clock = clock;
            Foreground = Brushes.White; FontFamily = new FontFamily("Malgun Gothic");
            var root = new DockPanel();
            var heading = new DockPanel();
            var button = new Button { Content = "← 현재" };
            button.Click += delegate { back(); };
            DockPanel.SetDock(button, Dock.Right); heading.Children.Add(button);
            heading.Children.Add(new TextBlock { Text = "이력 · 최근 30분", Foreground = Brushes.White });
            DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
            var legend = new TextBlock { Text = "빈 구간은 미측정 · 점은 실제 관측값", FontSize = 10, Foreground = Brushes.LightGray };
            DockPanel.SetDock(legend, Dock.Bottom); root.Children.Add(legend);
            DockPanel.SetDock(_metric, Dock.Top); root.Children.Add(_metric);
            root.Children.Add(Chart); Content = root;
            _metric.SelectionChanged += delegate { if (!_loading) Render(); };
            Reload();
        }
        internal override void Reload()
        {
            var selected = _metric.SelectedItem as SystemHistoryMetric;
            _loading = true;
            _metric.ItemsSource = _store.Read(_clock()).SelectMany(s => s.Metrics.Values).GroupBy(m => m.Key).Select(g => g.Last()).ToArray();
            _metric.SelectedItem = _metric.Items.Cast<SystemHistoryMetric>().FirstOrDefault(m => selected != null && m.Key == selected.Key);
            if (_metric.SelectedIndex < 0) _metric.SelectedIndex = 0;
            _loading = false;
            Render();
        }
        private void Render()
        {
            Chart.SetSamples(_store.Read(_clock()), _metric.SelectedItem as SystemHistoryMetric, _clock());
        }
    }
    internal sealed class SystemHistoryChart : FrameworkElement
    {
        private List<SystemHistorySample> _samples = new List<SystemHistorySample>();
        private SystemHistoryMetric _metric;
        internal DateTime Start { get; private set; }
        internal DateTime End { get; private set; }
        internal double Maximum { get; private set; }
        internal string AxisUnit { get; private set; }
        private double _displayDivisor = 1;
        internal List<List<Point>> Segments { get; private set; }
        internal Rect Plot { get; private set; }
        internal SystemHistoryChart() { ClipToBounds = true; MinHeight = 90; Segments = new List<List<Point>>(); }
        internal void SetSamples(List<SystemHistorySample> samples, SystemHistoryMetric metric, DateTime now)
        {
            _samples = samples; _metric = metric; End = now; Start = now.AddMinutes(-30);
            InvalidateVisual();
        }
        internal void BuildGeometry(Size size)
        {
            Plot = new Rect(52, 22, Math.Max(1, size.Width - 68), Math.Max(1, size.Height - 48));
            var visible = _samples.Where(s => s.Time >= Start && s.Time <= End).ToArray();
            Maximum = _metric != null && _metric.Unit == "%" ? 100 : Math.Max(1,
                visible.Select(s => Value(s)).Where(v => v.HasValue).Select(v => v.Value).DefaultIfEmpty(0).Max());
            AxisUnit = _metric == null ? "" : _metric.Unit;
            _displayDivisor = 1;
            if (AxisUnit == "B/s")
            {
                if (Maximum >= 1073741824) { _displayDivisor = 1073741824; AxisUnit = "GiB/s"; }
                else if (Maximum >= 1048576) { _displayDivisor = 1048576; AxisUnit = "MiB/s"; }
                else if (Maximum >= 1024) { _displayDivisor = 1024; AxisUnit = "KiB/s"; }
            }
            Segments = new List<List<Point>>();
            List<Point> segment = null; DateTime? previous = null;
            foreach (var s in visible)
            {
                double? value = Value(s);
                if (!value.HasValue) { segment = null; previous = null; continue; }
                // Two seconds represents a missed tick in the one-second collector.
                if (segment == null || (previous.HasValue && s.Time - previous.Value >= TimeSpan.FromSeconds(2)))
                { segment = new List<Point>(); Segments.Add(segment); }
                segment.Add(new Point(Plot.Left + (s.Time - Start).TotalSeconds / 1800 * Plot.Width,
                    Plot.Bottom - Math.Max(0, Math.Min(Maximum, value.Value)) / Maximum * Plot.Height));
                previous = s.Time;
            }
        }
        private double? Value(SystemHistorySample sample)
        {
            double? value;
            return _metric != null && sample.Values.TryGetValue(_metric.Key, out value) ? value : null;
        }
        private void Text(DrawingContext dc, string text, Point point)
        {
            dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Malgun Gothic"), 10, Brushes.LightGray, VisualTreeHelper.GetDpi(this).PixelsPerDip), point);
        }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc); BuildGeometry(RenderSize);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(28, 28, 32)), null, new Rect(RenderSize));
            var grid = new Pen(Brushes.DimGray, 0.5);
            for (int i = 0; i <= 3; i++)
            {
                double x = Plot.Left + i * Plot.Width / 3;
                dc.DrawLine(grid, new Point(x, Plot.Top), new Point(x, Plot.Bottom));
                Text(dc, Start.AddMinutes(i * 10).ToLocalTime().ToString("HH:mm"), new Point(x - 14, Plot.Bottom + 5));
            }
            for (int i = 0; i <= 2; i++)
            {
                double y = Plot.Bottom - Plot.Height * i / 2;
                dc.DrawLine(grid, new Point(Plot.Left, y), new Point(Plot.Right, y));
                Text(dc, (Maximum * i / 2 / _displayDivisor).ToString("0.##"), new Point(2, y - 6));
            }
            Text(dc, _metric == null ? "아직 기록이 없습니다." : AxisUnit, new Point(2, 2));
            var line = new Pen(Brushes.DeepSkyBlue, 1.5);
            foreach (var segment in Segments)
            {
                for (int i = 1; i < segment.Count; i++) dc.DrawLine(line, segment[i - 1], segment[i]);
                foreach (var point in segment) dc.DrawEllipse(Brushes.DeepSkyBlue, null, point, 1.5, 1.5);
            }
            if (_metric != null && Segments.Count == 0)
                Text(dc, "측정 가능한 기록이 없습니다.", new Point(Plot.Left + 6, Plot.Top + 6));
        }
    }
}
