namespace EdifierManager;

// This transport never opens Bluetooth or Windows audio endpoints.
public sealed class PreviewTransport : ICommandTransport
{
    public bool Connected { get; private set; } = true;
    public string TransportName => "离线预览 · 示例数据";
    public WireProfile Profile { get; }
    public Capabilities Capabilities { get; }
    public HeadsetModel Model { get; }
    public Dictionary<string, int> Values { get; } = new() { ["anc"] = 2, ["ambient"] = 6, ["eq"] = 0, ["game"] = 0, ["prompt"] = 7, ["cycle"] = 7, ["wear"] = 1, ["touch1"] = 1, ["touch2"] = 1, ["touch3"] = 2, ["touch4"] = 3 };
    public List<byte> Writes { get; } = [];
    public bool IgnoreWrites { get; set; }
    public int? MultipointIndex { get; set; }
    public List<(byte Command, byte[] Payload)> WriteRequests { get; } = [];
    private readonly bool observedLolli;
    private string deviceName;
    private int timerMinutes;
    private CustomEqualizer customEq = new(1, 0, [new(0, 100, 6, 7), new(1, 2000, 6, 7), new(2, 4000, 6, 7), new(3, 8000, 6, 7)]);
    public PreviewTransport(bool tws) : this(tws ? HeadsetModel.Lolli : HeadsetModel.W820Gold) { }
    public PreviewTransport(HeadsetModel model, bool observedFirmware = false)
    {
        observedLolli = model == HeadsetModel.Lolli && observedFirmware;
        Model = model; bool tws = model.Tws; bool modern = tws || model == HeadsetModel.W830;
        deviceName = "EDIFIER " + model.Label;
        Profile = new(modern ? 2 : 1); MultipointIndex = tws ? 2 : null;
        byte[] caps = new byte[30]; caps[0] = (byte)(12 | (tws ? 128 : 0)); caps[1] = caps[2] = caps[5] = 1;
        caps[13] = 1; caps[16] = (byte)(tws ? 14 : 15); caps[17] = 16 | 64;
        if (modern) caps[20] = 128;
        if (model == HeadsetModel.W830) { caps[13] = 8; caps[16] = 0; caps[29] = 64; Values["anc"] = 1; caps[3] = 4; caps[8] = caps[9] = caps[10] = 1; caps[18] = 4; }
        Values["multipoint"] = 0;
        if (tws) caps[17] |= 4;
        if (observedLolli)
        {
            caps = Convert.FromHexString("E30101040101010100010101093201001EE4004000200000010002");
            Values["anc"] = 1;
        }
        Capabilities = new(caps);
        foreach (int slot in Capabilities.TouchSlots) Values.TryAdd("touch" + slot, 2);
        if (observedLolli) { Values["touch8"] = Values["touch9"] = 5; Values["cycle8"] = Values["cycle9"] = 7; }
        Values["beep1"] = 1; Values["beep2"] = 7; Values["sensitivity"] = 8; Values["idleShutdown"] = 1; Values["promptSwitch"] = 1; Values["wind"] = 0; Values["ldac"] = 0;
    }
    public Task<byte[]?> RequestAsync(byte command, byte[]? payload = null, bool optional = false, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); if (!Connected) throw new IOException("预览已关闭。");
        byte[] p = payload ?? [];
        int Get(string key) => Values[key];
        if (command is 0xC1 or 0xC4 or 0x09 or 0x06 or 0xF1 or 0xFC or 0x7D or 0xCA or 0xD1 or 0xD2 or 0xD6 or 0xFA or 0x5D or 0x49 or 0xFE or 0x87 or 0x44 or 0x45 or 0xCD or 0xCF or 0xCE or 0x07 or 0xC7)
        {
            Writes.Add(command);
            WriteRequests.Add((command, p.ToArray()));
            if (!IgnoreWrites)
            {
                if (command == 0xC1) { int start = Profile.Version == 2 ? 1 : 0; Values["anc"] = p[start]; if (p.Length > start + 1) Values["ambient"] = p[start + 1]; }
                else if (command == 0xF1) { Values[p[0] == 10 ? "cycle" : "touch" + p[0]] = p[1]; if (p.Length == 3) Values["cycle" + p[0]] = p[2]; }
                else if (command == 0xFC) Values["wear"] = p[1];
                else if (command == 0x7D) Values["multipoint"] = p.Length == 2 ? p[1] : p[0] == 2 ? 1 : 0;
                else if (command == 0xCA) deviceName = System.Text.Encoding.UTF8.GetString(p);
                else if (command == 0xD1) timerMinutes = p[0] * 256 + p[1];
                else if (command == 0xD2) timerMinutes = 0;
                else if (command == 0x87) Values["beep" + p[1]] = p[2];
                else if (command == 0xFE) Values["sensitivity"] = p[^1];
                else if (command is 0xD6 or 0xFA or 0x5D or 0x49) Values[command switch { 0xD6 => "idleShutdown", 0xFA => "promptSwitch", 0x5D => "wind", _ => "ldac" }] = p[0];
                else if (command == 0x44) customEq.Bands[p[0]] = new(p[0], p[2] * 256 + p[3], p[4], p[5], p[1]);
                else if (command == 0x45) customEq = customEq with { Bands = customEq.Bands.Select(x => x with { Gain = 6 }).ToArray() };
                else if (command is 0xCD or 0xCF or 0xCE or 0x07 or 0xC7) { }
                else Values[command switch { 0xC4 => "eq", 0x09 => "game", _ => "prompt" }] = p[0];
            }
            return Task.FromResult<byte[]?>(p.ToArray());
        }
        byte[]? reply = command switch
        {
            0xD8 => Capabilities.Raw.ToArray(), 0xC6 => observedLolli ? [1, 0, 9] : [1, 0, 0],
            0xC9 => System.Text.Encoding.UTF8.GetBytes(deviceName), 0xD3 => [(byte)(timerMinutes >> 8), (byte)timerMinutes],
            0x86 => [4, (byte)Get("beep1"), (byte)Get("beep2")], 0xFD => [(byte)2, (byte)Get("sensitivity")],
            0xD7 => [(byte)Get("idleShutdown")], 0xF9 => [(byte)Get("promptSwitch")], 0x5C => [(byte)Get("wind")], 0x48 => [(byte)Get("ldac")],
            0x43 => (Profile.Version == 2 ? new byte[] { (byte)customEq.Group, (byte)customEq.Bands.Length } : []).Concat(customEq.Bands.SelectMany(customEq.EncodeBand)).ToArray(),
            0xD0 => [82], 0xF2 => Model.Tws ? [3, 86, 79, 255, 0, 0] : [4, 0, 0, 82, 0, 0],
            0xCC when observedLolli => Get("anc") == 2 ? [23,2,(byte)Get("ambient")] : [23,(byte)Get("anc")],
            0xCC => Profile.Version == 2 ? [(byte)(Model == HeadsetModel.W830 ? 22 : 12), (byte)Get("anc"), (byte)Get("ambient")] : [(byte)Get("anc"), (byte)Get("ambient")],
            0x7C => MultipointIndex is int index ? [(byte)index, (byte)Get("multipoint")] : [(byte)(Get("multipoint") == 1 ? 2 : 1)],
            0xD5 => [(byte)Get("eq")], 0x08 => [(byte)Get("game")], 0x05 => [(byte)Get("prompt")],
            0xFB => [(byte)(observedLolli ? 4 : 1), (byte)Get("wear")],
            0xF0 when p.Length == 1 && observedLolli && Get("touch" + p[0]) == 5 => [p[0], 5, (byte)Get("cycle" + p[0])],
            0xF0 when p.Length == 1 => [p[0], (byte)Get(p[0] == 10 ? "cycle" : "touch" + p[0])],
            _ => null
        };
        if (reply == null && !optional) throw new IOException("预览不支持该操作。");
        return Task.FromResult(reply);
    }
    public ValueTask DisposeAsync() { Connected = false; return ValueTask.CompletedTask; }
}
