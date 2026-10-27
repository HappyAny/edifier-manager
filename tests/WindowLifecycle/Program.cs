using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EdifierManager;

internal static class Program
{
    private static readonly List<object> Results = [];
    private static readonly List<string> DispatcherErrors = [];
    private static int failures;

    [STAThread]
    private static int Main()
    {
        var app = new App(); app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (_, e) => { DispatcherErrors.Add(e.Exception.ToString()); e.Handled = true; };
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            foreach (string scenario in new[] { "disconnected", "w820-preview", "lolli-preview", "w830-preview", "repeated-close", "pending-disposal", "failed-disposal" })
                await VerifyCloseAsync(scenario);
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            File.WriteAllText(Path.Combine(root, "docs/window-close-tests.json"), JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, passed = Results.Count - failures, failed = failures, hardwareAccessed = false, results = Results }, HeadsetController.JsonOptions));
            Console.WriteLine($"{Results.Count - failures}/{Results.Count} window lifecycle tests passed");
            app.Dispatcher.InvokeShutdown();
        }));
        Dispatcher.Run();
        return failures == 0 ? 0 : 1;
    }

    private static async Task VerifyCloseAsync(string scenario)
    {
        var window = new MainWindow { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -3000, Top = -3000 };
        string testData = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts", "window-test-" + Guid.NewGuid().ToString("N")));
        typeof(MainWindow).GetField("dataDirectory", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, testData);
        int errorStart = DispatcherErrors.Count, closedCount = 0;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => { closedCount++; closed.TrySetResult(); };
        var transport = scenario is "pending-disposal" or "failed-disposal" ? new ControlledTransport(scenario == "failed-disposal") : null;
        try
        {
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (scenario.EndsWith("preview"))
            {
                var menu = (Button)window.FindName("MenuButton");
                string tag = scenario.Split('-')[0];
                menu.ContextMenu.Items.OfType<MenuItem>().Single(x => (x.Tag as string) == tag).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                if (((TextBlock)window.FindName("DeviceStatus")).Text != "示例") throw new Exception("Preview menu did not open the expected mode.");
                bool multipoint = scenario != "w820-preview";
                if (((Border)window.FindName("MultipointCard")).Visibility == Visibility.Visible != multipoint) throw new Exception("Wrong multipoint card visibility.");
                if (scenario == "lolli-preview")
                {
                    if (((Grid)window.FindName("MultipointPanel")).Visibility != Visibility.Collapsed || ((Button)window.FindName("MultipointSetupButton")).Visibility != Visibility.Visible)
                        throw new Exception("Lolli firmware without a switch must show the pairing guide.");
                    if (!((TextBlock)window.FindName("MultipointHint")).Text.Contains("设备 B")) throw new Exception("Pairing instructions missing.");
                    if (((TabItem)window.FindName("ControlTab")).Visibility != Visibility.Visible) throw new Exception("Lolli touch controls missing.");
                    await VerifyLolliFeaturesAsync(window);
                }
                if (scenario == "w830-preview")
                {
                    await VerifyEqualizerAsync(window);
                    ((TabControl)window.FindName("Tabs")).SelectedItem = window.FindName("SettingsTab");
                    ((CheckBox)window.FindName("MultipointEnabled")).IsChecked = true;
                    ((Button)window.FindName("MultipointApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    for (int i = 0; i < 250 && ((TextBlock)window.FindName("MultipointStatus")).Text != "当前已开启"; i++) await Task.Delay(20);
                    if (((TextBlock)window.FindName("MultipointStatus")).Text != "当前已开启")
                        throw new Exception("Multipoint UI did not apply and read back the state: " + ((TextBlock)window.FindName("StatusText")).Text + "\n" + ((TextBox)window.FindName("LogBox")).Text);
                }
            }
            if (transport != null) typeof(MainWindow).GetField("transport", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, transport);
            window.Close();
            if (scenario is "repeated-close" or "pending-disposal") window.Close();
            if (scenario == "pending-disposal")
            {
                if (closedCount != 0 || transport!.DisposeCount != 1) throw new Exception("Window closed before cleanup or cleanup ran twice.");
                transport.Release.TrySetResult();
            }
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (closedCount != 1 || window.IsVisible || DispatcherErrors.Count != errorStart) throw new Exception("Close raised a dispatcher error or did not finish exactly once.");
            if (transport != null && transport.DisposeCount != 1) throw new Exception("Transport cleanup did not run exactly once.");
            Results.Add(new { scenario, passed = true }); Console.WriteLine("PASS " + scenario);
        }
        catch (Exception ex)
        {
            failures++;
            Results.Add(new { scenario, passed = false, error = ex.ToString(), dispatcherErrors = DispatcherErrors.Skip(errorStart).ToArray() });
            Console.WriteLine("FAIL " + scenario + ": " + ex.Message);
            foreach (string error in DispatcherErrors.Skip(errorStart)) Console.WriteLine(error);
        }
        finally
        {
            transport?.Release.TrySetResult();
            if (closedCount == 0)
            {
                typeof(MainWindow).GetField("closing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                window.Close();
            }
        }
    }

    private static T Private<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject element) foreach (var nested in Descendants(element)) yield return nested;
    }
    private static Button ButtonWithName(MainWindow window, string name) => Descendants(window).OfType<Button>().Single(x => System.Windows.Automation.AutomationProperties.GetName(x) == name);
    private static async Task WaitIdle(MainWindow window)
    {
        for (int i = 0; i < 300 && Private<bool>(window, "busy"); i++) await Task.Delay(20);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (Private<bool>(window, "busy")) throw new Exception("UI operation did not finish.");
        if (Private<HeadsetController>(window, "controller").StateUncertain) throw new Exception("UI write failed: " + ((TextBlock)window.FindName("StatusText")).Text);
    }
    private static async Task VerifyLolliFeaturesAsync(MainWindow window)
    {
        var tabs = (TabControl)window.FindName("Tabs"); tabs.SelectedItem = window.FindName("ControlTab");
        var choices = Private<Dictionary<string, ComboBox>>(window, "touchChoices");
        if (choices.Count != 6) throw new Exception("Six Lolli gestures were not rendered.");
        choices["touch1"].SelectedItem = choices["touch1"].Items.Cast<Choice>().First(x => x.Value == 5);
        var cycles = Private<Dictionary<int, CheckBox[]>>(window, "touchCycles");
        cycles[1][0].IsChecked = true; cycles[1][1].IsChecked = true; cycles[1][2].IsChecked = false;
        ((Button)window.FindName("TouchApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitIdle(window);
        var controller = Private<HeadsetController>(window, "controller");
        if (controller.Current!.Settings["touch1"] != 5 || controller.Current.Settings["cycle1"] != 3) throw new Exception("Nested touch UI did not apply.");
        tabs.SelectedItem = window.FindName("SettingsTab");
        var prompt = Descendants(window).OfType<Slider>().Single(x => System.Windows.Automation.AutomationProperties.GetName(x) == "提示音量");
        prompt.Value = 10; ButtonWithName(window, "应用提示音量").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitIdle(window);
        if (controller.Current.Settings["beep2"] != 10) throw new Exception("New prompt volume UI did not apply.");
        var name = Descendants(window).OfType<TextBox>().Single(x => System.Windows.Automation.AutomationProperties.GetName(x) == "设备名称");
        name.Text = "喵喵测试耳机"; ButtonWithName(window, "保存名称").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitIdle(window);
        if (controller.Current.DeviceName != "喵喵测试耳机") throw new Exception("Rename UI did not apply.");
        var minutes = Descendants(window).OfType<ComboBox>().Single(x => System.Windows.Automation.AutomationProperties.GetName(x) == "定时关机分钟数");
        minutes.Text = "90"; ButtonWithName(window, "设置定时").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitIdle(window);
        if (controller.Current.ShutdownMinutes != 90) throw new Exception("Timer UI did not apply.");
        var eq = (ComboBox)window.FindName("EqChoice"); eq.SelectedItem = eq.Items.Cast<Choice>().First(x => x.Value == 4);
        typeof(MainWindow).GetMethod("EqApply_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]); await WaitIdle(window);
        if (controller.Current.Settings["eq"] != 4) throw new Exception("Five-preset EQ UI did not apply.");
    }
    private static async Task VerifyEqualizerAsync(MainWindow window)
    {
        ((TabControl)window.FindName("Tabs")).SelectedItem = window.FindName("SoundTab");
        var gain = Descendants(window).OfType<ComboBox>().Single(x => System.Windows.Automation.AutomationProperties.GetName(x) == "EQ 第 1 段增益");
        gain.SelectedItem = gain.Items.Cast<Choice>().First(x => x.Value == 8);
        ButtonWithName(window, "应用均衡器").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitIdle(window);
        if (Private<HeadsetController>(window, "controller").Current!.CustomEq!.Bands[0].Gain != 8) throw new Exception("Custom EQ editor did not apply.");
    }
    private sealed class ControlledTransport(bool fail) : ICommandTransport
    {
        public bool Connected => true;
        public string TransportName => "lifecycle test";
        public WireProfile Profile => new();
        public int DisposeCount { get; private set; }
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<byte[]?> RequestAsync(byte command, byte[]? payload = null, bool optional = false, CancellationToken token = default) => throw new InvalidOperationException("This test must not send commands.");
        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (fail) throw new IOException("Simulated cleanup failure.");
            await Release.Task;
        }
    }
}
