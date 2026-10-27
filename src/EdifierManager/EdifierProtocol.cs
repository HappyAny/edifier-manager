namespace EdifierManager;

public record EdifierFrame(byte Header, byte Command, byte[] Data);
public record WireProfile(int Version = 1, int Encryption = 0)
{
    public void Validate()
    {
        if (Version is not (1 or 2) || Encryption is not (0 or 16) || Version == 1 && Encryption != 0)
            throw new NotSupportedException("此耳机的协议版本或加密方式尚未适配。");
    }
}

public static class EdifierProtocol
{
    public static byte[] Encode(byte command, ReadOnlySpan<byte> data, byte header = 0xAA, WireProfile? profile = null)
    {
        profile ??= new(); profile.Validate();
        if (profile.Version == 2)
        {
            if (data.Length > 2048) throw new ArgumentException("报文数据过长。");
            if (header is not (0xAA or 0xBB or 0xCC)) throw new ArgumentException("无效的报文头。");
            byte[] packet = new byte[data.Length + 6];
            packet[0] = header; packet[1] = 0xEC; packet[2] = command;
            packet[3] = (byte)(data.Length >> 8); packet[4] = (byte)data.Length;
            for (int i = 0; i < data.Length; i++) packet[i + 5] = (byte)(data[i] ^ (profile.Encryption == 16 ? 0xA5 : 0));
            packet[^1] = (byte)packet[..^1].Sum(b => b);
            return packet;
        }
        if (data.Length > 254) throw new ArgumentException("报文数据过长。");
        if (header is not (0xAA or 0xBB or 0xCC)) throw new ArgumentException("无效的报文头。");
        byte[] result = new byte[data.Length + 5];
        result[0] = header; result[1] = (byte)(data.Length + 1); result[2] = command;
        data.CopyTo(result.AsSpan(3));
        int sum = 0x2019;
        foreach (byte b in result.AsSpan(0, result.Length - 2)) sum += b;
        result[^2] = (byte)(sum >> 8); result[^1] = (byte)sum;
        return result;
    }

    public static EdifierFrame Decode(ReadOnlySpan<byte> bytes, WireProfile? profile = null)
    {
        profile ??= new(); profile.Validate();
        if (profile.Version == 2)
        {
            if (bytes.Length < 6 || bytes[0] is not (0xAA or 0xBB or 0xCC) || bytes[1] != 0xEC
                || bytes.Length != ((bytes[3] << 8) | bytes[4]) + 6 || bytes.Length > 2054)
                throw new FormatException("报文长度或报文头无效。");
            int checksum = 0; foreach (byte b in bytes[..^1]) checksum += b;
            if ((byte)checksum != bytes[^1]) throw new FormatException("报文校验失败。");
            byte[] data = bytes[5..^1].ToArray();
            if (profile.Encryption == 16) for (int i = 0; i < data.Length; i++) data[i] ^= 0xA5;
            return new(bytes[0], bytes[2], data);
        }
        if (bytes.Length < 5 || bytes[0] is not (0xAA or 0xBB or 0xCC) || bytes[1] < 1 || bytes.Length != bytes[1] + 4)
            throw new FormatException("报文长度或报文头无效。");
        int sum = 0x2019;
        foreach (byte b in bytes[..^2]) sum += b;
        if ((ushort)sum != (bytes[^2] << 8 | bytes[^1])) throw new FormatException("报文校验失败。");
        return new(bytes[0], bytes[2], bytes[3..^2].ToArray());
    }
}

// A notification can contain part of a packet or several complete packets.
public sealed class FrameDecoder
{
    public WireProfile Profile { get; set; } = new();
    private readonly List<byte> buffer = [];
    public void Clear() => buffer.Clear();
    public IReadOnlyList<EdifierFrame> Push(ReadOnlySpan<byte> bytes)
    {
        var result = new List<EdifierFrame>();
        foreach (byte b in bytes) buffer.Add(b);
        if (buffer.Count > 4096) { buffer.Clear(); return result; }
        while (buffer.Count >= 2)
        {
            if (buffer[0] is not (0xBB or 0xCC) || buffer[1] == 0) { buffer.RemoveAt(0); continue; }
            if (Profile.Version == 2 && buffer[1] != 0xEC) { buffer.RemoveAt(0); continue; }
            if (Profile.Version == 2 && buffer.Count < 5) break;
            int length = Profile.Version == 1 ? buffer[1] + 4 : ((buffer[3] << 8) | buffer[4]) + 6;
            if (length > 2054) { buffer.RemoveAt(0); continue; }
            if (buffer.Count < length) break;
            try { result.Add(EdifierProtocol.Decode(buffer.Take(length).ToArray(), Profile)); buffer.RemoveRange(0, length); }
            catch (FormatException) { buffer.RemoveAt(0); }
        }
        return result;
    }
}

public interface ICommandTransport : IAsyncDisposable
{
    bool Connected { get; }
    string TransportName { get; }
    WireProfile Profile { get; }
    Task<byte[]?> RequestAsync(byte command, byte[]? payload = null, bool optional = false, CancellationToken token = default);
}
