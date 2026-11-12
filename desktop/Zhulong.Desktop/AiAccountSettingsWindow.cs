using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Zhulong.Runtime.Providers.AI;

namespace Zhulong.Desktop;

internal sealed class AiAccountSettingsWindow : Window
{
    public AiAccountSettingsWindow(string directory)
    {
        Title = "烛龙 · AI 账户";
        Width = 560; Height = 620; MinWidth = 470; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(246, 243, 235));
        Foreground = new SolidColorBrush(Color.FromRgb(48, 55, 49));
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14;
        var panel = new StackPanel { Margin = new Thickness(26) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var status = Note("");
        var preferences = new AiAccountPreferences();
        Try(() => preferences = AiAccountProfiles.Load(directory));

        panel.Children.Add(Heading("Kimi Code"));
        panel.Children.Add(Note("默认目录沿用当前登录。曾在该目录切换账号的会话，显示为本机未归属用量。"));
        var primary = Name(preferences.KimiPrimary, "默认目录别名");
        var secondary = Name(preferences.KimiSecondary, "第二账号别名");
        var login = Button("登录第二账号");
        var open = Button("打开第二账号的 Kimi CLI");
        var kimiActions = new WrapPanel(); kimiActions.Children.Add(login); kimiActions.Children.Add(open);
        panel.Children.Add(kimiActions);
        panel.Children.Add(Note("第二账号使用独立目录。请在 Kimi 官方登录页选中第二账号，完成后回到 AI 账本刷新。"));
        login.Click += (_, _) => Try(() =>
        {
            AiAccountProfiles.StartSecondaryKimi(directory, true);
            status.Text = "请按 Kimi CLI 给出的设备登录指引操作。";
        });
        open.Click += (_, _) => Try(() => AiAccountProfiles.StartSecondaryKimi(directory, false));

        panel.Children.Add(Heading("GLM Coding Plan"));
        var glm = Name(preferences.Glm, "套餐账户别名");
        var region = new ComboBox { Margin = new Thickness(0, 6, 0, 8) };
        region.Items.Add("智谱 BigModel · 国内");
        region.Items.Add("Z.ai · 国际");
        region.SelectedIndex = preferences.GlmDomestic ? 0 : 1;
        panel.Children.Add(region);
        var key = new PasswordBox { MaxLength = 512, Margin = new Thickness(0, 6, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(key, "GLM Coding Plan API Key");
        panel.Children.Add(key);
        var keyHint = Note("API Key 保存到 Windows 凭据管理器。留空保留现有 Key。");
        panel.Children.Add(keyHint);
        var save = Button("保存账户设置");
        var clear = Button("移除已保存的 GLM Key");
        var glmActions = new WrapPanel(); glmActions.Children.Add(save); glmActions.Children.Add(clear);
        panel.Children.Add(glmActions);
        panel.Children.Add(status);
        save.Click += (_, _) => Try(() => Save(false));
        clear.Click += (_, _) => Try(() => Save(true));
        Try(UpdateKeyHint);

        TextBox Name(string value, string label)
        {
            panel.Children.Add(Note(label));
            var input = new TextBox { Text = value, MaxLength = 32, Margin = new Thickness(0, 4, 0, 8) };
            System.Windows.Automation.AutomationProperties.SetName(input, label);
            panel.Children.Add(input);
            return input;
        }

        void Save(bool remove)
        {
            var previous = AiAccountProfiles.Load(directory);
            var defaults = new AiAccountPreferences();
            var value = previous with { KimiPrimary = Alias(primary, defaults.KimiPrimary),
                KimiSecondary = Alias(secondary, defaults.KimiSecondary), Glm = Alias(glm, defaults.Glm),
                GlmDomestic = region.SelectedIndex == 0 };
            var token = key.Password.Trim();
            AiAccountProfiles.Save(directory, value, !remove && token.Length > 0 ? token : null, remove);
            key.Clear();
            UpdateKeyHint();
            status.Text = remove ? "GLM Key 已移除。" : "账户设置已保存，可回到账本刷新。";
        }

        void UpdateKeyHint()
        {
            var exists = AiAccountProfiles.HasGlmKey(directory);
            keyHint.Text = exists ? "已保存 GLM Key。留空保留现有 Key。" : "输入 GLM Coding Plan API Key 并保存，才能读取套餐。";
            clear.IsEnabled = exists;
        }

        void Try(Action work)
        {
            try { work(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                or Win32Exception or InvalidOperationException)
            { status.Text = "操作未完成。 " + error.Message; }
        }
    }

    static string Alias(TextBox input, string fallback) => input.Text.Trim() is { Length: > 0 } label ? label : fallback;
    static TextBlock Heading(string text) => new() { Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 5) };
    static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 6) };
    static Button Button(string text) => new() { Content = text, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 8) };
}
