using System.Runtime.InteropServices;

namespace EdifierManager;

// Uses an exact Core Audio endpoint ID; it never selects the default speaker.
public static class HeadsetFinder
{
    public static byte[] MakeTone(int channel, int seconds = 8, int sampleRate = 48000)
    {
        if (channel is < 1 or > 3 || seconds is < 1 or > 8 || sampleRate != 48000) throw new ArgumentOutOfRangeException();
        var bytes = new byte[seconds * sampleRate * 4];
        for (int sample = 0; sample < seconds * sampleRate; sample++)
        {
            double t = sample / (double)sampleRate, phase = t % 1.0;
            double envelope = phase < .28 ? Math.Min(1, Math.Min(phase / .025, (.28 - phase) / .025)) : 0;
            short value = (short)(Math.Sin(2 * Math.PI * ((int)t % 2 == 0 ? 1200 : 1600) * t) * envelope * 6553);
            for (int ear = 0; ear < 2; ear++)
                if ((channel & (1 << ear)) != 0)
                { int position = sample * 4 + ear * 2; bytes[position] = (byte)value; bytes[position + 1] = (byte)(value >> 8); }
        }
        return bytes;
    }
    public static string? EndpointForWaveDevice(uint id)
    {
        IntPtr sizePointer = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(sizePointer, 0);
            if (waveOutMessage((IntPtr)id, 0x812, sizePointer, IntPtr.Zero) != 0) return null;
            int size = Marshal.ReadInt32(sizePointer);
            if (size is < 2 or > 65536 || (size & 1) != 0) return null;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try { return waveOutMessage((IntPtr)id, 0x811, buffer, (IntPtr)size) == 0 ? Marshal.PtrToStringUni(buffer, size / 2)?.TrimEnd('\0') : null; }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { Marshal.FreeHGlobal(sizePointer); }
    }
    public static async Task PlayAsync(string endpointId, int channel, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(endpointId)) throw new ArgumentException("缺少音频端点。");
        token.ThrowIfCancellationRequested();
        uint? id = null;
        for (uint i = 0; i < waveOutGetNumDevs(); i++)
            if (string.Equals(EndpointForWaveDevice(i), endpointId, StringComparison.OrdinalIgnoreCase)) { id = i; break; }
        if (id == null) throw new IOException("未找到所选耳机的播放端点，请刷新音频设备。");
        var format = new WaveFormat { Format = 1, Channels = 2, SamplesPerSecond = 48000, BytesPerSecond = 192000, BlockAlign = 4, BitsPerSample = 16 };
        Check(waveOutOpen(out IntPtr handle, id.Value, ref format, IntPtr.Zero, IntPtr.Zero, 0));
        IntPtr data = IntPtr.Zero, header = IntPtr.Zero; bool prepared = false;
        try
        {
            Check(waveOutGetID(handle, out uint openedId));
            if (!string.Equals(EndpointForWaveDevice(openedId), endpointId, StringComparison.OrdinalIgnoreCase)) throw new IOException("音频设备已变化，已取消提示音。");
            byte[] pcm = MakeTone(channel); data = Marshal.AllocHGlobal(pcm.Length); Marshal.Copy(pcm, 0, data, pcm.Length);
            header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
            Marshal.StructureToPtr(new WaveHeader { Data = data, Length = (uint)pcm.Length }, header, false);
            Check(waveOutPrepareHeader(handle, header, (uint)Marshal.SizeOf<WaveHeader>())); prepared = true;
            token.ThrowIfCancellationRequested(); Check(waveOutWrite(handle, header, (uint)Marshal.SizeOf<WaveHeader>()));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while ((Marshal.PtrToStructure<WaveHeader>(header).Flags & 1) == 0)
            {
                await Task.Delay(40, token).ConfigureAwait(false);
                if (DateTime.UtcNow > deadline) throw new IOException("提示音播放超时，已停止。");
            }
        }
        finally
        {
            waveOutReset(handle);
            if (prepared) waveOutUnprepareHeader(handle, header, (uint)Marshal.SizeOf<WaveHeader>());
            waveOutClose(handle);
            if (header != IntPtr.Zero) Marshal.FreeHGlobal(header);
            if (data != IntPtr.Zero) Marshal.FreeHGlobal(data);
        }
    }
    private static void Check(uint code) { if (code != 0) throw new IOException($"Windows 音频播放失败（{code}）。"); }
    [StructLayout(LayoutKind.Sequential, Pack = 2)] private struct WaveFormat { public ushort Format, Channels; public uint SamplesPerSecond, BytesPerSecond; public ushort BlockAlign, BitsPerSample, ExtraSize; }
    [StructLayout(LayoutKind.Sequential)] private struct WaveHeader { public IntPtr Data; public uint Length, Recorded; public UIntPtr User; public uint Flags, Loops; public IntPtr Next; public UIntPtr Reserved; }
    [DllImport("winmm.dll")] private static extern uint waveOutGetNumDevs();
    [DllImport("winmm.dll")] private static extern uint waveOutMessage(IntPtr id, uint message, IntPtr param1, IntPtr param2);
    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr handle, uint id, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutGetID(IntPtr handle, out uint id);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr handle);
}
