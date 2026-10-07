using System.Runtime.InteropServices;

namespace Tuner.Core.Audio;

/// <summary>
/// 默认播放设备切换（IPolicyConfig COM 接口，未公开文档但被所有 Windows 音频设置界面使用）。
/// 仅用于用户主动切换默认设备；接口 GUID/方法 vtable 布局在各 Windows 版本间稳定。
/// </summary>
public static class DefaultDeviceSwitcher
{
    private static readonly Guid PolicyConfigClientId = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format, IntPtr oldFormat);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }

    private sealed class PolicyConfigCom
    {
        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid clsid, IntPtr punk, uint clsContext, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);

        public static IPolicyConfig Create()
        {
            var clsid = PolicyConfigClientId;
            var iid = new Guid("f8679f50-850a-41cf-9c72-430f290290c8");
            var hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1 /*CLSCTX_ALL*/, ref iid, out var obj);
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);
            return (IPolicyConfig)obj;
        }
    }

    /// <summary>把指定设备（ID 形如 "{0.0.0.00000000}.{guid}"）设为默认播放设备（Multimedia + Communications 两种角色）。</summary>
    public static void SetDefaultDevice(string deviceId)
    {
        var config = PolicyConfigCom.Create();
        const int eConsole = 0, eMultimedia = 1;
        var hr = config.SetDefaultEndpoint(deviceId, eConsole);
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
        config.SetDefaultEndpoint(deviceId, eMultimedia);
    }
}
