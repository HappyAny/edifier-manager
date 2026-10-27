using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace EdifierManager;

public record DiscoveredHeadset(ulong Address, string Name, string? ClassicId = null, ulong? BleAddress = null,
    BluetoothAddressType AddressType = BluetoothAddressType.Public, short? Rssi = null, bool AudioConnected = false,
    WireProfile? AdvertisedProfile = null, string? ProductCode = null)
{
    public override string ToString() => $"{Name}  ·  {(Rssi.HasValue ? "附近" : AudioConnected ? "音频已连接" : "已配对")}";
}

public sealed class EdifierTransport : ICommandTransport
{
    private static readonly Guid SerialService = new("edf00000-edfe-dfed-fedf-edfedfedfedf");
    private readonly SemaphoreSlim requests = new(1, 1);
    private readonly object gate = new();
    private readonly FrameDecoder decoder = new();
    private readonly HashSet<byte> timedOutCommands = [];
    private (byte command, byte[] payload, TaskCompletionSource<byte[]> completion)? pending;
    private BluetoothDevice? classic;
    private BluetoothLEDevice? ble;
    private RfcommDeviceService? rfcomm;
    private StreamSocket? socket;
    private DataReader? reader;
    private DataWriter? writer;
    private GattDeviceService? gatt;
    private GattCharacteristic? tx, rx;
    private GattSession? session;
    private readonly CancellationTokenSource lifetime = new();
    private Task? readTask;
    private bool connected;
    private long lastReceive;
    public bool Connected => connected;
    public string TransportName { get; private set; } = "";
    public string? ServiceUuid { get; private set; }
    public WireProfile Profile { get; private set; } = new();
    public void Configure(WireProfile profile)
    {
        profile.Validate();
        lock (gate)
        {
            if (pending != null) throw new InvalidOperationException("正在处理耳机请求。");
            Profile = profile; decoder.Profile = profile; decoder.Clear(); timedOutCommands.Clear();
        }
    }
    public event Action<string>? Log;
    public event Action? Disconnected;

