using System.Runtime.InteropServices;
namespace CorePilot.Services;

// Uses Windows Core Audio for the default render device; no external dependencies.
internal static class AudioEndpoint
{
    private static readonly Guid VolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    public static bool TryGet(out float level, out bool muted)
    {
        float currentLevel = 0; bool currentMuted = false;
        bool result = WithVolume(endpoint => { endpoint.GetMasterVolumeLevelScalar(out currentLevel); endpoint.GetMute(out currentMuted); });
        level = currentLevel; muted = currentMuted; return result;
    }
    public static bool TrySet(float value) => WithVolume(endpoint => {
        var context = Guid.Empty; endpoint.SetMasterVolumeLevelScalar(Math.Clamp(value, 0f, 1f), ref context);
    });
    public static bool TrySetMute(bool mute) => WithVolume(endpoint => {
        var context = Guid.Empty; endpoint.SetMute(mute, ref context);
    });

    private static bool WithVolume(Action<IAudioEndpointVolume> call)
    {
        object? obj = null; IMMDevice? device = null; IAudioEndpointVolume? volume = null;
        try
        {
            obj = new MMDeviceEnumerator();
            ((IMMDeviceEnumerator)obj).GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device);
            var guid = VolumeIid;
            device.Activate(ref guid, 23, IntPtr.Zero, out IntPtr ptr);
            try { volume = (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(ptr); }
            finally { Marshal.Release(ptr); }
            call(volume); return true;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or PlatformNotSupportedException) { return false; }
        finally
        {
            if (volume is not null) Marshal.ReleaseComObject(volume);
            if (device is not null) Marshal.ReleaseComObject(device);
            if (obj is not null) Marshal.ReleaseComObject(obj);
        }
    }

    private enum EDataFlow { Render, Capture, All }
    private enum ERole { Console, Multimedia, Communications }
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator {
        void EnumAudioEndpoints(EDataFlow flow, uint stateMask, out IntPtr devices);
        void GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice device);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        void RegisterEndpointNotificationCallback(IntPtr callback);
        void UnregisterEndpointNotificationCallback(IntPtr callback);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice {
        void Activate(ref Guid iid, uint context, IntPtr parameters, out IntPtr value);
        void OpenPropertyStore(uint mode, out IntPtr store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out uint state);
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume {
        void RegisterControlChangeNotify(IntPtr callback);
        void UnregisterControlChangeNotify(IntPtr callback);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float db, ref Guid context);
        void SetMasterVolumeLevelScalar(float level, ref Guid context);
        void GetMasterVolumeLevel(out float level);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float db, ref Guid context);
        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        void GetChannelVolumeLevel(uint channel, out float db);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        void GetVolumeStepInfo(out uint step, out uint steps);
        void VolumeStepUp(ref Guid context);
        void VolumeStepDown(ref Guid context);
        void QueryHardwareSupport(out uint value);
        void GetVolumeRange(out float min, out float max, out float increment);
    }
}
