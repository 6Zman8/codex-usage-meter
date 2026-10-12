using System;
using System.Collections.Generic;
namespace CodexUsageMeter
{
    internal sealed class SystemHistoryMetric
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public string Unit { get; set; }
    }
    internal sealed class SystemHistorySample
    {
        public DateTime Time { get; set; }
        public Dictionary<string, double?> Values { get; set; }
        public Dictionary<string, SystemHistoryMetric> Metrics { get; set; }
    }
    internal sealed class SystemHistory
    {
        private readonly List<SystemHistorySample> _samples = new List<SystemHistorySample>();
        internal List<SystemHistorySample> Read(DateTime now)
        {
            _samples.RemoveAll(s => s.Time < now.AddMinutes(-30));
            return new List<SystemHistorySample>(_samples);
        }
        private static void Metric(SystemHistorySample sample, string key, string label, string unit, double value, bool available)
        {
            sample.Metrics[key] = new SystemHistoryMetric { Key = key, Label = label, Unit = unit };
            sample.Values[key] = available && !Double.IsNaN(value) && !Double.IsInfinity(value) ? (double?)value : null;
        }
        internal void Add(DateTime time, SystemSnapshot snapshot)
        {
            Read(time);
            var s = new SystemHistorySample { Time = time, Values = new Dictionary<string, double?>(), Metrics = new Dictionary<string, SystemHistoryMetric>() };
            if (snapshot != null)
            {
                Metric(s, "cpu", "CPU 사용률", "%", snapshot.CpuPercent, snapshot.CpuAvailable);
                Metric(s, "memory", "메모리 사용률", "%", snapshot.MemoryPercent, snapshot.MemoryAvailable);
                Metric(s, "memory:used", "메모리 사용량", "GB", snapshot.MemoryUsedGb, snapshot.MemoryAvailable);
                Metric(s, "memory:total", "메모리 전체 용량", "GB", snapshot.MemoryTotalGb, snapshot.MemoryAvailable);
                foreach (var g in snapshot.Gpus)
                    Metric(s, "gpu:" + g.Key, "GPU " + g.Index + " · " + g.Name + " 사용률", "%", g.Percent, g.Available);
                foreach (var d in snapshot.Disks)
                {
                    Metric(s, "disk:" + d.Key, d.Name + " 사용률", "%", d.Percent, d.Available);
                    Metric(s, "disk:" + d.Key + ":read", d.Name + " 읽기", "B/s", d.ReadBytesPerSecond, d.Available);
                    Metric(s, "disk:" + d.Key + ":write", d.Name + " 쓰기", "B/s", d.WriteBytesPerSecond, d.Available);
                }
                foreach (var n in snapshot.Networks)
                {
                    Metric(s, "network:" + n.Key, n.Name + " 네트워크 사용률", "%", n.Percent, n.RatesAvailable && n.LinkSpeedBitsPerSecond > 0);
                    Metric(s, "network:" + n.Key + ":receive", n.Name + " 수신", "B/s", n.ReceiveBytesPerSecond, n.RatesAvailable);
                    Metric(s, "network:" + n.Key + ":send", n.Name + " 송신", "B/s", n.SendBytesPerSecond, n.RatesAvailable);
                }
            }
            _samples.Add(s);
            if (_samples.Count > 1801) _samples.RemoveRange(0, _samples.Count - 1801);
        }
    }
}
