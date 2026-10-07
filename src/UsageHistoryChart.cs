using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexUsageMeter
{
    internal sealed class UsageHistoryChartSpan
    {
        internal DateTime FirstUtc, LastUtc;
        internal DateTime ObservedFirstUtc;
        internal UsageHistoryQuota Quota;
        internal string Plan;
        internal bool Connected;
    }

    // Draw from the saved observation intervals; gaps and changed quota windows stay separate.
    internal sealed class UsageHistoryChart : FrameworkElement
    {
        private static readonly Brush Mint = ColorBrush("#72D4B5"), Purple = ColorBrush("#B59AFB"), Muted = ColorBrush("#AEB7C4");
        private readonly Typeface _typeface = new Typeface("Malgun Gothic");
        private DrawingGroup _drawing;
        private Rect _plot;
        private DateTime _start, _end;
        private UsageHistorySample _latest;
        private UsageHistoryChartSpan _hover;
        private bool _hoverSecondary;
        internal List<UsageHistoryChartSpan> Primary { get; private set; }
        internal List<UsageHistoryChartSpan> Secondary { get; private set; }

        internal UsageHistoryChart()
        {
            Primary = new List<UsageHistoryChartSpan>(); Secondary = new List<UsageHistoryChartSpan>();
            ClipToBounds = true;
            MouseMove += Hover;
            MouseLeave += delegate { _hover = null; ToolTip = null; InvalidateVisual(); };
            SizeChanged += delegate { _drawing = null; InvalidateVisual(); };
        }

        internal void SetSamples(List<UsageHistorySample> samples, DateTime since)
        {
            Primary = Series(samples, since, false); Secondary = Series(samples, since, true);
            _latest = samples.LastOrDefault(s => s.LastUtc >= since);
            var all = Primary.Concat(Secondary).ToList();
            if (all.Count > 0)
            {
                _start = all.Min(s => s.FirstUtc); _end = all.Max(s => s.LastUtc);
                // Give a lone observation room without extending its line into unobserved time.
                if (_start == _end) { _start = _start.AddSeconds(-30); _end = _end.AddSeconds(30); }
            }
            _hover = null; ToolTip = null; _drawing = null; InvalidateVisual();
        }

        internal static List<UsageHistoryChartSpan> Series(IEnumerable<UsageHistorySample> samples, DateTime since, bool secondary)
        {
            var result = new List<UsageHistoryChartSpan>();
            UsageHistoryChartSpan previous = null;
            foreach (UsageHistorySample sample in samples)
            {
                if (sample.LastUtc < since) continue;
                UsageHistoryQuota quota = secondary ? sample.Secondary : sample.Primary;
                if (quota == null) { previous = null; continue; }
                var span = new UsageHistoryChartSpan { FirstUtc = sample.FirstUtc < since ? since : sample.FirstUtc,
                    LastUtc = sample.LastUtc, ObservedFirstUtc = sample.FirstUtc, Quota = quota, Plan = sample.Plan };
                span.Connected = previous != null && span.FirstUtc >= previous.LastUtc &&
                    span.FirstUtc - previous.LastUtc <= TimeSpan.FromMinutes(2) && previous.Plan == span.Plan &&
                    previous.Quota.DurationMinutes == quota.DurationMinutes && previous.Quota.ResetsAtUtc == quota.ResetsAtUtc &&
                    quota.RemainingPercent <= previous.Quota.RemainingPercent;
                result.Add(span); previous = span;
            }
            return result;
        }

        internal Point Position(DateTime time, double remaining)
        {
            return new Point(_plot.Left + (time - _start).TotalSeconds / (_end - _start).TotalSeconds * _plot.Width,
                _plot.Bottom - remaining / 100 * _plot.Height);
        }

        protected override void OnRender(DrawingContext context)
        {
            base.OnRender(context);
            if (ActualWidth < 80 || ActualHeight < 65) return;
            if (_drawing == null) BuildDrawing();
            context.DrawDrawing(_drawing);
            if (_hover != null)
            {
                Point point = Position(_hover.LastUtc, _hover.Quota.RemainingPercent);
                context.DrawLine(new Pen(Muted, 0.6), new Point(point.X, _plot.Top), new Point(point.X, _plot.Bottom));
                context.DrawEllipse(_hoverSecondary ? Purple : Mint, new Pen(Brushes.White, 1), point, 4, 4);
            }
        }

        private void BuildDrawing()
        {
            _plot = new Rect(35, 29, Math.Max(1, ActualWidth - 43), Math.Max(1, ActualHeight - 57));
            _drawing = new DrawingGroup();
            using (DrawingContext dc = _drawing.Open())
            {
                dc.DrawRoundedRectangle(ColorBrush("#1A1C21"), null, new Rect(RenderSize), 6, 6);
                Legend(dc, "단기", _latest == null ? null : _latest.Primary, Mint, 8);
                Legend(dc, "주간", _latest == null ? null : _latest.Secondary, Purple, ActualWidth / 2);
                foreach (int percent in new[] { 0, 50, 100 })
                {
                    double y = _plot.Bottom - percent / 100.0 * _plot.Height;
                    dc.DrawLine(new Pen(ColorBrush("#353A43"), 0.6), new Point(_plot.Left, y), new Point(_plot.Right, y));
                    Text(dc, percent + "%", Muted, new Point(4, y - 7), 9);
                }
                if (Primary.Count + Secondary.Count == 0) return;
                string format = "MM/dd HH:mm";
                string left = _start.ToLocalTime().ToString(format), right = _end.ToLocalTime().ToString(format);
                Text(dc, left, Muted, new Point(_plot.Left, _plot.Bottom + 6), 9);
                FormattedText endText = Label(right, Muted, 9);
                dc.DrawText(endText, new Point(_plot.Right - endText.Width, _plot.Bottom + 6));
                if (_plot.Width > 480)
                {
                    DateTime mid = _start.AddTicks((_end - _start).Ticks / 2);
                    FormattedText middle = Label(mid.ToLocalTime().ToString(format), Muted, 9);
                    dc.DrawText(middle, new Point(_plot.Left + _plot.Width / 2 - middle.Width / 2, _plot.Bottom + 6));
                }
                DrawSeries(dc, Secondary, Purple); DrawSeries(dc, Primary, Mint);
            }
            _drawing.Freeze();
        }

        private void DrawSeries(DrawingContext dc, List<UsageHistoryChartSpan> spans, Brush brush)
        {
            Pen line = new Pen(brush, 1.8); line.Freeze();
            StreamGeometry path = new StreamGeometry();
            using (StreamGeometryContext geometry = path.Open())
            {
                foreach (UsageHistoryChartSpan span in spans)
                {
                    Point start = Position(span.FirstUtc, span.Quota.RemainingPercent), end = Position(span.LastUtc, span.Quota.RemainingPercent);
                    if (span.Connected) geometry.LineTo(start, true, false);
                    else geometry.BeginFigure(start, false, false);
                    geometry.LineTo(end, true, false);
                }
            }
            path.Freeze(); dc.DrawGeometry(null, line, path);
            for (int i = 0; i < spans.Count; i++)
            {
                UsageHistoryChartSpan span = spans[i];
                // Dots mark isolated observations and the ends of each continuous run.
                if (!span.Connected) dc.DrawEllipse(brush, null, Position(span.FirstUtc, span.Quota.RemainingPercent), 2.5, 2.5);
                if (i == spans.Count - 1 || !spans[i + 1].Connected)
                    dc.DrawEllipse(brush, null, Position(span.LastUtc, span.Quota.RemainingPercent), 2.5, 2.5);
            }
        }

        private void Legend(DrawingContext dc, string name, UsageHistoryQuota quota, Brush brush, double x)
        {
            if (name == "단기" && quota != null && quota.DurationMinutes % 60 == 0) name = quota.DurationMinutes / 60 + "시간";
            dc.DrawEllipse(brush, null, new Point(x + 4, 12), 3, 3);
            Text(dc, name + "  " + UsageHistoryView.Percent(quota), brush, new Point(x + 12, 4), 11);
        }

        private void Hover(object sender, MouseEventArgs e)
        {
            bool secondary;
            UsageHistoryChartSpan nearest = FindObservation(e.GetPosition(this), out secondary);
            if (nearest == _hover && secondary == _hoverSecondary) return;
            _hover = nearest; _hoverSecondary = secondary;
            ToolTip = nearest == null ? null : Describe(nearest, secondary);
            InvalidateVisual();
        }

        internal UsageHistoryChartSpan FindObservation(Point cursor, out bool secondary)
        {
            UsageHistoryChartSpan nearest = null; secondary = false; double distance = 12;
            if (_plot.Contains(cursor))
            {
                foreach (bool isSecondary in new[] { false, true })
                {
                    UsageHistoryChartSpan previous = null;
                    foreach (UsageHistoryChartSpan span in isSecondary ? Secondary : Primary)
                    {
                        Point start = Position(span.FirstUtc, span.Quota.RemainingPercent), end = Position(span.LastUtc, span.Quota.RemainingPercent);
                        double d = Distance(cursor, start, end);
                        if (d < distance) { distance = d; nearest = span; secondary = isSecondary; }
                        if (span.Connected && previous != null)
                        {
                            Point previousEnd = Position(previous.LastUtc, previous.Quota.RemainingPercent);
                            d = Distance(cursor, previousEnd, start);
                            if (d < distance)
                            {
                                distance = d; secondary = isSecondary;
                                nearest = (cursor - previousEnd).Length < (cursor - start).Length ? previous : span;
                            }
                        }
                        previous = span;
                    }
                }
            }
            return nearest;
        }

        private static double Distance(Point point, Point start, Point end)
        {
            Vector line = end - start;
            double ratio = line.LengthSquared == 0 ? 0 : Math.Max(0, Math.Min(1, Vector.Multiply(point - start, line) / line.LengthSquared));
            return (point - (start + line * ratio)).Length;
        }

        internal static string Describe(UsageHistoryChartSpan span, bool secondary)
        {
            return (secondary ? "주간" : "단기") + " 잔여 " + UsageHistoryView.Percent(span.Quota) +
                "\n마지막 확인 " + UsageHistoryView.Stamp(span.LastUtc) +
                (span.ObservedFirstUtc == span.LastUtc ? "" : "\n같은 값 시작 " + UsageHistoryView.Stamp(span.ObservedFirstUtc)) +
                "\n예정 초기화 " + UsageHistoryView.Stamp(span.Quota.ResetsAtUtc) + "\n플랜 " + span.Plan;
        }

        private FormattedText Label(string value, Brush color, double size)
        { return new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _typeface, size, color, VisualTreeHelper.GetDpi(this).PixelsPerDip); }
        private void Text(DrawingContext dc, string value, Brush color, Point origin, double size) { dc.DrawText(Label(value, color, size), origin); }
        private static Brush ColorBrush(string color) { Brush brush = (Brush)new BrushConverter().ConvertFromString(color); brush.Freeze(); return brush; }
    }
}
