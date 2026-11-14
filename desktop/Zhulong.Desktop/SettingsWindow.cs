using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Zhulong.Desktop;

// 两个设置页编辑同一草稿，最终仍由中央验证与原子保存。
internal sealed class SettingsWindow : Window
{
    static readonly JsonSerializerOptions Format = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    readonly SettingsStore store;
    readonly string? openedHash;
    readonly TabControl tabs = new();
    readonly TextBox editor = new();
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    readonly Dictionary<string, TextBox> numbers = new();
    readonly CheckBox notifications = new(), pingEnabled = new(), automatic = new(), throttle = new(), memoryTrim = new();
    readonly TextBox pingHost = new();
    RuntimeSettings draft = new();
    int activeTab;
    bool changingTab;

    public SettingsWindow(string directory, IReadOnlyList<(string Group, string Label, Func<Task> Run)>? commands = null)
    {
        store = new SettingsStore(directory);
        var bytes = File.Exists(store.PathName) ? File.ReadAllBytes(store.PathName) : null;
        openedHash = Hash(bytes);
        var original = bytes is null ? Serialize(draft) : Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        string? initialError = null;
        try { draft = Parse(original); }
        catch (Exception error) when (IsInputError(error)) { initialError = error.Message; }
        Title = "烛龙新版 · 设置";
        Width = 880; Height = 800; MinWidth = 700; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(246, 243, 235));
        Foreground = new SolidColorBrush(Color.FromRgb(48, 55, 49));
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14;

