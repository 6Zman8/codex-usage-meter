using System.Diagnostics;
using System.Text;

namespace CodexUsageMeter.WindowsWidget;

internal static class WidgetFiles
{
    internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageMeter", "windows-widget");
    internal static string ReadSnapshot()
    {
        string path=Path.Combine(Root,"snapshot.json");
        try
        {
            if(!File.Exists(path))return "";
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            if(stream.Length>512*1024)return "{invalid-size";
            using var reader=new StreamReader(stream,Encoding.UTF8,true);
            return reader.ReadToEnd();
        }
        catch(IOException){return "{unreadable";}
        catch(UnauthorizedAccessException){return "{unreadable";}
    }

    internal static ProcessStartInfo CreateStartInfo(string path,string verb)
    {
        if(verb is not "open" and not "refresh")throw new ArgumentException("Unknown widget action");
        if(path.IndexOfAny(new[]{'\r','\n','\0'})>=0 || !Path.IsPathFullyQualified(path) || !path.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A single absolute executable path is required");
        return new ProcessStartInfo
        {
            FileName=path, Arguments=verb=="refresh"?"--widget-refresh":"", WorkingDirectory=Path.GetDirectoryName(path)!,
            UseShellExecute=false, CreateNoWindow=true
        };
    }

    internal static string? Invoke(string verb)
    {
        try
        {
            var file=new FileInfo(Path.Combine(Root,"app-path.txt"));
            if(!file.Exists || file.Length>32768)return "미터기에서 위젯 연결을 다시 설정해 주세요.";
            string path=File.ReadAllText(file.FullName,Encoding.UTF8).TrimEnd('\r','\n').Trim();
            var start=CreateStartInfo(path,verb);
            if(!File.Exists(start.FileName))return "미터기를 찾을 수 없습니다. 위젯 연결을 다시 설정해 주세요.";
            int? exitCode=WindowsLauncher.StartAndObserve(start);
            return LaunchNotice(exitCode);
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        { Log("action",error);return "미터기를 열지 못했습니다. 연결 설정을 확인해 주세요."; }
    }

    internal static string? LaunchNotice(int? exitCode) => exitCode is null or 0?null:"미터기가 요청을 처리하지 못했습니다. 본체 업데이트와 실행 상태를 확인해 주세요.";

    internal static void Log(string operation,Exception error)
    {
        try
        {
            Directory.CreateDirectory(Root);
            string path=Path.Combine(Root,"provider-errors.log");
            // Deliberately record only operation/type/HRESULT, never snapshot values or account paths.
            if(File.Exists(path) && new FileInfo(path).Length>65536)return;
            File.AppendAllText(path,$"{DateTimeOffset.UtcNow:O} {operation} {error.GetType().Name} 0x{error.HResult:X8}\n");
        }
        catch(IOException) { }
        catch(UnauthorizedAccessException) { }
    }
}
