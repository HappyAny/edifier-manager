namespace EdifierManager;

public record Choice(int Value, string Label)
{
    public override string ToString() => Label;
}

public record HeadsetModel(string Id, string Label, bool Tws, string Code)
{
    public static readonly HeadsetModel W820 = new("w820nb", "W820NB", false, "3801");
    public static readonly HeadsetModel W820Gold = new("w820nb-double-gold", "W820NB 双金标版", false, "7901");
    public static readonly HeadsetModel W830 = new("w830nb", "W830NB", false, "8501");
    public static readonly HeadsetModel Lolli = new("lolli-anc", "Lolli ANC", true, "4201");
    public static HeadsetModel Identify(string name, string? productCode = null)
    {
        if (productCode != null) return productCode switch { "3801" => W820, "7901" => W820Gold, "8501" or "1001" => W830, "4201" => Lolli, _ => throw new NotSupportedException("此型号尚未适配。") };
        string value = name.Trim();
        if (value.StartsWith("EDIFIER ", StringComparison.OrdinalIgnoreCase)) value = value[8..].Trim();
        if (value.Equals("Lolli ANC", StringComparison.OrdinalIgnoreCase)) return Lolli;
        if (value.Equals("W820NB 双金标版", StringComparison.OrdinalIgnoreCase) || value.Equals("W820NB Double Gold", StringComparison.OrdinalIgnoreCase)) return W820Gold;
        if (value.Equals("W820NB", StringComparison.OrdinalIgnoreCase)) return W820;
        if (value.Equals("W830NB", StringComparison.OrdinalIgnoreCase)) return W830;
        throw new NotSupportedException("暂不支持这个型号或名称。当前适配 W820NB、W820NB 双金标版、W830NB 和 Lolli ANC。");
    }
    public static string? ProductCodeFromService(Guid service) => service.ToString().ToLowerInvariant() switch
    {
        "00004201-0000-1000-8000-00805f9b34fb" or "48094201-1a48-11e9-ab14-d663bd873d93" => "4201",
        "00007900-0000-1000-8000-00805f9b34fb" or "48097901-1a48-11e9-ab14-d663bd873d93" => "7901",
        "00003800-0000-1000-8000-00805f9b34fb" or "48093801-1a48-11e9-ab14-d663bd873d93" => "3801",
        "00008500-0000-1000-8000-00805f9b34fb" or "48098501-1a48-11e9-ab14-d663bd873d93" => "8501",
        "00001001-0000-1000-8000-00805f9b34fb" or "48091001-1a48-11e9-ab14-d663bd873d93" => "1001",
        _ => null
    };
}

