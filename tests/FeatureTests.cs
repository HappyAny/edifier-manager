using EdifierManager;
using System.Text.Json;

static class FeatureTests
{
    public static void Register(List<(string name, Func<Task> run)> tests, string scratch)
    {
        void Check(bool ok, string message = "Assertion failed") { if (!ok) throw new Exception(message); }
        void Test(string name, Func<Task> test) => tests.Add((name, test));
        HeadsetController Controller(PreviewTransport link, ICommandTransport? substitute = null, string? directory = null) => new(substitute ?? link, link.Model, link.Capabilities, "features-device", directory ?? scratch, true);
        async Task Reject(Func<Task> run)
        {
            try { await run(); } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or OperationCanceledException) { return; }
            throw new Exception("Expected rejection");
        }
        Test("Observed Lolli capabilities expose EQ50 and all six touch slots without inventing sensitivity", async () =>
        {
            var link = new PreviewTransport(HeadsetModel.Lolli, true); var c = Controller(link); var s = await c.RefreshAsync();
            Check(c.Capabilities.EqOptions.Select(x => x.Label).SequenceEqual(new[] { "经典", "重低音", "清澈人声", "高音增强", "剧院" }));
            Check(c.Capabilities.TouchSlots.SequenceEqual(new[] { 1, 2, 3, 4, 8, 9 }));
            Check(s.Settings["cycle8"] == 7 && s.Settings["cycle9"] == 7 && s.Settings["beep2"] == 7);
            Check(!c.Capabilities.Sensitivity && !s.Settings.ContainsKey("sensitivity"));
            Check(s.ShutdownMinutes == 0 && s.DeviceName == "EDIFIER Lolli ANC");
            await c.ApplyAsync(new Dictionary<string, int> { ["eq"] = 4 }); Check(link.WriteRequests.Single().Payload.SequenceEqual(new byte[] { 4 }));
        });
        Test("Nested touch cycles use the selected action and preserve both the parent and mask on restore", async () =>
        {
            var link = new PreviewTransport(HeadsetModel.Lolli, true); var c = Controller(link); await c.RefreshAsync();
            await c.ApplyAsync(new Dictionary<string, int> { ["touch1"] = 5, ["cycle1"] = 3, ["touch8"] = 5, ["cycle8"] = 5 });
            Check(link.WriteRequests.Any(x => x.Command == 0xF1 && x.Payload.SequenceEqual(new byte[] { 1, 5, 3 })));
            Check(link.WriteRequests.Any(x => x.Command == 0xF1 && x.Payload.SequenceEqual(new byte[] { 8, 5, 5 })));
            var backup = JsonSerializer.Deserialize<SettingsBackup>(File.ReadAllText(c.LastBackup!))!;
            c.ValidateBackup(backup); await c.ApplyAsync(backup.Snapshot.Settings, expectedBackup: backup);
            Check(c.Current!.Settings["touch1"] == 1 && c.Current.Settings["cycle8"] == 7);
        });
        Test("Invalid or incomplete touch cycles reject the whole batch before any write", async () =>
        {
            foreach (int mask in new[] { 0, 1, 2, 4, 8, 15, -1 })
            {
                var link = new PreviewTransport(HeadsetModel.Lolli, true); var c = Controller(link); await c.RefreshAsync();
                await Reject(() => c.ApplyAsync(new Dictionary<string, int> { ["eq"] = 1, ["touch1"] = 5, ["cycle1"] = mask })); Check(link.Writes.Count == 0);
            }
            var inner = new PreviewTransport(HeadsetModel.Lolli, true); var controller = Controller(inner); await controller.RefreshAsync();
            await Reject(() => controller.ApplyAsync(new Dictionary<string, int> { ["touch1"] = 5 }));
            await Reject(() => controller.ApplyAsync(new Dictionary<string, int> { ["touch1"] = 2, ["cycle1"] = 3 })); Check(inner.Writes.Count == 0);
        });
        Test("A missing nested touch mask cannot be overwritten as a simple action", async () =>
        {
            var inner = new PreviewTransport(HeadsetModel.Lolli, true);
            var link = new FeatureFaultTransport(inner, (cmd, body) => cmd == 0xF0 ? (true, new byte[] { body![0], 5 }) : (false, null));
            var c = Controller(inner, link); await c.RefreshAsync();
            Check(!c.Current!.Settings.Keys.Any(x => x.StartsWith("touch")));
            await Reject(() => c.ApplyAsync(new Dictionary<string, int> { ["touch1"] = 2 })); Check(inner.Writes.Count == 0);
        });
        Test("Touch and sensitivity writes require both earbuds online", async () =>
        {
            var inner = new PreviewTransport(HeadsetModel.Lolli, true); inner.Capabilities.Raw[17] |= 8;
            var link = new FeatureFaultTransport(inner, (cmd, _) => cmd == 0xF2 ? (true, new byte[] { 1, 90, 90, 0, 0, 0 }) : (false, null));
            var c = Controller(inner, link); await c.RefreshAsync();
            await Reject(() => c.ApplyAsync(new Dictionary<string, int> { ["touch1"] = 3 }));
            await Reject(() => c.ApplyAsync(new Dictionary<string, int> { ["sensitivity"] = 9 })); Check(inner.Writes.Count == 0);
        });
        Test("New prompt protocol carries its reported group and setting position", async () =>
        {
            var link = new PreviewTransport(HeadsetModel.Lolli, true); var c = Controller(link); await c.RefreshAsync();
            await c.ApplyAsync(new Dictionary<string, int> { ["beep1"] = 0, ["beep2"] = 12 });
            Check(link.WriteRequests[0].Command == 0x87 && link.WriteRequests[0].Payload.SequenceEqual(new byte[] { 4, 1, 0 }));
            Check(link.WriteRequests[1].Payload.SequenceEqual(new byte[] { 4, 2, 12 }));
            Check(c.Current!.Settings["beep1"] == 0 && c.Current.Settings["beep2"] == 12);
            var backup = c.Export() with { Snapshot = c.Current with { BeepGroup = 7 } };
            await Reject(() => { c.ValidateBackup(backup); return Task.CompletedTask; });
        });
        Test("Malformed and unknown prompt responses provide no editable prompt values", async () =>
        {
            foreach (byte[] response in new byte[][] { [4], [4, 1], [99, 1, 1], [4, 8, 255] })
            {
                var inner = new PreviewTransport(HeadsetModel.Lolli, true); var link = new FeatureFaultTransport(inner, (cmd, _) => cmd == 0x86 ? (true, response) : (false, null));
                var c = Controller(inner, link); await c.RefreshAsync(); Check(!c.Current!.Settings.Keys.Any(x => x.StartsWith("beep")));
                await Reject(() => c.ApplyAsync(new Dictionary<string, int> { ["beep1"] = 1 })); Check(inner.Writes.Count == 0);
            }
        });
        Test("Sensitivity validates the reported range and preserves the index", async () =>
        {
            var link = new PreviewTransport(HeadsetModel.Lolli, true); link.Capabilities.Raw[17] |= 8; var c = Controller(link); await c.RefreshAsync();
            await Reject(() => c.ApplyAsync(new Dictionary<string, int> { ["sensitivity"] = 16 })); Check(link.Writes.Count == 0);
            await c.ApplyAsync(new Dictionary<string, int> { ["sensitivity"] = 15 });
            Check(link.WriteRequests.Single().Payload.SequenceEqual(new byte[] { 2, 15 }));
        });
        Test("Name changes validate UTF8 bytes and read the new name back", async () =>
        {
            var link = new PreviewTransport(HeadsetModel.Lolli, true); var c = Controller(link); await c.RefreshAsync();
            await Reject(() => c.RenameAsync(new string('喵', 12))); await Reject(() => c.RenameAsync("a\0b")); Check(link.Writes.Count == 0);
            await c.RenameAsync("喵喵的耳机"); Check(c.Current!.DeviceName == "喵喵的耳机");
            Check(File.Exists(c.LastBackup) && JsonSerializer.Deserialize<SettingsBackup>(File.ReadAllText(c.LastBackup!))!.Snapshot.DeviceName == "EDIFIER Lolli ANC");
            Check(FeatureProtocol.DecodeName([0xFF]) == null && FeatureProtocol.DecodeName([65, 0, 0]) == "A");
        });
        Test("A name acknowledgement without the target readback remains uncertain", async () =>
        {
            var link = new PreviewTransport(HeadsetModel.Lolli, true) { IgnoreWrites = true }; var c = Controller(link); await c.RefreshAsync();
            await Reject(() => c.RenameAsync("Changed name")); Check(c.StateUncertain && link.Writes.Count == 1);
        });
        Test("Renamed device identities persist by address digest and invalid records are ignored", () =>
        {
            string path = Path.Combine(scratch, "known-headsets.json"); KnownHeadsets.Remember(0x123456789ABC, HeadsetModel.Lolli, path);
            string body = File.ReadAllText(path); Check(!body.Contains("123456789ABC"));
            Check(KnownHeadsets.Load(path)[HeadsetController.MakeDeviceKey(0x123456789ABC)] == "4201");
            File.WriteAllText(path, "{\"invalid\":\"4201\",\"01234567890123456789\":\"unknown\"}"); Check(KnownHeadsets.Load(path).Count == 0);
            return Task.CompletedTask;
        });
        Test("Shutdown timer handles high-byte durations and cancellation without putting it into batch settings", async () =>
        {
            var link = new PreviewTransport(HeadsetModel.Lolli, true); var c = Controller(link); await c.RefreshAsync();
            await c.SetShutdownTimerAsync(300); Check(c.Current!.ShutdownMinutes == 300 && link.WriteRequests[0].Payload.SequenceEqual(new byte[] { 1, 44 }));
            Check(!c.Export().Snapshot.Settings.ContainsKey("timer"));
            await c.SetShutdownTimerAsync(0); Check(c.Current!.ShutdownMinutes == 0 && link.WriteRequests[^1].Command == 0xD2 && link.WriteRequests[^1].Payload.Length == 0);
            int writes = link.Writes.Count; await Reject(() => c.SetShutdownTimerAsync(65536)); Check(link.Writes.Count == writes);
        });
        Test("A decrementing timer is accepted but a disabled timer is not accepted as an active timer", async () =>
        {
            var inner = new PreviewTransport(HeadsetModel.Lolli, true);
            var link = new FeatureFaultTransport(inner, (cmd, _) => cmd == 0xD3 && inner.Writes.Count > 0 ? (true, new byte[] { 0, 29 }) : (false, null));
            var c = Controller(inner, link); await c.RefreshAsync(); await c.SetShutdownTimerAsync(30); Check(c.Current!.ShutdownMinutes == 29);
            var other = new PreviewTransport(HeadsetModel.Lolli, true) { IgnoreWrites = true }; var c2 = Controller(other); await c2.RefreshAsync();
            await Reject(() => c2.SetShutdownTimerAsync(30)); Check(c2.StateUncertain);
        });
        Test("Device actions are capability gated, backed up, sent once, and require reconnection", async () =>
        {
            var link = new PreviewTransport(HeadsetModel.Lolli, true); var c = Controller(link); await c.RefreshAsync();
            await Reject(() => c.DeviceActionAsync("reset")); Check(link.Writes.Count == 0);
            Check(await c.DeviceActionAsync("poweroff")); Check(File.Exists(c.LastBackup) && link.WriteRequests.Single().Command == 0xCE);
            Check(c.StateUncertain && c.ReconnectRequired); await Reject(() => c.DeviceActionAsync("poweroff")); Check(link.Writes.Count == 1);
        });
        Test("A device action with a lost acknowledgement is not reported as completed", async () =>
        {
            var inner = new PreviewTransport(HeadsetModel.Lolli, true);
            var link = new FeatureFaultTransport(inner, (cmd, _) => cmd == 0xCF ? throw new IOException("link lost") : (false, null));
            var c = Controller(inner, link); await c.RefreshAsync(); Check(!await c.DeviceActionAsync("pair")); Check(c.StateUncertain && c.ReconnectRequired);
        });
        Test("LDAC is a separate operation and does not silently restore from a general settings backup", async () =>
        {
            var link = new PreviewTransport(HeadsetModel.W830); var c = Controller(link); await c.RefreshAsync();
            await Reject(() => c.ApplyAsync(new Dictionary<string, int> { ["ldac"] = 2 })); Check(link.Writes.Count == 0);
            Check(await c.SetLdacAsync(2)); Check(link.WriteRequests.Single().Command == 0x49 && c.Current!.Settings["ldac"] == 2);
            var other = new PreviewTransport(HeadsetModel.W830) { IgnoreWrites = true }; var c2 = Controller(other); await c2.RefreshAsync();
            Check(!await c2.SetLdacAsync(2) && c2.StateUncertain && c2.ReconnectRequired && other.Writes.Count == 1);
        });
        Test("Custom EQ parses independent fixed packets and rejects malformed bands", () =>
        {
            byte[] packet = Convert.FromHexString("0104000000640607010007D0060702000FA0060703001F400607");
            var eq = CustomEqualizer.Parse(packet, new(2)); Check(eq != null && eq.Bands.Length == 4 && eq.Bands[2].Frequency == 4000 && eq.GainDb(eq.Bands[0].Gain) == 0);
            Check(CustomEqualizer.Parse(packet[2..], new(1))!.SameBands(eq));
            foreach (var bad in new[] { packet[..^1], new byte[] { 99, 4, 0, 0, 0, 0 }, packet.Select((b, i) => i == 8 ? (byte)0 : b).ToArray(), packet.Select((b, i) => i == 6 ? (byte)255 : b).ToArray() }) Check(CustomEqualizer.Parse(bad, new(2)) == null);
            return Task.CompletedTask;
        });
        Test("Custom EQ edits validate all bands before writing and confirm each changed band", async () =>
        {
            var link = new PreviewTransport(HeadsetModel.W830); var c = Controller(link); await c.RefreshAsync(); var eq = c.Current!.CustomEq!;
            var target = eq with { Bands = eq.Bands.Select(x => x with { Gain = 8, Q = 10 }).ToArray() };
            var invalid = target with { Bands = target.Bands.Select(x => x.Index == 3 ? x with { Frequency = 100 } : x).ToArray() };
            await Reject(() => c.ApplyCustomEqAsync(invalid)); Check(link.Writes.Count == 0);
            await c.ApplyCustomEqAsync(target); Check(target.SameBands(c.Current!.CustomEq) && link.WriteRequests.Count(x => x.Command == 0x44) == 4);
            Check(link.WriteRequests[0].Payload.SequenceEqual(new byte[] { 0, 0, 0, 100, 8, 10 }));
            var backup = JsonSerializer.Deserialize<SettingsBackup>(File.ReadAllText(c.LastBackup!))!; Check(backup.Snapshot.CustomEq!.SameBands(eq));
            await c.ApplyCustomEqAsync(backup.Snapshot.CustomEq); Check(c.Current!.CustomEq!.SameBands(eq));
        });
        Test("Custom EQ mismatch stops after the first band without retries or false success", async () =>
        {
            var link = new PreviewTransport(HeadsetModel.W830) { IgnoreWrites = true }; var c = Controller(link); await c.RefreshAsync();
            var target = c.Current!.CustomEq! with { Bands = c.Current.CustomEq!.Bands.Select(x => x with { Gain = 9 }).ToArray() };
            await Reject(() => c.ApplyCustomEqAsync(target)); Check(c.StateUncertain && link.WriteRequests.Count == 1 && link.WriteRequests[0].Command == 0x44);
        });
        Test("Four-byte and banked custom EQ packets preserve frequencies and bank identity", () =>
        {
            byte[] bands = Convert.FromHexString("00003C0601009606020190060303E80604096006053A9806");
            var six = CustomEqualizer.Parse(new byte[] { 2, 6 }.Concat(bands).ToArray(), new(2));
            Check(six != null && six.Bands.Length == 6 && six.Bands[2].Frequency == 400 && !six.AdjustableFrequency && six.GainDb(six.Bands[2].Gain) == 0);
            var banked = CustomEqualizer.Parse(new byte[] { 7, 6, 3 }.Concat(bands).ToArray(), new(2));
            Check(banked != null && banked.Bank == 3 && banked.EncodeBand(banked.Bands[2]).SequenceEqual(new byte[] { 3, 2, 1, 144, 6 }));
            return Task.CompletedTask;
        });
        Test("Variable-count EQ honors the returned count and rejects duplicate or excessive band indexes", () =>
        {
            byte[] packet = Convert.FromHexString("110300003C060101900602271006");
            var eq = CustomEqualizer.Parse(packet, new(2)); Check(eq != null && eq.Bands.Length == 3 && eq.GainDb(eq.Bands[2].Gain) == 0);
            packet[1] = 17; Check(CustomEqualizer.Parse(packet, new(2)) == null);
            packet[1] = 3; packet[10] = 1; Check(CustomEqualizer.Parse(packet, new(2)) == null);
            return Task.CompletedTask;
        });
        Test("Custom EQ reset is read back and gain units follow the device group", async () =>
        {
            var link = new PreviewTransport(HeadsetModel.W830); var c = Controller(link); await c.RefreshAsync();
            await c.ApplyCustomEqAsync(c.Current!.CustomEq! with { Bands = c.Current.CustomEq!.Bands.Select(x => x with { Gain = 9 }).ToArray() });
            await c.ResetCustomEqAsync(); Check(c.Current!.CustomEq!.Bands.All(x => x.Gain == 6));
            Check(new CustomEqualizer(5, 0, []).EncodeGain(0) == 30 && new CustomEqualizer(4, 0, []).EncodeGain(0) == 9 && new CustomEqualizer(18, 0, []).EncodeGain(0) == 4);
        });
        Test("Every new operation fails closed when the backup cannot be written", async () =>
        {
            string file = Path.Combine(scratch, "not-a-backup-directory"); File.WriteAllText(file, "block");
            var link = new PreviewTransport(HeadsetModel.W830); var c = Controller(link, directory: Path.Combine(file, "sub")); await c.RefreshAsync();
            await Reject(() => c.RenameAsync("New name")); await Reject(() => c.SetShutdownTimerAsync(30)); await Reject(() => c.SetLdacAsync(2));
            await Reject(() => c.ApplyCustomEqAsync(c.Current!.CustomEq!)); await Reject(() => c.DeviceActionAsync("poweroff")); Check(link.Writes.Count == 0);
        });
        Test("Finder audio has bounded duration and amplitude with the unselected channel silent", () =>
        {
            byte[] left = HeadsetFinder.MakeTone(1), right = HeadsetFinder.MakeTone(2); Check(left.Length == 8 * 48000 * 4);
            int peak = 0, audible = 0;
            for (int offset = 0; offset < left.Length; offset += 4)
            {
                short l = BitConverter.ToInt16(left, offset), r = BitConverter.ToInt16(right, offset + 2);
                Check(left[offset + 2] == 0 && left[offset + 3] == 0 && right[offset] == 0 && right[offset + 1] == 0 && l == r);
                peak = Math.Max(peak, Math.Abs((int)l)); if (l != 0) audible++;
            }
            Check(peak <= 6553 && peak > 6000 && audible > 48000 && audible < 48000 * 3); return Task.CompletedTask;
        });
    }
}

sealed class FeatureFaultTransport(PreviewTransport inner, Func<byte, byte[]?, (bool Override, byte[]? Data)> callback) : ICommandTransport
{
    public bool Connected => inner.Connected;
    public WireProfile Profile => inner.Profile;
    public string TransportName => "feature fault fixture";
    public Task<byte[]?> RequestAsync(byte command, byte[]? payload = null, bool optional = false, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); var result = callback(command, payload);
        return result.Override ? Task.FromResult(result.Data) : inner.RequestAsync(command, payload, optional, token);
    }
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
