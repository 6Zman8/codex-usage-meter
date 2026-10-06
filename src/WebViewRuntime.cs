using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace CodexUsageMeter
{
    internal static class WebViewRuntime
    {
        internal const string Version = "1.0.4258.31";
        internal static void Register()
        {
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs args) {
                if (new AssemblyName(args.Name).Name != "Microsoft.Web.WebView2.Core") return null;
                return Assembly.Load(Resource("Microsoft.Web.WebView2.Core.dll"));
            };
        }

        internal static string LoaderFolder()
        {
            string architecture = IntPtr.Size == 8 ? "x64" : "x86";
            if (IntPtr.Size == 8 && String.Equals(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"), "ARM64", StringComparison.OrdinalIgnoreCase)) architecture = "arm64";
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageMeter", "runtime", Version, architecture);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "WebView2Loader.dll");
            byte[] wanted = Resource(architecture + ".WebView2Loader.dll");
            if (File.Exists(path))
            {
                using (SHA256 hash = SHA256.Create())
                    if (Convert.ToBase64String(hash.ComputeHash(File.ReadAllBytes(path))) == Convert.ToBase64String(hash.ComputeHash(wanted))) return directory;
                throw new InvalidOperationException("웹 연결 구성 파일을 확인하지 못했습니다.");
            }
            try { using (FileStream file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) file.Write(wanted, 0, wanted.Length); }
            catch (IOException) { if (!File.Exists(path)) throw; return LoaderFolder(); }
            return directory;
        }

        private static byte[] Resource(string name)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexUsageMeter.WebView2." + name))
            {
                if (stream == null) throw new InvalidOperationException("웹 연결 구성 파일이 없습니다.");
                using (MemoryStream data = new MemoryStream()) { stream.CopyTo(data); return data.ToArray(); }
            }
        }
    }
}
