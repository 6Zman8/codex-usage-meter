using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexUsageMeter
{
    internal sealed class UpdateReleaseInfo
    {
        public Version Version { get; set; }
        public string VersionText { get; set; }
        public string TagName { get; set; }
        public string DownloadUrl { get; set; }
        public string Sha256 { get; set; }
        public long Size { get; set; }
        public string Notes { get; set; }
        public string ReleaseUrl { get; set; }
    }

    internal sealed class UpdateCheckResult
    {
        public string CurrentVersionText { get; set; }
        public string LatestVersionText { get; set; }
        public bool UpdateAvailable { get; set; }
        public UpdateReleaseInfo Release { get; set; }
    }

    internal static class UpdateClient
    {
        public const string RepositoryOwner = "6Zman8";
        public const string RepositoryName = "codex-usage-meter";
        public const string ReleaseAssetName = "CodexUsageMeter.exe";
        private const string UpdaterResourceName = "CodexUsageMeter.Updater.exe";

        public static string CurrentVersionText
        {
            get { return FormatVersion(GetCurrentVersion()); }
        }

        public static Task<UpdateCheckResult> CheckLatestAsync()
        {
            return CheckLatestAsync(null);
        }

        internal static Task<UpdateCheckResult> CheckLatestAsync(Version assumedCurrentVersion)
        {
            return Task.Run<UpdateCheckResult>(() => CheckLatest(assumedCurrentVersion));
        }

        private static UpdateCheckResult CheckLatest(Version assumedCurrentVersion)
        {
            EnableTls12();
            string endpoint = "https://api.github.com/repos/" + RepositoryOwner + "/" + RepositoryName + "/releases/latest";
            string json;
            try
            {
                using (WebClient client = CreateWebClient())
                {
                    json = client.DownloadString(endpoint);
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new InvalidOperationException("아직 공개된 정식 버전이 없습니다.");
                }
                throw new InvalidOperationException("GitHub에서 최신 버전을 확인하지 못했습니다. " + FriendlyWebError(ex));
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null)
            {
                throw new InvalidOperationException("GitHub의 릴리스 응답 형식을 읽지 못했습니다.");
            }

            string tag = StringValue(root, "tag_name");
            Version latest = ParseVersion(tag);
            Version current = assumedCurrentVersion ?? GetCurrentVersion();
            UpdateReleaseInfo release = ParseRelease(root, latest, tag);
            return new UpdateCheckResult {
                CurrentVersionText = FormatVersion(current),
                LatestVersionText = FormatVersion(latest),
                UpdateAvailable = latest > current,
                Release = release
            };
        }

        private static UpdateReleaseInfo ParseRelease(Dictionary<string, object> root, Version version, string tag)
        {
            object rawAssets;
            if (!root.TryGetValue("assets", out rawAssets))
            {
                throw new InvalidOperationException("최신 릴리스에 실행 파일이 없습니다.");
            }

            Dictionary<string, object> selected = null;
            IEnumerable assets = rawAssets as IEnumerable;
            if (assets != null)
            {
                foreach (object rawAsset in assets)
                {
                    Dictionary<string, object> asset = rawAsset as Dictionary<string, object>;
                    if (asset != null && String.Equals(StringValue(asset, "name"), ReleaseAssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        selected = asset;
                        break;
                    }
                }
            }
            if (selected == null)
            {
                throw new InvalidOperationException("최신 릴리스에서 " + ReleaseAssetName + " 파일을 찾지 못했습니다.");
            }

            string digest = StringValue(selected, "digest");
            if (String.IsNullOrWhiteSpace(digest) || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("최신 릴리스에 SHA-256 검증 정보가 없습니다.");
            }
            string downloadUrl = StringValue(selected, "browser_download_url");
            if (String.IsNullOrWhiteSpace(downloadUrl))
            {
                throw new InvalidOperationException("최신 릴리스의 다운로드 주소가 비어 있습니다.");
            }

            return new UpdateReleaseInfo {
                Version = version,
                VersionText = FormatVersion(version),
                TagName = tag,
                DownloadUrl = downloadUrl,
                Sha256 = digest.Substring("sha256:".Length).Trim().ToLowerInvariant(),
                Size = LongValue(selected, "size"),
                Notes = StringValue(root, "body"),
                ReleaseUrl = StringValue(root, "html_url")
            };
        }

        public static async Task<string> DownloadAndVerifyAsync(UpdateReleaseInfo release)
        {
            if (release == null) throw new ArgumentNullException("release");
            EnableTls12();
            string updateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexUsageMeter", "updates");
            Directory.CreateDirectory(updateRoot);
            string stagedPath = Path.Combine(updateRoot, "CodexUsageMeter-" + release.VersionText + ".exe.download");
            if (File.Exists(stagedPath)) File.Delete(stagedPath);
            try
            {
                using (WebClient client = CreateWebClient())
                {
                    await client.DownloadFileTaskAsync(new Uri(release.DownloadUrl), stagedPath);
                }
                FileInfo staged = new FileInfo(stagedPath);
                if (!staged.Exists || staged.Length <= 0 || (release.Size > 0 && staged.Length != release.Size))
                {
                    throw new InvalidOperationException("다운로드한 실행 파일의 크기가 릴리스 정보와 다릅니다.");
                }
                string actualHash = ComputeSha256(stagedPath);
                if (!String.Equals(actualHash, release.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("다운로드한 실행 파일의 SHA-256 값이 릴리스 정보와 다릅니다.");
                }
                using (FileStream input = File.OpenRead(stagedPath))
                {
                    if (input.ReadByte() != 'M' || input.ReadByte() != 'Z')
                    {
                        throw new InvalidOperationException("다운로드한 파일이 Windows 실행 파일이 아닙니다.");
                    }
                }
                return stagedPath;
            }
            catch
            {
                try { if (File.Exists(stagedPath)) File.Delete(stagedPath); } catch { }
                throw;
            }
        }

        public static void StartUpdater(string stagedPath, UpdateReleaseInfo release)
        {
            if (release == null) throw new ArgumentNullException("release");
            string targetPath = Assembly.GetExecutingAssembly().Location;
            string updateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexUsageMeter", "updates");
            Directory.CreateDirectory(updateRoot);
            string helperPath = Path.Combine(updateRoot, "CodexUsageMeter.Updater.exe");
            ExtractUpdater(helperPath);

            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = helperPath;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WorkingDirectory = Path.GetDirectoryName(targetPath);
            start.Arguments = Process.GetCurrentProcess().Id.ToString() + " " + Quote(stagedPath) + " " +
                Quote(targetPath) + " " + Quote(release.Sha256);
            Process process = Process.Start(start);
            if (process == null)
            {
                throw new InvalidOperationException("업데이트 교체 프로그램을 시작하지 못했습니다.");
            }
        }

        public static void RunUpdaterSelfTest()
        {
            string testRoot = Path.Combine(Path.GetTempPath(), "codex-meter-updater-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);
            try
            {
                string helper = Path.Combine(testRoot, "Updater.exe");
                string target = Path.Combine(testRoot, "target.bin");
                string staged = Path.Combine(testRoot, "staged.bin");
                File.WriteAllText(target, "old", Encoding.UTF8);
                File.WriteAllText(staged, "new", Encoding.UTF8);
                ExtractUpdater(helper);
                string hash = ComputeSha256(staged);
                ProcessStartInfo start = new ProcessStartInfo();
                start.FileName = helper;
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.Arguments = "0 " + Quote(staged) + " " + Quote(target) + " " + Quote(hash) + " --no-restart";
                using (Process process = Process.Start(start))
                {
                    if (process == null || !process.WaitForExit(15000) || process.ExitCode != 0)
                    {
                        throw new InvalidOperationException("내장 업데이트 교체 프로그램 자체 검사에 실패했습니다.");
                    }
                }
                if (File.ReadAllText(target, Encoding.UTF8) != "new" || File.Exists(staged) || File.Exists(target + ".old"))
                {
                    throw new InvalidOperationException("업데이트 교체 결과가 올바르지 않습니다.");
                }
            }
            finally
            {
                try { if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true); } catch { }
            }
        }

        private static void ExtractUpdater(string path)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream input = assembly.GetManifestResourceStream(UpdaterResourceName))
            {
                if (input == null) throw new InvalidOperationException("내장 업데이트 교체 프로그램을 찾지 못했습니다.");
                using (FileStream output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                }
            }
        }

        internal static string ComputeSha256(string path)
        {
            using (SHA256 hash = SHA256.Create())
            using (FileStream input = File.OpenRead(path))
            {
                byte[] bytes = hash.ComputeHash(input);
                StringBuilder text = new StringBuilder(bytes.Length * 2);
                foreach (byte value in bytes) text.Append(value.ToString("x2"));
                return text.ToString();
            }
        }

        private static WebClient CreateWebClient()
        {
            WebClient client = new WebClient();
            client.Encoding = Encoding.UTF8;
            client.Headers[HttpRequestHeader.UserAgent] = "CodexUsageMeter/" + CurrentVersionText;
            client.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
            client.Headers["X-GitHub-Api-Version"] = "2026-03-10";
            return client;
        }

        private static void EnableTls12()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        }

        private static Version GetCurrentVersion()
        {
            return NormalizeVersion(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0));
        }

        private static Version ParseVersion(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("최신 릴리스의 버전 태그가 비어 있습니다.");
            string cleaned = text.Trim();
            if (cleaned.StartsWith("v", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned.Substring(1);
            int suffix = cleaned.IndexOf('-');
            if (suffix >= 0) cleaned = cleaned.Substring(0, suffix);
            Version parsed;
            if (!Version.TryParse(cleaned, out parsed))
            {
                throw new InvalidOperationException("최신 릴리스 버전을 해석하지 못했습니다: " + text);
            }
            return NormalizeVersion(parsed);
        }

        private static Version NormalizeVersion(Version version)
        {
            return new Version(Math.Max(0, version.Major), Math.Max(0, version.Minor),
                Math.Max(0, version.Build), Math.Max(0, version.Revision));
        }

        private static string FormatVersion(Version version)
        {
            return version.Major.ToString() + "." + version.Minor.ToString() + "." + Math.Max(0, version.Build).ToString();
        }

        private static string StringValue(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : String.Empty;
        }

        private static long LongValue(Dictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return 0L;
            try { return Convert.ToInt64(value); } catch { return 0L; }
        }

        private static string FriendlyWebError(WebException error)
        {
            if (error.Status == WebExceptionStatus.NameResolutionFailure) return "인터넷 연결이나 DNS를 확인해 주세요.";
            if (error.Status == WebExceptionStatus.Timeout) return "요청 시간이 초과됐습니다.";
            HttpWebResponse response = error.Response as HttpWebResponse;
            if (response != null) return "GitHub 응답 " + ((int)response.StatusCode).ToString() + ".";
            return error.Message;
        }

        private static string Quote(string value)
        {
            if (value == null) return "\"\"";
            if (value.IndexOf('"') >= 0) throw new ArgumentException("경로에 큰따옴표를 사용할 수 없습니다.", "value");
            return "\"" + value + "\"";
        }
    }
}
