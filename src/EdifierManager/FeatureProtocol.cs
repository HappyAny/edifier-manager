using System.Numerics;
using System.Text;

namespace EdifierManager;

public record SettingCommand(byte Command, byte[] Payload);
public record BeepOption(int Position, string Label, int Minimum = 0, int Maximum = 1);

public static class FeatureProtocol
{
    public static IReadOnlyList<BeepOption> BeepOptions(int? group) => group switch
    {
        1 => [new(1, "开机提示音"), new(2, "蓝牙连接提示音"), new(3, "声音模式提示音"), new(4, "灯光提示音")],
        2 => [new(1, "蓝牙连接提示音")],
        3 => [new(1, "蓝牙连接提示音"), new(2, "最大音量提示音")],
        4 => [new(1, "触控提示音"), new(2, "提示音量", 0, 15)],
        5 => [new(1, "蓝牙连接提示音"), new(2, "最大音量提示音"), new(3, "低电量提示音")],
        6 => [new(1, "蓝牙连接提示音"), new(2, "最大音量提示音"), new(3, "双耳连接提示音")],
        7 => [new(1, "提示音"), new(2, "提示音量", 0, 15)],
        13 => [new(1, "蓝牙连接提示音"), new(2, "低电量提示音"), new(3, "双耳连接提示音")],
        15 => [new(1, "提示音"), new(2, "提示音量", 1, 15)],
        18 => [new(1, "按压提示音"), new(2, "提示音量", 0, 15)],
        _ => []
    };
    public static bool ValidCycleMask(int value, int modes) => modes is >= 2 and <= 8 && value > 0 && value < (1 << modes) && BitOperations.PopCount((uint)value) is >= 2 and <= 3;
    public static byte[] EncodeTouch(int slot, int value, IReadOnlyDictionary<string, int> changes, Capabilities capabilities)
    {
        if (!capabilities.TouchSlots.Contains(slot) || !capabilities.TouchOptions(slot).Any(x => x.Value == value)) throw new InvalidDataException("无效触控动作。");
        if (capabilities.TouchGroup != 30 || value != 5) return [(byte)slot, (byte)value];
        if (!changes.TryGetValue("cycle" + slot, out int mask) || !ValidCycleMask(mask, capabilities.AncOptions.Count)) throw new InvalidDataException("降噪循环需要选择两至三种模式。");
        return [(byte)slot, (byte)value, (byte)mask];
    }
    public static bool ValidSetting(string key, int value, HeadsetSnapshot state, Capabilities caps)
    {
        if (key.StartsWith("cycle") && int.TryParse(key.AsSpan(5), out int slot))
            return caps.TouchGroup == 30 && caps.TouchSlots.Contains(slot) && ValidCycleMask(value, caps.AncOptions.Count);
        if (key.StartsWith("beep") && int.TryParse(key.AsSpan(4), out int position))
            return caps.NewPrompt && BeepOptions(state.BeepGroup).Any(x => x.Position == position && value >= x.Minimum && value <= x.Maximum);
        return key switch
        {
            "promptSwitch" => caps.PromptSwitch && value is 0 or 1,
            "idleShutdown" => caps.IdleShutdown && value is 0 or 1,
            "wind" => caps.Wind && value is 0 or 1,
            "sensitivity" => caps.Sensitivity && state.SensitivityIndex is null or 1 or 2 && value >= 0 && value <= (state.SensitivityIndex == 2 ? 15 : 31),
            _ => false
        };
    }
    public static SettingCommand? EncodeSetting(string key, int value, HeadsetSnapshot state)
    {
        if (key.StartsWith("beep") && int.TryParse(key.AsSpan(4), out int position) && state.BeepGroup is int group)
            return new(0x87, [(byte)group, (byte)position, (byte)value]);
        return key switch
        {
            "promptSwitch" => new(0xFA, [(byte)value]),
            "idleShutdown" => new(0xD6, [(byte)value]),
            "wind" => new(0x5D, [(byte)value]),
            "sensitivity" => new(0xFE, state.SensitivityIndex is int index ? [(byte)index, (byte)value] : [(byte)value]),
            _ => null
        };
    }
    public static string? DecodeName(byte[]? bytes)
    {
        if (bytes is not { Length: >= 1 and <= 128 }) return null;
        try
        {
            var name = new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\0');
            return string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) ? null : name;
        }
        catch (DecoderFallbackException) { return null; }
    }
    public static byte[] EncodeName(string name, int limit)
    {
        if (limit == 0 || string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl)) throw new InvalidDataException("名称不能为空，也不能包含换行或控制字符。");
        var bytes = new UTF8Encoding(false, true).GetBytes(name);
        if (bytes.Length > limit) throw new InvalidDataException($"名称不能超过 {limit} 个 UTF-8 字节；一个汉字通常占 3 字节。");
        return bytes;
    }
    public static int? ParseTimer(byte[]? data) => data switch { [0] => 0, { Length: 2 } => (data[0] << 8) | data[1], _ => null };
    public static string Label(string key) => key switch
    {
        "idleShutdown" => "无音频自动关机", "promptSwitch" => "提示音开关", "wind" => "防风噪", "sensitivity" => "触控灵敏度", "ldac" => "LDAC 音质", _ => key
    };
}