public sealed class Capabilities(byte[] raw)
{
    public byte[] Raw { get; } = raw.ToArray();
    private int B(int index) => index < Raw.Length ? Raw[index] : 0;
    private int? reportedAncGroup;
    public int AncGroup => reportedAncGroup ?? (B(0) & 15);
    public void SetReportedAncGroup(int group) => reportedAncGroup = group;
    public bool Tws => (B(0) & 128) != 0;
    public bool Battery => B(1) != 0;
    public bool Firmware => B(5) == 1;
    public int EqGroup => B(13);
    public int TouchGroup => B(16);
    public bool Prompt => (B(17) & 16) != 0;
    public bool WearDetection => (B(17) & 4) != 0;
    public int WearGroup { get; private set; }
    public void SetWearGroup(int group) => WearGroup = group;
    public IReadOnlyList<Choice> WearOptions => WearGroup switch
    {
        1 or 4 => [new(0, "关闭入耳检测"), new(1, "摘下暂停，戴回继续播放"), new(2, "摘下暂停，戴回不自动播放")],
        2 => [new(0, "关闭入耳检测"), new(1, "摘下暂停，戴回继续播放")],
        3 => [new(0, "关闭入耳检测"), new(2, "摘下暂停，戴回不自动播放")],
        _ => []
    };
    public bool Game => (B(17) & 64) != 0;
    public bool BoxBattery => (B(17) & 128) != 0;
    public bool NewBattery => (B(29) & 64) != 0;
    public bool Multipoint => (B(20) & 128) != 0;
    public bool QueryName => B(2) == 1;
    public int NameLimit => B(3) switch { 1 or 6 => 24, 2 or 5 => 30, 3 => 29, 4 => 35, _ => 0 };
    public bool IdleShutdown => B(8) == 1;
    public bool Timer => B(9) > 0;
    public bool PromptSwitch => (B(17) & 2) != 0;
    public bool NewPrompt => (B(21) & 32) != 0;
    public bool Sensitivity => (B(17) & 8) != 0;
    public bool Ldac => (B(18) & 4) != 0;
    public bool Wind => (B(18) & 16) != 0;
    public bool CanDeviceAction(string action) => action switch
    {
        "disconnect" => B(6) == 1, "pair" => B(7) == 1, "poweroff" => B(10) == 1,
        "reset" => (B(17) & 1) != 0, "clear-pairing" => (B(18) & 64) != 0, _ => false
    };
    public static bool IsValid(byte[]? value) => value is { Length: >= 14 and <= 128 } && value[2] <= 1 && value[5] <= 1 && value.Any(x => x != 0);
    public IReadOnlyList<Choice> AncOptions => AncGroup switch
    {
        1 or 3 => [new(2, "降噪"), new(3, "环境声")],
        2 => [new(1, "环境声关闭"), new(2, "环境声")],
        4 => [new(2, "关闭"), new(1, "环境声")],
        5 => [new(2, "深度降噪"), new(3, "舒适降噪"), new(4, "环境声")],
        6 => [new(1, "标准"), new(2, "深度降噪"), new(3, "舒适降噪"), new(4, "环境声")],
        7 or 9 => [new(1, "标准"), new(2, "降噪")],
        10 => [new(1, "标准"), new(2, "深度降噪"), new(3, "舒适降噪"), new(4, "环境声"), new(5, "防风噪")],
        11 => [new(1, "标准"), new(2, "舒适降噪")],
        12 or 13 => [new(1, "标准"), new(2, "降噪"), new(3, "环境声")],
        15 => [new(1, "深度降噪"), new(2, "舒适降噪"), new(3, "环境声"), new(4, "标准"), new(5, "防风噪")],
        16 => [new(1, "深度降噪"), new(2, "舒适降噪"), new(3, "防风噪"), new(4, "环境声"), new(5, "降噪关闭")],
        17 => [new(1, "自适应降噪"), new(2, "环境声"), new(3, "降噪关闭")],
        18 => [new(1, "深度降噪"), new(2, "舒适降噪"), new(3, "环境声"), new(4, "降噪关闭")],
        20 => [new(1, "深度降噪"), new(2, "宽频降噪"), new(3, "环境声"), new(4, "降噪关闭")],
        22 => [new(1, "深度降噪"), new(2, "宽频降噪"), new(3, "防风噪"), new(4, "环境声"), new(5, "降噪关闭")],
        23 => [new(1, "降噪"), new(2, "环境声"), new(3, "降噪关闭")],
        _ => []
    };
    public int? AmbientMode => AncGroup switch { 3 or 12 or 13 or 15 or 18 or 20 => 3, 4 => 1, 5 or 6 or 16 or 22 => 4, 17 or 23 => 2, _ => null };
    public IReadOnlyList<Choice> EqOptions => EqGroup switch
    {
        1 => [new(0, "经典"), new(1, "流行"), new(2, "古典"), new(3, "摇滚")],
        2 => [new(0, "经典"), new(1, "流行"), new(2, "古典"), new(3, "摇滚"), new(4, "舞曲"), new(5, "蓝调")],
        3 => [new(0, "经典"), new(1, "流行"), new(2, "摇滚")],
        4 => [new(0, "经典"), new(1, "流行"), new(2, "古典"), new(3, "摇滚"), new(4, "耳机内的自定义预设")],
        6 => [new(0, "标准"), new(1, "环绕"), new(2, "游戏")],
        7 => [new(0, "纯正"), new(1, "动感"), new(2, "耳机内的自定义预设")],
        8 => [new(0, "经典"), new(1, "动感"), new(2, "空间音效"), new(3, "耳机内的自定义预设")],
        50 => [new(0, "经典"), new(1, "重低音"), new(2, "清澈人声"), new(3, "高音增强"), new(4, "剧院")],
        _ => []
    };
    public int[] TouchSlots => TouchGroup switch { 5 or 6 or 8 or 9 or 14 or 23 => [1, 2, 3, 4], 13 or 30 => [1, 2, 3, 4, 8, 9], _ => [] };
    public IReadOnlyList<Choice> TouchOptions(int slot)
    {
        if (!TouchSlots.Contains(slot)) return [];
        bool left = slot is 1 or 3 or 8;
        string volume = left ? "音量增加" : "音量减小";
        string track = left ? "上一曲" : "下一曲";
        string[] labels = TouchGroup switch
        {
            5 => ["播放 / 暂停", volume, track, "语音助手", "游戏模式"],
            6 => ["播放 / 暂停", volume, track, "语音助手", "游戏模式", "降噪模式切换"],
            8 => ["播放 / 暂停", volume, "上一曲", "下一曲", "游戏模式", "语音助手", "降噪模式切换"],
            9 => ["降噪模式切换", track, volume, "播放 / 暂停", "语音助手", "游戏模式"],
            13 => ["上一曲", "下一曲", "游戏模式", "音量增加", "音量减小", "语音助手"],
            14 => ["播放 / 暂停", "上一曲", "下一曲", "游戏模式", "语音助手"],
            23 => ["播放 / 暂停", "上一曲", "下一曲", "降噪模式切换", "游戏模式", "语音助手"],
            30 => ["无功能", "播放 / 暂停", "上一曲", "下一曲", "降噪模式切换", "游戏模式", "音量增加", "音量减小", "语音助手"],
            _ => []
        };
        return labels.Select((label, index) => new Choice(index + 1, label)).ToArray();
    }
    public static string TouchLabel(int slot) => slot switch { 1 => "左耳 · 双击", 2 => "右耳 · 双击", 3 => "左耳 · 三击", 4 => "右耳 · 三击", 8 => "左耳 · 长按", 9 => "右耳 · 长按", _ => "未知操作" };
}

