using System.Text.Json;

namespace EdifierManager;

public sealed partial class HeadsetController
{
    private async Task<HeadsetSnapshot> ReadFeaturesAsync(HeadsetSnapshot state, CancellationToken token)
    {
        var values = state.Settings;
        async Task<byte[]?> Get(byte cmd) => await transport.RequestAsync(cmd, optional: true, token: token);
        async Task ReadBoolean(bool supported, byte cmd, string key)
        { if (supported && await Get(cmd) is { Length: 1 } data && data[0] <= 1) values[key] = data[0]; }
        if (capabilities.QueryName) state = state with { DeviceName = FeatureProtocol.DecodeName(await Get(0xC9)) };
        if (capabilities.Timer) state = state with { ShutdownMinutes = FeatureProtocol.ParseTimer(await Get(0xD3)) };
        await ReadBoolean(capabilities.IdleShutdown, 0xD7, "idleShutdown");
        await ReadBoolean(capabilities.PromptSwitch, 0xF9, "promptSwitch");
        await ReadBoolean(capabilities.Wind, 0x5C, "wind");
        if (capabilities.Ldac && await Get(0x48) is { Length: 1 } ldac && ldac[0] <= 2) values["ldac"] = ldac[0];
        if (capabilities.NewPrompt)
        {
            var data = await Get(0x86);
            if (data is { Length: >= 2 and <= 16 })
            {
                state = state with { BeepGroup = data[0] };
                var options = FeatureProtocol.BeepOptions(data[0]);
                if (options.Count > 0 && data.Length == options.Count + 1)
                    foreach (var option in options)
                        if (data[option.Position] >= option.Minimum && data[option.Position] <= option.Maximum) values["beep" + option.Position] = data[option.Position];
            }
        }
        if (capabilities.Sensitivity)
        {
            var data = await Get(0xFD);
            if (data is { Length: 1 } && transport.Profile.Version == 1 && data[0] <= 31) values["sensitivity"] = data[0];
            else if (data is { Length: 2 } && data[0] is 1 or 2 && data[1] <= (data[0] == 2 ? 15 : 31))
            { state = state with { SensitivityIndex = data[0] }; values["sensitivity"] = data[1]; }
        }
        if (capabilities.EqOptions.Any(x => x.Label.Contains("自定义"))) state = state with { CustomEq = CustomEqualizer.Parse(await Get(0x43), transport.Profile) };
        if (!transport.Connected) throw new IOException("耳机已断开，请重新连接。");
        return state;
    }
    private async Task SaveBeforeActionAsync(CancellationToken token)
    {
        if (StateUncertain) throw new IOException("上次修改尚未确认，请重新连接后再试。");
        Current = await ReadAsync(token);
        Directory.CreateDirectory(backupDirectory);
        string path = Path.Combine(backupDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Export(), JsonOptions), token);
        LastBackup = path;
    }
    public async Task RenameAsync(string name, CancellationToken token = default)
    {
        byte[] bytes = FeatureProtocol.EncodeName(name, capabilities.NameLimit);
        await operations.WaitAsync(token); bool wrote = false;
        try
        {
            await SaveBeforeActionAsync(token);
            if (Current!.DeviceName == null) throw new IOException("尚未读取当前名称。");
            if (Current.DeviceName == name) return;
            wrote = true; await transport.RequestAsync(0xCA, bytes, token: token);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                await Task.Delay(200, token);
                var actual = FeatureProtocol.DecodeName(await transport.RequestAsync(0xC9, token: token));
                Current = Current with { DeviceName = actual };
                if (actual == name) return;
            }
            throw new IOException("名称未读回确认。Windows 显示名称可能需要重新配对才能更新。");
        }
        catch { if (wrote) StateUncertain = true; throw; }
        finally { operations.Release(); }
    }
    public async Task SetShutdownTimerAsync(int minutes, CancellationToken token = default)
    {
        if (!capabilities.Timer || minutes < 0 || minutes > 65535) throw new InvalidDataException("此耳机不支持该定时值。");
        await operations.WaitAsync(token); bool wrote = false;
        try
        {
            await SaveBeforeActionAsync(token);
            if (Current!.ShutdownMinutes == null) throw new IOException("尚未读取定时关机状态。");
            wrote = true;
            await transport.RequestAsync(minutes == 0 ? (byte)0xD2 : (byte)0xD1, minutes == 0 ? [] : [(byte)(minutes >> 8), (byte)minutes], token: token);
            await Task.Delay(200, token);
            int? actual = FeatureProtocol.ParseTimer(await transport.RequestAsync(0xD3, token: token));
            Current = Current with { ShutdownMinutes = actual };
            if (actual == minutes || minutes > 1 && actual == minutes - 1) return;
            throw new IOException("定时关机状态未确认，请重新读取。");
        }
        catch { if (wrote) StateUncertain = true; throw; }
        finally { operations.Release(); }
    }
    public async Task<bool> SetLdacAsync(int value, CancellationToken token = default)
    {
        if (!capabilities.Ldac || value is < 0 or > 2) throw new InvalidDataException("不支持此 LDAC 设置。");
        await operations.WaitAsync(token); bool wrote = false;
        try
        {
            await SaveBeforeActionAsync(token);
            if (!Current!.Settings.TryGetValue("ldac", out int old)) throw new IOException("尚未读取 LDAC 状态。");
            if (old == value) return true;
            wrote = true; await transport.RequestAsync(0x49, [(byte)value], token: token);
            var data = await transport.RequestAsync(0x48, token: token);
            if (data is { Length: 1 } && data[0] == value)
            { Current.Settings["ldac"] = value; return true; }
            StateUncertain = true; ReconnectRequired = true; return false;
        }
        catch (Exception ex) when (wrote && ex is IOException or TimeoutException)
        { StateUncertain = true; ReconnectRequired = true; return false; }
        catch { if (wrote) StateUncertain = true; throw; }
        finally { operations.Release(); }
    }
    public async Task<bool> DeviceActionAsync(string action, CancellationToken token = default)
    {
        if (!capabilities.CanDeviceAction(action)) throw new InvalidDataException("耳机没有上报此设备操作。");
        byte command = action switch { "disconnect" => 0xCD, "pair" => 0xCF, "poweroff" => 0xCE, "reset" => 0x07, "clear-pairing" => 0xC7, _ => throw new InvalidDataException() };
        await operations.WaitAsync(token); bool wrote = false;
        try
        {
            await SaveBeforeActionAsync(token); wrote = true;
            await transport.RequestAsync(command, token: token); return true;
        }
        catch (Exception ex) when (wrote && ex is IOException or TimeoutException) { return false; }
        finally { if (wrote) { StateUncertain = true; ReconnectRequired = true; } operations.Release(); }
    }
    public async Task ApplyCustomEqAsync(CustomEqualizer target, CancellationToken token = default)
    {
        await operations.WaitAsync(token); bool wrote = false;
        try
        {
            await SaveBeforeActionAsync(token);
            var before = Current!.CustomEq ?? throw new IOException("尚未读取可编辑的自定义 EQ。");
            target.ValidateAgainst(before);
            foreach (var band in target.Bands)
            {
                if (band == before.Bands[band.Index]) continue;
                wrote = true; await transport.RequestAsync(0x44, target.EncodeBand(band), token: token);
                bool matched = false;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    await Task.Delay(150, token);
                    var actual = CustomEqualizer.Parse(await transport.RequestAsync(0x43, token: token), transport.Profile);
                    Current = Current with { CustomEq = actual };
                    if (actual != null && actual.Group == target.Group && actual.Bank == target.Bank && actual.Bands.Length == target.Bands.Length && actual.Bands[band.Index] == band) { matched = true; break; }
                }
                if (!matched) throw new IOException($"第 {band.Index + 1} 个 EQ 频段未读回确认，已停止后续写入。");
            }
            if (!target.SameBands(Current.CustomEq)) throw new IOException("EQ 完整配置读回不一致，请重新连接。");
        }
        catch { if (wrote) StateUncertain = true; throw; }
        finally { operations.Release(); }
    }
    public async Task ResetCustomEqAsync(CancellationToken token = default)
    {
        await operations.WaitAsync(token); bool wrote = false;
        try
        {
            await SaveBeforeActionAsync(token);
            var before = Current!.CustomEq ?? throw new IOException("尚未读取可编辑的自定义 EQ。");
            wrote = true; await transport.RequestAsync(0x45, token: token);
            await Task.Delay(250, token);
            var actual = CustomEqualizer.Parse(await transport.RequestAsync(0x43, token: token), transport.Profile);
            Current = Current with { CustomEq = actual };
            if (actual == null || actual.Group != before.Group || actual.Bands.Any(x => actual.GainDb(x.Gain) != 0)) throw new IOException("未确认 EQ 已恢复默认值，请重新读取。");
        }
        catch { if (wrote) StateUncertain = true; throw; }
        finally { operations.Release(); }
    }
}
