using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace EdifierManager;

public partial class MainWindow : Window
{
    private ICommandTransport? transport;
    private HeadsetController? controller;
    private SettingsBackup? imported;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, ComboBox> touchChoices = [];
    private bool busy, preview, closing, closePending;
    private (string DeviceKey, string ModelId, int Value)? pendingMultipoint;
    private readonly string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdifierManager");
    private string BackupDirectory => Path.Combine(dataDirectory, preview ? "preview-backups" : "backups");
    private readonly string[] arguments = Environment.GetCommandLineArgs();

    public MainWindow()
    {
        InitializeComponent();
        ControlStack.Children.Remove(CycleCard); SoundStack.Children.Insert(1, CycleCard);
        InitializeFeatures();
        AdaptLayout();
        string mode = "System";
        try { string saved = File.ReadAllText(Path.Combine(dataDirectory, "theme.txt")).Trim(); if (saved is "System" or "Light" or "Dark") mode = saved; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        ApplyTheme(mode);
        SystemEvents.UserPreferenceChanged += ThemePreferenceChanged;
        Closing += WindowClosing;
        Loaded += WindowLoaded;
        UpdateEnabled();
    }
    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (arguments.Contains("--preview-ui") || arguments.Contains("--preview"))
            await RunAsync("正在准备预览…", async () => await StartPreviewAsync(arguments.Contains("--lolli") ? HeadsetModel.Lolli : arguments.Contains("--w830") ? HeadsetModel.W830 : HeadsetModel.W820Gold));
        if (arguments.Contains("--verify-native"))
        {
            try { await CapturePreviewAsync(); Close(); }
            catch (Exception ex)
            {
                string? dir = Environment.GetEnvironmentVariable("EDIFIER_PREVIEW_DIR");
                if (dir != null) { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "failure.txt"), ex.ToString()); }
                Application.Current.Shutdown(1);
            }
        }
    }
    private async Task RunAsync(string message, Func<Task> operation)
    {
        if (busy || closing || closePending) return;
        busy = true; StatusText.Text = message; UpdateEnabled();
        try { await operation(); }
        catch (OperationCanceledException) { if (!closing) StatusText.Text = "操作已取消或超时。"; }
        catch (Exception ex) { if (controller?.Current != null) RenderState(); StatusText.Text = ex.Message; Log(ex.Message); }
        finally { busy = false; if (!closing) UpdateEnabled(); }
    }
    private void Log(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => { if (!closing) Log(message); }); return; }
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss} {message}\n");
        if (LogBox.Text.Length > 16000) LogBox.Text = LogBox.Text[^12000..];
        LogBox.ScrollToEnd();
    }
    private async void Scan_Click(object sender, RoutedEventArgs e) => await RunAsync("正在扫描耳机…", async () =>
    {
        if (transport != null) await DisconnectAsync();
        var list = await EdifierTransport.DiscoverAsync(lifetime.Token);
        Devices.ItemsSource = list; Devices.SelectedIndex = list.Count > 0 ? 0 : -1;
        ConnectionText.Text = list.Count > 0 ? "选择耳机后连接管理服务。" : "未发现耳机。请开机并在 Windows 中配对后重试。";
        StatusText.Text = $"扫描完成 · {list.Count} 台耳机";
    });
    private async void Connect_Click(object sender, RoutedEventArgs e) => await RunAsync("正在连接并读取耳机…", async () =>
    {
        if (transport != null) { await DisconnectAsync(); StatusText.Text = "已断开管理连接。"; return; }
        if (Devices.SelectedItem is not DiscoveredHeadset selected) throw new IOException("请先扫描并选择耳机。");
        var model = HeadsetModel.Identify(selected.Name, selected.ProductCode);
        var link = new EdifierTransport();
        link.Log += Log;
        link.Disconnected += () => Dispatcher.BeginInvoke(() => { if (!closing && ReferenceEquals(transport, link)) { ClearDisplay(); ConnectionText.Text = "耳机连接已断开，请重新连接。"; UpdateEnabled(); } });
        transport = link; preview = false;
        try
        {
            await link.ConnectAsync(selected, lifetime.Token);
            var caps = await HeadsetController.NegotiateAsync(link, selected.AdvertisedProfile, lifetime.Token);
            if (caps.Tws != model.Tws) throw new IOException("耳机返回的类型与所选型号不符，已停止连接。");
            controller = new(link, model, caps, HeadsetController.MakeDeviceKey(selected.Address), BackupDirectory);
            controller.Log += Log;
            await controller.RefreshAsync(lifetime.Token);
            KnownHeadsets.Remember(selected.Address, model);
            RenderState();
            try { RefreshAudio(); } catch (Exception ex) { AudioStatus.Text = "音频端点暂不可用，可稍后刷新。"; Log(ex.Message); }
            ConnectionText.Text = $"{model.Label} · {link.TransportName} · 管理连接已就绪";
            StatusText.Text = "读取完成 · 修改前自动备份";
            if (pendingMultipoint is { } pending && pending.DeviceKey == HeadsetController.MakeDeviceKey(selected.Address) && pending.ModelId == model.Id)
            {
                if (controller.Current!.Settings.TryGetValue("multipoint", out int actual))
                {
                    StatusText.Text = actual == pending.Value ? $"重连校验通过 · 双设备连接已{(actual == 1 ? "开启" : "关闭")}" : "重连后双设备状态与目标不一致，请检查。";
                    pendingMultipoint = null;
                }
                else StatusText.Text = "已连接，但未能读取双设备连接状态，暂时无法确认上次修改。";
            }
        }
        catch { await DisconnectAsync(); throw; }
    });
    private async Task DisconnectAsync()
    {
        findCancellation?.Cancel();
        bool wasPreview = preview;
        var old = transport; transport = null; controller = null; imported = null; preview = false;
        ClearDisplay();
        if (wasPreview) Devices.ItemsSource = null;
        if (old != null) await old.DisposeAsync();
        ConnectionText.Text = "管理连接已关闭。打开耳机后可重新连接。";
    }
    private async void Disconnect_Click(object sender, RoutedEventArgs e) => await RunAsync("正在断开…", async () => { await DisconnectAsync(); StatusText.Text = "已断开。"; });
    private async Task StartPreviewAsync(HeadsetModel model)
    {
        await DisconnectAsync(); preview = true;
        var link = new PreviewTransport(model, observedFirmware: model == HeadsetModel.Lolli); transport = link;
        controller = new(link, model, link.Capabilities, "preview-" + model.Id, BackupDirectory, true);
        await controller.RefreshAsync(lifetime.Token);
        Devices.ItemsSource = new[] { model.Label + " · 离线示例" }; Devices.SelectedIndex = 0;
        RenderState();
        ConnectionText.Text = "离线示例，可体验设置、备份和恢复；不会连接或更改真实耳机。";
        AudioStatus.Text = "离线预览不操作系统音频。";
        StatusText.Text = "预览模式 · 电量与功能均为示例，非实测";
    }
    private async void Preview_Click(object sender, RoutedEventArgs e) => await RunAsync("正在准备预览…", async () => await StartPreviewAsync(((sender as MenuItem)?.Tag as string) switch { "lolli" => HeadsetModel.Lolli, "w830" => HeadsetModel.W830, _ => HeadsetModel.W820Gold }));
    private void GoSound_Click(object sender, RoutedEventArgs e) => Tabs.SelectedItem = SoundTab;
    private void GoTouch_Click(object sender, RoutedEventArgs e) => Tabs.SelectedItem = ControlTab;
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync("正在读取耳机…", async () => { RequireController(); await controller!.RefreshAsync(lifetime.Token); RenderState(); StatusText.Text = "已重新读取耳机状态。"; });
    private void RequireController()
    {
        if (controller == null || transport?.Connected != true) throw new IOException("请先连接耳机。");
    }
    private void RenderState()
    {
        if (controller?.Current is not { } s) return;
        var c = controller.Capabilities;
        ModelText.Text = controller.Model.Label;
        PreviewBadge.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        DeviceStatus.Text = preview ? "示例" : "已连接";
        ConnectionDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");
        LeftLabel.Text = controller.Model.Tws ? "左耳" : "耳机电量";
        LeftSymbol.Text = controller.Model.Tws ? "L" : "H";
        ProductArt.Tws = controller.Model.Tws;
        LeftBattery.FontSize = controller.Model.Tws ? 22 : 30;
        RightBatteryPanel.Visibility = controller.Model.Tws ? Visibility.Visible : Visibility.Collapsed;
        LeftBattery.Text = s.LeftBattery is int l ? $"{l}%" : "—"; LeftGauge.Percent = s.LeftBattery ?? double.NaN;
        RightBattery.Text = s.RightBattery is int r ? $"{r}%" : "—"; RightGauge.Percent = s.RightBattery ?? double.NaN;
        BoxText.Text = controller.Model.Tws ? s.BoxBattery is int b ? $"充电盒 {b}%" : "充电盒电量未上报" : "";
        FirmwareText.Text = preview ? "固件 · 示例" : "固件 " + s.Firmware;
        UpdatedText.Text = preview ? "示例电量" : $"更新于 {s.ReadAt:HH:mm:ss}";
        void Select(ComboBox box, IReadOnlyList<Choice> options, string key)
        { box.ItemsSource = options; box.SelectedItem = s.Settings.TryGetValue(key, out int value) ? options.FirstOrDefault(x => x.Value == value) : null; }
        Select(AncChoice, c.AncOptions, "anc"); Select(EqChoice, c.EqOptions, "eq"); Select(WearChoice, c.WearOptions, "wear");
        AmbientSlider.Value = s.Settings.GetValueOrDefault("ambient", 6) - 6;
        GameEnabled.IsChecked = s.Settings.GetValueOrDefault("game") == 1; PromptSlider.Value = s.Settings.GetValueOrDefault("prompt", 7);
        MultipointEnabled.IsChecked = s.Settings.GetValueOrDefault("multipoint") == 1;
        bool hasMultipointSwitch = s.Settings.ContainsKey("multipoint");
        MultipointStatus.Text = s.Multipoint is { } mp ? (mp.Enabled ? "当前已开启" : "当前已关闭")
            : c.Multipoint ? "开关状态未读取" : "通过配对连接";
        MultipointPanel.Visibility = hasMultipointSwitch ? Visibility.Visible : Visibility.Collapsed;
        MultipointSetupButton.Visibility = controller.Model == HeadsetModel.Lolli ? Visibility.Visible : Visibility.Collapsed;
        MultipointHint.Text = controller.Model == HeadsetModel.Lolli && !hasMultipointSwitch
            ? (c.Multipoint ? "暂未读到开关状态，请重新读取。\n\n" : "此固件未上报独立开关，按官方配对流程使用：\n\n")
              + "1. 先在设备 A 配对并连接耳机。\n2. 耳机继续处于配对模式，在设备 B 搜索并连接。\n3. 回到设备 A，手动连接耳机，完成双设备连接。\n\n若 B 搜不到耳机，将双耳放入盒内并开盖，长按盒上按键约 3 秒进入配对。再次开机会回连最后两台设备。"
            : controller.Model.Tws
                ? "左右耳需同时在线。开启后先连接设备 A，再连接 B，最后在 A 上手动连接一次。切换可能重启耳机或中断音频。"
                : "开启后先连接设备 A，长按模式键约 3 秒，再配对 B，最后在 A 上手动连接一次。切换可能重启耳机，并影响 LDAC / 空间音效。";
        int cycle = s.Settings.GetValueOrDefault("cycle");
        CycleNormal.IsChecked = (cycle & 1) != 0; CycleAnc.IsChecked = (cycle & 2) != 0; CycleAmbient.IsChecked = (cycle & 4) != 0;
        string Missing(string key) => s.Settings.ContainsKey(key) ? "已读取 · 修改后点击应用" : "耳机未提供此功能，或返回格式尚未适配。";
        AncHint.Text = Missing("anc"); EqHint.Text = Missing("eq"); GameHint.Text = Missing("game"); PromptHint.Text = Missing("prompt");
        CycleHint.Text = s.Settings.ContainsKey("cycle") ? "至少保留两种模式，按键在所选模式间切换。" : "此耳机的按键模式未读取或尚未适配。";
        CycleCard.Visibility = controller.Model.Tws ? Visibility.Collapsed : Visibility.Visible;
        TouchCard.Visibility = controller.Model.Tws ? Visibility.Visible : Visibility.Collapsed;
        touchChoices.Clear(); TouchRows.Children.Clear(); touchCycles.Clear();
        foreach (int slot in c.TouchSlots)
        {
            string key = "touch" + slot;
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(115) }); grid.ColumnDefinitions.Add(new());
            grid.Children.Add(new TextBlock { Text = Capabilities.TouchLabel(slot), VerticalAlignment = VerticalAlignment.Center });
            var choice = new ComboBox { MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center, IsEnabled = s.Settings.ContainsKey(key) };
            System.Windows.Automation.AutomationProperties.SetName(choice, Capabilities.TouchLabel(slot));
            Select(choice, c.TouchOptions(slot), key); Grid.SetColumn(choice, 1); grid.Children.Add(choice);
            touchChoices[key] = choice; TouchRows.Children.Add(grid);
            AddTouchCycleControls(slot, choice, s, c);
        }
        TouchHint.Text = touchChoices.Any(x => s.Settings.ContainsKey(x.Key)) ? "已读取手势。未识别的扩展动作不会写入。" : "此固件的触控配置未读取或尚未适配，耳机原有触控仍可使用。";
        ProtocolText.Text = $"{transport!.TransportName} · 协议 v{transport.Profile.Version} · ANC {c.AncGroup} / EQ {c.EqGroup} / 触控 {c.TouchGroup}";
        CurrentModeText.Text = s.Settings.TryGetValue("anc", out int m) ? "当前 · " + c.AncOptions.First(x => x.Value == m).Label : "聆听模式未读取";
        RenderFeatures(s, c);
        AdaptLayout();
        UpdateEnabled();
    }
    private void AdaptLayout()
    {
        bool connected = controller?.Current != null && transport?.Connected == true;
        bool tws = connected && controller!.Model.Tws;
        bool Has(string key) => connected && controller!.Current!.Settings.ContainsKey(key);
        void Show(UIElement element, bool show) => element.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Show(WelcomePanel, !connected); Show(BatteryCard, connected); Show(AudioCard, connected);
        Show(AncCard, Has("anc")); Show(EqCard, Has("eq")); Show(GameCard, Has("game")); Show(PromptCard, Has("prompt"));
        Show(MultipointCard, Has("multipoint") || connected && controller!.Model == HeadsetModel.Lolli);
        Show(CycleCard, !tws && Has("cycle")); Show(WearCard, tws && Has("wear"));
        bool touch = tws && controller!.Current!.Settings.Keys.Any(x => x.StartsWith("touch"));
        Show(ControlTab, touch || Has("sensitivity")); Show(TouchCard, touch);
        bool sound = Has("anc") || Has("eq") || Has("game") || Has("cycle");
        Show(SoundTab, sound); Show(SettingsTab, connected); Show(TwsQuickCard, tws && (sound || touch));
        Show(GoSoundButton, sound); Show(GoTouchButton, touch);
        HomeTab.Header = connected ? tws ? "双耳" : "头戴耳机" : "耳机";
        SoundTab.Header = tws ? "声音" : "降噪与声音";
        if (tws && HomeStack.Children.Contains(AudioCard)) { HomeStack.Children.Remove(AudioCard); SettingsStack.Children.Insert(1, AudioCard); }
        if (!tws && SettingsStack.Children.Contains(AudioCard)) { SettingsStack.Children.Remove(AudioCard); HomeStack.Children.Add(AudioCard); }
        if (Tabs.SelectedItem is TabItem selected && selected.Visibility != Visibility.Visible) Tabs.SelectedItem = HomeTab;
    }
    private void ClearDisplay()
    {
        ModelText.Text = "W820NB / W830NB / Lolli ANC"; UpdatedText.Text = "等待连接"; DeviceStatus.Text = "未连接";
        ConnectionDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SecondaryBrush");
        PreviewBadge.Visibility = Visibility.Collapsed; LeftBattery.Text = RightBattery.Text = "—";
        LeftGauge.Percent = RightGauge.Percent = double.NaN; FirmwareText.Text = "固件 —"; BoxText.Text = "";
        CurrentModeText.Text = "连接后读取耳机状态"; ProtocolText.Text = "";
        AncChoice.ItemsSource = EqChoice.ItemsSource = null; TouchRows.Children.Clear(); touchChoices.Clear(); touchCycles.Clear(); ClearFeatures();
        AudioDevices.ItemsSource = null; AudioStatus.Text = "选择耳机后刷新音频设备。";
        ImportText.Text = "每次修改前自动备份，写入后重新读取确认。";
        AncHint.Text = "连接后显示此耳机支持的模式。"; EqHint.Text = "支持的预设由耳机型号和固件决定。";
        GameHint.Text = "连接后读取游戏模式。"; PromptHint.Text = "连接后读取提示音量。";
        WearChoice.ItemsSource = null; AdaptLayout();
    }
    private void UpdateEnabled()
    {
        if (ConnectButton == null) return;
        bool ready = controller?.Current != null && transport?.Connected == true && !busy && controller.StateUncertain == false;
        bool Has(string key) => ready && controller!.Current!.Settings.ContainsKey(key);
        ScanButton.IsEnabled = !busy; Devices.IsEnabled = !busy && transport == null; ConnectButton.IsEnabled = !busy;
        ConnectButton.Content = transport == null ? "连接设备" : "断开连接";
        RefreshButton.IsEnabled = !busy && transport?.Connected == true;
        AncPanel.IsEnabled = Has("anc"); EqPanel.IsEnabled = Has("eq"); GamePanel.IsEnabled = Has("game"); PromptPanel.IsEnabled = Has("prompt"); CyclePanel.IsEnabled = Has("cycle");
        WearPanel.IsEnabled = Has("wear"); WelcomePanel.IsEnabled = !busy;
        MultipointPanel.IsEnabled = Has("multipoint") && (!controller!.Model.Tws || controller.Current!.BothEarsOnline);
        if (Has("multipoint") && controller!.Model.Tws && !controller.Current!.BothEarsOnline) MultipointStatus.Text = "需左右耳同时在线";
        AmbientPanel.Visibility = controller?.Current?.Settings.ContainsKey("ambient") == true && (AncChoice.SelectedItem as Choice)?.Value == controller?.Capabilities.AmbientMode ? Visibility.Visible : Visibility.Collapsed;
        AmbientPanel.IsEnabled = Has("ambient") && (AncChoice.SelectedItem as Choice)?.Value == controller?.Capabilities.AmbientMode;
        bool touchReady = ready && (!controller!.Model.Tws || controller.Current!.BothEarsOnline);
        TouchRows.IsEnabled = touchReady; TouchApplyButton.IsEnabled = touchReady && touchChoices.Any(x => controller!.Current!.Settings.ContainsKey(x.Key));
        if (ready && controller!.Model.Tws && !controller.Current!.BothEarsOnline) TouchHint.Text = "修改触控需要左右耳同时在线，请取出双耳后重新读取。";
        ExportButton.IsEnabled = ready; ImportButton.IsEnabled = ready; RestoreButton.IsEnabled = ready && imported != null;
        UpdateFeatureEnabled(ready);
        AudioPanel.IsEnabled = !busy && !preview; AudioApplyButton.IsEnabled = !busy && !preview && AudioDevices.SelectedItem != null;
        foreach (var item in MenuButton.ContextMenu.Items.OfType<MenuItem>())
            if (item.Tag is string tag && tag is "w820" or "lolli") item.IsEnabled = !busy;
    }
    private async Task ApplyAsync(Dictionary<string, int> changes, SettingsBackup? expectedBackup = null)
    {
        RequireController(); await controller!.ApplyAsync(changes, lifetime.Token, expectedBackup);
        if (controller.ReconnectRequired)
        {
            if (!preview && Devices.SelectedItem is DiscoveredHeadset selected && controller.PendingMultipointValue is int wanted)
                pendingMultipoint = (HeadsetController.MakeDeviceKey(selected.Address), controller.Model.Id, wanted);
            imported = null;
            await DisconnectAsync();
            ConnectionText.Text = "双设备设置尚待确认。耳机可能正在重启，请稍后点击“连接设备”重新读取。";
            StatusText.Text = "双设备修改待重连校验 · 尚未确认生效";
            return;
        }
        imported = null; RenderState(); ImportText.Text = "修改前的设置已自动保存，可从菜单打开备份目录。";
        StatusText.Text = preview ? "预览设置已更新 · 未操作真实耳机" : "设置已写入，读回校验通过。";
    }
    private void AncChoice_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (AmbientPanel != null) UpdateEnabled(); }
    private async void AncApply_Click(object sender, RoutedEventArgs e) => await RunAsync("正在应用声音模式…", async () =>
    {
        if (AncChoice.SelectedItem is not Choice mode) throw new IOException("请选择声音模式。");
        var values = new Dictionary<string, int> { ["anc"] = mode.Value };
        if (controller!.Current!.Settings.ContainsKey("ambient") && mode.Value == controller.Capabilities.AmbientMode) values["ambient"] = (int)AmbientSlider.Value + 6;
        await ApplyAsync(values);
    });
    private async void EqApply_Click(object sender, RoutedEventArgs e) => await RunAsync("正在应用预设…", async () => { if (EqChoice.SelectedItem is not Choice value) throw new IOException("请选择预设。"); await ApplyAsync(new() { ["eq"] = value.Value }); });
    private async void GameApply_Click(object sender, RoutedEventArgs e) => await RunAsync("正在应用游戏模式…", async () => await ApplyAsync(new() { ["game"] = GameEnabled.IsChecked == true ? 1 : 0 }));
    private async void MultipointApply_Click(object sender, RoutedEventArgs e) => await RunAsync("正在修改双设备连接…", async () => await ApplyAsync(new() { ["multipoint"] = MultipointEnabled.IsChecked == true ? 1 : 0 }));
    private async void PromptApply_Click(object sender, RoutedEventArgs e) => await RunAsync("正在应用提示音量…", async () => await ApplyAsync(new() { ["prompt"] = (int)PromptSlider.Value }));
    private async void WearApply_Click(object sender, RoutedEventArgs e) => await RunAsync("正在应用入耳检测…", async () => { if (WearChoice.SelectedItem is not Choice value) throw new IOException("请选择入耳检测模式。"); await ApplyAsync(new() { ["wear"] = value.Value }); });
    private async void CycleApply_Click(object sender, RoutedEventArgs e) => await RunAsync("正在应用按键模式…", async () => await ApplyAsync(new() { ["cycle"] = (CycleNormal.IsChecked == true ? 1 : 0) | (CycleAnc.IsChecked == true ? 2 : 0) | (CycleAmbient.IsChecked == true ? 4 : 0) }));
    private async void TouchApply_Click(object sender, RoutedEventArgs e) => await RunAsync("正在应用触控…", async () =>
    {
        var changes = touchChoices.Where(x => controller!.Current!.Settings.ContainsKey(x.Key) && x.Value.SelectedItem is Choice).ToDictionary(x => x.Key, x => ((Choice)x.Value.SelectedItem).Value);
        foreach (var (slot, boxes) in touchCycles)
            if (changes.GetValueOrDefault("touch" + slot, -1) == 5) changes["cycle" + slot] = boxes.Select((box, index) => box.IsChecked == true ? 1 << index : 0).Aggregate(0, (a, b) => a | b);
        await ApplyAsync(changes);
    });
    private void Devices_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (AudioDevices != null && !busy) { AudioDevices.ItemsSource = null; UpdateEnabled(); } }
    private void RefreshAudio()
    {
        if (preview) return;
        string? name = (Devices.SelectedItem as DiscoveredHeadset)?.Name;
        if (name == null) throw new IOException("请先扫描并选择耳机。");
        AudioDevices.ItemsSource = WindowsAudio.List(name); AudioDevices.SelectedIndex = AudioDevices.Items.Count > 0 ? 0 : -1;
        AudioStatus.Text = AudioDevices.Items.Count > 0 ? "仅调整所选耳机的 Windows 音频端点。" : "没有活动音频端点。请先在 Windows 中连接这副耳机。";
    }
    private async void AudioRefresh_Click(object sender, RoutedEventArgs e) => await RunAsync("正在读取音频设备…", () => { RefreshAudio(); StatusText.Text = "音频设备已刷新。"; return Task.CompletedTask; });
    private void AudioDevices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AudioDevices.SelectedItem is AudioEndpoint value) { AudioVolume.Value = value.Volume; AudioMute.IsChecked = value.Muted; }
        UpdateEnabled();
    }
    private async void AudioApply_Click(object sender, RoutedEventArgs e) => await RunAsync("正在应用音量…", () =>
    {
        if (preview || AudioDevices.SelectedItem is not AudioEndpoint endpoint || Devices.SelectedItem is not DiscoveredHeadset selected) throw new IOException("请先选择真实耳机的音频设备。");
        WindowsAudio.Set(endpoint.Id, (int)AudioVolume.Value, AudioMute.IsChecked == true, selected.Name);
        RefreshAudio(); StatusText.Text = "Windows 音量已更新，读回校验通过。"; return Task.CompletedTask;
    });
    private void Previous_Click(object sender, RoutedEventArgs e) { if (!preview) WindowsAudio.MediaKey(0xB1); }
    private void Play_Click(object sender, RoutedEventArgs e) { if (!preview) WindowsAudio.MediaKey(0xB3); }
    private void Next_Click(object sender, RoutedEventArgs e) { if (!preview) WindowsAudio.MediaKey(0xB0); }
    private async void Export_Click(object sender, RoutedEventArgs e) => await RunAsync("正在导出备份…", async () =>
    {
        RequireController(); await controller!.RefreshAsync(lifetime.Token); RenderState();
        var dialog = new SaveFileDialog { Filter = "JSON 备份|*.json", FileName = $"{controller.Model.Id}-{DateTime.Now:yyyyMMdd-HHmmss}.json" };
        if (dialog.ShowDialog(this) != true) { StatusText.Text = "已取消导出。"; return; }
        await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(controller.Export(), HeadsetController.JsonOptions), lifetime.Token);
        StatusText.Text = "备份已导出。";
    });
    private async void Import_Click(object sender, RoutedEventArgs e) => await RunAsync("正在检查备份…", async () =>
    {
        RequireController(); imported = null;
        var dialog = new OpenFileDialog { Filter = "JSON 备份|*.json" };
        if (dialog.ShowDialog(this) != true) { StatusText.Text = "已取消导入。"; return; }
        if (new FileInfo(dialog.FileName).Length > 64000) throw new InvalidDataException("备份文件过大。");
        var backup = JsonSerializer.Deserialize<SettingsBackup>(await File.ReadAllTextAsync(dialog.FileName, lifetime.Token), HeadsetController.JsonOptions) ?? throw new InvalidDataException("无效备份。");
        controller!.ValidateBackup(backup); imported = backup;
        var diff = backup.Snapshot.Settings.Where(x => controller.Current!.Settings.GetValueOrDefault(x.Key, -1) != x.Value).ToArray();
        bool eqChanged = backup.Snapshot.CustomEq != null && !backup.Snapshot.CustomEq.SameBands(controller.Current!.CustomEq);
        ImportText.Text = $"已检查 {controller.Model.Label} 备份 · {diff.Length} 项变化" + (eqChanged ? "，另含自定义 EQ 变化" : "") + "\n" + string.Join("；", diff.Select(x => $"{SettingLabel(x.Key)}：{controller.Current!.Settings.GetValueOrDefault(x.Key)} → {x.Value}")) + "\n不恢复名称或关机倒计时；LDAC 请在音质卡片单独修改。";
        StatusText.Text = "导入仅预览，点击“应用导入设置”后才会写入。";
    });
    private static string SettingLabel(string key) => key switch { "multipoint" => "双设备连接", "anc" => "声音模式", "ambient" => "环境声强度", "game" => "游戏模式", "prompt" => "提示音量", "eq" => "声音预设", "cycle" => "按键模式", "wear" => "入耳检测", _ => int.TryParse(key.AsSpan(Math.Min(5, key.Length)), out int slot) ? Capabilities.TouchLabel(slot) : key };
    private async void Restore_Click(object sender, RoutedEventArgs e) => await RunAsync("正在恢复并校验…", async () =>
    {
        RequireController(); if (imported == null) throw new IOException("请先导入备份。");
        var backup = imported; controller!.ValidateBackup(backup);
        if (backup.Snapshot.CustomEq is { } custom && !custom.SameBands(controller.Current!.CustomEq)) await controller.ApplyCustomEqAsync(custom, lifetime.Token);
        await ApplyAsync(backup.Snapshot.Settings, backup);
    });
    private async void Diagnostics_Click(object sender, RoutedEventArgs e) => await RunAsync("正在保存诊断…", async () =>
    {
        var dialog = new SaveFileDialog { Filter = "JSON 诊断|*.json", FileName = "edifier-diagnostics.json" };
        if (dialog.ShowDialog(this) != true) { StatusText.Text = "已取消保存。"; return; }
        var report = new { version = "0.3.0", time = DateTimeOffset.Now, preview, hardwareVerified = false, transport = transport?.TransportName, protocol = transport?.Profile, stateUncertain = controller?.StateUncertain, snapshot = controller?.Current, log = LogBox.Text };
        await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(report, HeadsetController.JsonOptions), lifetime.Token); StatusText.Text = "诊断报告已保存，不包含蓝牙地址。";
    });
    private void BluetoothSettings_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
    private void SoundSettings_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
    private void OpenBackups_Click(object sender, RoutedEventArgs e) { Directory.CreateDirectory(BackupDirectory); Process.Start(new ProcessStartInfo("explorer.exe", BackupDirectory) { UseShellExecute = true }); }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true; if (closePending) return; closePending = true;
        IsEnabled = false;
        SystemEvents.UserPreferenceChanged -= ThemePreferenceChanged;
        try { lifetime.Cancel(); if (transport != null) await transport.DisposeAsync(); }
        catch { }
        finally
        {
            closing = true;
            // Cleanup can complete synchronously. Let this Closing event return before closing again.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Close));
        }
    }
    private async Task CapturePreviewAsync()
    {
        string directory = Environment.GetEnvironmentVariable("EDIFIER_PREVIEW_DIR") ?? Path.Combine(AppContext.BaseDirectory, "preview");
        Directory.CreateDirectory(directory);
        var captures = new List<string>();
        var layouts = new List<object>();
        async Task SaveCaptureAsync(string name)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); UpdateLayout();
            var surface = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth + surface.Margin.Left + surface.Margin.Right), (int)Math.Ceiling(surface.ActualHeight + surface.Margin.Top + surface.Margin.Bottom), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(this); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(Path.Combine(directory, name))) encoder.Save(output);
            captures.Add(name);
        }
        await DisconnectAsync(); ApplyTheme("Light"); Tabs.SelectedItem = HomeTab;
        ConnectionText.Text = "打开耳机，点击扫描后选择要连接的设备。";
        StatusText.Text = "就绪 · 当前版本尚未完成实机验证";
        await SaveCaptureAsync("welcome-light.png");
        foreach (var model in new[] { HeadsetModel.W820Gold, HeadsetModel.Lolli, HeadsetModel.W830 })
        {
            await StartPreviewAsync(model);
            layouts.Add(new { model = controller!.Model.Id, tabs = Tabs.Items.OfType<TabItem>().Where(x => x.Visibility == Visibility.Visible).Select(x => x.Header).ToArray(), audioLocation = model.Tws ? "settings" : "home", wearVisible = WearCard.Visibility == Visibility.Visible, cycleVisible = CycleCard.Visibility == Visibility.Visible, touchVisible = ControlTab.Visibility == Visibility.Visible, multipointVisible = MultipointCard.Visibility == Visibility.Visible, multipointSwitchVisible = MultipointPanel.Visibility == Visibility.Visible, multipointGuideVisible = MultipointSetupButton.Visibility == Visibility.Visible });
            foreach (string theme in new[] { "Light", "Dark" })
            {
                ApplyTheme(theme);
                for (int tab = 0; tab < 4; tab++)
                {
                    if (((TabItem)Tabs.Items[tab]).Visibility != Visibility.Visible) continue;
                    Tabs.SelectedIndex = tab;
                    if (((TabItem)Tabs.Items[tab]).Content is ScrollViewer viewer) viewer.ScrollToTop();
                    string name = $"{(model.Tws ? "lolli" : model == HeadsetModel.W830 ? "w830" : "w820")}-{theme.ToLowerInvariant()}-{tab}.png";
                    await SaveCaptureAsync(name);
                }
                string prefix = $"{(model.Tws ? "lolli" : model == HeadsetModel.W830 ? "w830" : "w820")}-{theme.ToLowerInvariant()}";
                Tabs.SelectedItem = SettingsTab;
                ((ScrollViewer)SettingsTab.Content).ScrollToVerticalOffset(420);
                await SaveCaptureAsync(prefix + "-settings-more.png");
                if (model == HeadsetModel.Lolli)
                {
                    Tabs.SelectedItem = ControlTab; ((ScrollViewer)ControlTab.Content).ScrollToBottom();
                    await SaveCaptureAsync(prefix + "-touch-more.png");
                    Tabs.SelectedItem = SettingsTab;
                    foreach (var border in featureSettings.Children.OfType<Border>())
                        if (border.Child is StackPanel panel)
                            foreach (var expand in panel.Children.OfType<Expander>()) expand.IsExpanded = true;
                    ((ScrollViewer)SettingsTab.Content).ScrollToBottom();
                    await SaveCaptureAsync(prefix + "-support.png");
                }
                if (model == HeadsetModel.W830)
                {
                    Tabs.SelectedItem = SoundTab; ((ScrollViewer)SoundTab.Content).ScrollToBottom();
                    await SaveCaptureAsync(prefix + "-equalizer.png");
                }
            }
        }
        foreach (var document in LicenseWindow.Documents) { using var stream = LicenseWindow.OpenDocument(document.Resource); if (stream.Length < 20) throw new InvalidDataException("许可证缺失。"); }
        if (Icon == null || !IsVisible) throw new InvalidOperationException("窗口或图标未加载。");
        File.WriteAllText(Path.Combine(directory, "native-verification.json"), JsonSerializer.Serialize(new { passed = true, captures, layouts, licenses = LicenseWindow.Documents.Length, bluetoothAccessed = false, audioAccessed = false, window = new { Width, Height } }, HeadsetController.JsonOptions));
    }
}
