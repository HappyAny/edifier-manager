using EdifierManager;
using System.Text.Json;
using System.Text.RegularExpressions;

var tests = new List<(string name, Func<Task> run)>();
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
string scratch = Path.Combine(root, "artifacts", "tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Test(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> run) => tests.Add((name, run));
async Task Throws(Func<Task> run) { try { await run(); } catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or InvalidOperationException or NotSupportedException) { return; } throw new Exception("Expected rejection"); }
HeadsetController Controller(PreviewTransport transport, string? directory = null, string key = "device-test") => new(transport, transport.Model, transport.Capabilities, key, directory ?? scratch, true);

Test("Captured W820NB packets decode and re-encode exactly", () =>
{
    int count = 0;
    foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(root, "tests/w820nb-captures.txt")), @"(?:sendData:|readData:)([0-9A-Fa-f]+)(?=[\r\n（]|$)"))
    {
        byte[] raw = Convert.FromHexString(m.Groups[1].Value);
        var frame = EdifierProtocol.Decode(raw);
        Check(raw.SequenceEqual(EdifierProtocol.Encode(frame.Command, frame.Data, frame.Header))); count++;
    }
    Check(count >= 60); Console.WriteLine($"  {count} captured packets");
});
Test("V2 plain and XOR packets match independent fixed vectors", () =>
{
    Check(Convert.ToHexString(EdifierProtocol.Encode(8, [], profile: new(2))) == "AAEC0800009E");
    Check(Convert.ToHexString(EdifierProtocol.Encode(9, [1], profile: new(2))) == "AAEC09000101A1");
    Check(Convert.ToHexString(EdifierProtocol.Encode(9, [1], profile: new(2, 16))) == "AAEC090001A444");
    Check(EdifierProtocol.Decode(Convert.FromHexString("BBEC090001A455"), new(2, 16)).Data.SequenceEqual(new byte[] { 1 }));
});
Test("V1 and V2 reject corruption, wrong lengths and unsupported encryption", () =>
{
    foreach (var profile in new[] { new WireProfile(), new(2), new(2, 16) })
    {
        var packet = EdifierProtocol.Encode(0xC1, [3, 7], 0xBB, profile);
        for (int i = 0; i < packet.Length; i++)
        {
            byte[] changed = packet.ToArray(); changed[i] ^= 1;
            bool rejected = false; try { EdifierProtocol.Decode(changed, profile); } catch (FormatException) { rejected = true; }
            Check(rejected, $"Corrupt byte accepted: {profile} {i}");
        }
    }
    bool unsupported = false; try { EdifierProtocol.Encode(8, [], profile: new(2, 49)); } catch (NotSupportedException) { unsupported = true; } Check(unsupported);
});
Test("Stream decoding accepts fragmented and combined notifications", () =>
{
    foreach (var profile in new[] { new WireProfile(), new(2), new(2, 16) })
    {
        var first = EdifierProtocol.Encode(0xD0, [82], 0xBB, profile);
        var second = EdifierProtocol.Encode(0x08, [1], 0xCC, profile);
        for (int split = 1; split < first.Length; split++)
        {
            var decoder = new FrameDecoder { Profile = profile };
            Check(decoder.Push(first[..split]).Count == 0);
            var frames = decoder.Push(first[split..].Concat(second).ToArray());
            Check(frames.Count == 2 && frames[0].Data[0] == 82 && frames[1].Command == 8);
        }
    }
});
Test("Decoder recovers after invalid checksum and bounds input", () =>
{
    var decoder = new FrameDecoder(); var packet = EdifierProtocol.Encode(0xD0, [82], 0xBB); var bad = packet.ToArray(); bad[^1] ^= 1;
    Check(decoder.Push(new byte[] { 0, 0xFF }.Concat(bad).Concat(packet).ToArray()).Count == 1);
    Check(decoder.Push(new byte[5000]).Count == 0);
    Check(decoder.Push(packet).Count == 1);
});
Test("V2 length uses both bytes", () =>
{
    var data = Enumerable.Range(0, 300).Select(x => (byte)x).ToArray(); var packet = EdifierProtocol.Encode(0xD8, data, 0xBB, new(2));
    Check(packet[3] == 1 && packet[4] == 44); Check(EdifierProtocol.Decode(packet, new(2)).Data.SequenceEqual(data));
});
Test("Exact model identity rejects related and unknown products", () =>
{
    Check(HeadsetModel.Identify("EDIFIER Lolli ANC") == HeadsetModel.Lolli);
    Check(HeadsetModel.Identify("EDIFIER W820NB 双金标版") == HeadsetModel.W820Gold);
    foreach (string name in new[] { "EDIFIER W820NB Plus", "W820NB 三金标版", "Lolli ANC 2", "W820NB空间音频版" })
    { bool rejected = false; try { HeadsetModel.Identify(name); } catch (NotSupportedException) { rejected = true; } Check(rejected); }
});
Test("Battery parser preserves unknown and single-ear states", () =>
{
    Check(HeadsetController.ParseBattery([0], false, false, false).left == 0);
    Check(HeadsetController.ParseBattery([255], false, false, false).left == null);
    Check(HeadsetController.ParseBattery([1, 80, 99, 255, 0, 0], true, true, true) == (80, null, null));
    Check(HeadsetController.ParseBattery([3, 80, 75, 60, 0, 0], true, true, false) == (80, 75, null));
    Check(HeadsetController.ParseBattery([4, 0, 0, 61], true, false, false).left == 61);
    Check(HeadsetController.ParseBattery(new byte[6], true, true, true) == (null, null, null));
});
Test("Unknown capability groups expose no writable options", () =>
{
    byte[] raw = new byte[30]; raw[13] = raw[16] = 255; var caps = new Capabilities(raw); caps.SetReportedAncGroup(99);
    Check(caps.AncOptions.Count == 0 && caps.EqOptions.Count == 0 && caps.TouchSlots.Length == 0);
    Check(!Capabilities.IsValid(new byte[30]));
});
AsyncTest("W820NB settings are backed up before writes and verified", async () =>
{
    var link = new PreviewTransport(false); var c = Controller(link); var before = await c.RefreshAsync();
    await c.ApplyAsync(new Dictionary<string, int> { ["anc"] = 3, ["ambient"] = 9, ["eq"] = 2, ["game"] = 1, ["prompt"] = 11, ["cycle"] = 3 });
    Check(c.Current!.Settings["ambient"] == 9 && c.Current.Settings["cycle"] == 3);
    var backup = JsonSerializer.Deserialize<SettingsBackup>(File.ReadAllText(c.LastBackup!))!;
    Check(backup.Snapshot.Settings["ambient"] == before.Settings["ambient"] && backup.Snapshot.Settings["eq"] == 0);
    c.ValidateBackup(backup); await c.ApplyAsync(backup.Snapshot.Settings);
    Check(before.Settings.All(x => c.Current!.Settings[x.Key] == x.Value));
});
AsyncTest("Lolli v2 ANC and touch settings use the TWS profile", async () =>
{
    var link = new PreviewTransport(true); var c = Controller(link); var before = await c.RefreshAsync();
    Check(before.LeftBattery == 86 && before.RightBattery == 79 && !before.Settings.ContainsKey("cycle"));
    await c.ApplyAsync(new Dictionary<string, int> { ["anc"] = 3, ["ambient"] = 4, ["touch1"] = 4, ["touch4"] = 5, ["wear"] = 2 });
    Check(c.Current!.Settings["touch1"] == 4 && c.Current.Settings["ambient"] == 4 && c.Current.Settings["wear"] == 2);
});
AsyncTest("V2 ANC group comes from the status response", async () =>
{
    var inner = new PreviewTransport(true); inner.Capabilities.Raw[0] = 128;
    var c = Controller(inner); await c.RefreshAsync();
    Check(c.Capabilities.AncGroup == 12 && c.Current!.Settings.ContainsKey("anc"));
    await c.ApplyAsync(new Dictionary<string, int> { ["anc"] = 3 }); Check(c.Current!.Settings["anc"] == 3);
});
AsyncTest("Unknown wear detection group cannot be written", async () =>
{
    var inner = new PreviewTransport(true); var link = new FaultTransport(inner, 0xFB, [99, 1]);
    var c = new HeadsetController(link, HeadsetModel.Lolli, inner.Capabilities, "test", scratch, true);
    await c.RefreshAsync(); Check(!c.Current!.Settings.ContainsKey("wear"));
    await Throws(() => c.ApplyAsync(new Dictionary<string, int> { ["wear"] = 1 })); Check(inner.Writes.Count == 0);
});
AsyncTest("Every value is validated before the first write", async () =>
{
    var link = new PreviewTransport(false); var c = Controller(link); await c.RefreshAsync();
    await Throws(() => c.ApplyAsync(new Dictionary<string, int> { ["eq"] = 1, ["prompt"] = 16 }));
    await Throws(() => c.ApplyAsync(new Dictionary<string, int> { ["cycle"] = 1 }));
    await Throws(() => c.ApplyAsync(new Dictionary<string, int> { ["ambient"] = 5 }));
    await Throws(() => c.ApplyAsync(new Dictionary<string, int> { ["touch1"] = 1 }));
    Check(link.Writes.Count == 0);
});
AsyncTest("Backup failure prevents all device writes", async () =>
{
    string path = Path.Combine(scratch, "not-a-folder"); File.WriteAllText(path, "occupied");
    var link = new PreviewTransport(false); var c = Controller(link, path); await c.RefreshAsync();
    await Throws(() => c.ApplyAsync(new Dictionary<string, int> { ["eq"] = 1 })); Check(link.Writes.Count == 0 && !c.StateUncertain);
});
AsyncTest("Readback mismatch stops a batch and requires reconnect", async () =>
{
    var link = new PreviewTransport(false) { IgnoreWrites = true }; var c = Controller(link); await c.RefreshAsync();
    await Throws(() => c.ApplyAsync(new Dictionary<string, int> { ["eq"] = 1, ["prompt"] = 10 }));
    Check(c.StateUncertain && link.Writes.SequenceEqual(new byte[] { 0xC4 }));
    await Throws(() => c.ApplyAsync(new Dictionary<string, int> { ["prompt"] = 5 })); Check(link.Writes.Count == 1);
});
AsyncTest("Restore rejects device, firmware, capability, protocol and preview mismatches", async () =>
{
    var c = Controller(new PreviewTransport(true)); await c.RefreshAsync(); var original = c.Export();
    foreach (var backup in new[] { original with { DeviceKey = "other" }, original with { Preview = false }, original with { Schema = 9 }, original with { Protocol = new(1) }, original with { Snapshot = original.Snapshot with { Firmware = "other" } }, original with { Snapshot = original.Snapshot with { Capabilities = "00" } }, original with { Snapshot = original.Snapshot with { Model = "other" } } })
        await Throws(() => { c.ValidateBackup(backup); return Task.CompletedTask; });
    c.ValidateBackup(original);
});
AsyncTest("Missing readback disables a setting before mutation", async () =>
{
    var inner = new PreviewTransport(false); var link = new FaultTransport(inner, 0xD5, [255]);
    var c = new HeadsetController(link, HeadsetModel.W820Gold, inner.Capabilities, "test", scratch, true);
    await c.RefreshAsync(); Check(!c.Current!.Settings.ContainsKey("eq"));
    await Throws(() => c.ApplyAsync(new Dictionary<string, int> { ["eq"] = 1 })); Check(inner.Writes.Count == 0);
});
AsyncTest("Cancelled operations do not write", async () =>
{
    var link = new PreviewTransport(false); var c = Controller(link); await c.RefreshAsync();
    using var token = new CancellationTokenSource(); token.Cancel();
    bool cancelled = false; try { await c.ApplyAsync(new Dictionary<string, int> { ["eq"] = 1 }, token.Token); } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled && link.Writes.Count == 0);
});
Test("W830NB identifies both official service families without accepting related products", () =>
{
    Check(HeadsetModel.Identify("EDIFIER W830NB") == HeadsetModel.W830);
    foreach (var uuid in new[] { "00008500-0000-1000-8000-00805f9b34fb", "48098501-1a48-11e9-ab14-d663bd873d93", "00001001-0000-1000-8000-00805f9b34fb", "48091001-1a48-11e9-ab14-d663bd873d93" })
        Check(HeadsetModel.Identify("renamed", HeadsetModel.ProductCodeFromService(new(uuid))) == HeadsetModel.W830);
    Check(HeadsetModel.ProductCodeFromService(Guid.Empty) == null);
    bool rejected = false; try { HeadsetModel.Identify("W830NB Plus"); } catch (NotSupportedException) { rejected = true; } Check(rejected);
});
Test("Multipoint decodes legacy and indexed status and preserves the index", () =>
{
    foreach (byte value in new byte[] { 1, 2, 3, 4 })
    {
        var s = MultipointState.Parse([value])!; Check(s.Enabled == (value is 2 or 4));
        Check(s.Encode(true).SequenceEqual(new byte[] { 2 }) && s.Encode(false).SequenceEqual(new byte[] { 1 }));
    }
    foreach (byte index in new byte[] { 0, 1, 2, 3, 255 })
    {
        Check(MultipointState.Parse([index, 1])!.Enabled);
        Check(MultipointState.Parse([index, 0])!.Encode(true).SequenceEqual(new byte[] { index, 1 }));
    }
    foreach (byte[]? bad in new byte[]?[] { null, [], [0], [5], [7, 1], [2, 2], [2, 1, 0] }) Check(MultipointState.Parse(bad) == null);
    Check(!new Capabilities(new byte[20]).Multipoint);
});
AsyncTest("W830NB reads headset battery and independently reported ANC modes", async () =>
{
    var link = new PreviewTransport(HeadsetModel.W830); var c = Controller(link); await c.RefreshAsync();
    Check(c.Current!.Model == "w830nb" && c.Current.LeftBattery == 82 && c.Current.RightBattery == null);
    Check(c.Capabilities.AncGroup == 22 && !c.Current.Settings.ContainsKey("cycle") && !c.Current.Settings.ContainsKey("wear"));
    await c.ApplyAsync(new Dictionary<string,int> { ["anc"] = 4, ["ambient"] = 8, ["eq"] = 1, ["multipoint"] = 1 });
    Check(c.Current!.Settings["multipoint"] == 1 && !c.StateUncertain);
    Check(link.WriteRequests.Last().Command == 0x7D && link.WriteRequests.Last().Payload.SequenceEqual(new byte[] { 2 }));
});
AsyncTest("Lolli multipoint uses indexed payload and restores from its automatic backup", async () =>
{
    var link = new PreviewTransport(true); var c = Controller(link); await c.RefreshAsync();
    await c.ApplyAsync(new Dictionary<string,int> { ["multipoint"] = 1 });
    Check(link.WriteRequests.Single().Payload.SequenceEqual(new byte[] { 2, 1 }));
    Check(c.Current!.Settings["multipoint"] == 1 && !c.ReconnectRequired);
    var backup = JsonSerializer.Deserialize<SettingsBackup>(File.ReadAllText(c.LastBackup!))!;
    Check(backup.Snapshot.Settings["multipoint"] == 0); c.ValidateBackup(backup);
    await c.ApplyAsync(backup.Snapshot.Settings, expectedBackup: backup);
    Check(c.Current!.Settings["multipoint"] == 0 && link.WriteRequests.Last().Payload.SequenceEqual(new byte[] { 2, 0 }));
});
AsyncTest("Single-ear and unsupported multipoint operations cannot write", async () =>
{
    var inner = new PreviewTransport(true); var link = new FaultTransport(inner, 0xF2, [1, 80, 255, 255, 0, 0]);
    var c = new HeadsetController(link, HeadsetModel.Lolli, inner.Capabilities, "test", scratch, true); await c.RefreshAsync();
    await Throws(() => c.ApplyAsync(new Dictionary<string,int> { ["eq"] = 1, ["multipoint"] = 1 })); Check(inner.Writes.Count == 0);
    var unsupported = new PreviewTransport(false); var other = Controller(unsupported); await other.RefreshAsync();
    await Throws(() => other.ApplyAsync(new Dictionary<string,int> { ["multipoint"] = 1 })); Check(unsupported.Writes.Count == 0);
});
AsyncTest("Unrecognized multipoint responses do not expose a writable setting", async () =>
{
    foreach (byte[]? reply in new byte[]?[] { null, [0], [7, 1], [1, 8], [1, 0, 0] })
    {
        var inner = new PreviewTransport(true); var link = new FaultTransport(inner, 0x7C, reply);
        var c = new HeadsetController(link, HeadsetModel.Lolli, inner.Capabilities, "test", scratch, true); await c.RefreshAsync();
        Check(!c.Current!.Settings.ContainsKey("multipoint")); await Throws(() => c.ApplyAsync(new Dictionary<string,int> { ["multipoint"] = 1 })); Check(inner.Writes.Count == 0);
    }
});
AsyncTest("Multipoint restart happens last and remains unconfirmed until a fresh connection", async () =>
{
    var inner = new PreviewTransport(true); var link = new RestartTransport(inner, true);
    var c = new HeadsetController(link, HeadsetModel.Lolli, inner.Capabilities, "test", scratch, true); await c.RefreshAsync();
    await c.ApplyAsync(new Dictionary<string,int> { ["multipoint"] = 1, ["eq"] = 1, ["prompt"] = 9, ["anc"] = 3 });
    Check(inner.Writes.Last() == 0x7D && c.ReconnectRequired && c.StateUncertain && c.PendingMultipointValue == 1);
    Check(c.Current!.Settings["multipoint"] == 0); Check(File.Exists(c.LastBackup));
    int count = inner.Writes.Count; await Throws(() => c.ApplyAsync(new Dictionary<string,int> { ["multipoint"] = 1 })); Check(count == inner.Writes.Count);
    var reconnected = Controller(inner); await reconnected.RefreshAsync(); Check(reconnected.Current!.Settings["multipoint"] == 1);
});
AsyncTest("An acknowledgement without a readable multipoint state is not success", async () =>
{
    var inner = new PreviewTransport(true); var link = new RestartTransport(inner, false);
    var c = new HeadsetController(link, HeadsetModel.Lolli, inner.Capabilities, "test", scratch, true); await c.RefreshAsync();
    await c.ApplyAsync(new Dictionary<string,int> { ["multipoint"] = 1 }); Check(c.ReconnectRequired && c.StateUncertain);
});
AsyncTest("Multipoint mismatch and backup format mismatch stop further writes", async () =>
{
    var inner = new PreviewTransport(true) { IgnoreWrites = true }; var c = Controller(inner); await c.RefreshAsync();
    var wrong = c.Export() with { Snapshot = c.Current! with { Multipoint = new(255, false) } };
    await Throws(() => { c.ValidateBackup(wrong); return Task.CompletedTask; });
    await Throws(() => c.ApplyAsync(new Dictionary<string,int> { ["multipoint"] = 1 })); Check(c.StateUncertain && !c.ReconnectRequired && inner.Writes.Count == 1);
});
AsyncTest("A failed backup or invalid multipoint value prevents the write", async () =>
{
    string path = Path.Combine(scratch, "multipoint-no-folder"); File.WriteAllText(path, "occupied");
    var inner = new PreviewTransport(true); var c = Controller(inner, path); await c.RefreshAsync();
    await Throws(() => c.ApplyAsync(new Dictionary<string,int> { ["multipoint"] = 1 })); Check(inner.Writes.Count == 0);
    await Throws(() => c.ApplyAsync(new Dictionary<string,int> { ["multipoint"] = 2 })); Check(inner.Writes.Count == 0);
});
AsyncTest("Lolli 1.0.9 ANC transitions tolerate stale reads and omit inactive ambient gain", async () =>
{
    var link = new LolliAncTransport { StaleReads = 2 };
    var c = new HeadsetController(link, HeadsetModel.Lolli, link.Capabilities, "fixture", scratch, true);
    await c.RefreshAsync(); Check(c.Current!.AncGroup == 23 && !c.Current.Settings.ContainsKey("ambient"));
    await c.ApplyAsync(new Dictionary<string,int> { ["anc"] = 2 });
    Check(c.Current!.Settings["anc"] == 2 && c.Current.Settings["ambient"] == 6);
    await c.ApplyAsync(new Dictionary<string,int> { ["anc"] = 1 });
    Check(c.Current!.Settings["anc"] == 1 && !c.Current.Settings.ContainsKey("ambient") && !c.StateUncertain);
    Check(link.Writes.Count == 2 && link.Writes[1].SequenceEqual(new byte[] {23,1}));
});
AsyncTest("Lolli ambient backup restores from a mode that does not report gain", async () =>
{
    var link = new LolliAncTransport { Mode = 2, Gain = 8 };
    var c = new HeadsetController(link, HeadsetModel.Lolli, link.Capabilities, "fixture", scratch, true);
    await c.RefreshAsync(); var backup = c.Export();
    await c.ApplyAsync(new Dictionary<string,int> { ["anc"] = 1 });
    c.ValidateBackup(backup); await c.ApplyAsync(backup.Snapshot.Settings, expectedBackup:backup);
    Check(c.Current!.Settings["anc"] == 2 && c.Current.Settings["ambient"] == 8 && !c.StateUncertain);
});
AsyncTest("Stored gain is verified in ambient mode before returning to ANC", async () =>
{
    var link = new LolliAncTransport();
    var c = new HeadsetController(link, HeadsetModel.Lolli, link.Capabilities, "fixture", scratch, true);
    await c.RefreshAsync(); await c.ApplyAsync(new Dictionary<string,int> { ["anc"] = 1, ["ambient"] = 9 });
    Check(link.Writes.Count == 2 && link.Writes[0].SequenceEqual(new byte[] {23,2,9}) && link.Writes[1].SequenceEqual(new byte[] {23,1}));
    Check(link.Gain == 9 && c.Current!.Settings["anc"] == 1 && !c.Current.Settings.ContainsKey("ambient"));
});
AsyncTest("Persistent ANC mismatch is explicit and never retries a write", async () =>
{
    var link = new LolliAncTransport { IgnoreWrites = true };
    var c = new HeadsetController(link, HeadsetModel.Lolli, link.Capabilities, "fixture", scratch, true);
    await c.RefreshAsync();
    string error=""; try { await c.ApplyAsync(new Dictionary<string,int> { ["anc"] = 1 }); } catch(IOException ex) { error=ex.Message; }
    Check(error.Contains("目标 降噪") && error.Contains("读回 降噪关闭") && c.StateUncertain && link.Writes.Count == 1);
    Check(c.Current!.Settings["anc"] == 3);
});
AsyncTest("Incorrect ambient gain blocks the final ANC write", async () =>
{
    var link = new LolliAncTransport { IgnoreGain = true };
    var c = new HeadsetController(link, HeadsetModel.Lolli, link.Capabilities, "fixture", scratch, true);
    await c.RefreshAsync(); await Throws(() => c.ApplyAsync(new Dictionary<string,int> { ["anc"] = 1, ["ambient"] = 9 }));
    Check(c.StateUncertain && link.Writes.Count == 1 && c.Current!.Settings["anc"] == 2);
});
AsyncTest("Unreadable ANC after a write cannot be accepted as success", async () =>
{
    var link = new LolliAncTransport { InvalidAfterWrite = true };
    var c = new HeadsetController(link, HeadsetModel.Lolli, link.Capabilities, "fixture", scratch, true);
    await c.RefreshAsync(); await Throws(() => c.ApplyAsync(new Dictionary<string,int> { ["anc"] = 1 }));
    Check(c.StateUncertain && link.Writes.Count == 1 && !c.Current!.Settings.ContainsKey("anc"));
});
AsyncTest("ANC no-op creates no write or uncertainty", async () =>
{
    var link = new LolliAncTransport { Mode = 1 };
    var c = new HeadsetController(link, HeadsetModel.Lolli, link.Capabilities, "fixture", scratch, true);
    await c.RefreshAsync(); await c.ApplyAsync(new Dictionary<string,int> { ["anc"] = 1 });
    Check(link.Writes.Count == 0 && !c.StateUncertain);
});
FeatureTests.Register(tests, scratch);
var results = new List<object>(); int failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; results.Add(new { name, passed = false, error = ex.ToString() }); Console.WriteLine("FAIL " + name + "\n" + ex); }
}
File.WriteAllText(Path.Combine(root, "docs/offline-tests.json"), JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, passed = tests.Count - failures, failed = failures, hardwareVerified = false, results }, HeadsetController.JsonOptions));
Console.WriteLine($"{tests.Count - failures}/{tests.Count} tests passed"); return failures == 0 ? 0 : 1;

