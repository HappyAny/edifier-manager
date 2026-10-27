using System.Runtime.InteropServices;

namespace EdifierManager;

public record AudioEndpoint(string Id, string Name, bool IsMicrophone, int Volume, bool Muted)
{
    public override string ToString() => Name;
}

public static class WindowsAudio
{
    private static bool Matches(string actual, string expected) => expected.Length >= 1 && System.Text.RegularExpressions.Regex.IsMatch(actual, @"(?:^|\()" + System.Text.RegularExpressions.Regex.Escape(expected) + @"(?=$|\)|\s+(?:Hands-Free|Stereo)\b)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
    private static readonly Guid VolumeId = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static IAudioEndpointVolume Volume(IMMDevice device)
    {
        Guid id = VolumeId;
        Check(device.Activate(ref id, 23, IntPtr.Zero, out object obj));
        return (IAudioEndpointVolume)obj;
    }
    private static string Name(IMMDevice device)
    {
        Check(device.OpenPropertyStore(0, out var store));
        try
        {
            var key = new PropertyKey { Format = new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
            Check(store.GetValue(ref key, out var value));
            try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) ?? "" : ""; }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    public static List<AudioEndpoint> List(string expectedName)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        var result = new List<AudioEndpoint>();
        try
        {
            for (int flow = 0; flow < 2; flow++)
            {
                Check(enumerator.EnumAudioEndpoints(flow, 1, out var collection));
                try
                {
                    Check(collection.GetCount(out uint count));
                    for (uint i = 0; i < count; i++)
                    {
                        Check(collection.Item(i, out var device));
                        try
                        {
                            string name = Name(device);
                            if (!Matches(name, expectedName)) continue;
                            Check(device.GetId(out string id));
                            var vol = Volume(device);
                            try
                            {
                                Check(vol.GetMasterVolumeLevelScalar(out float scalar)); Check(vol.GetMute(out bool muted));
                                result.Add(new(id, name, flow == 1, (int)Math.Round(scalar * 100), muted));
                            }
                            finally { Marshal.ReleaseComObject(vol); }
                        }
                        finally { Marshal.ReleaseComObject(device); }
                    }
                }
                finally { Marshal.ReleaseComObject(collection); }
            }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
        return result;
    }

    public static void Set(string id, int volume, bool mute, string expectedName)
    {
        if (volume is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(volume));
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        try
        {
            Check(enumerator.GetDevice(id, out var device));
            try
            {
                Check(device.GetState(out uint state));
                if (!Matches(Name(device), expectedName) || state != 1)
                    throw new IOException("所选耳机音频设备已断开。请刷新音频设备。");
                var vol = Volume(device);
                try
                {
                    Guid context = Guid.Empty;
                    Check(vol.SetMasterVolumeLevelScalar(volume / 100f, ref context)); Check(vol.SetMute(mute, ref context));
                    Check(vol.GetMasterVolumeLevelScalar(out float actual)); Check(vol.GetMute(out bool actualMute));
                    if (Math.Abs(actual * 100 - volume) > 1.5 || actualMute != mute) throw new IOException("Windows 音频设置读回不符。");
                }
                finally { Marshal.ReleaseComObject(vol); }
            }
            finally { Marshal.ReleaseComObject(device); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    public static void MediaKey(byte key)
    {
        if (key is not (0xB0 or 0xB1 or 0xB3)) throw new ArgumentOutOfRangeException(nameof(key));
        keybd_event(key, 0, 0, UIntPtr.Zero); keybd_event(key, 0, 2, UIntPtr.Zero);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(uint mode, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant
    { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr client);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr client);
        [PreserveSig] int GetChannelCount(out uint channels);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float db, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
