using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;

namespace EdifierManager;

public partial class MainWindow
{
    private readonly StackPanel featureSettings = new(), featureSound = new(), featureControls = new();
    private readonly Dictionary<int, CheckBox[]> touchCycles = [];
    private readonly List<FrameworkElement> featureEditors = [];
    private readonly List<(TextBox Frequency, ComboBox Gain, TextBox Q)> eqEditors = [];
    private CancellationTokenSource? findCancellation;
    private TextBlock? findStatus;
    private void InitializeFeatures()
    {
        SettingsStack.Children.Insert(2, featureSettings);
        SoundStack.Children.Insert(3, featureSound);
        ControlStack.Children.Insert(1, featureControls);
    }
    private void ClearFeatures()
    {
        featureSettings.Children.Clear(); featureSound.Children.Clear(); featureControls.Children.Clear();
        featureEditors.Clear(); eqEditors.Clear(); findStatus = null;
    }
    private void UpdateFeatureEnabled(bool ready)
    {
        foreach (var item in featureEditors) item.IsEnabled = ready;
    }
    private TextBlock Hint(string text) => new() { Text = text, Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 6, 0, 0) };
    private StackPanel FeatureCard(Panel host, string title)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Heading") });
        host.Children.Add(new Border { Style = (Style)FindResource("Card"), Child = content });
        return content;
    }
    private Button FeatureButton(string title, Func<Task> operation, bool editor = true)
    {
        var button = new Button { Content = title, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 6, 0) };
        AutomationProperties.SetName(button, title);
        button.Click += async (_, _) => await RunAsync("正在" + title + "…", operation);
        if (editor) featureEditors.Add(button);
        return button;
    }
    private void AddTouchCycleControls(int slot, ComboBox choice, HeadsetSnapshot state, Capabilities caps)
    {
        if (caps.TouchGroup != 30) return;
        var panel = new StackPanel { Margin = new Thickness(115, 0, 0, 12) };
        panel.Children.Add(Hint("切换时经过的模式（选两至三项）"));
        var wrap = new WrapPanel(); panel.Children.Add(wrap);
        // The protocol uses the order in the ANC option list as the bit index.
        int mask = state.Settings.GetValueOrDefault("cycle" + slot, (1 << caps.AncOptions.Count) - 1);
        var boxes = caps.AncOptions.Select((option, index) => new CheckBox { Content = option.Label, IsChecked = (mask & (1 << index)) != 0, Margin = new Thickness(0, 5, 12, 0) }).ToArray();
        foreach (var box in boxes) { AutomationProperties.SetName(box, Capabilities.TouchLabel(slot) + "循环" + box.Content); wrap.Children.Add(box); }
        touchCycles[slot] = boxes; TouchRows.Children.Add(panel);
        void Update() => panel.Visibility = (choice.SelectedItem as Choice)?.Value == 5 ? Visibility.Visible : Visibility.Collapsed;
        choice.SelectionChanged += (_, _) => Update(); Update();
    }
    private void AddBoolean(Panel host, string key, string label, int value)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var check = new CheckBox { Content = label, IsChecked = value == 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var button = FeatureButton("应用", async () => await ApplyAsync(new() { [key] = check.IsChecked == true ? 1 : 0 }));
        AutomationProperties.SetName(button, "应用" + label); button.Margin = new Thickness(0);
        Grid.SetColumn(button, 1); grid.Children.Add(check); grid.Children.Add(button); host.Children.Add(grid); featureEditors.Add(check);
    }
    private void AddRange(Panel host, string key, string label, int value, int min, int max)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = new GridLength(46) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var caption = new TextBlock { Text = value.ToString(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(slider, label); slider.ValueChanged += (_, _) => caption.Text = ((int)slider.Value).ToString();
        var button = FeatureButton("应用", async () => await ApplyAsync(new() { [key] = (int)slider.Value }));
        AutomationProperties.SetName(button, "应用" + label); button.Margin = new Thickness(0);
        Grid.SetColumn(caption, 1); Grid.SetColumn(button, 2); grid.Children.Add(slider); grid.Children.Add(caption); grid.Children.Add(button);
        host.Children.Add(Hint(label)); host.Children.Add(grid); featureEditors.Add(slider);
    }
    private void RenderFeatures(HeadsetSnapshot state, Capabilities caps)
    {
        ClearFeatures();
        if (state.DeviceName != null)
        {
            var card = FeatureCard(featureSettings, "设备名称");
            var name = new TextBox { Text = state.DeviceName, MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center, IsReadOnly = caps.NameLimit == 0 };
            AutomationProperties.SetName(name, "设备名称"); card.Children.Add(name); featureEditors.Add(name);
            if (caps.NameLimit > 0)
            {
                card.Children.Add(FeatureButton("保存名称", async () =>
                {
                    RequireController();
                    await controller!.RenameAsync(name.Text.Trim(), lifetime.Token); RenderState();
                    StatusText.Text = "名称已读回确认 · Windows 显示可能需重新配对后更新";
                }));
                card.Children.Add(Hint($"最多 {caps.NameLimit} 个 UTF-8 字节。工具会记住设备身份，改名后仍可识别。"));
            }
        }
        var beepOptions = FeatureProtocol.BeepOptions(state.BeepGroup).Where(x => state.Settings.ContainsKey("beep" + x.Position)).ToArray();
        if (beepOptions.Length > 0 || state.Settings.ContainsKey("promptSwitch"))
        {
            var card = FeatureCard(featureSettings, "提示音");
            foreach (var option in beepOptions)
            {
                string key = "beep" + option.Position; int value = state.Settings[key];
                if (option.Maximum == 1) AddBoolean(card, key, option.Label, value);
                else AddRange(card, key, option.Label, value, option.Minimum, option.Maximum);
            }
            if (state.Settings.TryGetValue("promptSwitch", out int enabled)) AddBoolean(card, "promptSwitch", "耳机提示音", enabled);
        }
        if (state.Settings.TryGetValue("sensitivity", out int sensitivity))
        {
            var card = FeatureCard(featureControls, "触控灵敏度");
            AddRange(card, "sensitivity", "灵敏度", sensitivity, 0, state.SensitivityIndex == 2 ? 15 : 31);
            card.Children.Add(Hint("双耳耳机需左右耳同时在线。数值范围由当前耳机返回的类型决定。"));
        }
        if (state.ShutdownMinutes.HasValue || state.Settings.ContainsKey("idleShutdown"))
        {
            var card = FeatureCard(featureSettings, "定时与电源");
            if (state.ShutdownMinutes is int remaining)
            {
                card.Children.Add(Hint(remaining == 0 ? "定时关机未开启" : $"剩余约 {remaining} 分钟"));
                var row = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
                var minutes = new ComboBox { MinWidth = 130, MinHeight = 32, IsEditable = true, ItemsSource = new[] { "5", "10", "15", "20", "30", "60", "90", "120" }, Text = remaining > 0 ? remaining.ToString() : "30", VerticalContentAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(minutes, "定时关机分钟数"); row.Children.Add(minutes); featureEditors.Add(minutes);
                var apply = FeatureButton("设置定时", async () =>
                {
                    if (!int.TryParse(minutes.Text, out int value) || value is < 1 or > 65535) throw new InvalidDataException("请输入 1–65535 分钟。");
                    await controller!.SetShutdownTimerAsync(value, lifetime.Token); RenderState(); StatusText.Text = "定时关机已读回确认";
                }); apply.Margin = new Thickness(6, 0, 0, 0); row.Children.Add(apply);
                var cancel = FeatureButton("取消定时", async () => { await controller!.SetShutdownTimerAsync(0, lifetime.Token); RenderState(); StatusText.Text = "定时关机已取消并读回确认"; });
                cancel.Margin = new Thickness(6, 0, 0, 0); row.Children.Add(cancel); card.Children.Add(row);
            }
            if (state.Settings.TryGetValue("idleShutdown", out int idle)) AddBoolean(card, "idleShutdown", "无音频输入时自动关机", idle);
            card.Children.Add(Hint("定时保存在耳机中，关闭本工具仍会生效。恢复设置备份不会重新启动倒计时。"));
        }
        if (state.Settings.TryGetValue("wind", out int wind)) AddBoolean(FeatureCard(featureSound, "风噪控制"), "wind", "防风噪", wind);
        if (state.Settings.TryGetValue("ldac", out int ldac))
        {
            var card = FeatureCard(featureSound, "LDAC 音质");
            Choice[] options = [new(0, "关闭 LDAC"), new(1, "LDAC · 48 kHz"), new(2, "LDAC · 96 kHz")];
            var choice = new ComboBox { ItemsSource = options, SelectedItem = options.First(x => x.Value == ldac), MinHeight = 32 };
            AutomationProperties.SetName(choice, "LDAC 音质"); card.Children.Add(choice); featureEditors.Add(choice);
            card.Children.Add(Hint("修改可能中断连接或需要重新配对；实际使用的编解码器还取决于发送端支持。"));
            card.Children.Add(FeatureButton("应用 LDAC", async () =>
            {
                if (choice.SelectedItem is not Choice option) return;
                if (MessageBox.Show(this, "此操作可能让耳机断开或需要重新配对。继续修改 LDAC？", "修改音质", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                bool confirmed = await controller!.SetLdacAsync(option.Value, lifetime.Token);
                if (confirmed) { RenderState(); StatusText.Text = "LDAC 设置已读回确认"; }
                else { await DisconnectAsync(); StatusText.Text = "LDAC 修改待重连确认，请重新连接读取；尚未确认生效"; }
            }));
        }
        if (state.CustomEq != null) RenderCustomEqualizer(state.CustomEq);
        RenderFindCard();
        RenderDeviceActions(caps);
        RenderCoverage(state, caps);
    }
    private void RenderCustomEqualizer(CustomEqualizer eq)
    {
        var card = FeatureCard(featureSound, "自定义均衡器");
        card.Children.Add(Hint("调节后点击应用。声音预设选为“自定义”时生效；固定参数只读显示。"));
        var headings = new Grid { Margin = new Thickness(0, 10, 0, 5) };
        foreach (int width in new[] { 28, 150, 130, 80 }) headings.ColumnDefinitions.Add(new() { Width = width == 28 ? new GridLength(width) : new GridLength(width, GridUnitType.Star) });
        foreach (var (text, i) in new[] { ("段", 0), ("频率 / Hz", 1), ("增益 / dB", 2), ("Q", 3) }) { var label = new TextBlock { Text = text }; Grid.SetColumn(label, i); headings.Children.Add(label); }
        card.Children.Add(headings);
        foreach (var band in eq.Bands)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            foreach (int width in new[] { 28, 150, 130, 80 }) row.ColumnDefinitions.Add(new() { Width = width == 28 ? new GridLength(width) : new GridLength(width, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = (band.Index + 1).ToString(), VerticalAlignment = VerticalAlignment.Center });
            var frequency = new TextBox { Text = band.Frequency.ToString(), IsReadOnly = !eq.AdjustableFrequency, MinHeight = 30, Margin = new Thickness(0, 0, 8, 0) };
            var options = Enumerable.Range(0, (int)Math.Round((eq.MaximumGain - eq.MinimumGain) / eq.GainStep) + 1).Select(i => new Choice(i * eq.GainMultiplier, (eq.MinimumGain + i * eq.GainStep).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture))).ToArray();
            var gain = new ComboBox { ItemsSource = options, SelectedItem = options.First(x => x.Value == band.Gain), MinHeight = 30, Margin = new Thickness(0, 0, 8, 0) };
            var q = new TextBox { Text = eq.HasQ ? (band.Q / 10.0).ToString("0.0", CultureInfo.InvariantCulture) : "—", IsReadOnly = !eq.AdjustableQ, MinHeight = 30 };
            foreach (var (item, column, label) in new (FrameworkElement, int, string)[] { (frequency, 1, "频率"), (gain, 2, "增益"), (q, 3, "Q 值") })
            { Grid.SetColumn(item, column); AutomationProperties.SetName(item, $"EQ 第 {band.Index + 1} 段{label}"); row.Children.Add(item); featureEditors.Add(item); }
            eqEditors.Add((frequency, gain, q)); card.Children.Add(row);
        }
        var buttons = new WrapPanel(); card.Children.Add(buttons);
        buttons.Children.Add(FeatureButton("应用均衡器", async () =>
        {
            var bands = eq.Bands.Select((old, i) =>
            {
                var editors = eqEditors[i];
                if (!int.TryParse(editors.Frequency.Text, out int frequency) || editors.Gain.SelectedItem is not Choice gain) throw new InvalidDataException("请检查频率和增益。");
                int q = old.Q;
                if (eq.AdjustableQ)
                {
                    if (!double.TryParse(editors.Q.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || Math.Abs(value * 10 - Math.Round(value * 10)) > .0001) throw new InvalidDataException("Q 值应保留一位小数。");
                    q = (int)Math.Round(value * 10);
                }
                return old with { Frequency = frequency, Gain = gain.Value, Q = q };
            }).ToArray();
            await controller!.ApplyCustomEqAsync(eq with { Bands = bands }, lifetime.Token); RenderState(); StatusText.Text = "自定义 EQ 已逐段读回确认";
        }));
        buttons.Children.Add(FeatureButton("恢复默认 EQ", async () =>
        {
            if (MessageBox.Show(this, "恢复耳机的默认自定义 EQ？当前配置会先自动备份。", "恢复 EQ", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await controller!.ResetCustomEqAsync(lifetime.Token); RenderState(); StatusText.Text = "默认 EQ 已重新读取，增益归零已确认";
        }));
        buttons.Children.Add(FeatureButton("导出 EQ", async () =>
        {
            var dialog = new SaveFileDialog { Filter = "EQ JSON|*.json", FileName = "edifier-eq.json" };
            if (dialog.ShowDialog(this) != true) return;
            await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(eq, HeadsetController.JsonOptions)); StatusText.Text = "EQ 已导出";
        }));
        buttons.Children.Add(FeatureButton("导入 EQ 预览", async () =>
        {
            var dialog = new OpenFileDialog { Filter = "EQ JSON|*.json" }; if (dialog.ShowDialog(this) != true) return;
            if (new FileInfo(dialog.FileName).Length > 16000) throw new InvalidDataException("EQ 文件过大。");
            var importedEq = JsonSerializer.Deserialize<CustomEqualizer>(await File.ReadAllTextAsync(dialog.FileName)) ?? throw new InvalidDataException("EQ 文件无效。");
            importedEq.ValidateAgainst(eq);
            for (int i = 0; i < eqEditors.Count; i++)
            { var b = importedEq.Bands[i]; var row = eqEditors[i]; row.Frequency.Text = b.Frequency.ToString(); row.Q.Text = eq.HasQ ? (b.Q / 10.0).ToString("0.0", CultureInfo.InvariantCulture) : "—"; row.Gain.SelectedItem = row.Gain.Items.Cast<Choice>().First(x => x.Value == b.Gain); }
            StatusText.Text = "EQ 已载入编辑器，点击“应用均衡器”后才会写入";
        }));
    }
    private void RenderDeviceActions(Capabilities caps)
    {
        var definitions = new[]
        {
            ("disconnect", "断开音频连接", "耳机会断开当前蓝牙音频连接。"),
            ("pair", "进入配对模式", "耳机将进入配对模式，当前连接可能中断。"),
            ("poweroff", "关闭耳机", "耳机会关机，之后需要手动重新开机。"),
            ("clear-pairing", "清除配对记录", "耳机会忘记已配对设备，之后需要重新配对手机和电脑。"),
            ("reset", "恢复出厂设置", "耳机的自定义设置和配对记录可能被清除，之后需要重新配对。")
        }.Where(x => caps.CanDeviceAction(x.Item1)).ToArray();
        if (definitions.Length == 0) return;
        var card = FeatureCard(featureSettings, "设备管理");
        var expand = new Expander { Header = "配对、电源与重置操作" }; var content = new StackPanel(); expand.Content = content; card.Children.Add(expand);
        foreach (var (action, title, explanation) in definitions)
            content.Children.Add(FeatureButton(title, async () =>
            {
                if (MessageBox.Show(this, explanation + "\n\n确认" + title + "？", title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                bool acknowledged = await controller!.DeviceActionAsync(action, lifetime.Token);
                await DisconnectAsync();
                StatusText.Text = acknowledged ? $"耳机已应答“{title}”，请检查耳机状态" : $"“{title}”已尝试发送，但未收到应答；请检查耳机状态，尚未确认完成";
            }));
    }
    private void RenderCoverage(HeadsetSnapshot state, Capabilities caps)
    {
        var card = FeatureCard(featureSettings, "功能与支持");
        string Status(bool supported, bool read) => read ? "可用" : supported ? "未读到已识别状态" : "耳机未上报";
        var entries = new[]
        {
            "设备名称 · " + Status(caps.QueryName, state.DeviceName != null),
            "EQ 预设 · " + Status(caps.EqGroup > 0, state.Settings.ContainsKey("eq")),
            "自定义 EQ · " + Status(caps.EqOptions.Any(x => x.Label.Contains("自定义")), state.CustomEq != null),
            "触控配置 · " + Status(caps.TouchGroup > 0 && controller!.Model.Tws, state.Settings.Keys.Any(x => x.StartsWith("touch"))),
            "提示音 · " + Status(caps.NewPrompt || caps.Prompt || caps.PromptSwitch, state.Settings.Keys.Any(x => x.StartsWith("beep") || x.StartsWith("prompt"))),
            "灵敏度 · " + Status(caps.Sensitivity, state.Settings.ContainsKey("sensitivity")),
            "定时关机 · " + Status(caps.Timer, state.ShutdownMinutes.HasValue),
            "LDAC · " + Status(caps.Ldac, state.Settings.ContainsKey("ldac"))
        };
        var expand = new Expander { Header = "查看当前耳机的功能状态", Content = Hint(string.Join("\n", entries)) }; card.Children.Add(expand);
        card.Children.Add(Hint("固件升级、云端 AI 翻译与语音包下载请使用 EDIFIER Connect 官方 App。未读出的功能可保存诊断后检查。"));
        string manual = controller!.Model.Id switch
        {
            "lolli-anc" => "https://cn-oss.edifier.com/Uploads/images/2025/07/22/2025072215432017531702001860.pdf",
            "w830nb" => "https://www.edifier.com/cn/instruction/W830NB.html",
            "w820nb-double-gold" => "https://www.edifier.com/cn/instruction/W820NBDoubleGold.html",
            _ => "https://www.edifier.com/cn/instruction/W820NB.html"
        };
        card.Children.Add(FeatureButton("打开这款耳机的官方说明书", () => { Process.Start(new ProcessStartInfo(manual) { UseShellExecute = true }); return Task.CompletedTask; }, false));
        card.Children.Add(FeatureButton("打开官方 App 下载页", () => { Process.Start(new ProcessStartInfo("https://admin-app.edifier.com/edifier_provider/public/appDownload?appTag=EdifierConnect") { UseShellExecute = true }); return Task.CompletedTask; }, false));
    }
    private void RenderFindCard()
    {
        var card = FeatureCard(featureSettings, "寻找耳机");
        card.Children.Add(Hint("通过这副耳机的 Windows 音频输出播放 8 秒提示音。请先摘下耳机；需保持音频连接，无法寻找已关机或超出蓝牙范围的耳机。"));
        findStatus = Hint("不会更改系统音量。可先在音频卡片中确认音量与静音状态。"); card.Children.Add(findStatus);
        var buttons = new WrapPanel(); card.Children.Add(buttons);
        foreach (var (label, channel) in controller!.Model.Tws ? new[] { ("找左耳", 1), ("找右耳", 2), ("找双耳", 3) } : new[] { ("播放寻找提示音", 3) })
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 8, 6, 0) };
            button.Click += async (_, _) => await FindHeadsetAsync(channel); buttons.Children.Add(button); featureEditors.Add(button);
        }
        var stop = new Button { Content = "停止提示音", Margin = new Thickness(0, 8, 0, 0) };
        stop.Click += (_, _) => findCancellation?.Cancel(); buttons.Children.Add(stop);
    }
    private async Task FindHeadsetAsync(int channel)
    {
        if (findCancellation != null || busy || closing) return;
        if (MessageBox.Show(this, "请先摘下耳机。接下来会向选定耳机播放 8 秒提示音。", "寻找耳机", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); findCancellation = cancel;
        try
        {
            if (preview) { if (findStatus != null) findStatus.Text = "离线预览不会播放声音。"; return; }
            string name = (Devices.SelectedItem as DiscoveredHeadset)?.Name ?? throw new IOException("请选择真实耳机。");
            var endpoints = WindowsAudio.List(name).Where(x => !x.IsMicrophone).ToArray();
            var selected = (AudioDevices.SelectedItem as AudioEndpoint) is { IsMicrophone: false } endpoint ? endpoints.FirstOrDefault(x => x.Id == endpoint.Id) : endpoints.Length == 1 ? endpoints[0] : null;
            if (selected == null) throw new IOException("请先在音频卡片选择这副耳机的播放端点。");
            if (selected.Muted || selected.Volume == 0) throw new IOException("耳机当前静音或音量为零，请先调整音量。");
            if (findStatus != null) findStatus.Text = "提示音播放中 · 8 秒后自动停止";
            await HeadsetFinder.PlayAsync(selected.Id, channel, cancel.Token);
            if (findStatus != null) findStatus.Text = "提示音已停止";
        }
        catch (OperationCanceledException) { if (findStatus != null) findStatus.Text = "提示音已停止"; }
        catch (Exception ex) { if (findStatus != null) findStatus.Text = ex.Message; Log(ex.Message); }
        finally { findCancellation = null; }
    }
}