public record HeadsetSnapshot(string Model, string Firmware, string Capabilities, Dictionary<string, int> Settings,
    int? LeftBattery = null, int? RightBattery = null, int? BoxBattery = null, DateTimeOffset? ReadAt = null, int AncGroup = 0, int WearGroup = 0,
    MultipointState? Multipoint = null, bool BothEarsOnline = false)
{
    public string? DeviceName { get; init; }
    public int? ShutdownMinutes { get; init; }
    public int? BeepGroup { get; init; }
    public int? SensitivityIndex { get; init; }
    public CustomEqualizer? CustomEq { get; init; }
}
public record SettingsBackup(int Schema, string DeviceKey, WireProfile Protocol, HeadsetSnapshot Snapshot, bool Preview = false);

public record MultipointState(int? Index, bool Enabled)
{
    public static MultipointState? Parse(byte[]? data) => data switch
    {
        { Length: 1 } when data[0] is >= 1 and <= 4 => new(null, data[0] is 2 or 4),
        { Length: 2 } when data[0] is 0 or 1 or 2 or 3 or 255 && data[1] <= 1 => new(data[0], data[1] == 1),
        _ => null
    };
    public byte[] Encode(bool enabled) => Index is int index
        ? [(byte)index, (byte)(enabled ? 1 : 0)] : [(byte)(enabled ? 2 : 1)];
}
