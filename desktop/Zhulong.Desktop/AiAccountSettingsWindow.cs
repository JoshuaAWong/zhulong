using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Zhulong.Runtime.Providers.AI;

namespace Zhulong.Desktop;

internal sealed class AiAccountSettingsWindow : Window
{
    readonly TabControl tabs = new();

    public event Action<string?>? AccountsChanged;

    public AiAccountSettingsWindow(string directory)
    {
        Title = "烛龙 · 管理 AI 账户";
        Width = 560; Height = 620; MinWidth = 470; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(246, 243, 235));
        Foreground = new SolidColorBrush(Color.FromRgb(48, 55, 49));
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14;
        tabs.Margin = new Thickness(20);
        System.Windows.Automation.AutomationProperties.SetName(tabs, "AI 账户提供方");
        tabs.Items.Add(Tab("Kimi Code", CreateKimiSettings(directory)));
        tabs.Items.Add(Tab("GLM Coding Plan", CreateApiSettings(directory, true)));
        tabs.Items.Add(Tab("DeepSeek", CreateApiSettings(directory, false)));
        Content = tabs;
        tabs.SelectedIndex = 0;
    }

    internal void SelectProvider(string? provider)
    {
        tabs.SelectedIndex = provider switch { "zcode" => 1, "deepseek" => 2, _ => 0 };
    }

    static TabItem Tab(string title, Panel section)
    {
        section.Margin = new Thickness(18);
        return new TabItem { Header = title, Padding = new Thickness(12, 7, 12, 7),
            Content = new ScrollViewer { Content = section, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
    }

    StackPanel CreateKimiSettings(string directory)
    {
        var panel = new StackPanel();
        panel.Children.Add(CreateKimiWebSettings(directory));
        var cliPanel = new StackPanel { Margin = new Thickness(12, 6, 0, 6) };
        panel.Children.Add(new Expander { Header = "本机 CLI 来源", Content = cliPanel, Margin = new Thickness(0, 8, 0, 12) });
        cliPanel.Children.Add(Note("默认目录沿用当前登录。曾在该目录切换账号的会话，显示为本机未归属用量。"));
        var defaults = new AiAccountPreferences();
        var primary = AliasField(defaults.KimiPrimary, "默认目录别名", cliPanel);
        var secondary = AliasField(defaults.KimiSecondary, "第二账号别名", cliPanel);
        var login = Button("登录第二账号");
        var open = Button("打开第二账号的 Kimi CLI");
        var saveCli = Button("保存 CLI 别名");
        var kimiActions = new WrapPanel();
        kimiActions.Children.Add(login); kimiActions.Children.Add(open); kimiActions.Children.Add(saveCli);
        cliPanel.Children.Add(kimiActions);
        cliPanel.Children.Add(Note("第二账号使用独立目录。若已登录到错误账号，先点「打开第二账号的 Kimi CLI」，在该窗口输入 /logout，再点「登录第二账号」。"));
        cliPanel.Children.Add(Note("把终端给出的设备授权链接复制到浏览器无痕窗口，登录目标账号并完成授权。终端提示成功后，回到第二账号卡片点「更新额度」。"));
        var kimiStatus = Note("");
        cliPanel.Children.Add(kimiStatus);
        Try(() =>
        {
            var preferences = AiAccountProfiles.Load(directory);
            primary.Text = preferences.KimiPrimary;
            secondary.Text = preferences.KimiSecondary;
        }, kimiStatus);
        saveCli.Click += (_, _) => Try(() =>
        {
            var preferences = AiAccountProfiles.Load(directory);
            AiAccountProfiles.Save(directory, preferences with { KimiPrimary = Alias(primary, defaults.KimiPrimary),
                KimiSecondary = Alias(secondary, defaults.KimiSecondary) }, null, false);
            kimiStatus.Text = "CLI 别名已保存。";
            AccountsChanged?.Invoke(null);
        }, kimiStatus);
        login.Click += (_, _) => Try(() =>
        {
            AiAccountProfiles.StartSecondaryKimi(directory, true);
            kimiStatus.Text = "请查看终端。如果直接提示已登录且没有授权链接，请先在第二账号的 Kimi CLI 输入 /logout。";
        }, kimiStatus);
        open.Click += (_, _) => Try(() => AiAccountProfiles.StartSecondaryKimi(directory, false), kimiStatus);
        return panel;
    }

    StackPanel CreateApiSettings(string directory, bool glm)
    {
        var panel = new StackPanel();
        var title = glm ? "GLM Coding Plan" : "DeepSeek API 钱包";
        var defaults = new AiAccountPreferences();
        var fallback = glm ? defaults.Glm : defaults.DeepSeek;
        panel.Children.Add(Heading(title));
        var summary = Note("");
        panel.Children.Add(summary);
        var actions = new WrapPanel();
        var edit = Button("新增配置");
        var remove = Button("删除配置");
        actions.Children.Add(edit); actions.Children.Add(remove);
        panel.Children.Add(actions);

        var form = new StackPanel { Visibility = Visibility.Collapsed };
        panel.Children.Add(form);
        var name = AliasField(fallback, "账户别名", form);
        ComboBox? region = null;
        if (glm)
        {
            form.Children.Add(Note("套餐地区"));
            region = new ComboBox { Margin = new Thickness(0, 2, 0, 8) };
            System.Windows.Automation.AutomationProperties.SetName(region, "GLM 套餐地区");
            region.Items.Add("智谱 BigModel · 国内");
            region.Items.Add("Z.ai · 国际");
            form.Children.Add(region);
        }
        form.Children.Add(Note("API Key"));
        var key = new PasswordBox { MaxLength = 512, Margin = new Thickness(0, 6, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(key, title + " API Key");
        form.Children.Add(key);
        var hint = Note("");
        form.Children.Add(hint);
        var save = Button("保存配置");
        var cancel = Button("取消");
        var formActions = new WrapPanel();
        formActions.Children.Add(save); formActions.Children.Add(cancel);
        form.Children.Add(formActions);
        var status = Note("");
        panel.Children.Add(status);
        edit.Click += (_, _) => Try(() =>
        {
            ShowConfig();
            actions.Visibility = Visibility.Collapsed;
            form.Visibility = Visibility.Visible;
            status.Text = "";
            name.Focus();
        }, status);
        cancel.Click += (_, _) => CloseForm();
        save.Click += (_, _) => Try(() => SaveConfig(false), status);
        remove.Click += (_, _) => Try(() =>
        {
            if (MessageBox.Show(this, $"删除「{title}」在烛龙中的配置和已保存的 Key？",
                "删除账户配置", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                SaveConfig(true);
        }, status);
        Try(ShowConfig, status);
        return panel;

        bool HasKey() => glm ? AiAccountProfiles.HasGlmKey(directory) : AiAccountProfiles.HasDeepSeekKey(directory);

        void ShowConfig()
        {
            var preferences = AiAccountProfiles.Load(directory);
            var exists = HasKey();
            name.Text = glm ? preferences.Glm : preferences.DeepSeek;
            if (region is not null) region.SelectedIndex = preferences.GlmDomestic ? 0 : 1;
            var regionName = glm ? $" · {(preferences.GlmDomestic ? "国内" : "国际")}" : "";
            summary.Text = exists ? $"{name.Text}{regionName} · 已保存 API Key" : "尚未配置。新增配置后可在账本更新读取。";
            edit.Content = exists ? "修改配置" : "新增配置";
            remove.Visibility = exists ? Visibility.Visible : Visibility.Collapsed;
            hint.Text = exists ? "留空保留已保存的 Key。新 Key 由 Windows 凭据管理器保存。"
                : "API Key 由 Windows 凭据管理器保存。";
        }

        void CloseForm()
        {
            key.Clear();
            form.Visibility = Visibility.Collapsed;
            actions.Visibility = Visibility.Visible;
        }

        void SaveConfig(bool deleting)
        {
            var value = deleting ? fallback : Alias(name, fallback);
            var replacement = !deleting && key.Password.Trim() is { Length: > 0 } password ? password : null;
            if (!deleting && replacement is null && !HasKey()) { status.Text = "请输入 API Key。"; return; }
            if (glm)
            {
                var preferences = AiAccountProfiles.Load(directory);
                AiAccountProfiles.Save(directory, preferences with { Glm = value,
                    GlmDomestic = deleting || region!.SelectedIndex == 0 }, replacement, deleting);
            }
            else AiAccountProfiles.SaveDeepSeekProfile(directory, value, replacement, deleting);
            CloseForm();
            AccountsChanged?.Invoke(null);
            ShowConfig();
            status.Text = deleting ? "配置已删除。" : "配置已保存，可回到账本更新读取。";
        }
    }

    FrameworkElement CreateKimiWebSettings(string directory)
    {
        var panel = new StackPanel();
        panel.Children.Add(Heading("网页授权账号"));
        var accounts = new StackPanel();
        panel.Children.Add(accounts);

        var form = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(form);
        var label = new TextBox { MaxLength = 32, Margin = new Thickness(0, 2, 0, 6) };
        System.Windows.Automation.AutomationProperties.SetName(label, "账号备注（选填）");
        form.Children.Add(Note("账号备注（选填）"));
        form.Children.Add(label);

        var region = new ComboBox { MinWidth = 140, Margin = new Thickness(0, 0, 8, 8) };
        System.Windows.Automation.AutomationProperties.SetName(region, "Kimi 账号地区");
        region.Items.Add("Kimi · 国内");
        region.Items.Add("Kimi · 国际");
        region.SelectedIndex = 0;
        var add = Button("新增网页账号");
        var addRow = new WrapPanel();
        addRow.Children.Add(region);
        addRow.Children.Add(add);
        form.Children.Add(addRow);

        var authorization = new StackPanel { Visibility = Visibility.Collapsed };
        panel.Children.Add(authorization);
        var authInfo = Note("");
        authorization.Children.Add(authInfo);
        var code = new TextBox { IsReadOnly = true, FontSize = 20, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(code, "Kimi 设备授权码");
        authorization.Children.Add(code);
        authorization.Children.Add(Note("用所选账号登录并确认授权。若浏览器已登录其他账号，请复制链接，在无痕窗口或该账号的浏览器配置文件中打开。"));
        var authActions = new WrapPanel();
        var openPage = Button("打开 Kimi 授权页");
        var copyLink = Button("复制授权链接");
        authActions.Children.Add(openPage);
        authActions.Children.Add(copyLink);
        authorization.Children.Add(authActions);

        var cancel = Button("取消授权");
        cancel.HorizontalAlignment = HorizontalAlignment.Left;
        cancel.Visibility = Visibility.Collapsed;
        panel.Children.Add(cancel);
        var status = Note("");
        panel.Children.Add(status);

        CancellationTokenSource? pending = null;
        string? verificationLink = null;
        var closed = false;
        Closed += (_, _) =>
        {
            closed = true;
            pending?.Cancel();
        };
        cancel.Click += (_, _) => pending?.Cancel();
        add.Click += async (_, _) => await BindAsync(region.SelectedIndex == 0, label.Text.Trim());
        openPage.Click += (_, _) =>
        {
            if (verificationLink is null) return;
            try
            {
                using var process = Process.Start(new ProcessStartInfo(verificationLink) { UseShellExecute = true });
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException)
            {
                status.Text = "未能打开浏览器。请复制授权链接后手动打开。";
            }
        };
        copyLink.Click += (_, _) =>
        {
            if (verificationLink is null) return;
            try
            {
                Clipboard.SetText(verificationLink);
                status.Text = "授权链接已复制，正在等待授权。";
            }
            catch (ExternalException)
            {
                status.Text = "剪贴板暂不可用，请重试或点「打开 Kimi 授权页」。";
            }
        };
        ReloadAccounts();
        return panel;

        async Task BindAsync(bool domestic, string? accountLabel)
        {
            await RunAsync(async token =>
            {
                status.Text = "正在申请 Kimi 授权…";
                cancel.Visibility = Visibility.Visible;
                status.BringIntoView();
                var flow = await KimiWebAccounts.StartAsync(domestic, token);
                token.ThrowIfCancellationRequested();
                verificationLink = flow.VerificationUri.ToString();
                code.Text = flow.UserCode;
                authInfo.Text = $"设备授权码 · {flow.ExpiresAt.ToLocalTime():HH:mm} 前有效";
                authorization.Visibility = Visibility.Visible;
                authorization.BringIntoView();
                status.Text = "正在等待完成浏览器授权…";
                var account = await KimiWebAccounts.BindAsync(directory, flow, accountLabel, null, token);
                if (!closed) label.Clear();
                return (account.Id, "已绑定「" + account.Label + "」，可回到账本查看额度。");
            });
        }

        async Task RunAsync(Func<CancellationToken, Task<(string? AccountId, string Message)>> operation)
        {
            if (pending is not null) return;
            using var stop = new CancellationTokenSource();
            pending = stop;
            form.IsEnabled = accounts.IsEnabled = false;
            try
            {
                var result = await operation(stop.Token);
                if (!closed)
                {
                    status.Text = result.Message;
                    ReloadAccounts();
                }
                AccountsChanged?.Invoke(result.AccountId);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                if (!closed) status.Text = "授权已取消。";
            }
            catch (KimiWebAuthException error)
            {
                if (!closed) status.Text = error.Message;
            }
            catch (Exception error) when (IsStorageError(error))
            {
                if (!closed) status.Text = "Kimi 账户设置操作未完成，请重试。";
            }
            finally
            {
                pending = null;
                verificationLink = null;
                if (!closed)
                {
                    code.Clear();
                    authorization.Visibility = cancel.Visibility = Visibility.Collapsed;
                    form.IsEnabled = accounts.IsEnabled = true;
                }
            }
        }

        void ReloadAccounts()
        {
            IReadOnlyList<KimiWebAccount> current;
            try { current = KimiWebAccounts.Load(directory); }
            catch (Exception error) when (error is KimiWebAuthException || IsStorageError(error))
            {
                status.Text = "暂时无法读取已绑定的 Kimi 账号。";
                return;
            }
            accounts.Children.Clear();
            if (current.Count == 0) accounts.Children.Add(Note("尚未绑定网页账号。"));
            foreach (var account in current)
            {
                var accountTitle = Note($"{account.Label} · {(account.Domestic ? "国内" : "国际")}");
                accountTitle.FontWeight = FontWeights.SemiBold;
                var row = new DockPanel();
                var remove = Button("删除绑定");
                System.Windows.Automation.AutomationProperties.SetName(remove, $"删除绑定 {account.Label}");
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                row.Children.Add(accountTitle);
                accounts.Children.Add(row);
                remove.Click += async (_, _) =>
                {
                    if (MessageBox.Show(this, $"删除烛龙中的「{account.Label}」绑定？", "删除 Kimi 绑定",
                        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                    await RunAsync(async token =>
                    {
                        await KimiWebAccounts.RemoveAsync(directory, account.Id, token);
                        return (null, "已删除「" + account.Label + "」的绑定。");
                    });
                };
            }
        }
    }

    static bool IsStorageError(Exception error) =>
        error is IOException or UnauthorizedAccessException or JsonException or Win32Exception or InvalidOperationException or ArgumentException;

    static void Try(Action work, TextBlock feedback)
    {
        try { work(); }
        catch (Exception error) when (IsStorageError(error))
        { feedback.Text = "操作未完成。" + error.Message; }
    }

    static TextBox AliasField(string value, string label, Panel section)
    {
        section.Children.Add(Note(label));
        var input = new TextBox { Text = value, MaxLength = 32, Margin = new Thickness(0, 4, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        section.Children.Add(input);
        return input;
    }

    static string Alias(TextBox input, string fallback) => input.Text.Trim() is { Length: > 0 } label ? label : fallback;
    static TextBlock Heading(string text) => new() { Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 5) };
    static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 6) };
    static Button Button(string text) => new() { Content = text, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 8) };
}