    public static bool IsTargetName(string name) => name.Contains("W820NB", StringComparison.OrdinalIgnoreCase)
        || name.Contains("W830NB", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Lolli ANC", StringComparison.OrdinalIgnoreCase);

    public static async Task<List<DiscoveredHeadset>> DiscoverAsync(CancellationToken token = default)
    {
        var found = new ConcurrentDictionary<ulong, DiscoveredHeadset>();
        var known = KnownHeadsets.Load();
        var infos = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true)).AsTask(token);
        foreach (var info in infos)
        {
            if (!IsTargetName(info.Name) && known.Count == 0) continue;
            try
            {
                using var dev = await BluetoothDevice.FromIdAsync(info.Id).AsTask(token);
                if (dev == null) continue;
                known.TryGetValue(HeadsetController.MakeDeviceKey(dev.BluetoothAddress), out string? savedCode);
                if (IsTargetName(dev.Name) || savedCode != null) found[dev.BluetoothAddress] = new(dev.BluetoothAddress, dev.Name, info.Id,
                    AudioConnected: dev.ConnectionStatus == BluetoothConnectionStatus.Connected, ProductCode: savedCode);
            }
            catch (Exception ex) when (!token.IsCancellationRequested && ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
        }
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, e) =>
        {
            string name = e.Advertisement.LocalName;
            string? code = e.Advertisement.ServiceUuids.Select(HeadsetModel.ProductCodeFromService).FirstOrDefault(x => x != null);
            ulong identity = e.BluetoothAddress; WireProfile? profile = null;
            var manufacturer = e.Advertisement.ManufacturerData.FirstOrDefault(x => x.CompanyId == 2016);
            if (manufacturer != null)
            {
                byte[] data = manufacturer.Data.ToArray();
                if (data.Length >= 8)
                {
                    ulong classicAddress = 0; foreach (byte b in data[..6]) classicAddress = (classicAddress << 8) | b;
                    if (classicAddress != 0) identity = classicAddress;
                    profile = new(data[^2], data[^1]);
                }
            }
            if (code == null) known.TryGetValue(HeadsetController.MakeDeviceKey(identity), out code);
            if (!IsTargetName(name) && code == null) return;
            if (name.Length == 0) name = "EDIFIER " + HeadsetModel.Identify("", code).Label;
            found.AddOrUpdate(identity,
                _ => new(identity, name, BleAddress: e.BluetoothAddress, AddressType: e.BluetoothAddressType, Rssi: e.RawSignalStrengthInDBm, AdvertisedProfile: profile, ProductCode: code),
                (_, old) => old with { BleAddress = e.BluetoothAddress, AddressType = e.BluetoothAddressType, Rssi = e.RawSignalStrengthInDBm, AdvertisedProfile = profile ?? old.AdvertisedProfile, ProductCode = code ?? old.ProductCode });
        };
        try { watcher.Start(); await Task.Delay(6500, token); }
        catch when (!token.IsCancellationRequested && !found.IsEmpty) { /* Paired devices remain usable through RFCOMM. */ }
        finally { watcher.Stop(); }
        if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Aborted && found.IsEmpty)
            throw new IOException("蓝牙扫描不可用。请打开 Windows 蓝牙后重试。");
        return found.Values.OrderByDescending(x => x.AudioConnected).ThenByDescending(x => x.Rssi).ThenBy(x => x.Name).ToList();
    }

    public async Task ConnectAsync(DiscoveredHeadset selected, CancellationToken token = default)
    {
        if (lifetime.IsCancellationRequested) throw new ObjectDisposedException(nameof(EdifierTransport));
        if (connected) throw new InvalidOperationException("请先断开当前耳机。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(24));
        try
        {
            if (selected.ClassicId != null)
            {
                try
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    attempt.CancelAfter(TimeSpan.FromSeconds(selected.BleAddress.HasValue ? 10 : 20));
                    await ConnectRfcommAsync(selected.ClassicId, attempt.Token).ConfigureAwait(false);
                    return;
                }
                catch when (!deadline.IsCancellationRequested && selected.BleAddress.HasValue)
                {
                    writer?.Dispose(); writer = null; reader?.Dispose(); reader = null;
                    socket?.Dispose(); socket = null; rfcomm?.Dispose(); rfcomm = null; classic?.Dispose(); classic = null;
                    Log?.Invoke("串口管理连接不可用，尝试 BLE。");
                }
            }
            if (selected.BleAddress.HasValue)
            {
                await ConnectBleAsync(selected, deadline.Token).ConfigureAwait(false);
                return;
            }
            throw new IOException("未找到可用的管理连接。请开启耳机并在 Windows 中完成配对。");
        }
        catch { await DisposeAsync(); throw; }
    }

    private async Task ConnectRfcommAsync(string id, CancellationToken token)
    {
        classic = await BluetoothDevice.FromIdAsync(id).AsTask(token).ConfigureAwait(false)
            ?? throw new IOException("Windows 无法打开耳机。");
        var result = await classic.GetRfcommServicesForIdAsync(RfcommServiceId.FromUuid(SerialService), BluetoothCacheMode.Uncached).AsTask(token).ConfigureAwait(false);
        rfcomm = result.Services.FirstOrDefault() ?? throw new IOException("耳机未提供漫步者管理服务，请确认耳机已开机。");
        foreach (var unused in result.Services.Skip(1)) unused.Dispose();
        socket = new StreamSocket();
        await socket.ConnectAsync(rfcomm.ConnectionHostName, rfcomm.ConnectionServiceName,
            SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication).AsTask(token).ConfigureAwait(false);
        reader = new DataReader(socket.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
        writer = new DataWriter(socket.OutputStream);
        connected = true; TransportName = "Bluetooth RFCOMM"; ServiceUuid = SerialService.ToString();
        readTask = ReadLoopAsync();
        Log?.Invoke("已建立蓝牙管理连接。");
    }

    private async Task ConnectBleAsync(DiscoveredHeadset selected, CancellationToken token)
    {
        ble = await BluetoothLEDevice.FromBluetoothAddressAsync(selected.BleAddress!.Value, selected.AddressType).AsTask(token).ConfigureAwait(false)
            ?? throw new IOException("Windows 无法打开 BLE 耳机。");
        var services = await ble.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(token).ConfigureAwait(false);
        if (services.Status != GattCommunicationStatus.Success) throw new IOException("无法读取耳机 BLE 服务。");
        foreach (var candidate in services.Services)
        {
            bool retained = false;
            try
            {
                string uuid = candidate.Uuid.ToString();
                if (!uuid.EndsWith("-1a48-11e9-ab14-d663bd873d93", StringComparison.OrdinalIgnoreCase)) continue;
                var chars = await candidate.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(token).ConfigureAwait(false);
                if (chars.Status != GattCommunicationStatus.Success) continue;
                var receive = chars.Characteristics.FirstOrDefault(c => c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
                    && (c.Uuid.ToString().StartsWith("48090001-") || c.Uuid.ToString().Contains("2-1a48-11e9-ab14-d663bd873d93"))
                    && !c.Uuid.ToString().StartsWith("48090002-"));
                var transmit = chars.Characteristics.FirstOrDefault(c => c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
                    && (c.Uuid.ToString().StartsWith("48090002-") || c.Uuid.ToString().Contains("3-1a48-11e9-ab14-d663bd873d93")));
                if (gatt != null || receive == null || transmit == null) continue;
                gatt = candidate; rx = receive; tx = transmit; retained = true;
            }
            finally { if (!retained) candidate.Dispose(); }
        }
        if (gatt == null || rx == null || tx == null) throw new IOException("未找到已适配的 BLE 管理接口。");
        rx.ValueChanged += OnValue;
        var status = await rx.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(token).ConfigureAwait(false);
        if (status != GattCommunicationStatus.Success) throw new IOException("无法启用耳机状态通知。");
        session = await GattSession.FromDeviceIdAsync(ble.BluetoothDeviceId).AsTask(token).ConfigureAwait(false);
        if (session.CanMaintainConnection) session.MaintainConnection = true;
        ble.ConnectionStatusChanged += OnBleConnection;
        ServiceUuid = gatt.Uuid.ToString(); connected = true; TransportName = "Bluetooth LE";
        Log?.Invoke("已建立 BLE 管理连接。");
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                uint length = await reader!.LoadAsync(4096).AsTask(lifetime.Token).ConfigureAwait(false);
                if (length == 0) throw new IOException("耳机已关闭管理连接。");
                byte[] bytes = new byte[length]; reader.ReadBytes(bytes); Receive(bytes);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!lifetime.IsCancellationRequested) FailConnection("蓝牙管理连接已断开。");
        }
    }

    private void OnBleConnection(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected) FailConnection("蓝牙管理连接已断开。");
    }
    private void OnValue(GattCharacteristic sender, GattValueChangedEventArgs args) => Receive(args.CharacteristicValue.ToArray());
    private void Receive(byte[] bytes)
    {
        lock (gate)
        {
            long now = Environment.TickCount64;
            if (now - lastReceive > 2000) decoder.Clear();
            lastReceive = now;
            foreach (var frame in decoder.Push(bytes))
            {
                Log?.Invoke($"接收 {frame.Header:X2}/{frame.Command:X2} · {frame.Data.Length} 字节" + (frame.Command is 0xC1 or 0xCC or 0xD8 or 0xD5 or 0xF0 or 0xFD or 0x86 or 0xD3 or 0xD7 or 0x48 or 0x5C ? " · " + Convert.ToHexString(frame.Data) : ""));
                if (pending is { } p && frame.Command == p.command
                    && (p.command is not (0xF0 or 0xF1) || p.payload.Length == 0 || frame.Data.Length > 0 && frame.Data[0] == p.payload[0]))
                    p.completion.TrySetResult(frame.Data);
            }
        }
    }

    public async Task<byte[]?> RequestAsync(byte command, byte[]? payload = null, bool optional = false, CancellationToken token = default)
    {
        await requests.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!connected) throw new IOException("请先连接耳机。");
            lock (gate)
            {
                if (timedOutCommands.Contains(command))
                {
                    if (optional) return null;
                    throw new IOException("该操作此前未得到回复，请重新连接后重试。");
                }
            }
            byte[] body = payload ?? [];
            if (command == 0xC1) Log?.Invoke("声音写入 C1 = " + Convert.ToHexString(body));
            var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) pending = (command, body, completion);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await WriteAsync(EdifierProtocol.Encode(command, body, profile: Profile), deadline.Token).ConfigureAwait(false);
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(3), deadline.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                lock (gate) timedOutCommands.Add(command);
                Log?.Invoke($"操作 {command:X2} 未回复；本次连接不再重试该命令。");
                if (optional) return null;
                throw new IOException("耳机未回复该操作。设置状态未知，请重新连接后读取。");
            }
            catch (OperationCanceledException)
            {
                lock (gate) timedOutCommands.Add(command);
                throw;
            }
        }
        finally { lock (gate) pending = null; requests.Release(); }
    }

    private async Task WriteAsync(byte[] bytes, CancellationToken token)
    {
        Log?.Invoke($"发送 {bytes[2]:X2} · {bytes.Length} 字节");
        if (writer != null)
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync().AsTask(token).ConfigureAwait(false);
        }
        else if (tx != null)
        {
            for (int offset = 0; offset < bytes.Length; offset += 20)
            {
                var status = await tx.WriteValueAsync(bytes.AsSpan(offset, Math.Min(20, bytes.Length - offset)).ToArray().AsBuffer(),
                    GattWriteOption.WriteWithResponse).AsTask(token).ConfigureAwait(false);
                if (status != GattCommunicationStatus.Success) throw new IOException($"蓝牙发送失败：{status}。");
                if (offset + 20 < bytes.Length) await Task.Delay(80, token).ConfigureAwait(false);
            }
        }
        else throw new IOException("蓝牙管理连接不可用。");
    }

    private void FailConnection(string message)
    {
        connected = false;
        lock (gate) pending?.completion.TrySetException(new IOException(message));
        Disconnected?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        connected = false; lifetime.Cancel();
        lock (gate) pending?.completion.TrySetException(new IOException("管理连接已关闭。"));
        if (rx != null) rx.ValueChanged -= OnValue;
        if (ble != null) ble.ConnectionStatusChanged -= OnBleConnection;
        socket?.Dispose(); socket = null;
        if (readTask != null) { try { await readTask.ConfigureAwait(false); } catch { } readTask = null; }
        writer?.Dispose(); writer = null; reader?.Dispose(); reader = null;
        if (session != null) { session.MaintainConnection = false; session.Dispose(); session = null; }
        gatt?.Dispose(); gatt = null; rx = tx = null;
        rfcomm?.Dispose(); rfcomm = null; ble?.Dispose(); ble = null; classic?.Dispose(); classic = null;
    }
}
