using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexUsageMeter.WindowsWidget;

internal static class WindowsLauncher
{
    // Same breakaway policy used by WindowsAppSDK DeploymentManager for packaged desktop callers.
    // The meter and its descendants must keep their existing unpackaged profile and registry view.
    internal static int? StartAndObserve(ProcessStartInfo start,int waitMilliseconds=8000)
    {
        IntPtr list=IntPtr.Zero,policy=IntPtr.Zero;
        PROCESS_INFORMATION process=default;
        bool initialized=false;
        try
        {
            nuint bytes=0;
            InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref bytes);
            list=Marshal.AllocHGlobal(checked((int)bytes));
            if(!InitializeProcThreadAttributeList(list,1,0,ref bytes))throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized=true;
            policy=Marshal.AllocHGlobal(sizeof(uint));
            Marshal.WriteInt32(policy,1); // PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_ENABLE_PROCESS_TREE
            if(!UpdateProcThreadAttribute(list,0,(nuint)0x00020012,policy,(nuint)sizeof(uint),IntPtr.Zero,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup=new STARTUPINFOEX();startup.StartupInfo.cb=Marshal.SizeOf<STARTUPINFOEX>();startup.AttributeList=list;
            var command=new StringBuilder(Quote(start.FileName)+(start.Arguments.Length>0?" "+start.Arguments:""));
            if(!CreateProcessW(start.FileName,command,IntPtr.Zero,IntPtr.Zero,false,0x00080000|0x08000000,IntPtr.Zero,start.WorkingDirectory,ref startup,out process))throw new Win32Exception(Marshal.GetLastWin32Error());
            uint wait=WaitForSingleObject(process.Process,(uint)waitMilliseconds);
            if(wait==0x102)return null; // A newly started resident meter is expected to remain running.
            if(wait!=0)throw new Win32Exception(Marshal.GetLastWin32Error());
            if(!GetExitCodeProcess(process.Process,out uint code))throw new Win32Exception(Marshal.GetLastWin32Error());
            return unchecked((int)code);
        }
        finally
        {
            if(process.Thread!=IntPtr.Zero)CloseHandle(process.Thread);
            if(process.Process!=IntPtr.Zero)CloseHandle(process.Process);
            if(initialized)DeleteProcThreadAttributeList(list);
            if(list!=IntPtr.Zero)Marshal.FreeHGlobal(list);
            if(policy!=IntPtr.Zero)Marshal.FreeHGlobal(policy);
        }
    }
    internal static string Quote(string value)
    {
        var result=new StringBuilder("\"");int slashes=0;
        foreach(char c in value)
        {
            if(c=='\\'){slashes++;continue;}
            result.Append('\\',c=='"'?slashes*2+1:slashes);result.Append(c);slashes=0;
        }
        result.Append('\\',slashes*2);return result.Append('"').ToString();
    }
    internal static (int Error,string? Name) PackageIdentity()
    {
        uint size=0;int error=GetCurrentPackageFullName(ref size,null);
        if(error!=122)return(error,null);
        var name=new StringBuilder((int)size);error=GetCurrentPackageFullName(ref size,name);
        return(error,error==0?name.ToString():null);
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct STARTUPINFO
    {
        public int cb;public IntPtr Reserved,Desktop,Title;public int X,Y,XSize,YSize,XCountChars,YCountChars,FillAttribute,Flags;
        public short ShowWindow,ReservedBytes;public IntPtr Reserved2,StdInput,StdOutput,StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX{public STARTUPINFO StartupInfo;public IntPtr AttributeList;}
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION{public IntPtr Process,Thread;public uint ProcessId,ThreadId;}
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,uint flags,ref nuint bytes);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,nuint attribute,IntPtr value,nuint bytes,IntPtr previous,IntPtr returnedBytes);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessW(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,[MarshalAs(UnmanagedType.Bool)]bool inheritHandles,uint flags,IntPtr environment,string directory,ref STARTUPINFOEX startup,out PROCESS_INFORMATION process);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(IntPtr process,out uint exitCode);
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern int GetCurrentPackageFullName(ref uint length,StringBuilder? name);
}
