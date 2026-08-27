using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace CodexUsageMeter
{
    internal sealed class GpuSnapshot
    {
        public int Index { get; set; }
        public string Key { get; set; }
        public string Name { get; set; }
        public double Percent { get; set; }
    }

    internal sealed class DiskSnapshot
    {
        public int Index { get; set; }
        public string Key { get; set; }
        public string Name { get; set; }
        public string Detail { get; set; }
        public double Percent { get; set; }
        public double ReadBytesPerSecond { get; set; }
        public double WriteBytesPerSecond { get; set; }
    }

    internal sealed class NetworkSnapshot
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public string Detail { get; set; }
        public bool Connected { get; set; }
        public long LinkSpeedBitsPerSecond { get; set; }
        public double ReceiveBytesPerSecond { get; set; }
        public double SendBytesPerSecond { get; set; }
        public double Percent { get; set; }
    }

    internal sealed class SystemSnapshot
    {
        public SystemSnapshot()
        {
            Gpus = new List<GpuSnapshot>();
            Disks = new List<DiskSnapshot>();
            Networks = new List<NetworkSnapshot>();
        }

        public double CpuPercent { get; set; }
        public double GpuPercent { get; set; }
        public bool GpuAvailable { get; set; }
        public List<GpuSnapshot> Gpus { get; private set; }
        public double MemoryPercent { get; set; }
        public double MemoryUsedGb { get; set; }
        public double MemoryTotalGb { get; set; }
        public double DiskPercent { get; set; }
        public bool DiskAvailable { get; set; }
        public double DiskReadBytesPerSecond { get; set; }
        public double DiskWriteBytesPerSecond { get; set; }
        public List<DiskSnapshot> Disks { get; private set; }
        public double NetworkReceiveBytesPerSecond { get; set; }
        public double NetworkSendBytesPerSecond { get; set; }
        public List<NetworkSnapshot> Networks { get; private set; }
        public string Warning { get; set; }
    }

    internal sealed class SystemMonitor
    {
        private sealed class DiskDescriptor
        {
            public string Model;
            public string Media;
            public ulong Size;
        }

        private sealed class GpuDescriptor
        {
            public string DeviceId;
            public string Name;
        }

        private readonly object _sampleLock;
        private readonly Stopwatch _networkClock;
        private readonly Dictionary<string, long> _previousNetworkReceived;
        private readonly Dictionary<string, long> _previousNetworkSent;
        private readonly Dictionary<int, DiskDescriptor> _diskDescriptors;
        private readonly List<GpuDescriptor> _gpuDescriptors;
        private readonly HashSet<string> _physicalNetworkIds;
        private ulong _previousIdle;
        private ulong _previousKernel;
        private ulong _previousUser;

        public SystemMonitor()
        {
            _sampleLock = new object();
            _networkClock = Stopwatch.StartNew();
            _previousNetworkReceived = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            _previousNetworkSent = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            _diskDescriptors = LoadDiskDescriptors();
            _gpuDescriptors = LoadGpuDescriptors();
            _physicalNetworkIds = LoadPhysicalNetworkIds();
        }

        public Task<SystemSnapshot> SampleAsync()
        {
            return Task.Run<SystemSnapshot>((Func<SystemSnapshot>)SampleCore);
        }

        private SystemSnapshot SampleCore()
        {
            lock (_sampleLock)
            {
                SystemSnapshot snapshot = new SystemSnapshot();
                List<string> warnings = new List<string>();
                try { snapshot.CpuPercent = ReadCpu(); } catch { warnings.Add("CPU"); }
                try { ReadMemory(snapshot); } catch { warnings.Add("RAM"); }
                try { ReadDisk(snapshot); } catch { warnings.Add("디스크"); }
                try { ReadGpu(snapshot); } catch { warnings.Add("GPU"); }
                try { ReadNetwork(snapshot); } catch { warnings.Add("네트워크"); }
                if (warnings.Count > 0)
                {
                    snapshot.Warning = String.Join(", ", warnings.ToArray()) + " 센서를 읽지 못했습니다.";
                }
                return snapshot;
            }
        }

        private double ReadCpu()
        {
            FileTime idleTime;
            FileTime kernelTime;
            FileTime userTime;
            if (!GetSystemTimes(out idleTime, out kernelTime, out userTime))
            {
                throw new InvalidOperationException("GetSystemTimes 실패");
            }
            ulong idle = ToUInt64(idleTime);
            ulong kernel = ToUInt64(kernelTime);
            ulong user = ToUInt64(userTime);
            ulong idleDelta = idle - _previousIdle;
            ulong totalDelta = (kernel - _previousKernel) + (user - _previousUser);
            _previousIdle = idle;
            _previousKernel = kernel;
            _previousUser = user;
            return totalDelta == 0 ? 0.0 : Clamp(100.0 * (totalDelta - Math.Min(idleDelta, totalDelta)) / totalDelta);
        }

        private static void ReadMemory(SystemSnapshot snapshot)
        {
            MemoryStatus status = new MemoryStatus();
            status.Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            if (!GlobalMemoryStatusEx(ref status)) throw new InvalidOperationException("GlobalMemoryStatusEx 실패");
            snapshot.MemoryTotalGb = status.TotalPhysical / 1073741824.0;
            snapshot.MemoryUsedGb = (status.TotalPhysical - status.AvailablePhysical) / 1073741824.0;
            snapshot.MemoryPercent = Clamp(status.MemoryLoad);
        }

        private void ReadDisk(SystemSnapshot snapshot)
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT Name,PercentDiskTime,DiskReadBytesPersec,DiskWriteBytesPersec FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk"))
            {
                foreach (ManagementObject item in searcher.Get())
                {
                    using (item)
                    {
                        string instanceName = Convert.ToString(item["Name"]);
                        if (String.IsNullOrWhiteSpace(instanceName) || String.Equals(instanceName, "_Total", StringComparison.OrdinalIgnoreCase)) continue;
                        int index = ParseLeadingInteger(instanceName);
                        DiskDescriptor descriptor;
                        _diskDescriptors.TryGetValue(index, out descriptor);
                        DiskSnapshot disk = new DiskSnapshot();
                        disk.Index = index;
                        disk.Key = "disk:" + index.ToString();
                        disk.Name = "디스크 " + index.ToString() + FormatDiskVolumes(instanceName);
                        disk.Detail = FormatDiskDetail(descriptor);
                        disk.Percent = Clamp(SafeDouble(item["PercentDiskTime"]));
                        disk.ReadBytesPerSecond = Math.Max(0.0, SafeDouble(item["DiskReadBytesPersec"]));
                        disk.WriteBytesPerSecond = Math.Max(0.0, SafeDouble(item["DiskWriteBytesPersec"]));
                        snapshot.Disks.Add(disk);
                        snapshot.DiskPercent = Math.Max(snapshot.DiskPercent, disk.Percent);
                        snapshot.DiskReadBytesPerSecond += disk.ReadBytesPerSecond;
                        snapshot.DiskWriteBytesPerSecond += disk.WriteBytesPerSecond;
                    }
                }
            }
            snapshot.Disks.Sort(delegate(DiskSnapshot left, DiskSnapshot right) { return left.Index.CompareTo(right.Index); });
            snapshot.DiskAvailable = snapshot.Disks.Count > 0;
        }

        private void ReadGpu(SystemSnapshot snapshot)
        {
            Dictionary<string, Dictionary<string, double>> adapterEngines = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> adapterInstanceCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "root\\CIMV2", "SELECT Name,UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine"))
            {
                foreach (ManagementObject item in searcher.Get())
                {
                    using (item)
                    {
                        string instanceName = Convert.ToString(item["Name"]);
                        string adapterKey = GetGpuAdapterKey(instanceName);
                        string engineKey = GetGpuEngineKey(instanceName);
                        if (String.IsNullOrWhiteSpace(adapterKey) || String.IsNullOrWhiteSpace(engineKey)) continue;
                        Dictionary<string, double> engines;
                        if (!adapterEngines.TryGetValue(adapterKey, out engines))
                        {
                            engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                            adapterEngines.Add(adapterKey, engines);
                        }
                        double current;
                        engines.TryGetValue(engineKey, out current);
                        engines[engineKey] = current + Math.Max(0.0, SafeDouble(item["UtilizationPercentage"]));
                        int instanceCount;
                        adapterInstanceCounts.TryGetValue(adapterKey, out instanceCount);
                        adapterInstanceCounts[adapterKey] = instanceCount + 1;
                    }
                }
            }

            Dictionary<string, ulong> committedMemory = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "root\\CIMV2", "SELECT Name,TotalCommitted FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory"))
                {
                    foreach (ManagementObject item in searcher.Get())
                    {
                        using (item)
                        {
                            string key = NormalizeGpuKey(Convert.ToString(item["Name"]));
                            if (!String.IsNullOrWhiteSpace(key)) committedMemory[key] = SafeUInt64(item["TotalCommitted"]);
                        }
                    }
                }
            }
            catch { }

            List<string> adapterKeys = new List<string>(adapterEngines.Keys);
            adapterKeys.Sort(delegate(string left, string right)
            {
                ulong leftMemory;
                ulong rightMemory;
                committedMemory.TryGetValue(left, out leftMemory);
                committedMemory.TryGetValue(right, out rightMemory);
                int memoryOrder = rightMemory.CompareTo(leftMemory);
                if (memoryOrder != 0) return memoryOrder;
                int leftCount;
                int rightCount;
                adapterInstanceCounts.TryGetValue(left, out leftCount);
                adapterInstanceCounts.TryGetValue(right, out rightCount);
                int countOrder = rightCount.CompareTo(leftCount);
                return countOrder != 0 ? countOrder : String.Compare(left, right, StringComparison.OrdinalIgnoreCase);
            });
            int gpuCount = _gpuDescriptors.Count > 0 ? _gpuDescriptors.Count : adapterKeys.Count;
            for (int index = 0; index < gpuCount; index++)
            {
                double busiestEngine = 0.0;
                string engineAdapterKey = index < adapterKeys.Count ? adapterKeys[index] : null;
                Dictionary<string, double> engines;
                if (engineAdapterKey != null && adapterEngines.TryGetValue(engineAdapterKey, out engines))
                {
                    foreach (double value in engines.Values) busiestEngine = Math.Max(busiestEngine, value);
                }
                GpuSnapshot gpu = new GpuSnapshot();
                gpu.Index = index;
                gpu.Key = "gpu:" + (engineAdapterKey ?? ("controller:" + index.ToString()));
                gpu.Name = index < _gpuDescriptors.Count ? _gpuDescriptors[index].Name : "그래픽 어댑터";
                gpu.Percent = Clamp(busiestEngine);
                snapshot.Gpus.Add(gpu);
                snapshot.GpuPercent = Math.Max(snapshot.GpuPercent, gpu.Percent);
            }
            snapshot.GpuAvailable = snapshot.Gpus.Count > 0;
        }

        private void ReadNetwork(SystemSnapshot snapshot)
        {
            double seconds = Math.Max(0.001, _networkClock.Elapsed.TotalSeconds);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback || adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                string normalizedId = NormalizeNetworkId(adapter.Id);
                bool knownPhysical = _physicalNetworkIds.Contains(normalizedId);
                if (_physicalNetworkIds.Count > 0 && !knownPhysical) continue;
                if (_physicalNetworkIds.Count == 0 && adapter.OperationalStatus != OperationalStatus.Up) continue;

                long received = 0;
                long sent = 0;
                bool countersAvailable = false;
                try
                {
                    IPv4InterfaceStatistics stats = adapter.GetIPv4Statistics();
                    received = stats.BytesReceived;
                    sent = stats.BytesSent;
                    countersAvailable = true;
                }
                catch { }

                double receiveRate = 0.0;
                double sendRate = 0.0;
                long previousReceived;
                long previousSent;
                if (countersAvailable && _previousNetworkReceived.TryGetValue(normalizedId, out previousReceived) && received >= previousReceived)
                    receiveRate = (received - previousReceived) / seconds;
                if (countersAvailable && _previousNetworkSent.TryGetValue(normalizedId, out previousSent) && sent >= previousSent)
                    sendRate = (sent - previousSent) / seconds;
                if (countersAvailable)
                {
                    _previousNetworkReceived[normalizedId] = received;
                    _previousNetworkSent[normalizedId] = sent;
                }
                seen.Add(normalizedId);

                long speed = Math.Max(0L, adapter.Speed);
                NetworkSnapshot network = new NetworkSnapshot();
                network.Key = "network:" + normalizedId;
                network.Name = String.IsNullOrWhiteSpace(adapter.Name) ? adapter.Description : adapter.Name;
                network.Detail = adapter.Description;
                network.Connected = adapter.OperationalStatus == OperationalStatus.Up;
                network.LinkSpeedBitsPerSecond = speed;
                network.ReceiveBytesPerSecond = receiveRate;
                network.SendBytesPerSecond = sendRate;
                network.Percent = speed > 0 ? Clamp(100.0 * ((receiveRate + sendRate) * 8.0) / speed) : 0.0;
                snapshot.Networks.Add(network);
                snapshot.NetworkReceiveBytesPerSecond += receiveRate;
                snapshot.NetworkSendBytesPerSecond += sendRate;
            }
            RemoveMissingNetworkCounters(_previousNetworkReceived, seen);
            RemoveMissingNetworkCounters(_previousNetworkSent, seen);
            snapshot.Networks.Sort(delegate(NetworkSnapshot left, NetworkSnapshot right)
            {
                int connected = right.Connected.CompareTo(left.Connected);
                return connected != 0 ? connected : String.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            _networkClock.Restart();
        }

        private static Dictionary<int, DiskDescriptor> LoadDiskDescriptors()
        {
            Dictionary<int, DiskDescriptor> descriptors = new Dictionary<int, DiskDescriptor>();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT Index,Model,Size FROM Win32_DiskDrive"))
                {
                    foreach (ManagementObject item in searcher.Get())
                    {
                        using (item)
                        {
                            int index = Convert.ToInt32(item["Index"]);
                            descriptors[index] = new DiskDescriptor { Model = Convert.ToString(item["Model"]), Size = SafeUInt64(item["Size"]), Media = "저장장치" };
                        }
                    }
                }
            }
            catch { }
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "root\\Microsoft\\Windows\\Storage", "SELECT DeviceId,FriendlyName,MediaType,Size FROM MSFT_PhysicalDisk"))
                {
                    foreach (ManagementObject item in searcher.Get())
                    {
                        using (item)
                        {
                            int index;
                            if (!Int32.TryParse(Convert.ToString(item["DeviceId"]), out index)) continue;
                            DiskDescriptor descriptor;
                            if (!descriptors.TryGetValue(index, out descriptor))
                            {
                                descriptor = new DiskDescriptor();
                                descriptors[index] = descriptor;
                            }
                            string name = Convert.ToString(item["FriendlyName"]);
                            if (!String.IsNullOrWhiteSpace(name)) descriptor.Model = name;
                            ulong size = SafeUInt64(item["Size"]);
                            if (size > 0) descriptor.Size = size;
                            descriptor.Media = MediaTypeName(Convert.ToInt32(item["MediaType"]));
                        }
                    }
                }
            }
            catch { }
            return descriptors;
        }

        private static List<GpuDescriptor> LoadGpuDescriptors()
        {
            List<GpuDescriptor> descriptors = new List<GpuDescriptor>();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "root\\CIMV2", "SELECT DeviceID,Name,PNPDeviceID FROM Win32_VideoController"))
                {
                    foreach (ManagementObject item in searcher.Get())
                    {
                        using (item)
                        {
                            string pnpId = Convert.ToString(item["PNPDeviceID"]);
                            if (String.IsNullOrWhiteSpace(pnpId) || !pnpId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue;
                            descriptors.Add(new GpuDescriptor { DeviceId = Convert.ToString(item["DeviceID"]), Name = Convert.ToString(item["Name"]) });
                        }
                    }
                }
            }
            catch { }
            descriptors.Sort(delegate(GpuDescriptor left, GpuDescriptor right) { return String.Compare(left.DeviceId, right.DeviceId, StringComparison.OrdinalIgnoreCase); });
            return descriptors;
        }

        private static HashSet<string> LoadPhysicalNetworkIds()
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "root\\CIMV2", "SELECT GUID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE AND NetEnabled = TRUE"))
                {
                    foreach (ManagementObject item in searcher.Get())
                    {
                        using (item)
                        {
                            string id = NormalizeNetworkId(Convert.ToString(item["GUID"]));
                            if (!String.IsNullOrWhiteSpace(id)) ids.Add(id);
                        }
                    }
                }
            }
            catch { }
            return ids;
        }

        private static string GetGpuAdapterKey(string instanceName)
        {
            if (String.IsNullOrWhiteSpace(instanceName)) return null;
            int luid = instanceName.IndexOf("_luid_", StringComparison.OrdinalIgnoreCase);
            int engine = luid < 0 ? -1 : instanceName.IndexOf("_eng_", luid, StringComparison.OrdinalIgnoreCase);
            return luid < 0 || engine <= luid ? null : instanceName.Substring(luid, engine - luid);
        }

        private static string GetGpuEngineKey(string instanceName)
        {
            if (String.IsNullOrWhiteSpace(instanceName)) return null;
            int engine = instanceName.IndexOf("_eng_", StringComparison.OrdinalIgnoreCase);
            if (engine < 0) return null;
            int type = instanceName.IndexOf("_engtype_", engine, StringComparison.OrdinalIgnoreCase);
            return type > engine ? instanceName.Substring(engine, type - engine) : instanceName.Substring(engine);
        }

        private static string NormalizeGpuKey(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            string normalized = value.Trim();
            return normalized.StartsWith("_", StringComparison.Ordinal) ? normalized : "_" + normalized;
        }

        private static int ParseLeadingInteger(string value)
        {
            int end = 0;
            while (end < value.Length && Char.IsDigit(value[end])) end++;
            int parsed;
            return end > 0 && Int32.TryParse(value.Substring(0, end), out parsed) ? parsed : 0;
        }

        private static string FormatDiskVolumes(string instanceName)
        {
            int separator = instanceName.IndexOf(' ');
            if (separator < 0 || separator >= instanceName.Length - 1) return String.Empty;
            string volumes = instanceName.Substring(separator + 1).Trim();
            return String.IsNullOrWhiteSpace(volumes) ? String.Empty : " (" + volumes + ")";
        }

        private static string FormatDiskDetail(DiskDescriptor descriptor)
        {
            if (descriptor == null) return "물리 저장장치";
            List<string> parts = new List<string>();
            if (!String.IsNullOrWhiteSpace(descriptor.Media)) parts.Add(descriptor.Media);
            if (descriptor.Size > 0) parts.Add(FormatCapacity(descriptor.Size));
            if (!String.IsNullOrWhiteSpace(descriptor.Model)) parts.Add(descriptor.Model.Trim());
            return String.Join(" · ", parts.ToArray());
        }

        private static string MediaTypeName(int mediaType)
        {
            if (mediaType == 3) return "HDD";
            if (mediaType == 4) return "SSD";
            if (mediaType == 5) return "SCM";
            return "저장장치";
        }

        private static string FormatCapacity(ulong bytes)
        {
            double gb = bytes / 1000000000.0;
            return gb >= 1000.0 ? (gb / 1000.0).ToString("0.0") + " TB" : gb.ToString("0") + " GB";
        }

        private static string NormalizeNetworkId(string value)
        {
            return String.IsNullOrWhiteSpace(value) ? String.Empty : value.Trim().Trim('{', '}').ToUpperInvariant();
        }

        private static void RemoveMissingNetworkCounters(Dictionary<string, long> values, HashSet<string> seen)
        {
            List<string> remove = new List<string>();
            foreach (string key in values.Keys) if (!seen.Contains(key)) remove.Add(key);
            foreach (string key in remove) values.Remove(key);
        }

        private static ulong SafeUInt64(object value)
        {
            try { return value == null ? 0UL : Convert.ToUInt64(value); }
            catch { return 0UL; }
        }

        private static double SafeDouble(object value)
        {
            if (value == null) return 0.0;
            try { return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return 0.0; }
        }

        private static double Clamp(double value) { return Math.Max(0.0, Math.Min(100.0, value)); }
        private static ulong ToUInt64(FileTime value) { return ((ulong)value.High << 32) | value.Low; }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime { public uint Low; public uint High; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MemoryStatus
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
    }
}