sealed class FaultTransport(PreviewTransport inner, byte command, byte[]? replacement) : ICommandTransport
{
    public bool Connected => inner.Connected;
    public string TransportName => "fault test";
    public WireProfile Profile => inner.Profile;
    public Task<byte[]?> RequestAsync(byte cmd, byte[]? payload = null, bool optional = false, CancellationToken token = default) => cmd == command ? Task.FromResult(replacement) : inner.RequestAsync(cmd, payload, optional, token);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

// Firmware/capabilities come from the user's local backup. Transition timing is simulated.
sealed class LolliAncTransport : ICommandTransport
{
    public bool Connected => true;
    public string TransportName => "Lolli ANC 1.0.9 regression fixture";
    public WireProfile Profile => new(2,16);
    public Capabilities Capabilities { get; } = new(Convert.FromHexString("E30101040101010100010101093201001EE4004000200000010002"));
    public int Mode { get; set; } = 3;
    public int Gain { get; set; } = 6;
    public int StaleReads { get; set; }
    public bool IgnoreWrites { get; set; }
    public bool IgnoreGain { get; set; }
    public bool InvalidAfterWrite { get; set; }
    public List<byte[]> Writes { get; } = [];
    private byte[]? stale;
    private int remaining;
    private byte[] State() => Mode == 2 ? [23,2,(byte)Gain] : [23,(byte)Mode];
    public Task<byte[]?> RequestAsync(byte command, byte[]? payload = null, bool optional = false, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if(command == 0xC1)
        {
            byte[] p = payload!; Writes.Add(p.ToArray());
            if(p.Length is not (2 or 3) || p[0] != 23) throw new IOException("Invalid ANC command");
            stale = State(); remaining=StaleReads;
            if(!IgnoreWrites) { Mode=p[1]; if(p.Length==3 && !IgnoreGain) Gain=p[2]; }
            return Task.FromResult<byte[]?>(p.ToArray());
        }
        byte[]? reply = command switch
        {
            0xD8 => Capabilities.Raw, 0xC6 => [1,0,9], 0xF2 => [3,100,100,0,0,0], 0x08 => [0], 0xFB => [4,1],
            0xCC when InvalidAfterWrite && Writes.Count>0 => [23,255],
            0xCC when remaining-- > 0 => stale,
            0xCC => State(), _ => null
        };
        return Task.FromResult(reply);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class RestartTransport(PreviewTransport inner, bool disconnectBeforeAck) : ICommandTransport
{
    private bool changed;
    public bool Connected => !changed;
    public string TransportName => "restart test";
    public WireProfile Profile => inner.Profile;
    public async Task<byte[]?> RequestAsync(byte cmd, byte[]? payload = null, bool optional = false, CancellationToken token = default)
    {
        if (changed) throw new IOException("Simulated restart");
        var result = await inner.RequestAsync(cmd, payload, optional, token);
        if (cmd == 0x7D) { changed = true; if (disconnectBeforeAck) throw new IOException("Disconnected before acknowledgement"); }
        return result;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
