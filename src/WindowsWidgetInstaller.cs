using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;
using Microsoft.Win32;

namespace CodexUsageMeter
{
    internal static class WindowsWidgetInstaller
    {
        internal const string PackageName = "CodexUsageMeter.WindowsWidget";
        internal const string AssetName = "CodexUsageMeter.WindowsWidget.zip";
        private static readonly object InstallationLock = new object();

        internal static bool DeveloperModeEnabled()
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\AppModelUnlock", false))
                return key != null && Convert.ToString(key.GetValue("AllowDevelopmentWithoutDevLicense")) == "1";
        }

        internal static bool SupportedWindows()
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion", false))
            {
                int build;
                return Environment.Is64BitOperatingSystem && key != null &&
                    Int32.TryParse(Convert.ToString(key.GetValue("CurrentBuildNumber")), out build) && build >= 22000;
            }
        }

        internal static Task<string> InstallAsync()
        {
            return Task.Run(delegate {
                lock (InstallationLock)
                {
                    Version registered = GetRegisteredVersion();
                    if (registered != null && registered > CurrentPackageVersion) return null;
                    return InstallCurrentVersion();
                }
            });
        }

        // Upgrade only a widget the user already installed. No new installation, downgrade or security-setting change.
        internal static Task<bool> UpdateRegisteredAsync()
        {
            return Task.Run(delegate {
                if (!SupportedWindows()) return false;
                lock (InstallationLock)
                    return SynchronizeRegisteredVersion(CurrentPackageVersion, GetRegisteredVersion,
                        delegate { InstallCurrentVersion(); });
            });
        }

        private static Version CurrentPackageVersion { get { return new Version(UpdateClient.CurrentVersionText + ".0"); } }

        private static string InstallCurrentVersion()
        {
            if (!SupportedWindows()) throw new InvalidOperationException("Windows 11 64비트에서 사용할 수 있습니다.");
            if (!DeveloperModeEnabled()) throw new InvalidOperationException("Windows 개발자 모드를 켠 뒤 다시 추가해 주세요.");
            string folder = PreparePackage(Path.Combine(WindowsWidgetBridge.DefaultRoot, "packages"));
            RegisterPackage(folder);
            return folder;
        }

        internal static bool SynchronizeRegisteredVersion(Version current, Func<Version> readRegistered, Action install)
        {
            Version registered = readRegistered();
            if (registered == null || registered >= current) return false;
            install();
            Version updated = readRegistered();
            if (updated == null || updated < current)
                throw new InvalidOperationException("Windows 위젯 업데이트 결과를 확인하지 못했습니다. 설정에서 다시 시도해 주세요.");
            return true;
        }

        internal static Version GetRegisteredVersion()
        {
            string script = "$ErrorActionPreference='Stop'; $p=Get-AppxPackage -Name '" + PackageName +
                "' | Sort-Object Version -Descending | Select-Object -First 1; if($p){[Console]::Out.Write($p.Version.ToString())}";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            ProcessStartInfo start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell\\v1.0\\powershell.exe"), "-NoProfile -NonInteractive -EncodedCommand " + encoded) {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (Process process = Process.Start(start))
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30000)) throw new InvalidOperationException("Windows 위젯 버전 확인에 시간이 걸립니다. 설정에서 다시 시도해 주세요.");
                string version = output.Result.Trim();
                string errorText = error.Result;
                if (process.ExitCode != 0) throw new InvalidOperationException("Windows 위젯 등록 상태를 확인하지 못했습니다.");
                if (version.Length == 0) return null;
                Version registered;
                if (!Version.TryParse(version, out registered)) throw new InvalidOperationException("Windows 위젯 버전 정보를 읽지 못했습니다.");
                return registered;
            }
        }

        // Also used by release verification: downloads and validates, but never installs.
        internal static string PreparePackage(string parent)
        {
            string version = UpdateClient.CurrentVersionText;
            string tag = "v" + version;
            string repository = UpdateClient.RepositoryOwner + "/" + UpdateClient.RepositoryName;
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            Dictionary<string, object> release;
            using (WebClient client = NewClient())
                release = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                    client.DownloadString("https://api.github.com/repos/" + repository + "/releases/tags/" + tag));
            if (Convert.ToString(release["tag_name"]) != tag || Convert.ToBoolean(release["draft"]) || Convert.ToBoolean(release["prerelease"]))
                throw new InvalidOperationException("현재 앱 버전의 정식 위젯 패키지를 확인하지 못했습니다.");
            Dictionary<string, object> asset = null;
            foreach (object value in (IEnumerable)release["assets"])
            {
                Dictionary<string, object> candidate = value as Dictionary<string, object>;
                if (candidate != null && Convert.ToString(candidate["name"]) == AssetName) { asset = candidate; break; }
            }
            if (asset == null) throw new InvalidOperationException("정식 릴리스에 Windows 위젯 구성요소가 없습니다.");
            string expectedUrl = "https://github.com/" + repository + "/releases/download/" + tag + "/" + AssetName;
            string digest = asset.ContainsKey("digest") ? Convert.ToString(asset["digest"]) : String.Empty;
            if (Convert.ToString(asset["browser_download_url"]) != expectedUrl || !digest.StartsWith("sha256:") || digest.Length != 71)
                throw new InvalidOperationException("위젯 다운로드 주소 또는 SHA-256 검증 정보가 올바르지 않습니다.");
            string folder = Path.Combine(Path.GetFullPath(parent), version + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string zip = Path.Combine(folder, "package.zip");
            using (WebClient client = NewClient()) client.DownloadFile(expectedUrl, zip);
            VerifyDownload(zip, Convert.ToInt64(asset["size"]), digest.Substring(7));
            string payload = Path.Combine(folder, "app");
            ExtractPackage(zip, payload);
            ValidateManifest(payload, version + ".0");
            return payload;
        }

        internal static void VerifyDownload(string path, long size, string expectedHash)
        {
            if (new FileInfo(path).Length != size || size <= 0) throw new InvalidDataException("위젯 패키지 크기가 일치하지 않습니다.");
            using (SHA256 sha = SHA256.Create())
            using (Stream stream = File.OpenRead(path))
            {
                string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", String.Empty);
                if (!String.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("위젯 패키지 SHA-256 검증에 실패했습니다.");
            }
        }

        internal static void ExtractPackage(string zip, string destination)
        {
            string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (ZipArchive archive = ZipFile.OpenRead(zip))
            {
                long total = 0;
                // Validate every path before writing any entry.
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string path = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains(":") ||
                        Path.IsPathRooted(entry.FullName) || (total += entry.Length) > 500L * 1024 * 1024)
                        throw new InvalidDataException("위젯 압축파일의 경로나 크기가 올바르지 않습니다.");
                }
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string path = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (String.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    entry.ExtractToFile(path, false);
                }
            }
        }

        internal static void ValidateManifest(string folder, string version)
        {
            XmlDocument document = new XmlDocument(); document.XmlResolver = null;
            using (XmlReader reader = XmlReader.Create(Path.Combine(folder, "AppxManifest.xml"),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) document.Load(reader);
            XmlNamespaceManager namespaces = new XmlNamespaceManager(document.NameTable);
            namespaces.AddNamespace("p", "http://schemas.microsoft.com/appx/manifest/foundation/windows10");
            XmlElement identity = document.SelectSingleNode("/p:Package/p:Identity", namespaces) as XmlElement;
            if (identity == null || identity.GetAttribute("Name") != PackageName || identity.GetAttribute("Publisher") != "CN=CodexUsageMeter" ||
                identity.GetAttribute("Version") != version || identity.GetAttribute("ProcessorArchitecture") != "x64")
                throw new InvalidDataException("위젯 패키지의 이름·버전·배포자를 확인하지 못했습니다.");
        }

        private static void RegisterPackage(string folder)
        {
            string manifest = Path.Combine(folder, "AppxManifest.xml").Replace("'", "''");
            string script = "$ErrorActionPreference='Stop'; " +
                "if(-not (Get-AppxPackage -Name MicrosoftWindows.Client.WebExperience)){throw 'Windows Web Experience Pack is missing'}; " +
                "Add-AppxPackage -Register -Path '" + manifest + "' -ForceApplicationShutdown; " +
                "$p=Get-AppxPackage -Name '" + PackageName + "'; if(-not $p){throw 'Widget registration was not found'}; " +
                "if($p.Version -ne '" + UpdateClient.CurrentVersionText + ".0'){throw 'Widget version mismatch'}; Write-Output 'PASS widget registered'";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            ProcessStartInfo start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell\\v1.0\\powershell.exe"), "-NoProfile -NonInteractive -EncodedCommand " + encoded) {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (Process process = Process.Start(start))
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(120000)) throw new InvalidOperationException("Windows 위젯 등록이 아직 끝나지 않았습니다. 잠시 후 다시 확인해 주세요.");
                string log = output.Result + Environment.NewLine + error.Result;
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(folder), "registration.log"), log, Encoding.UTF8);
                if (process.ExitCode != 0) throw new InvalidOperationException("Windows 위젯 등록에 실패했습니다. 개발자 모드와 Windows Web Experience Pack을 확인해 주세요.\n\n등록 기록: " + Path.Combine(Path.GetDirectoryName(folder), "registration.log"));
            }
        }

        private static WebClient NewClient()
        {
            WebClient client = new TimedWebClient();
            client.Encoding = Encoding.UTF8;
            client.Headers[HttpRequestHeader.UserAgent] = "CodexUsageMeter/" + UpdateClient.CurrentVersionText;
            client.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
            return client;
        }

        private sealed class TimedWebClient : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest request = base.GetWebRequest(address); request.Timeout = 120000; return request;
            }
        }
    }
}
