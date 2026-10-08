// COM activation follows Microsoft's WindowsAppSDK Widgets sample (MIT).
using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;
using WinRT;

namespace CodexUsageMeter.WindowsWidget;

[ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IClassFactory
{
    [PreserveSig] int CreateInstance(IntPtr outer,ref Guid iid,out IntPtr instance);
    [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool locked);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class ProviderFactory : IClassFactory
{
    private readonly Lazy<WidgetProvider> provider=new(()=>new WidgetProvider());
    public int CreateInstance(IntPtr outer,ref Guid iid,out IntPtr instance)
    {
        instance=IntPtr.Zero;
        if(outer!=IntPtr.Zero)return unchecked((int)0x80040110);
        try
        {
            IntPtr inspectable=MarshalInspectable<IWidgetProvider>.FromManaged(provider.Value);
            try { return Marshal.QueryInterface(inspectable,ref iid,out instance); }
            finally { Marshal.Release(inspectable); }
        }
        catch(Exception error){WidgetFiles.Log("com-create",error);return Marshal.GetHRForException(error);}
    }
    public int LockServer(bool locked)=>0;
}

internal sealed class ComServer : IDisposable
{
    private readonly ProviderFactory factory=new();
    private readonly uint cookie;
    private readonly bool initialized;
    [DllImport("ole32.dll")]private static extern int CoInitializeEx(IntPtr reserved,uint concurrency);
    [DllImport("ole32.dll")]private static extern void CoUninitialize();
    [DllImport("ole32.dll")]private static extern int CoRegisterClassObject([MarshalAs(UnmanagedType.LPStruct)]Guid clsid,[MarshalAs(UnmanagedType.IUnknown)]object factory,uint context,uint flags,out uint cookie);
    [DllImport("ole32.dll")]private static extern int CoRevokeClassObject(uint cookie);

    internal ComServer()
    {
        Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero,0));
        initialized=true;
        Marshal.ThrowExceptionForHR(CoRegisterClassObject(new Guid(WidgetProvider.ClassId),factory,4,1,out cookie));
    }
    public void Dispose(){CoRevokeClassObject(cookie);GC.KeepAlive(factory);if(initialized)CoUninitialize();}
}