        var layout = new DockPanel { Margin = new Thickness(28, 24, 28, 24) };
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        heading.Children.Add(new TextBlock { Text = "调息与守望", FontSize = 24, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(Note("设置保存后，从托盘重启新版生效。自动处置、内存整理与限流默认关闭。"));
        DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading);

        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var reset = new Button { Content = "默认值放入编辑区", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 10, 0) };
        var save = new Button { Content = "保存设置", Padding = new Thickness(22, 7, 22, 7), IsDefault = true };
        buttons.Children.Add(reset); buttons.Children.Add(save);
        DockPanel.SetDock(buttons, Dock.Top); footer.Children.Add(buttons); footer.Children.Add(status);
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);

        var hasTools = commands is { Count: > 0 };
        var basic = BuildBasic(hasTools);
        editor.AcceptsReturn = true; editor.AcceptsTab = true;
        editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        editor.FontFamily = new FontFamily("Consolas"); editor.FontSize = 13; editor.Padding = new Thickness(12);
        editor.Background = Brushes.White; editor.Text = original;
        System.Windows.Automation.AutomationProperties.SetName(editor, "高级设置 JSON");
        System.Windows.Automation.AutomationProperties.SetName(tabs, "设置页面");
        var advanced = new DockPanel { Margin = new Thickness(14) };
        var explanation = Note("完整策略、进程路径与服务触发名单在这里编辑。字段名需与当前格式一致；格式或策略错误时保留本页供修正。");
        explanation.Margin = new Thickness(0, 0, 0, 12); DockPanel.SetDock(explanation, Dock.Top);
        advanced.Children.Add(explanation); advanced.Children.Add(editor);
        tabs.Items.Add(new TabItem { Header = "基础设置", Content = basic, Padding = new Thickness(18, 8, 18, 8) });
        tabs.Items.Add(new TabItem { Header = "高级 JSON", Content = advanced, Padding = new Thickness(18, 8, 18, 8) });
        if (commands is { Count: > 0 })
            tabs.Items.Add(new TabItem { Header = "工具与维护", Content = BuildTools(commands), Padding = new Thickness(18, 8, 18, 8) });
        layout.Children.Add(tabs); Content = layout;
        FillBasic(draft);
        activeTab = initialError is null ? 0 : 1; tabs.SelectedIndex = activeTab;
        if (initialError is null) ShowStatus("白名单和路径策略可在高级页调整；切换页面会保留有效编辑。", false);
        else ShowStatus("当前设置需要修正：" + initialError, true);
        tabs.SelectionChanged += (_, args) =>
        {
            if (!ReferenceEquals(args.Source, tabs)) return;
            var editing = tabs.SelectedIndex is 0 or 1;
            footer.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            if (editing) ChangeEditorPage();
        };
        reset.Click += (_, _) =>
        {
            draft = new RuntimeSettings(); FillBasic(draft); editor.Text = Serialize(draft);
            ShowStatus("默认值已放入编辑区，尚未保存。", false);
        };
        save.Click += (_, _) => Save();
    }

    ScrollViewer BuildBasic(bool hasTools)
    {
        var panel = new StackPanel { Margin = new Thickness(18, 12, 18, 18) };
        Section(panel, "采样与记录");
        Numeric(panel, "sample", "采样间隔（秒）", "5–300秒；数值越小，采集越频繁。");
        Numeric(panel, "retention", "记录保留（天）", "1–365天；清理新版过期采样与事件，主动导入的原始历史保留。");
        Toggle(panel, notifications, "显示系统提醒", hasTools ? "通知与采样独立；可在工具与维护中临时静音。" : "通知与采样独立。");
        Section(panel, "网络探测");
        Toggle(panel, pingEnabled, "探测网关与公网延迟", "低频发送ICMP探测；关闭后仍可统计本地网络速率。");
        Row(panel, "公网目标 IP", pingHost, "填写有效IP地址，例如223.5.5.5。");
        pingEnabled.Checked += (_, _) => pingHost.IsEnabled = true;
        pingEnabled.Unchecked += (_, _) => pingHost.IsEnabled = false;
        Section(panel, "受限处置");
        Toggle(panel, automatic, "自动执行白名单处置", "按显式策略结束泄漏进程或重启服务；保存时需要原生确认。");
        Toggle(panel, memoryTrim, "允许白名单内存整理", "仍需自动处置总开关、压力门槛与名单；只回收工作集，不降低提交额度。");
        Toggle(panel, throttle, "启用限流与错峰绑核", "独立开关；仅按已配置名单调整优先级与CPU核组。");
        var thresholds = new StackPanel { Margin = new Thickness(0, 8, 0, 4) };
        Numeric(thresholds, "commitEnter", "提交水位进入（%）", "达到此值并持续足够时间后提醒。");
        Numeric(thresholds, "commitExit", "提交水位解除（%）", "须低于进入水位，形成迟滞以避免反复提醒。");
        Numeric(thresholds, "commitSeconds", "水位持续时长（秒）", "默认90秒。");
        Numeric(thresholds, "cooldown", "同规则冷却（秒）", "默认600秒。");
        Numeric(thresholds, "coreEnter", "单核饱和水位（%）", "最忙逻辑核的占用率，默认95%。");
        Numeric(thresholds, "coreAverage", "整机占用低于（%）", "与单核条件同时满足，默认70%。");
        Numeric(thresholds, "coreSeconds", "单核持续时长（秒）", "默认90秒。");
        Numeric(thresholds, "trimPressure", "整理压力门槛（%）", "物理内存达到此值才考虑自动整理，默认60%。");
        panel.Children.Add(new Expander { Header = "告警与整理门槛", Content = thresholds, Margin = new Thickness(0, 16, 0, 0) });
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    static ScrollViewer BuildTools(IReadOnlyList<(string Group, string Label, Func<Task> Run)> commands)
    {
        var panel = new StackPanel { Margin = new Thickness(18, 12, 18, 18) };
        panel.Children.Add(Note("限流操作会立即更新配置。有未保存的编辑时，请先保存，再重新打开设置使用相应工具。"));
        foreach (var group in commands.GroupBy(command => command.Group))
        {
            Section(panel, group.Key);
            var actions = new WrapPanel();
            foreach (var command in group)
            {
                var button = new Button
                {
                    Content = command.Label, MinWidth = 132, MinHeight = 34,
                    Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 5, 12, 5)
                };
                button.Click += async (_, _) =>
                {
                    button.IsEnabled = false;
                    try { await command.Run(); }
                    finally { button.IsEnabled = true; }
                };
                actions.Children.Add(button);
            }
            panel.Children.Add(actions);
        }
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.FromRgb(100, 106, 96)), FontSize = 12, Margin = new Thickness(0, 5, 0, 0) };
    static void Section(Panel panel, string title) => panel.Children.Add(new TextBlock
        { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 8) });
    void Numeric(Panel panel, string key, string label, string note)
    {
        var input = new TextBox { Width = 128, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 5, 8, 5) };
        numbers[key] = input; Row(panel, label, input, note);
    }
    static void Row(Panel panel, string label, Control input, string note)
    {
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        var row = new Grid { Margin = new Thickness(0, 4, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 12, 0) });
        var fields = new StackPanel(); fields.Children.Add(input); fields.Children.Add(Note(note));
        Grid.SetColumn(fields, 1); row.Children.Add(fields); panel.Children.Add(row);
    }
    static void Toggle(Panel panel, CheckBox checkbox, string label, string note)
    {
        checkbox.Content = label; checkbox.Margin = new Thickness(0, 6, 0, 2);
        panel.Children.Add(checkbox); panel.Children.Add(Note(note));
    }

    void FillBasic(RuntimeSettings value)
    {
        void Set(string key, object number) => numbers[key].Text = Convert.ToString(number, CultureInfo.InvariantCulture) ?? "";
        Set("sample", value.SampleSeconds); Set("retention", value.RetentionDays);
        Set("commitEnter", value.Alerts.CommitEnter); Set("commitExit", value.Alerts.CommitExit);
        Set("commitSeconds", value.Alerts.CommitSeconds); Set("cooldown", value.Alerts.CooldownSeconds);
        Set("coreEnter", value.Alerts.SingleCoreEnter); Set("coreAverage", value.Alerts.SingleCoreAverageBelow);
        Set("coreSeconds", value.Alerts.SingleCoreSeconds); Set("trimPressure", value.TrimPressurePercent);
        notifications.IsChecked = value.Notifications; pingEnabled.IsChecked = value.PingEnabled; pingHost.Text = value.PingHost;
        pingHost.IsEnabled = value.PingEnabled; automatic.IsChecked = value.AutomaticActions;
        throttle.IsChecked = value.ThrottleEnabled; memoryTrim.IsChecked = value.MemoryTrimEnabled;
    }
    RuntimeSettings ReadBasic()
    {
        var value = Parse(Serialize(draft));
        int Integer(string key, string label) => int.TryParse(numbers[key].Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number : throw new ArgumentException(label + "须填写整数。");
        double Number(string key, string label) => double.TryParse(numbers[key].Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number : throw new ArgumentException(label + "须填写有效数字。");
        value.SampleSeconds = Integer("sample", "采样间隔"); value.RetentionDays = Integer("retention", "记录保留天数");
        value.Notifications = notifications.IsChecked == true; value.PingEnabled = pingEnabled.IsChecked == true; value.PingHost = pingHost.Text.Trim();
        value.AutomaticActions = automatic.IsChecked == true; value.ThrottleEnabled = throttle.IsChecked == true; value.MemoryTrimEnabled = memoryTrim.IsChecked == true;
        value.Alerts = new AlertPolicy(Number("commitEnter", "进入水位"), Number("commitExit", "解除水位"), Integer("commitSeconds", "水位持续时长"),
            Integer("cooldown", "规则冷却"), Number("coreEnter", "单核水位"), Number("coreAverage", "整机水位"), Integer("coreSeconds", "单核持续时长"));
        value.TrimPressurePercent = Number("trimPressure", "整理压力门槛");
        SettingsStore.Validate(value); return value;
    }
    void ChangeEditorPage()
    {
        if (changingTab || tabs.SelectedIndex == activeTab) return;
        try
        {
            if (activeTab == 0) { draft = ReadBasic(); editor.Text = Serialize(draft); }
            else { var value = Parse(editor.Text); FillBasic(value); draft = value; }
            activeTab = tabs.SelectedIndex;
            ShowStatus("编辑已同步；保存后下次启动生效。", false);
        }
        catch (Exception error) when (IsInputError(error))
        {
            changingTab = true; tabs.SelectedIndex = activeTab; changingTab = false;
            ShowStatus(error.Message, true);
            if (activeTab == 1) editor.Focus();
        }
    }
    void Save()
    {
        try
        {
            var value = activeTab == 0 ? ReadBasic() : Parse(editor.Text);
            CheckUnchanged();
            if (value.AutomaticActions && MessageBox.Show(this,
                "启用自动处置后，新版可对显式白名单执行进程结束或服务重启。请核对高级页的路径、阈值与频次。是否保存？",
                "确认自动处置", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            CheckUnchanged();
            store.Save(value);
            MessageBox.Show(this, "设置已保存；从托盘重启新版后生效。", "烛龙新版"); Close();
        }
        catch (Exception error) when (IsInputError(error) || error is IOException or UnauthorizedAccessException)
        { ShowStatus("设置未保存：" + error.Message, true); }
    }
    void CheckUnchanged()
    {
        var current = File.Exists(store.PathName) ? File.ReadAllBytes(store.PathName) : null;
        if (Hash(current) != openedHash) throw new IOException("设置文件已被其他操作修改。为保留新设置，请关闭并重新打开本窗口；当前编辑仍保留在这里，可先复制高级页内容。");
    }
    void ShowStatus(string text, bool error)
    {
        status.Text = text;
        status.Foreground = new SolidColorBrush(error ? Color.FromRgb(145, 53, 43) : Color.FromRgb(100, 106, 96));
    }
    internal static RuntimeSettings Parse(string json)
    {
        var value = JsonSerializer.Deserialize<RuntimeSettings>(json, Format) ?? throw new ArgumentException("设置不能为空。");
        SettingsStore.Validate(value); return value;
    }
    internal static string Serialize(RuntimeSettings value) => JsonSerializer.Serialize(value, Format);
    internal static int RunConversionTests()
    {
        var assertions = 0;
        void Check(bool condition, string reason) { assertions++; if (!condition) throw new InvalidOperationException(reason); }
        var original = new RuntimeSettings
        {
            SampleSeconds = 45, RetentionDays = 60, Notifications = false, PingEnabled = false, PingHost = "1.1.1.1",
            AutomaticActions = true, ThrottleEnabled = true, MemoryTrimEnabled = true, TrimPressurePercent = 72,
            Alerts = new AlertPolicy(88, 75, 120, 900, 96, 65, 180),
            WatchProcesses = [new("example.exe", 22, 120, 3, @"D:\工具\launcher")],
            TrimProcesses = [new("example.exe", 3, 600, 2, @"D:\工具\launcher")],
            RestartServiceRules = [new("ExampleService", ["example.exe"], 2)]
        };
        var roundTrip = Parse(Serialize(original));
        Check(roundTrip.SampleSeconds == 45 && roundTrip.RetentionDays == 60, "基础数值未保留");
        Check(!roundTrip.Notifications && !roundTrip.PingEnabled && roundTrip.PingHost == "1.1.1.1", "通知网络设置未保留");
        Check(roundTrip.AutomaticActions && roundTrip.ThrottleEnabled && roundTrip.MemoryTrimEnabled, "开关未保留");
        Check(roundTrip.Alerts == original.Alerts && roundTrip.TrimPressurePercent == 72, "告警或压力门槛丢失");
        Check(roundTrip.WatchProcesses[0].MaxCommitGb == 22 && roundTrip.WatchProcesses[0].PathContains == @"D:\工具\launcher", "高级路径策略丢失");
        Check(roundTrip.TrimProcesses[0].MinWorkingSetGb == 3 && roundTrip.TrimProcesses[0].MinAgeSeconds == 600, "整理独立策略丢失");
        Check(roundTrip.RestartServiceRules[0].TriggerProcesses.Single() == "example.exe", "服务触发策略丢失");
        roundTrip.SampleSeconds = 60;
        var edited = Parse(Serialize(roundTrip));
        Check(edited.SampleSeconds == 60 && edited.WatchProcesses[0].PathContains == @"D:\工具\launcher", "修改基础数值不应覆盖高级策略");
        var invalid = false; try { Parse("{"); } catch (JsonException) { invalid = true; }
        Check(invalid, "错误JSON应拒绝转换");
        invalid = false; try { Parse("{\"SampleSeconds\":0}"); } catch (ArgumentException) { invalid = true; }
        Check(invalid, "无效数值应交中央校验拒绝");
        invalid = false; try { Parse("{\"UnknownSetting\":true}"); } catch (JsonException) { invalid = true; }
        Check(invalid, "未知字段不能静默丢弃");
        return assertions;
    }
    static string? Hash(byte[]? bytes) => bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes));
    static bool IsInputError(Exception error) => error is JsonException or ArgumentException or InvalidDataException;
}
