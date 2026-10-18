using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SaturnTracktor;

public record AudioDevice(string Id,string Name,bool IsVirtual);
public sealed class RouteBackup
{
    public string VirtualId {get;set;}="";
    public Dictionary<int,string> Original {get;set;}=new();
}

public static class WindowsAudio
{
    public static string RecoveryPath=>Path.Combine(StateStore.Root,"route-recovery.json");
    public static AudioDevice[] Enumerate()
    {
        using var e=new MMDeviceEnumerator();
        return e.EnumerateAudioEndPoints(DataFlow.Render,DeviceState.Active).Select(d=>new AudioDevice(d.ID,d.FriendlyName,IsVirtual(d.FriendlyName))).ToArray();
    }
    public static bool IsVirtual(string name)=>name.Contains("FxSound",StringComparison.OrdinalIgnoreCase)||name.Contains("Saturn Audio Enhancer",StringComparison.OrdinalIgnoreCase);
    public static void RenameVirtualEndpoint()
    {
        using var e=new MMDeviceEnumerator();
        foreach(var d in e.EnumerateAudioEndPoints(DataFlow.Render,DeviceState.Active))
        {
            using(d)
            {
                if(!IsVirtual(d.FriendlyName))continue;
                object instance=new PolicyConfigClient();
                try
                {
                    SetLabel((IPolicyConfig)instance,d.ID,PropertyKeys.PKEY_Device_DeviceDesc,"Динамики");
                    SetLabel((IPolicyConfig)instance,d.ID,new PropertyKey(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"),6),"Saturn Audio Enhancer");
                }
                finally{Marshal.FinalReleaseComObject(instance);}
            }
        }
    }
    static void SetLabel(IPolicyConfig policy,string id,PropertyKey property,string label)
    {
        var text=Marshal.StringToCoTaskMemUni(label);var value=new PropVariant{vt=(short)VarEnum.VT_LPWSTR,pointerValue=text};
        try{Marshal.ThrowExceptionForHR(policy.SetPropertyValue(id,false,ref property,ref value));}
        finally{Marshal.FreeCoTaskMem(text);}
    }
    public static string Default(Role role=Role.Multimedia){using var e=new MMDeviceEnumerator();using var d=e.GetDefaultAudioEndpoint(DataFlow.Render,role);return d.ID;}
    public static void SetDefault(string id,Role role)
    {
        object instance=new PolicyConfigClient();
        try{var p=(IPolicyConfig)instance;Marshal.ThrowExceptionForHR(p.SetDefaultEndpoint(id,role));}
        finally{Marshal.FinalReleaseComObject(instance);}
    }
    public static RouteBackup Activate(string virtualId,string? physicalFallback=null)
    {
        var b=new RouteBackup{VirtualId=virtualId};
        foreach(var r in new[]{Role.Console,Role.Multimedia})
        {
            var id=Default(r);b.Original[(int)r]=id==virtualId&&physicalFallback!=null?physicalFallback:id;
        }
        Directory.CreateDirectory(StateStore.Root);File.WriteAllText(RecoveryPath,JsonSerializer.Serialize(b));
        try {foreach(var r in b.Original.Keys)SetDefault(virtualId,(Role)r);}
        catch{Restore(b);throw;}return b;
    }
    public static void Restore(RouteBackup? backup)
    {
        if(backup==null)return;var okay=true;
        foreach(var p in backup.Original)
        {
            try
            {
                if(Default((Role)p.Key)!=backup.VirtualId)continue;
                var devices=Enumerate();var target=devices.FirstOrDefault(d=>d.Id==p.Value&&!d.IsVirtual)??devices.FirstOrDefault(d=>!d.IsVirtual);
                if(target!=null)SetDefault(target.Id,(Role)p.Key);else okay=false;
            }
            catch(Exception ex){okay=false;StateStore.Log("Restore: "+ex.Message);}
        }
        if(okay)try{File.Delete(RecoveryPath);}catch{}
    }
    public static void Recover(){try{if(File.Exists(RecoveryPath))Restore(JsonSerializer.Deserialize<RouteBackup>(File.ReadAllText(RecoveryPath)));}catch(Exception ex){StateStore.Log("Recovery: "+ex);}}
}

[ComImport,Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]class PolicyConfigClient{}
[ComImport,Guid("F8679F50-850A-41CF-9C72-430F290290C8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPolicyConfig
{
    [PreserveSig]int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)]string id,out IntPtr format);
    [PreserveSig]int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)]string id,int def,out IntPtr format);
    [PreserveSig]int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)]string id);
    [PreserveSig]int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)]string id,IntPtr endpoint,IntPtr mix);
    [PreserveSig]int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)]string id,int def,out long defPeriod,out long minPeriod);
    [PreserveSig]int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)]string id,ref long period);
    [PreserveSig]int GetShareMode([MarshalAs(UnmanagedType.LPWStr)]string id,IntPtr mode);
    [PreserveSig]int SetShareMode([MarshalAs(UnmanagedType.LPWStr)]string id,IntPtr mode);
    [PreserveSig]int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)]string id,[MarshalAs(UnmanagedType.Bool)]bool fxStore,ref PropertyKey key,out PropVariant value);
    [PreserveSig]int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)]string id,[MarshalAs(UnmanagedType.Bool)]bool fxStore,ref PropertyKey key,ref PropVariant value);
    [PreserveSig]int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)]string id,Role role);
    [PreserveSig]int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)]string id,int visible);
}
