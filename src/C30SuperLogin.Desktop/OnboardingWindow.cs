using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using C30SuperLogin.Core;
using FluentAvalonia.UI.Windowing;

namespace C30SuperLogin.Desktop;

public sealed class OnboardingWindow : AppWindow
{
    private readonly Store store;
    private readonly Settings settings;
    private readonly StackPanel page = new() { Spacing = 16 };
    private readonly StackPanel steps = new() { Spacing = 8 };
    private readonly TextBlock step = Text("", 12), status = Text("", 12);
    private readonly Button back = new() { Content = "上一步" }, next = new() { Content = "下一步" };
    private readonly Button skip = new() { Content = "稍后配置账号" };
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private int index;
    private RuntimeMode mode;
    private bool completed, working, skipAccounts;
    private Guid? editing;
    private TextBox? label, username, password;
    private ListBox? accountList;
    private IReadOnlyList<AccountRecord> records = [];
    private StackPanel? preferencesChecks;
    private string installationStatus = "";
    public event Action? Completed;

    public OnboardingWindow(Store store, Settings settings, bool exitOnCancel = true)
    {
        this.store = store; this.settings = settings; mode = settings.RuntimeMode;
        Title = "欢迎使用 C30 登录助手"; Width = 960; Height = 680; MinWidth = 880; MinHeight = 620;
        TitleBar.Height = 36; TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.None];
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AutomationProperties.SetAutomationId(this, "OnboardingWindow");
        using var icon = Avalonia.Platform.AssetLoader.Open(new Uri("avares://C30SuperLogin/Assets/app.ico"));
        ((Window)this).Icon = new WindowIcon(icon);
        back.Click += (_, _) => { SaveAccount(); index--; Render(); };
        next.Classes.Add("accent"); next.Click += (_, _) => {
            SaveAccount();
            if (index == 2 && !skipAccounts && !new AccountBook(store, settings).Read().Any(x => x.CanLogin)) {
                status.Text = "先读取或新建一个可登录的账号，也可以选择「稍后配置账号」。"; return;
            }
            if (index < 4) { index++; Render(); return; }
            settings.RuntimeMode = mode; settings.OnboardingVersion = 1;
            try { store.Save(settings); } catch { status.Text = "设置还没保存，请检查目录是否可写。"; return; }
            completed = true; Completed?.Invoke(); Close();
        };
        AutomationProperties.SetAutomationId(back, "OobeBack"); AutomationProperties.SetAutomationId(next, "OobeNext"); AutomationProperties.SetAutomationId(status, "OobeStatus");
        AutomationProperties.SetAutomationId(skip, "OobeSkipAccounts"); skip.Click += (_, _) => { SaveAccount(); skipAccounts = true; index++; Render(); };
        skip.Background = Brushes.Transparent; skip.BorderThickness = new Thickness(0);
        saveTimer.Tick += (_, _) => SaveAccount();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { back, next } };
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(0, 14, 0, 0) }; footer.Children.Add(skip); skip.HorizontalAlignment = HorizontalAlignment.Left; Grid.SetColumn(buttons, 1); footer.Children.Add(buttons);
        var body = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Margin = new Thickness(32, 24), RowSpacing = 10 };
        step.HorizontalAlignment = HorizontalAlignment.Right; step.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"); body.Children.Add(step);
        var scroll = new ScrollViewer { Content = page, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        page.Margin = new Thickness(0, 8, 8, 12);
        Grid.SetRow(scroll, 1); body.Children.Add(scroll); Grid.SetRow(status, 2); body.Children.Add(status); Grid.SetRow(footer, 3); body.Children.Add(footer);
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"); status.MinHeight = 18;
        var sidebar = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new Thickness(24, 26, 20, 22) };
        using var logo = Avalonia.Platform.AssetLoader.Open(new Uri("avares://C30SuperLogin/Assets/app.png"));
        var brand = new StackPanel { Spacing = 10, Children = { new Image { Source = new Avalonia.Media.Imaging.Bitmap(logo), Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Left }, Text("C30 登录助手", 17), Text("几步设置，轻松开始", 12) } };
        sidebar.Children.Add(brand); Grid.SetRow(steps, 1); steps.VerticalAlignment = VerticalAlignment.Center; sidebar.Children.Add(steps);
        var privacy = Text("账号只留在本机\n你随时可以修改设置", 12); privacy.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"); Grid.SetRow(privacy, 2); sidebar.Children.Add(privacy);
        var layout = new Grid { ColumnDefinitions = new("214,*") }; layout.Children.Add(sidebar);
        var surface = new Border { Child = body, CornerRadius = new CornerRadius(12, 0, 0, 0) }; surface.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush"); Grid.SetColumn(surface, 1); layout.Children.Add(surface);
        Content = layout; Render();
        Closing += (_, e) => { if (working) { e.Cancel = true; status.Text = "正在读取账号，请稍等。"; } else SaveAccount(); };
        Closed += (_, _) => { saveTimer.Stop(); if (!completed && exitOnCancel) (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown(); };
    }
    private static TextBlock Text(string text, double size = 14) => new() { Text = text, FontSize = size, FontWeight = size >= 17 ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
    private static StackPanel Stack(params Control[] controls) { var panel = new StackPanel { Spacing = 10 }; foreach (var c in controls) panel.Children.Add(c); return panel; }
    private static Border Card(Control child) { var card = new Border { Child = child, Padding = new Thickness(16), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) }; card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush"); card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush"); return card; }
    private static Grid Pair(Control left, Control right) { var grid = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 14 }; grid.Children.Add(left); Grid.SetColumn(right, 1); grid.Children.Add(right); return grid; }
    private static TextBox Field(string id, string hint) { var field = new TextBox { Watermark = hint }; AutomationProperties.SetAutomationId(field, id); AutomationProperties.SetName(field, hint); return field; }
    private static Button Action(string text, string id, Action click) { var button = new Button { Content = text }; AutomationProperties.SetAutomationId(button, id); button.Click += (_, _) => click(); return button; }
    private void Render()
    {
        saveTimer.Stop(); label = username = password = null; accountList = null; editing = null;
        step.Text = $"{index + 1} / 5"; back.IsVisible = index > 0; next.Content = index == 4 ? "开始使用" : "下一步"; skip.IsVisible = index == 2;
        steps.Children.Clear(); string[] labels = ["欢迎", "运行方式", "准备账号", "使用偏好", "完成设置"];
        for (int i = 0; i < labels.Length; i++) {
            var number = Text(i < index ? "✓" : (i + 1).ToString(), 12); number.Width = 24; number.VerticalAlignment = VerticalAlignment.Center;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { number, Text(labels[i], 14) } };
            var item = new Border { Child = row, Padding = new Thickness(12), CornerRadius = new CornerRadius(6) };
            if (i == index) item.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush"); else item.Opacity = i < index ? .8 : .5;
            steps.Children.Add(item);
        }
        status.Text = ""; page.Children.Clear(); page.Spacing = 14;
        if (index == 0) {
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://C30SuperLogin/Assets/app.png"));
            page.Children.Add(new Image { Source = new Avalonia.Media.Imaging.Bitmap(stream), Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 8) });
            page.Children.Add(Text("上课前，少点几下。", 28));
            page.Children.Add(Text("把登录前的准备，一次做好。", 15));
            page.Children.Add(Card(Stack(Text("01   选好运行方式", 14), Text("02   导入或添加账号", 14), Text("03   配好课前提醒与自动化", 14))));
            page.Children.Add(Text("账号自动加密保存，途中关闭也不会丢失已保存的账号。", 12));
        } else if (index == 1) {
            page.Children.Add(Text("怎么运行，由你决定", 25));
            page.Children.Add(Text("有 UAC 弹窗，选常驻会更省心。", 13));
            var resident = new RadioButton { Content = Stack(Text("后台常驻 · 推荐", 17), Text("授权一次，CI 直接调用。关掉窗口后留在托盘。", 13)), GroupName = "Runtime", IsChecked = mode == RuntimeMode.Resident, HorizontalAlignment = HorizontalAlignment.Stretch };
            var demand = new RadioButton { Content = Stack(Text("仅调用时运行", 17), Text("平时不驻留，用完就退出。每次可能需要确认权限。", 13)), GroupName = "Runtime", IsChecked = mode == RuntimeMode.OnDemand, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(resident, "后台常驻"); AutomationProperties.SetName(demand, "仅调用时运行");
            AutomationProperties.SetAutomationId(resident, "OobeResident"); AutomationProperties.SetAutomationId(demand, "OobeOnDemand");
            resident.IsCheckedChanged += (_, _) => { if (resident.IsChecked == true) mode = RuntimeMode.Resident; };
            demand.IsCheckedChanged += (_, _) => { if (demand.IsChecked == true) mode = RuntimeMode.OnDemand; };
            page.Children.Add(Card(resident)); page.Children.Add(Card(demand));
            page.Children.Add(Text("连接 C30 必须使用管理员权限，授权由 Windows 处理，CI 自身无需管理员权限。", 13));
            page.Children.Add(Text("当前还未加入开机自启。以后可在设置中切换。", 12));
        } else if (index == 2) Accounts();
        else if (index == 3) Preferences();
        else {
            page.Children.Add(Text("准备好了", 28));
            page.Children.Add(Text("以后也可以在设置里随时调整。", 13));
            page.Children.Add(Card(Text(mode == RuntimeMode.Resident ? "已选：后台常驻。关闭主窗口后留在托盘。" : "已选：仅调用时运行。关闭主窗口后直接退出。")));
            var records = new AccountBook(store, settings).Read();
            page.Children.Add(Card(Stack(Text($"已保存 {records.Count} 个账号，其中 {records.Count(x => x.CanLogin)} 个可用于登录。"), Text($"课前提醒：{(settings.PreLoginWarningSeconds > 0 ? settings.PreLoginWarningSeconds + " 秒" : "关闭")} · {settings.Rules.Count} 个课程任务", 12))));
            if (records.Count > 0) page.Children.Add(new Expander { Header = "供自动化使用的命令", Content = Text($"login --id {records[0].Number} --trigger classisland --headless", 12) });
            page.Children.Add(Text("准备就绪后，打开 C30 登录页，在助手中检查连接即可。", 13));
            page.Children.Add(Text("预览版尚未完成真实账号认证验收，在线更新服务稍后开放。", 12));
        }
    }
    private void Accounts()
    {
        page.Children.Add(Text("把账号准备好", 25));
        page.Children.Add(Text("读取已有账号，或直接新建。重复账号只留一份。", 13));
        var installation = Installation.Discover(settings);
        var location = new StackPanel { Spacing = 10 };
        location.Children.Add(Text(installation.Root, 12));
        location.Children.Add(Action("选择 C30 文件夹", "OobeInstallFolder", async () => {
            var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "选择 C30 安装文件夹", AllowMultiple = false });
            if (folders.Count == 0) return;
            settings.InstallRoot = folders[0].Path.LocalPath; settings.AutoDetectInstall = false; store.Save(settings); Render();
        }));
        location.Children.Add(Action("恢复自动识别", "OobeAutoDetect", () => { settings.AutoDetectInstall = true; store.Save(settings); Render(); }));
        var source = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        source.Children.Add(Stack(Text("C30 已保存的账号", 14), Text(installation.LatestInstalledVersion == "unknown" ? "打开 C30 登录页后读取" : "本机版本 " + installation.LatestInstalledVersion, 12)));
        var import = Action("读取账号", "OobeImportAccounts", () => _ = ImportAccounts()); import.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(import, 1); source.Children.Add(import);
        var install = new Expander { Header = "安装位置 · 自动识别或手动选择", Content = location };
        page.Children.Add(Card(Stack(source, install)));
        page.Children.Add(Action("立即检测 C30 目录", "OobeDetectInstall", DetectInstallation));
        if (!string.IsNullOrWhiteSpace(installationStatus)) page.Children.Add(Text(installationStatus, 12));
        accountList = new ListBox { MaxHeight = 76 }; accountList.Classes.Add("accounts"); AutomationProperties.SetAutomationId(accountList, "OobeAccountList"); page.Children.Add(accountList); RefreshAccountList();
        label = Field("OobeAccountLabel", "备注（可选，如：张老师）"); username = Field("OobeAccountUsername", "C30 登录账号"); password = Field("OobeAccountPassword", "密码"); password.PasswordChar = '●';
        foreach (var field in new[] { label, username, password }) field.TextChanged += (_, _) => { saveTimer.Stop(); saveTimer.Start(); };
        accountList.SelectionChanged += (_, _) => {
            if (accountList.SelectedIndex < 0 || accountList.SelectedIndex >= records.Count) return;
            var record = records[accountList.SelectedIndex]; editing = record.Id;
            label.Text = record.Label; username.Text = record.Username; password.Text = "";
            status.Text = record.CanLogin ? $"数字 ID：{record.Number}，已保存，可直接使用。" : "这个账号需要补填密码，填好后会自动保存。";
        };
        var another = Action("再添加一个", "OobeNewAccount", () => { SaveAccount(); editing = null; label.Text = username.Text = password.Text = ""; status.Text = "可以填写下一个账号。"; }); another.HorizontalAlignment = HorizontalAlignment.Left;
        page.Children.Add(Card(Stack(Text("新建 / 补全账号", 14), Pair(Stack(Text("登录账号", 12), username), Stack(Text("密码", 12), password)), label, another)));
        page.Children.Add(Text("填好自动保存，数字 ID 会自动分配。", 12));
    }
    private void DetectInstallation()
    {
        var installation = Installation.Discover(settings, forceAutoDetect: true);
        if (installation.LatestDirectory is null) {
            installationStatus = "没有找到可用的 C30 安装，已保留当前设置。";
        } else {
            settings.InstallRoot = installation.Root;
            settings.AutoDetectInstall = true;
            try {
                store.Save(settings);
                installationStatus = $"已找到 C30 {installation.LatestInstalledVersion}，后续会自动跟踪新版本。";
            } catch {
                installationStatus = "找到了 C30，但设置未能保存；原有路径保持不变。";
            }
        }
        Render();
    }
    private void SaveAccount()
    {
        saveTimer.Stop();
        if (index != 2 || string.IsNullOrWhiteSpace(username?.Text) || string.IsNullOrEmpty(password?.Text)) return;
        try {
            var saved = new AccountBook(store, settings).Save(editing, string.IsNullOrWhiteSpace(label?.Text) ? username.Text : label.Text, username.Text, password.Text, updateExisting: true);
            editing = saved.Id; skipAccounts = false; status.Text = $"已自动保存，数字 ID：{saved.Number}"; RefreshAccountList();
        } catch { status.Text = "账号暂未保存，请检查填写内容；重复账号会保留原来的 ID。"; }
    }
    private void RefreshAccountList() { records = new AccountBook(store, settings).Read(); if (accountList is not null) accountList.ItemsSource = records.Select(x => $"{x.Number}  ·  {x.Label}  ·  {(x.CanLogin ? "已就绪" : "需要补填密码")}").ToArray(); }
    private async Task ImportAccounts()
    {
        if (working) return; SaveAccount(); working = true; page.IsEnabled = back.IsEnabled = next.IsEnabled = false;
        status.Text = "请打开 C30 登录页，正在读取已保存账号…";
        try {
            // One explicitly requested import, not a persistent host before OOBE completes.
            var options = System.Text.Json.JsonSerializer.Deserialize<Settings>(Json.Serialize(settings), Json.Options)!; options.RuntimeMode = RuntimeMode.OnDemand;
            using var telemetry = new Telemetry(options.Telemetry); await using var queue = new DiagnosticQueue(store, telemetry);
            var coordinator = new Coordinator(store, options, Path.Combine(AppContext.BaseDirectory, "C30Native.exe")); coordinator.DiagnosticReady += queue.Add;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var routed = await RuntimeAccess.RouteAsync(["accounts", "sync"], store, options);
            var result = routed?.Result ?? await coordinator.RunAsync(null, false, false, timeout.Token, true);
            File.WriteAllText(Path.Combine(store.Root, "last-operation.json"), Json.Serialize(result));
            var loaded = store.Load(); settings.Profiles = loaded.Profiles; settings.ProfileAliases = loaded.ProfileAliases;
            settings.ProfileNumberAliases = loaded.ProfileNumberAliases; settings.NextProfileNumber = loaded.NextProfileNumber; settings.Rules = loaded.Rules;
            RefreshAccountList();
            status.Text = result.Code == "accounts_synced" ? "账号已读取，重复记录已合并。" : result.Code == "elevation_cancelled" ? "已取消管理员授权，可以手动新建账号。" : "暂未读到账号。请打开 C30 登录页重试，或在下面手动新建。";
        } catch { status.Text = "读取没有完成，可以重试或手动新建账号。"; }
        finally { working = false; page.IsEnabled = back.IsEnabled = next.IsEnabled = true; }
    }
    private void Preferences()
    {
        page.Children.Add(Text("最后，按你的习惯来", 25));
        var theme = new ComboBox { ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = settings.Theme == "Light" ? 1 : settings.Theme == "Dark" ? 2 : 0 };
        AutomationProperties.SetAutomationId(theme, "OobeTheme"); theme.SelectionChanged += (_, _) => { settings.Theme = theme.SelectedIndex == 1 ? "Light" : theme.SelectedIndex == 2 ? "Dark" : "System"; store.Save(settings); App.ApplyTheme(settings.Theme); };
        var warning = new ComboBox { ItemsSource = new[] { "不提醒", "提前 5 秒", "提前 10 秒" }, SelectedIndex = settings.PreLoginWarningSeconds <= 0 ? 0 : settings.PreLoginWarningSeconds >= 10 ? 2 : 1 };
        AutomationProperties.SetAutomationId(warning, "OobeWarning"); warning.SelectionChanged += (_, _) => { settings.PreLoginWarningSeconds = warning.SelectedIndex == 0 ? 0 : warning.SelectedIndex == 2 ? 10 : 5; store.Save(settings); };
        page.Children.Add(Card(Pair(Stack(Text("外观", 12), theme), Stack(Text("课前提醒", 12), warning))));
        preferencesChecks = new StackPanel { Spacing = 0 };
        AddCheck("使用情况统计", "OobeUsageStatistics", settings.Telemetry.PostHogEnabled, x => settings.Telemetry.PostHogEnabled = x);
        AddCheck("出错时自动报告", "OobeErrorReports", settings.Telemetry.SentryEnabled, x => settings.Telemetry.SentryEnabled = x);
        AddCheck("自动检查助手更新", "OobeUpdates", settings.Updates.AutoCheck, x => settings.Updates.AutoCheck = x);
        AddCheck("需要时自动打开 C30", "OobeLaunchC30", settings.LaunchC30, x => settings.LaunchC30 = x);
        page.Children.Add(Card(preferencesChecks)); preferencesChecks = null;
        page.Children.Add(Text("两项上报默认开启，不上传账号密码和课堂内容。", 12));
        var automation = new StackPanel { Spacing = 10 };
        var profiles = new ComboBox { ItemsSource = settings.Profiles, DisplayMemberBinding = new Avalonia.Data.Binding("Label"), SelectedIndex = settings.Profiles.Count > 0 ? 0 : -1 }; AutomationProperties.SetAutomationId(profiles, "OobeRuleAccount");
        var subject = Field("OobeSubjectId", "ClassIsland 科目 ID"); var name = Field("OobeSubjectName", "科目名称（例如：数学）");
        automation.Children.Add(Text("可选：配置课程和账号，在这里直接导出。其他规则不受影响。", 12)); automation.Children.Add(profiles); automation.Children.Add(Pair(subject, name));
        automation.Children.Add(Action("添加课程任务", "OobeAddRule", () => {
            if (profiles.SelectedItem is not AccountProfile profile || string.IsNullOrWhiteSpace(subject.Text)) { status.Text = "先选账号并填写科目 ID。"; return; }
            settings.Rules.RemoveAll(x => x.SubjectId == subject.Text.Trim()); settings.Rules.Add(new(Guid.NewGuid(), profile.Id, subject.Text.Trim(), string.IsNullOrWhiteSpace(name.Text) ? subject.Text.Trim() : name.Text.Trim(), 20)); store.Save(settings); status.Text = $"已保存 {settings.Rules.Count} 个课程任务，提前 20 秒触发。";
        }));
        automation.Children.Add(Action("导出到 ClassIsland", "OobeExportRules", async () => {
            if (settings.Rules.Count == 0) { status.Text = "先添加一个课程任务。"; return; }
            if (System.Diagnostics.Process.GetProcessesByName("ClassIsland").Length > 0) { status.Text = "先退出 ClassIsland，避免配置被覆盖。"; return; }
            var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "导出自动化", SuggestedFileName = "C30SuperLogin.json" }); if (file is null) return;
            try { string path = file.Path.LocalPath; string data = ClassIsland.Export(settings.Rules, Environment.ProcessPath!, settings.Profiles); if (File.Exists(path)) { File.Copy(path, path + "." + DateTime.Now.ToString("yyyyMMddHHmmssfff") + ".bak"); data = ClassIsland.Merge(File.ReadAllText(path), data); } File.WriteAllText(path, data); status.Text = "已导出，请在 ClassIsland 选择这份配置。"; } catch { status.Text = "导出没有完成，原文件已保留。"; }
        }));
        var expand = new Expander { Header = "ClassIsland 课程自动化（可选）", Content = automation }; AutomationProperties.SetAutomationId(expand, "OobeAutomation"); page.Children.Add(expand);
    }
    private void AddCheck(string text, string id, bool value, Action<bool> set)
    {
        var check = new ToggleSwitch { IsChecked = value, OnContent = "", OffContent = "" }; AutomationProperties.SetAutomationId(check, id); AutomationProperties.SetName(check, text);
        check.IsCheckedChanged += (_, _) => { set(check.IsChecked == true); store.Save(settings); };
        var row = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(0, 6) }; var title = Text(text, 14); title.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(title); Grid.SetColumn(check, 1); row.Children.Add(check); (preferencesChecks ?? page).Children.Add(row);
    }
}
