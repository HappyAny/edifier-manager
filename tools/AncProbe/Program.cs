using EdifierManager;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using System.Text.Json;

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
try
{
    var infos = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
    var info = infos.SingleOrDefault(x => x.Name.Equals("EDIFIER Lolli ANC", StringComparison.OrdinalIgnoreCase));
    if (info == null) { Console.WriteLine("No paired Lolli ANC found."); return; }
    using var dev = await BluetoothDevice.FromIdAsync(info.Id);
    if (dev.ConnectionStatus != BluetoothConnectionStatus.Connected) { Console.WriteLine("Lolli ANC is not currently connected; no connection attempt made."); return; }
    await using var link = new EdifierTransport();
    var logs = new List<string>(); link.Log += message => logs.Add(message);
    await link.ConnectAsync(new(dev.BluetoothAddress, dev.Name, info.Id), deadline.Token);
    var caps = await HeadsetController.NegotiateAsync(link, new(2,16), deadline.Token);
    var controller = new HeadsetController(link, HeadsetModel.Lolli, caps, HeadsetController.MakeDeviceKey(dev.BluetoothAddress), "artifacts/probe-unused-backups");
    var snapshot = await controller.RefreshAsync(deadline.Token);
    Directory.CreateDirectory("artifacts");
    File.WriteAllText("artifacts/lolli-features-readonly.json", JsonSerializer.Serialize(new { utc=DateTimeOffset.UtcNow, readOnly=true, snapshot, logs }, HeadsetController.JsonOptions));
    Console.WriteLine(JsonSerializer.Serialize(new { snapshot.Firmware, snapshot.Capabilities, settingKeys=snapshot.Settings.Keys, hasName=snapshot.DeviceName != null, snapshot.ShutdownMinutes, snapshot.BeepGroup, snapshot.SensitivityIndex, customEqGroup=snapshot.CustomEq?.Group, writesPerformed=false }));
}
catch (Exception ex) { Console.WriteLine("Read-only probe unavailable: " + ex.GetType().Name + ": " + ex.Message); Environment.ExitCode=2; }
