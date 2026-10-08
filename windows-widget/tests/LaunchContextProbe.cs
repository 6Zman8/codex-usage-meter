using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Web.Script.Serialization;

internal static class LaunchContextProbe
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern int GetCurrentPackageFullName(ref uint length,StringBuilder name);
    [STAThread] private static int Main(string[] args)
    {
        if(args.Length<1)return 2;
        uint length=0;int status=GetCurrentPackageFullName(ref length,null);string identity=null;
        if(status==122){var name=new StringBuilder((int)length);status=GetCurrentPackageFullName(ref length,name);if(status==0)identity=name.ToString();}
        var report=new Dictionary<string,object>();
        report["packageError"]=status;report["packageFullName"]=identity;
        report["localApplicationData"]=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        report["roamingApplicationData"]=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        report["argumentCount"]=args.Length;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
        File.WriteAllText(args[0],new JavaScriptSerializer().Serialize(report),Encoding.UTF8);
        return args.Length>1?int.Parse(args[1]):0;
    }
}
