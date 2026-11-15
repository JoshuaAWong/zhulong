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
    public event Action<string?>? KimiAccountsChanged;

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
        panel.Children.Add(CreateKimiWebSettings(directory));
        var cliPanel = new StackPanel { Margin = new Thickness(12, 6, 0, 6) };
        panel.Children.Add(new Expander { Header = "本机 CLI 来源", Content = cliPanel, Margin = new Thickness(0, 8, 0, 12) });
        cliPanel.Children.Add(Note("默认目录沿用当前登录。曾在该目录切换账号的会话，显示为本机未归属用量。"));
        var primary = Name(preferences.KimiPrimary, "默认目录别名", cliPanel);
        var secondary = Name(preferences.KimiSecondary, "第二账号别名", cliPanel);
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
        saveCli.Click += (_, _) => Try(() =>
        {
            AiAccountProfiles.Save(directory, WithCliAliases(AiAccountProfiles.Load(directory)), null, false);
            kimiStatus.Text = "CLI 别名已保存。";
        }, kimiStatus);
        login.Click += (_, _) => Try(() =>
        {
            AiAccountProfiles.StartSecondaryKimi(directory, true);
            kimiStatus.Text = "请查看终端。如果直接提示已登录且没有授权链接，请先在第二账号的 Kimi CLI 输入 /logout。";
        }, kimiStatus);
        open.Click += (_, _) => Try(() => AiAccountProfiles.StartSecondaryKimi(directory, false), kimiStatus);

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

        panel.Children.Add(Heading("DeepSeek API 钱包"));
        var deepSeekKey = new PasswordBox { MaxLength = 512, Margin = new Thickness(0, 6, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(deepSeekKey, "DeepSeek API Key");
        panel.Children.Add(deepSeekKey);
        var deepSeekHint = Note("");
        panel.Children.Add(deepSeekHint);
        var saveDeepSeek = Button("保存 DeepSeek Key");
        var clearDeepSeek = Button("移除已保存的 DeepSeek Key");
        var deepSeekActions = new WrapPanel();
        deepSeekActions.Children.Add(saveDeepSeek); deepSeekActions.Children.Add(clearDeepSeek);
        panel.Children.Add(deepSeekActions);
        saveDeepSeek.Click += (_, _) => Try(() => SaveDeepSeek(false), deepSeekHint);
        clearDeepSeek.Click += (_, _) => Try(() => SaveDeepSeek(true), deepSeekHint);
        Try(UpdateDeepSeekHint, deepSeekHint);

        TextBox Name(string value, string label, Panel? section = null)
        {
            section ??= panel;
            section.Children.Add(Note(label));
            var input = new TextBox { Text = value, MaxLength = 32, Margin = new Thickness(0, 4, 0, 8) };
            System.Windows.Automation.AutomationProperties.SetName(input, label);
            section.Children.Add(input);
            return input;
        }

        AiAccountPreferences WithCliAliases(AiAccountPreferences previous)
        {
            var defaults = new AiAccountPreferences();
            return previous with { KimiPrimary = Alias(primary, defaults.KimiPrimary),
                KimiSecondary = Alias(secondary, defaults.KimiSecondary) };
        }

        void Save(bool remove)
        {
            var previous = WithCliAliases(AiAccountProfiles.Load(directory));
            var defaults = new AiAccountPreferences();
            var value = previous with { Glm = Alias(glm, defaults.Glm),
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

        void SaveDeepSeek(bool remove)
        {
            var token = deepSeekKey.Password.Trim();
            if (!remove && token.Length == 0)
            {
                deepSeekHint.Text = "请输入 DeepSeek 开放平台的 API Key。";
                return;
            }
            AiAccountProfiles.SaveDeepSeekKey(directory, remove ? null : token);
            deepSeekKey.Clear();
            UpdateDeepSeekHint();
            deepSeekHint.Text = remove ? "DeepSeek Key 已移除。" : "已保存到 Windows 凭据管理器，可回到账本查询余额。";
        }

        void UpdateDeepSeekHint()
        {
            clearDeepSeek.IsEnabled = AiAccountProfiles.HasDeepSeekKey(directory);
            deepSeekHint.Text = clearDeepSeek.IsEnabled ? "已保存 DeepSeek API Key。"
                : "输入 DeepSeek 开放平台的 API Key 并保存，即可查询钱包余额。";
        }

        void Try(Action work, TextBlock? feedback = null)
        {
            try { work(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                or Win32Exception or InvalidOperationException or ArgumentException)
            { (feedback ?? status).Text = "操作未完成。 " + error.Message; }
        }
    }

    FrameworkElement CreateKimiWebSettings(string directory)
    {
        var panel = new StackPanel();
        panel.Children.Add(Note("通过 Kimi 授权绑定账号，可在账本分别查看各账号额度。"));

        var form = new StackPanel();
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
        var add = Button("绑定 Kimi 账号");
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
        var accounts = new StackPanel();
        panel.Children.Add(accounts);

        CancellationTokenSource? pending = null;
        string? verificationLink = null;
        var closed = false;
        Closed += (_, _) =>
        {
            closed = true;
            pending?.Cancel();
        };
        cancel.Click += (_, _) => pending?.Cancel();
        add.Click += async (_, _) => await BindAsync(region.SelectedIndex == 0, label.Text.Trim(), null);
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

        async Task BindAsync(bool domestic, string? accountLabel, string? replaceId)
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
                var account = await KimiWebAccounts.BindAsync(directory, flow, accountLabel, replaceId, token);
                if (!closed && replaceId is null) label.Clear();
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
                KimiAccountsChanged?.Invoke(result.AccountId);
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
            foreach (var account in current)
            {
                var accountTitle = Note($"{account.Label} · {(account.Domestic ? "国内" : "国际")}");
                accountTitle.FontWeight = FontWeights.SemiBold;
                accounts.Children.Add(accountTitle);
                var row = new WrapPanel();
                var reauthorize = Button("重新授权");
                var remove = Button("移除绑定");
                System.Windows.Automation.AutomationProperties.SetName(reauthorize, $"重新授权 {account.Label}");
                System.Windows.Automation.AutomationProperties.SetName(remove, $"移除绑定 {account.Label}");
                row.Children.Add(reauthorize);
                row.Children.Add(remove);
                accounts.Children.Add(row);
                reauthorize.Click += async (_, _) => await BindAsync(account.Domestic, account.Label, account.Id);
                remove.Click += async (_, _) =>
                {
                    if (MessageBox.Show(this, $"移除烛龙中的「{account.Label}」绑定？", "移除 Kimi 账号",
                        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                    await RunAsync(async token =>
                    {
                        await KimiWebAccounts.RemoveAsync(directory, account.Id, token);
                        return (null, "已移除「" + account.Label + "」的绑定。");
                    });
                };
            }
        }
    }

    static bool IsStorageError(Exception error) =>
        error is IOException or UnauthorizedAccessException or JsonException or Win32Exception or InvalidOperationException or ArgumentException;

    static string Alias(TextBox input, string fallback) => input.Text.Trim() is { Length: > 0 } label ? label : fallback;
    static TextBlock Heading(string text) => new() { Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 5) };
    static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 6) };
    static Button Button(string text) => new() { Content = text, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 8) };
}
