using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace CodexUsageMeter
{
    internal static class SystemHistoryRegressionTests
    {
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        internal static void Run(Action<string> log, string testRoot)
        {
            var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var s = new SystemSnapshot { CpuAvailable = true, MemoryAvailable = true };
            for (int i = 0; i < 150; i++)
            {
                s.Gpus.Add(new GpuSnapshot { Key = "secret-g" + i, Name = "그래픽 장치 " + i, Available = true, Percent = i % 100 });
                s.Disks.Add(new DiskSnapshot { Key = "secret-d" + i, Name = "디스크 " + i, Available = true, ReadBytesPerSecond = i, WriteBytesPerSecond = i + 1 });
                s.Networks.Add(new NetworkSnapshot { Key = "secret-n" + i, Name = "이더넷 " + i, Connected = true, RatesAvailable = true, LinkSpeedBitsPerSecond = 1000, SendBytesPerSecond = i });
            }
            var store = new SystemHistory(); Require(store.Read(now).Count == 0, "Empty");
            store.Add(now.AddMinutes(-30), s); store.Add(now, null);
            Require(store.Read(now).Count == 2 && store.Read(now)[0].Values.Count == 1054, "All device metrics / inclusive cutoff");
            Require(store.Read(now)[0].Values["cpu"] == 0, "Real zero");
            Require(store.Read(now)[0].Values["disk:secret-d149:write"] == 150 && store.Read(now)[0].Values["network:secret-n149:send"] == 149, "Device identity");
            Require(store.Read(now.AddTicks(1)).Count == 1, "Actual timestamp cutoff");
            var chartStore = new SystemHistory();
            chartStore.Add(now.AddMinutes(-30), s);
            s.CpuPercent = 50; chartStore.Add(now.AddMinutes(-30).AddSeconds(1), s);
            s.CpuAvailable = false; s.Warning = null; chartStore.Add(now.AddMinutes(-30).AddSeconds(2), s);
            s.CpuAvailable = true; s.CpuPercent = 100; s.Warning = "CPU"; chartStore.Add(now, s);
            bool back = false;
            var view = new SystemHistoryView(chartStore, delegate { back = true; }, delegate { return now; });
            view.Measure(new Size(600, 240)); view.Arrange(new Rect(0, 0, 600, 240)); view.UpdateLayout();
            var chart = view.Chart; chart.BuildGeometry(new Size(600, 180));
            Require(chart.Start == now.AddMinutes(-30) && chart.End == now && chart.Maximum == 100, "Fixed 30 minute axis");
            Require(chart.Segments.Count == 2 && chart.Segments[0].Count == 2 && chart.Segments[1].Count == 1, "Missing sensor breaks line");
            Require(Math.Abs(chart.Segments[0][0].X - chart.Plot.Left) < .001 && Math.Abs(chart.Segments[0][0].Y - chart.Plot.Bottom) < .001, "Zero coordinate");
            Require(Math.Abs(chart.Segments[0][1].X - chart.Plot.Left - chart.Plot.Width / 1800) < .001 &&
                Math.Abs(chart.Segments[0][1].Y - chart.Plot.Top - chart.Plot.Height / 2) < .001, "Time and percentage coordinates");
            Require(Math.Abs(chart.Segments[1][0].X - chart.Plot.Right) < .001 && Math.Abs(chart.Segments[1][0].Y - chart.Plot.Top) < .001, "Right axis coordinate / warning independence");
            var root = (DockPanel)view.Content;
            var selector = root.Children.OfType<ComboBox>().Single();
            Require(selector.Items.Count == 1054 && selector.Items.Cast<SystemHistoryMetric>().All(m => !m.Label.Contains("secret-")), "All metrics have friendly labels");
            selector.SelectedItem = selector.Items.Cast<SystemHistoryMetric>().Single(m => m.Key == "disk:secret-d149:write");
            chart.BuildGeometry(new Size(600, 180)); Require(chart.Maximum == 150, "Metric selection and B/s scale");
            var renamed = s.Disks[149]; renamed.Name = "저장장치 새 이름"; chartStore.Add(now, s); view.Reload();
            Require(((SystemHistoryMetric)selector.SelectedItem).Label == "저장장치 새 이름 쓰기", "Stable identity when renamed");
            ((DockPanel)root.Children[0]).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(back, "Return action");
            view.UpdateLayout();
            var bitmap = new RenderTargetBitmap(600, 240, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view); var pixels = new byte[600 * 240 * 4]; bitmap.CopyPixels(pixels, 600 * 4, 0);
            Require(Enumerable.Range(0, 600 * 240).Any(i => pixels[i * 4] == 255 &&
                pixels[i * 4 + 1] == 191 && pixels[i * 4 + 2] == 0 && pixels[i * 4 + 3] == 255), "WPF graph stroke output");
            var image = new PngBitmapEncoder();
            image.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = System.IO.File.Create(System.IO.Path.Combine(testRoot, "pc-history.png"))) image.Save(output);
            var gapStore = new SystemHistory(); gapStore.Add(now.AddSeconds(-3), s); gapStore.Add(now, s);
            view = new SystemHistoryView(gapStore, delegate { }, delegate { return now; }); view.Chart.BuildGeometry(new Size(600, 180));
            Require(view.Chart.Segments.Count == 2, "Missing observations break line");
            s.Gpus[0].Available = false; s.Disks[0].Available = false; s.Networks[0].RatesAvailable = false;
            gapStore.Add(now, s); var last = gapStore.Read(now).Last();
            Require(!last.Values["gpu:secret-g0"].HasValue && !last.Values["disk:secret-d0"].HasValue &&
                !last.Values["network:secret-n0:receive"].HasValue, "Structured device availability");
            s.Networks[1].SendBytesPerSecond = 2097152;
            gapStore.Add(now, s); view.Reload();
            var rates = ((DockPanel)view.Content).Children.OfType<ComboBox>().Single();
            rates.SelectedItem = rates.Items.Cast<SystemHistoryMetric>().Single(m => m.Key == "network:secret-n1:send");
            view.Chart.BuildGeometry(new Size(600, 180));
            Require(view.Chart.AxisUnit == "MiB/s" && view.Chart.Maximum == 2097152, "Readable rate units without changing raw values");
            var empty = new SystemHistoryView(new SystemHistory(), delegate { }, delegate { return now; });
            empty.Chart.BuildGeometry(new Size(600, 180)); Require(empty.Chart.Segments.Count == 0, "Restart / empty graph");
            var bounded = new SystemHistory();
            for (int i = 0; i < 2000; i++) bounded.Add(now.AddMilliseconds(i), null);
            Require(bounded.Read(now.AddSeconds(2)).Count == 1801, "Sample bound");
            log("PASS system history: graph coordinates, fixed axis, gaps, structured availability, all device metrics, Korean labels, return action, restart and bounds");
        }
    }
}
