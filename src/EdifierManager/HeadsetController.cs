using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EdifierManager;

public sealed partial class HeadsetController(ICommandTransport transport, HeadsetModel model, Capabilities capabilities, string deviceKey, string backupDirectory, bool preview = false)
{
    public ICommandTransport Transport => transport;
    public HeadsetModel Model => model;
    public Capabilities Capabilities => capabilities;
    public HeadsetSnapshot? Current { get; private set; }
    public bool StateUncertain { get; private set; }
    public bool ReconnectRequired { get; private set; }
    public int? PendingMultipointValue { get; private set; }
    public string? LastBackup { get; private set; }
    public event Action<string>? Log;
    private readonly SemaphoreSlim operations = new(1, 1);
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = false };
    public static string MakeDeviceKey(ulong address) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address.ToString("X12"))))[..20];

    public static async Task<Capabilities> NegotiateAsync(EdifierTransport link, WireProfile? advertised, CancellationToken token)
    {
        var profiles = advertised != null ? new[] { advertised } : [new WireProfile(), new(2), new(2, 16)];
        foreach (var profile in profiles)
        {
            link.Configure(profile);
            var data = await link.RequestAsync(0xD8, optional: true, token: token);
            if (Capabilities.IsValid(data)) return new(data!);
            await Task.Delay(150, token);
        }
        throw new IOException("未能读取耳机能力。请关闭手机上的 EDIFIER Connect，重新连接后再试。");
    }

    public async Task<HeadsetSnapshot> RefreshAsync(CancellationToken token = default)
    {
        await operations.WaitAsync(token);
        try { return Current = await ReadAsync(token); }
        finally { operations.Release(); }
    }
    private async Task<HeadsetSnapshot> ReadAsync(CancellationToken token)
    {
        var values = new Dictionary<string, int>();
        int? left = null, right = null, box = null;
        bool bothEarsOnline = false;
        MultipointState? multipoint = null;
        string firmware = "未上报";
        async Task<byte[]?> Get(byte cmd, byte[]? data = null) => await transport.RequestAsync(cmd, data, true, token);
        if (capabilities.Firmware)
        {
            var fw = await Get(0xC6);
            if (fw is { Length: >= 3 and <= 16 }) firmware = string.Join('.', fw.Take(3));
        }
        if (capabilities.Battery)
        {
            bool multi = model.Tws || capabilities.NewBattery;
            var bat = await Get(multi ? (byte)0xF2 : (byte)0xD0);
            (left, right, box) = ParseBattery(bat, multi, model.Tws, capabilities.BoxBattery);
            bothEarsOnline = model.Tws && bat is { Length: >= 4 and <= 8 } && bat[0] == 3;
        }
        if (capabilities.AncOptions.Count > 0 || transport.Profile.Version == 2)
        {
            var anc = await ReadAncAsync(token);
            if (anc != null) StoreAnc(values, anc);
        }
        if (capabilities.EqOptions.Count > 0)
        {
            var eq = await Get(0xD5);
            if (eq is { Length: 1 } && capabilities.EqOptions.Any(x => x.Value == eq[0])) values["eq"] = eq[0];
        }
        if (capabilities.Game)
        {
            var game = await Get(0x08); if (game is { Length: 1 } && game[0] <= 1) values["game"] = game[0];
        }
        if (capabilities.Prompt)
        {
            var prompt = await Get(0x05); if (prompt is { Length: 1 } && prompt[0] <= 15) values["prompt"] = prompt[0];
        }
        if (capabilities.Multipoint)
        {
            multipoint = MultipointState.Parse(await Get(0x7C));
            if (multipoint != null) values["multipoint"] = multipoint.Enabled ? 1 : 0;
        }
        if (model == HeadsetModel.W820Gold && capabilities.TouchGroup == 15 && capabilities.AncGroup is 12 or 13)
        {
            var cycle = await Get(0xF0, [10]);
            if (cycle is { Length: 2 } && cycle[0] == 10 && cycle[1] is 3 or 5 or 6 or 7) values["cycle"] = cycle[1];
        }
        if (model.Tws)
        {
            if (capabilities.WearDetection)
            {
                var wear = await Get(0xFB);
                if (wear is { Length: 2 })
                {
                    capabilities.SetWearGroup(wear[0]);
                    if (capabilities.WearOptions.Any(x => x.Value == wear[1])) values["wear"] = wear[1];
                }
            }
            foreach (int slot in capabilities.TouchSlots)
            {
                var touch = await Get(0xF0, [(byte)slot]);
                if (touch is { Length: 2 or 3 } && touch[0] == slot && capabilities.TouchOptions(slot).Any(x => x.Value == touch[1]))
                {
                    values["touch" + slot] = touch[1];
                    if (capabilities.TouchGroup == 30 && touch[1] == 5)
                    {
                        if (touch.Length == 3 && FeatureProtocol.ValidCycleMask(touch[2], capabilities.AncOptions.Count)) values["cycle" + slot] = touch[2];
                        else values.Remove("touch" + slot);
                    }
                }
            }
        }
        if (!transport.Connected) throw new IOException("耳机已断开。请重新连接。");
        return await ReadFeaturesAsync(new(model.Id, firmware, Convert.ToHexString(capabilities.Raw), values, left, right, box, DateTimeOffset.Now, capabilities.AncGroup, capabilities.WearGroup, multipoint, bothEarsOnline), token);
    }
    private sealed record AncState(int Group, int Mode, int? Gain);
    private async Task<AncState?> ReadAncAsync(CancellationToken token)
    {
        var data = await transport.RequestAsync(0xCC, optional: true, token: token);
        Log?.Invoke("声音状态 CC = " + (data == null ? "未回复" : Convert.ToHexString(data)));
        int offset = transport.Profile.Version == 2 ? 1 : 0;
        if (data == null || data.Length < offset + 1 || data.Length > offset + 2) return null;
        if (offset == 1) capabilities.SetReportedAncGroup(data[0]);
        if (!capabilities.AncOptions.Any(x => x.Value == data[offset])) return null;
        int? gain = data.Length == offset + 2 && capabilities.AmbientMode != null && data[offset + 1] is >= 3 and <= 9 ? data[offset + 1] : null;
        return new(capabilities.AncGroup, data[offset], gain);
    }
    private static void StoreAnc(Dictionary<string, int> values, AncState state)
    {
        values["anc"] = state.Mode;
        values.Remove("ambient");
        if (state.Gain is int gain) values["ambient"] = gain;
    }
    private async Task VerifyAncAsync(int group, int mode, int? gain, CancellationToken token)
    {
        AncState? last = null;
        // A write acknowledgement can precede the mode transition. Retry reads only.
        for (int attempt = 0; attempt < 6; attempt++)
        {
            await Task.Delay(attempt == 0 ? 200 : 300, token);
            last = await ReadAncAsync(token);
            if (!transport.Connected) throw new IOException("耳机已断开，声音设置尚未确认。请重新连接。");
            if (Current != null)
            {
                var settings = new Dictionary<string, int>(Current.Settings);
                settings.Remove("anc"); settings.Remove("ambient");
                if (last != null) StoreAnc(settings, last);
                Current = Current with { Settings = settings, AncGroup = capabilities.AncGroup, ReadAt = DateTimeOffset.Now };
            }
            if (last != null && last.Group == group && last.Mode == mode && (gain == null || last.Gain == gain)) return;
            Log?.Invoke($"声音校验 {attempt + 1}/6：目标 {group}/{mode}/{gain?.ToString() ?? "—"}，读回 {last?.Group.ToString() ?? "—"}/{last?.Mode.ToString() ?? "—"}/{last?.Gain?.ToString() ?? "—"}");
        }
        string Name(int value) => capabilities.AncOptions.FirstOrDefault(x => x.Value == value)?.Label ?? value.ToString();
        string actual = last == null ? "未取得有效状态" : Name(last.Mode) + (gain.HasValue ? $"，强度 {last.Gain?.ToString() ?? "未上报"}" : "");
        throw new IOException($"声音模式未确认：目标 {Name(mode)}{(gain.HasValue ? $"，强度 {gain}" : "")}；读回 {actual}。请重新连接检查。");
    }
    public static (int? left, int? right, int? box) ParseBattery(byte[]? bytes, bool multi, bool tws, bool hasBox)
    {
        int? Valid(byte b) => b <= 100 ? b : null;
        if (!multi) return bytes is { Length: 1 } ? (Valid(bytes[0]), null, null) : (null, null, null);
        if (bytes is not { Length: >= 4 and <= 8 } || bytes.All(x => x == 0)) return (null, null, null);
        if (!tws) return bytes[0] == 4 ? (Valid(bytes[3]), null, null) : (null, null, null);
        return (bytes[0] is 1 or 3 ? Valid(bytes[1]) : null, bytes[0] is 2 or 3 ? Valid(bytes[2]) : null, hasBox ? Valid(bytes[3]) : null);
    }
    public SettingsBackup Export()
    {
        var state = Current ?? throw new InvalidOperationException("请先读取耳机设置。");
        // Codec changes can disconnect/re-pair. They have a separate explicit operation.
        return new(1, deviceKey, transport.Profile, state with { Settings = state.Settings.Where(x => x.Key != "ldac").ToDictionary() }, preview);
    }
    public void ValidateBackup(SettingsBackup backup)
    {
        if (backup == null || backup.Schema != 1 || backup.Snapshot == null || backup.Snapshot.Settings == null || backup.Snapshot.Settings.Count > 64
            || backup.DeviceKey != deviceKey || backup.Snapshot.Model != model.Id || backup.Protocol != transport.Profile || backup.Preview != preview
            || backup.Snapshot.Capabilities != Convert.ToHexString(capabilities.Raw) || backup.Snapshot.Firmware != Current?.Firmware
            || backup.Snapshot.AncGroup != capabilities.AncGroup || backup.Snapshot.WearGroup != capabilities.WearGroup
            || backup.Snapshot.Settings.Keys.Any(x => x.StartsWith("beep")) && backup.Snapshot.BeepGroup != Current?.BeepGroup
            || backup.Snapshot.Settings.ContainsKey("sensitivity") && backup.Snapshot.SensitivityIndex != Current?.SensitivityIndex
            || backup.Snapshot.Settings.ContainsKey("multipoint") && (backup.Snapshot.Multipoint == null || backup.Snapshot.Multipoint.Index != Current?.Multipoint?.Index))
            throw new InvalidDataException("备份与当前耳机、固件或能力不匹配。请选择这副耳机导出的备份。");
        ValidateChanges(backup.Snapshot.Settings, Current ?? throw new IOException("请先读取耳机设置。"));
        if (backup.Snapshot.CustomEq != null) backup.Snapshot.CustomEq.ValidateAgainst(Current!.CustomEq ?? throw new InvalidDataException("当前耳机没有可恢复的自定义 EQ。"));
    }
    public void ValidateChanges(IReadOnlyDictionary<string, int> changes, HeadsetSnapshot snapshot)
    {
        if (changes.Count == 0 || changes.Count > 64) throw new InvalidDataException("没有可应用的设置。");
        foreach (var (key, value) in changes)
        {
            bool restoringAmbient = key == "ambient" && changes.ContainsKey("anc") && snapshot.Settings.ContainsKey("anc") && capabilities.AmbientMode != null;
            bool newCycle = key.StartsWith("cycle") && int.TryParse(key.AsSpan(5), out int newSlot) && capabilities.TouchGroup == 30 && snapshot.Settings.ContainsKey("touch" + newSlot) && changes.GetValueOrDefault("touch" + newSlot, -1) == 5;
            if (!snapshot.Settings.ContainsKey(key) && !restoringAmbient && !newCycle) throw new InvalidDataException("此设置尚未从耳机读取：" + key);
            bool valid = key switch
            {
                "anc" => capabilities.AncOptions.Any(x => x.Value == value),
                "ambient" => capabilities.AmbientMode != null && value is >= 3 and <= 9,
                "eq" => capabilities.EqOptions.Any(x => x.Value == value),
                "game" => capabilities.Game && value is 0 or 1,
                "prompt" => capabilities.Prompt && value is >= 0 and <= 15,
                "multipoint" => capabilities.Multipoint && snapshot.Multipoint != null && value is 0 or 1,
                "wear" => model.Tws && capabilities.WearDetection && capabilities.WearOptions.Any(x => x.Value == value),
                "cycle" => model == HeadsetModel.W820Gold && capabilities.TouchGroup == 15 && value is 3 or 5 or 6 or 7,
                "ldac" => capabilities.Ldac && value is >= 0 and <= 2 && snapshot.Settings.GetValueOrDefault("ldac", -1) == value,
                _ => FeatureProtocol.ValidSetting(key, value, snapshot, capabilities) || key.StartsWith("touch") && int.TryParse(key.AsSpan(5), out int slot) && model.Tws && capabilities.TouchOptions(slot).Any(x => x.Value == value)
            };
            if (!valid) throw new InvalidDataException("设置值不在已适配范围内：" + key);
            if (key == "multipoint" && value != snapshot.Settings[key] && model.Tws && !snapshot.BothEarsOnline)
                throw new IOException("修改双设备连接需要左右耳同时在线，请取出两只耳机后重新读取。");
            if ((key.StartsWith("touch") || key.StartsWith("cycle") && key != "cycle" || key == "sensitivity") && model.Tws && !snapshot.BothEarsOnline)
                throw new IOException("修改触控需要左右耳同时在线，请取出两只耳机后重新读取。");
        }
        if (changes.ContainsKey("ambient") && !changes.ContainsKey("anc")) throw new InvalidDataException("环境声强度必须与声音模式一起应用。");
        if (capabilities.TouchGroup == 30)
            foreach (int slot in capabilities.TouchSlots)
            {
                if (changes.TryGetValue("cycle" + slot, out _) && changes.GetValueOrDefault("touch" + slot, -1) != 5) throw new InvalidDataException("降噪循环必须与对应的触控动作一起应用。");
                if (changes.GetValueOrDefault("touch" + slot, -1) == 5 && !changes.ContainsKey("cycle" + slot)) throw new InvalidDataException("请选择降噪循环中的至少两种模式。");
            }
    }
    public async Task ApplyAsync(IReadOnlyDictionary<string, int> changes, CancellationToken token = default, SettingsBackup? expectedBackup = null)
    {
        await operations.WaitAsync(token);
        bool wrote = false;
        try
        {
            if (StateUncertain) throw new IOException("上次设置结果不确定，请断开并重新连接后再修改。");
            var before = await ReadAsync(token); Current = before;
            if (expectedBackup != null) ValidateBackup(expectedBackup);
            ValidateChanges(changes, before);
            Directory.CreateDirectory(backupDirectory);
            string path = Path.Combine(backupDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Export(), JsonOptions), token);
            LastBackup = path;
            async Task Put(byte cmd, byte[] body)
            {
                wrote = true;
                await transport.RequestAsync(cmd, body, token: token);
            }
            foreach (var (key, value) in changes.Where(x => x.Key is not ("anc" or "ambient" or "multipoint")))
            {
                if (key.StartsWith("cycle") && key != "cycle") continue;
                bool cycleChanged = key.StartsWith("touch") && changes.TryGetValue("cycle" + key[5..], out int child) && before.Settings.GetValueOrDefault("cycle" + key[5..], -1) != child;
                if (before.Settings[key] == value && !cycleChanged) continue;
                if (key.StartsWith("touch")) await Put(0xF1, FeatureProtocol.EncodeTouch(int.Parse(key.AsSpan(5)), value, changes, capabilities));
                else if (key == "cycle") await Put(0xF1, [10, (byte)value]);
                else if (key == "wear") await Put(0xFC, [(byte)capabilities.WearGroup, (byte)value]);
                else if (FeatureProtocol.EncodeSetting(key, value, before) is { } feature) await Put(feature.Command, feature.Payload);
                else await Put(key switch { "eq" => 0xC4, "game" => 0x09, "prompt" => 0x06, _ => throw new InvalidDataException() }, [(byte)value]);
                var readback = await ReadAsync(token); Current = readback;
                if (!readback.Settings.TryGetValue(key, out int actual) || actual != value) throw new IOException("设置读回不一致，已停止后续修改。");
                if (key.StartsWith("touch") && changes.TryGetValue("cycle" + key[5..], out int expectedCycle) && readback.Settings.GetValueOrDefault("cycle" + key[5..], -1) != expectedCycle) throw new IOException("触控降噪循环读回不一致，已停止后续修改。");
            }
            if (changes.TryGetValue("anc", out int mode))
            {
                bool ambientChange = changes.TryGetValue("ambient", out int gain) && (!before.Settings.TryGetValue("ambient", out int oldGain) || oldGain != gain);
                if (ambientChange || before.Settings["anc"] != mode)
                {
                    int group = capabilities.AncGroup;
                    byte[] Prefix(int m, int? g) => (transport.Profile.Version == 2 ? new[] { (byte)group, (byte)m } : new[] { (byte)m }).Concat(g.HasValue ? new[] { (byte)g.Value } : []).ToArray();
                    if (ambientChange)
                    {
                        int ambientMode = capabilities.AmbientMode!.Value;
                        await Put(0xC1, Prefix(ambientMode, gain));
                        await VerifyAncAsync(group, ambientMode, gain, token);
                    }
                    if (!ambientChange || mode != capabilities.AmbientMode)
                    {
                        await Put(0xC1, Prefix(mode, null));
                        await VerifyAncAsync(group, mode, mode == capabilities.AmbientMode && changes.ContainsKey("ambient") ? gain : null, token);
                    }
                }
            }
            // This setting can restart the headset. It must be the last write in a batch.
            if (changes.TryGetValue("multipoint", out int wanted) && before.Settings["multipoint"] != wanted)
            {
                // Refresh again: earlier writes may have changed the connection state or codec mode.
                var latest = await ReadAsync(token); Current = latest;
                ValidateChanges(new Dictionary<string, int> { ["multipoint"] = wanted }, latest);
                PendingMultipointValue = wanted;
                try { await Put(0x7D, latest.Multipoint!.Encode(wanted == 1)); }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException && !token.IsCancellationRequested)
                {
                    // A lost acknowledgement is not evidence that the setting failed or succeeded.
                    StateUncertain = true; ReconnectRequired = true; return;
                }
                try { Current = await ReadAsync(token); }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException && !token.IsCancellationRequested)
                { StateUncertain = true; ReconnectRequired = true; return; }
                if (!Current.Settings.TryGetValue("multipoint", out int actual))
                { StateUncertain = true; ReconnectRequired = true; return; }
                if (actual != wanted) throw new IOException("双设备连接读回与目标不一致，未确认生效。请重新连接检查。");
                PendingMultipointValue = null;
            }
        }
        catch { if (wrote) StateUncertain = true; throw; }
        finally { operations.Release(); }
    }
}
