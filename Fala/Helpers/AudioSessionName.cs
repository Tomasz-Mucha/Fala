using System.Runtime.InteropServices;

namespace Fala.Helpers;

internal static class AudioSessionName
{
    public static void Set(string name, string iconPath)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        enumerator.GetDefaultAudioEndpoint(0, 1, out var device);
        var iid = typeof(IAudioSessionManager2).GUID;
        device.Activate(ref iid, 23, IntPtr.Zero, out var raw);
        var manager = (IAudioSessionManager2)raw;
        manager.GetSessionEnumerator(out var sessions);
        sessions.GetCount(out var count);
        var pid = (uint)Environment.ProcessId;
        for (var i = 0; i < count; i++)
        {
            sessions.GetSession(i, out var control);
            uint spid = 0;
            try
            {
                ((IAudioSessionControl2)control).GetProcessId(out spid);
            }
            catch
            {
                continue;
            }
            if (spid != pid)
                continue;
            control.SetDisplayName(name, IntPtr.Zero);
            if (!string.IsNullOrWhiteSpace(iconPath))
                control.SetIconPath(iconPath, IntPtr.Zero);
        }
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        void RegisterEndpointNotificationCallback(IntPtr pClient);
        void UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        void OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        void GetState(out int pdwState);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        void GetAudioSessionControl(IntPtr AudioSessionGuid, uint StreamFlags, out IAudioSessionControl SessionControl);
        void GetSimpleAudioVolume(IntPtr AudioSessionGuid, uint StreamFlags, out IntPtr AudioVolume);
        void GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
        void RegisterSessionNotification(IntPtr SessionNotification);
        void UnregisterSessionNotification(IntPtr SessionNotification);
        void RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionID, IntPtr duckNotification);
        void UnregisterDuckNotification(IntPtr duckNotification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        void GetCount(out int SessionCount);
        void GetSession(int SessionCount, out IAudioSessionControl Session);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8BA-D2B17441127A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        void GetState(out int pRetVal);
        void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, IntPtr EventContext);
        void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, IntPtr EventContext);
        void GetGroupingParam(out Guid pRetVal);
        void SetGroupingParam(ref Guid Override, IntPtr EventContext);
        void RegisterAudioSessionNotification(IntPtr NewNotifications);
        void UnregisterAudioSessionNotification(IntPtr NewNotifications);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA7-4C459B4F9B1B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        void GetState(out int pRetVal);
        void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, IntPtr EventContext);
        void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, IntPtr EventContext);
        void GetGroupingParam(out Guid pRetVal);
        void SetGroupingParam(ref Guid Override, IntPtr EventContext);
        void RegisterAudioSessionNotification(IntPtr NewNotifications);
        void UnregisterAudioSessionNotification(IntPtr NewNotifications);
        void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        void GetProcessId(out uint pRetVal);
        void IsSystemSoundsSession();
        void SetDuckingPreference(int optOut);
    }
}
