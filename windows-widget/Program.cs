using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.ApplicationModel.AppExtensions;
using Microsoft.Windows.Widgets.Providers;
using System.Runtime.InteropServices;
using WinRT;

namespace CodexUsageMeter.WindowsWidget;

internal static class Program
{
    internal static readonly ManualResetEvent Shutdown=new(false);
    [MTAThread]
    private static int Main(string[] args)
    {
        try
        {
            if(args.Length>=1 && args[0]=="--self-test")return SelfTests.Run(args.Length>=2?args[1]:null);
            if(args.Length is 2 or 3 && args[0]=="--card-preview")return Preview(args[1],args.Length==3?args[2]:null);
            if(args.Length==2 && args[0]=="--catalog-probe")return CatalogProbe(args[1]);
            if(args.Length==2 && args[0]=="--runtime-probe")return RuntimeProbe(args[1]);
            if(args.Length==3 && args[0]=="--launch-context-test")return LaunchContextTest(args[1],args[2]);
            if(args.Length>0 && args[0] is not "-RegisterProcessAsComServer" and not "-Embedding" and not "/Embedding")return 2;
            using var server=new ComServer();
            Shutdown.WaitOne();
            return 0;
        }
        catch(Exception error){WidgetFiles.Log("startup",error);return 1;}
    }
    private static int LaunchContextTest(string fixture,string output)
    {
        output=Path.GetFullPath(output);
        var identity=WindowsLauncher.PackageIdentity();
        var report=new JsonObject{["providerPackageError"]=identity.Error,["providerPackageFullName"]=identity.Name,["providerLocalApplicationData"]=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)};
        try
        {
            var start=WidgetFiles.CreateStartInfo(Path.GetFullPath(fixture),"open");
            string childOutput=output+".child.json";
            start.Arguments=WindowsLauncher.Quote(childOutput)+" 1";
            int? code=WindowsLauncher.StartAndObserve(start);
            report["childExitCode"]=code;
            report["failureNoticeDetected"]=WidgetFiles.LaunchNotice(code) is not null;
            if(File.Exists(childOutput))report["child"]=JsonNode.Parse(File.ReadAllText(childOutput));
            report["passed"]=code==1 && report["child"]?["packageError"]?.GetValue<int>()==15700 && report["failureNoticeDetected"]!.GetValue<bool>();
        }
        catch(Exception error){report["passed"]=false;report["errorType"]=error.GetType().Name;report["hresult"]=$"0x{error.HResult:X8}";}
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output,report.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
        return report["passed"]!.GetValue<bool>()?0:1;
    }
    private static int Preview(string output,string? snapshotPath)
    {
        Directory.CreateDirectory(output);
        string snapshot=snapshotPath is null?SelfTests.SampleJson:File.ReadAllText(snapshotPath);
        foreach(string size in new[]{"Small","Medium","Large"})
        {
            var card=WidgetContent.Build(snapshot,size,snapshotPath is null?SelfTests.TestNow:DateTimeOffset.UtcNow);
            File.WriteAllText(Path.Combine(output,size.ToLowerInvariant()+".json"),card.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
        }
        File.WriteAllText(Path.Combine(output,"snapshot.example.json"),snapshot);
        return 0;
    }
    private static int CatalogProbe(string output)
    {
        var report=new JsonObject{["catalogReadSucceeded"]=false,["registered"]=false,["expectedPackageName"]="CodexUsageMeter.WindowsWidget"};
        try
        {
            var catalog=AppExtensionCatalog.Open("com.microsoft.windows.widgets");
            var extensions=catalog.FindAllAsync().AsTask().GetAwaiter().GetResult();
            var extension=extensions.FirstOrDefault(x=>x.Package.Id.Name=="CodexUsageMeter.WindowsWidget");
            report["catalogReadSucceeded"]=true;report["registered"]=extension is not null;
            if(extension is not null){report["packageFullName"]=extension.Package.Id.FullName;report["extensionId"]=extension.Id;}
        }
        catch(Exception error){report["errorType"]=error.GetType().Name;report["hresult"]=$"0x{error.HResult:X8}";}
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output,report.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
        return report["catalogReadSucceeded"]!.GetValue<bool>()?0:1;
    }
    private static int RuntimeProbe(string output)
    {
        var report=new JsonObject{["nativeWinRtActivation"]=false,["providerComInterface"]=false};
        try
        {
            var options=new WidgetUpdateRequestOptions("fixture-widget"){Template="{\"type\":\"AdaptiveCard\"}",Data="{}",CustomState="fixture"};
            report["nativeWinRtActivation"]=options.CustomState=="fixture" && options.Data=="{}";
            IntPtr pointer=MarshalInspectable<IWidgetProvider>.FromManaged(new ProbeProvider());
            try
            {
                Guid iid=typeof(IWidgetProvider).GUID;
                int hr=Marshal.QueryInterface(pointer,ref iid,out var queried);
                report["providerComInterface"]=hr==0 && queried!=IntPtr.Zero;
                if(queried!=IntPtr.Zero)Marshal.Release(queried);
            }
            finally{Marshal.Release(pointer);}
        }
        catch(Exception error){report["errorType"]=error.GetType().Name;report["hresult"]=$"0x{error.HResult:X8}";report["message"]=error.Message;}
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output,report.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
        return report["nativeWinRtActivation"]!.GetValue<bool>() && report["providerComInterface"]!.GetValue<bool>()?0:1;
    }
}

// A packaging probe only: no WidgetManager calls, profile reads, process starts or OS registration.
[ComVisible(true),ClassInterface(ClassInterfaceType.None),ComDefaultInterface(typeof(IWidgetProvider)),Guid("C8BBA859-E3C4-456A-80A7-9A2E87A79DA0")]
public sealed class ProbeProvider:IWidgetProvider
{
    public void CreateWidget(WidgetContext context){}
    public void DeleteWidget(string id,string state){}
    public void OnActionInvoked(WidgetActionInvokedArgs args){}
    public void OnWidgetContextChanged(WidgetContextChangedArgs args){}
    public void Activate(WidgetContext context){}
    public void Deactivate(string id){}
}
