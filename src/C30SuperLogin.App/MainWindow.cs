using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using C30SuperLogin.Core;
using Microsoft.Win32;

namespace C30SuperLogin.App;

public sealed class MainWindow : Window
{
    private readonly Store store;
    private readonly Settings settings;
    private readonly Coordinator coordinator;
    private readonly Telemetry telemetry;
    private readonly DiagnosticQueue queue;
    private readonly AccountBook accountBook;
    private readonly System.Windows.Forms.NotifyIcon tray;
    private readonly StackPanel content = new() { Margin = new Thickness(34, 24, 34, 32) };
    private readonly List<Button> navigation = [];
    private readonly List<string> history = [];
    private readonly TextBlock footer = Text("●  等待连接", 12, "#91A4C1");
    private readonly TextBlock state = Text("尚未连接 C30", 18);
    private readonly TextBlock detail = Text("先检查注入连接，再使用保存的账号登录。", 12, "#738097");
    private readonly TextBlock elapsed = Text("—", 26);
    private readonly TextBlock deliveries = Text("PostHog / Sentry：默认启用，待配置项目。", 12, "#738097");
    private readonly ComboBox homeProfiles = new();
    private CancellationTokenSource? running;
    private bool exiting;
    private int page;
    private Guid? editing;
    private IReadOnlyList<Subject> subjects = [];

    public MainWindow(Store store, Settings settings, string helper)
    {
        this.store = store; this.settings = settings;
        accountBook = new(store, settings);
        coordinator = new(store, settings, helper);
        telemetry = new(settings.Telemetry); queue = new(store, telemetry);
        coordinator.DiagnosticReady += queue.Add;
        coordinator.Progress += message => Dispatcher.Invoke(() => { state.Text = message; footer.Text = "●  正在连接"; });
        queue.Delivered += result => Dispatcher.InvokeAsync(() => deliveries.Text = string.Join("  ·  ", result.Select(x => x.Provider + ": " + DeliveryLabel(x.State))));
        Title = "C30 Super Login"; Width = 1120; Height = 780; MinWidth = 920; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("#F5F7FB"); FontFamily = new FontFamily("Microsoft YaHei UI");
        var root = new Grid(); root.ColumnDefinitions.Add(new() { Width = new GridLength(222) }); root.ColumnDefinitions.Add(new());
        var sidebar = new Grid { Background = Brush("#16243C") }; root.Children.Add(sidebar);
        sidebar.RowDefinitions.Add(new()); sidebar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var nav = new StackPanel { Margin = new Thickness(20, 28, 14, 20) }; sidebar.Children.Add(nav);
        var logo = new Border { Background = Brush("#4D7CF8"), CornerRadius = new CornerRadius(14), Width = 54, Height = 54, HorizontalAlignment = HorizontalAlignment.Left, Child = Text("C30", 17, "#FFFFFF", true) };
        ((TextBlock)logo.Child).HorizontalAlignment = HorizontalAlignment.Center; ((TextBlock)logo.Child).VerticalAlignment = VerticalAlignment.Center;
        nav.Children.Add(logo); nav.Children.Add(Text("Super Login", 21, "#FFFFFF", true, 16)); nav.Children.Add(Text("教学准备，快一步。", 11, "#93A6C4", top: 5));
        nav.Children.Add(new Border { Height = 32 });
        string[] labels = ["⌂   概览", "◉   账号档案", "◷   课程自动化", "⇄   连接与诊断", "◇   隐私与上报", "ⓘ   关于"];
        for (int i = 0; i < labels.Length; i++)
        {
            int index = i; var b = Button(labels[i], () => ShowPage(index)); b.HorizontalContentAlignment = HorizontalAlignment.Left;
            b.Margin = new Thickness(0, 0, 0, 7); b.Padding = new Thickness(14, 13, 14, 13); b.Foreground = Brush("#B6C4DB"); b.Background = Brushes.Transparent;
            navigation.Add(b); nav.Children.Add(b);
        }
        var bottom = new StackPanel { Margin = new Thickness(26, 18, 12, 24) }; bottom.Children.Add(footer); bottom.Children.Add(Text("0.1.0 preview  /  x86 bridge", 10, "#687D9E", top: 12));
        Grid.SetRow(bottom, 1); sidebar.Children.Add(bottom);
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1); root.Children.Add(scroll); Content = root;
        tray = new() { Icon = System.Drawing.SystemIcons.Application, Text = "C30 Super Login · 待连接", Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(Restore);
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => Dispatcher.Invoke(Restore));
        menu.Items.Add("跳过下一次自动登录", null, (_, _) => { store.SetSkipNext(); Dispatcher.Invoke(() => Note("已设置跳过下一次自动登录")); });
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(() => { exiting = true; Close(); })); tray.ContextMenuStrip = menu;
        Closing += async (_, e) =>
        {
            if (!exiting) { e.Cancel = true; Hide(); return; }
            running?.Cancel(); tray.Dispose(); await queue.DisposeAsync(); telemetry.Dispose(); System.Windows.Application.Current.Shutdown();
        };
        ShowPage(0);
        queue.Add(new(DiagnosticKind.AppStarted, DiagnosticStage.Startup, Target.ReadInstallVersion(settings.InstallRoot)));
    }
    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    private static TextBlock Text(string value, double size = 13, string color = "#17253C", bool bold = false, double top = 0) => new()
    { Text = value, FontSize = size, Foreground = Brush(color), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, top, 0, 0) };
    private static Button Button(string label, Action action, bool primary = false)
    {
        var result = new Button { Content = label };
        if (primary) { result.Background = Brush("#4776F5"); result.Foreground = Brushes.White; }
        result.Click += (_, _) => action(); return result;
    }
    private static Border Card(UIElement child, double bottom = 18) => new()
    { Background = Brushes.White, CornerRadius = new CornerRadius(13), BorderBrush = Brush("#E4E9F2"), BorderThickness = new Thickness(1), Padding = new Thickness(23), Margin = new Thickness(0, 0, 0, bottom), Child = child };
    private static StackPanel Stack(params UIElement[] items) { var panel = new StackPanel(); foreach (var i in items) panel.Children.Add(i); return panel; }
    private static StackPanel Row(params UIElement[] items) { var panel = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var i in items) panel.Children.Add(i); return panel; }
    private static void Label(Panel panel, string value) => panel.Children.Add(Text(value, 12, "#63728B"));
    private void Header(string title, string subtitle)
    { content.Children.Add(Text("C30 SUPER LOGIN   /   " + new[] { "OVERVIEW", "PROFILES", "AUTOMATION", "DIAGNOSTICS", "TELEMETRY", "ABOUT" }[page], 10, "#7D8BA2", true)); content.Children.Add(Text(title, 27, bold: true, top: 14)); content.Children.Add(Text(subtitle, 12, "#738097", top: 8)); content.Children.Add(new Border { Height = 27 }); }
    private void ShowPage(int index)
    {
        // Detach reusable controls before clearing pages.
        foreach (var item in new FrameworkElement[] { state, detail, elapsed, deliveries, homeProfiles }) if (item.Parent is Panel parent) parent.Children.Remove(item);
        page = index; content.Children.Clear();
        for (int i = 0; i < navigation.Count; i++) { navigation[i].Background = i == index ? Brush("#283E61") : Brushes.Transparent; navigation[i].Foreground = Brush(i == index ? "#FFFFFF" : "#B6C4DB"); }
        switch (index) { case 0: Home(); break; case 1: Profiles(); break; case 2: Automation(); break; case 3: Diagnostics(); break; case 4: Privacy(); break; case 5: About(); break; }
    }
    private void FillProfiles(ComboBox box)
    {
        box.ItemsSource = settings.Profiles; box.DisplayMemberPath = "Label"; box.SelectedValuePath = "Id";
        if (settings.Profiles.Count > 0) box.SelectedIndex = 0;
    }
    private void Home()
    {
        Header("少一点等待，多一点课堂。", "原生注入 · 不依赖图像识别 · 不显示常驻横幅");
        var status = Stack(Text("CONNECTION", 10, "#8C9AB0", true), state, detail); state.Margin = new Thickness(0, 12, 0, 8);
        content.Children.Add(Card(status));
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1.6, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new());
        FillProfiles(homeProfiles);
        var login = Stack(Text("快速登录", 18, bold: true), Text("选择本机加密保存的账号档案", 12, "#738097", top: 9), homeProfiles,
            Row(Button("立即登录  →", () => _ = Run(false), true), Button("检查连接", () => _ = Run(true))),
            Text("还没有档案？在「账号档案」中添加。", 11, "#8491A7", top: 20));
        var left = Card(login); left.Margin = new Thickness(0, 0, 18, 18); grid.Children.Add(left);
        var metrics = Stack(Text("最近一次操作耗时", 12, "#738097"), elapsed, Text("尚未测试时不预估速度", 10, "#8B98AD", top: 5), new Border { Height = 22 },
            Text("C30  " + Target.ReadInstallVersion(settings.InstallRoot), 13, bold: true), Text("DuiLib / CEF  ·  x86", 11, "#738097", top: 7));
        elapsed.Margin = new Thickness(0, 10, 0, 0); var right = Card(metrics); Grid.SetColumn(right, 1); grid.Children.Add(right); content.Children.Add(grid);
        content.Children.Add(Card(Stack(Text("按你的课堂节奏运行", 16, bold: true), Text("课程触发可以静默执行，也可以随时跳过一次。", 12, "#738097", top: 8),
            new Border { Height = 16 }, Row(Button("配置 ClassIsland", () => ShowPage(2)), Button("跳过下一次", () => { store.SetSkipNext(); Note("已设置：跳过下一次自动登录"); }),
                Button(settings.AutomationPaused ? "恢复自动化" : "暂停自动化", () => { settings.AutomationPaused = !settings.AutomationPaused; store.Save(settings); ShowPage(0); })))));
        content.Children.Add(Text("提交回执仅表示请求已交给 C30；账号认证结果以 C30 的实际显示为准。", 11, "#8491A7"));
    }
    private async Task Run(bool probe, Guid? requestedId = null, bool readAccounts = false)
    {
        if (running is not null) return;
        running = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try
        {
            Guid? selected = requestedId ?? (homeProfiles.SelectedItem is AccountProfile profile ? profile.Id : settings.Profiles.FirstOrDefault()?.Id);
            var result = await coordinator.RunAsync(selected, probe, false, running.Token, readAccounts);
            state.Text = result.Code == "ready" ? "登录页已连接" : result.Submitted ? "登录请求已提交" : "等待处理";
            detail.Text = result.Message; elapsed.Text = result.ElapsedMs < 1000 ? $"{result.ElapsedMs:F0} ms" : $"{result.ElapsedMs / 1000:F2} s";
            footer.Text = result.Code == "ready" || result.Submitted ? "●  连接正常" : "●  需要关注";
            tray.Text = "C30 Super Login · " + (result.Submitted ? "已提交" : result.Code == "ready" ? "已连接" : "需要关注");
            Note(result.Message);
            if ((result.Code is not ("ready" or "accounts_synced") && !result.Submitted) || !settings.QuietSuccess)
            { tray.BalloonTipTitle = "C30 Super Login"; tray.BalloonTipText = result.Message; tray.ShowBalloonTip(3000); }
            File.WriteAllText(Path.Combine(store.Root, "last-operation.json"), Json.Serialize(result));
            if (readAccounts && page == 1) ShowPage(1);
        }
        finally { running.Dispose(); running = null; }
    }
    private void Profiles()
    {
        Header("账号档案", "读取助手和 C30 已保存记录；同一账号合并，档案 ID 保持稳定，调用时不传密码。");
        var grid = new DataGrid { ItemsSource = accountBook.Read(), IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false,
            SelectionMode = DataGridSelectionMode.Single, MaxHeight = 250, Margin = new Thickness(0, 14, 0, 16), HeadersVisibility = DataGridHeadersVisibility.Column };
        foreach (var column in new[] { ("显示名称", "Label"), ("账号", "Username"), ("来源", "Source"), ("可登录", "CanLogin"), ("档案 ID", "Id") })
            grid.Columns.Add(new DataGridTextColumn { Header = column.Item1, Binding = new System.Windows.Data.Binding(column.Item2), Width = DataGridLength.SizeToCells });
        var lookup = new TextBox { ToolTip = "输入助手档案 GUID；不是 C30 用户编号" };
        void LoginId(string value) {
            if (accountBook.Resolve(value) is not Guid id) { Note("档案 ID 不存在，请先读取或新增账号"); return; }
            _ = Run(false, id);
        }
        content.Children.Add(Card(Stack(Text($"已保存 {settings.Profiles.Count} 个账号", 17, bold: true), grid,
            Row(Button("读取助手记录", () => { settings.Profiles = store.Load().Profiles; ShowPage(1); Note("已重新读取助手记录"); }),
                Button("读取 C30 已保存账号", () => _ = Run(true, readAccounts: true), true)),
            Text("C30 未记住密码的记录也会保留，但需要补充密码。导入只读账号元数据，密码留在 C30 内。", 11, "#8491A7", top: 14))));
        content.Children.Add(Card(Stack(Text("通过档案 ID 登录 / 调用", 17, bold: true), lookup,
            Row(Button("按 ID 登录", () => LoginId(lookup.Text), true), Button("所选账号登录", () => { if (grid.SelectedItem is AccountRecord r) LoginId(r.Id.ToString()); }),
                Button("复制所选 ID", () => { if (grid.SelectedItem is AccountRecord r) { Clipboard.SetText(r.Id.ToString()); Note("档案 ID 已复制"); } })),
            Text("调用：C30SuperLogin.exe login --id PROFILE_GUID --headless", 11, "#8491A7", top: 14))));
        var picker = new ComboBox(); FillProfiles(picker); picker.SelectedIndex = -1;
        var name = new TextBox(); var account = new TextBox(); var password = new PasswordBox();
        var profileId = new TextBox { Text = Guid.NewGuid().ToString(), ToolTip = "新建时可指定 GUID；已有档案 ID 不变" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(name, "ProfileLabel");
        System.Windows.Automation.AutomationProperties.SetAutomationId(account, "ProfileUsername");
        System.Windows.Automation.AutomationProperties.SetAutomationId(password, "ProfilePassword");
        System.Windows.Automation.AutomationProperties.SetAutomationId(profileId, "ProfileId");
        System.Windows.Automation.AutomationProperties.SetAutomationId(lookup, "LoginProfileId");
        var form = Stack(Text("添加 / 编辑档案", 17, bold: true), Text("已有档案", 12, "#738097", top: 16), picker);
        Label(form, "助手档案 ID（GUID）"); form.Children.Add(profileId);
        Label(form, "显示名称"); form.Children.Add(name); Label(form, "教师账号"); form.Children.Add(account); Label(form, "密码（编辑时留空表示保留）"); form.Children.Add(password);
        editing = null;
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is not AccountProfile profile) return;
            try { editing = profile.Id; profileId.Text = profile.Id.ToString(); profileId.IsReadOnly = true; name.Text = profile.Label; account.Text = store.LoadSecret(profile.Id).Username; password.Clear(); lookup.Text = profile.Id.ToString(); }
            catch { Note("档案解密失败，请重新填写并保存"); }
        };
        grid.SelectionChanged += (_, _) => { if (grid.SelectedItem is AccountRecord r) picker.SelectedValue = r.Id; };
        void SaveAccount(bool login)
        {
            if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(account.Text)) { Note("请填写显示名称和账号"); return; }
            if (password.Password.Length == 0 && editing is null) { Note("请填写密码"); return; }
            if (!Guid.TryParse(profileId.Text, out Guid requested) || requested == Guid.Empty) { Note("请填写有效的档案 GUID"); return; }
            try
            {
                var profile = accountBook.Save(editing ?? requested, name.Text, account.Text, password.Password, editing.HasValue);
                password.Clear(); Note("已保存/合并账号；档案 ID：" + profile.Id); ShowPage(1);
                if (login) _ = Run(false, profile.Id);
            }
            catch (InvalidDataException) { Note("档案 ID 或账号与已有记录冲突，请选择已有档案编辑；原记录已保留"); }
            catch { Note("档案保存失败，请检查数据目录权限或重新填写密码"); }
        }
        form.Children.Add(Row(Button("保存档案", () => SaveAccount(false), true), Button("保存并登录", () => SaveAccount(true)), Button("新建", () => { editing = null; picker.SelectedIndex = -1; profileId.IsReadOnly = false; profileId.Text = Guid.NewGuid().ToString(); name.Clear(); account.Clear(); password.Clear(); }), Button("删除", () =>
        {
            if (editing is not Guid id || MessageBox.Show("删除此档案及引用它的课程规则？", "删除档案", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            store.DeleteSecret(id); settings.Profiles.RemoveAll(x => x.Id == id); settings.Rules.RemoveAll(x => x.ProfileId == id); store.Save(settings); ShowPage(1);
        })));
        content.Children.Add(Card(form));
    }
    private void Automation()
    {
        Header("让课程自动衔接", "参考 EasiAuto 的档案绑定方式，接入 ClassIsland 课前触发；不把密码放进参数。");
        var profiles = new ComboBox(); FillProfiles(profiles);
        var subject = new ComboBox { ItemsSource = subjects, DisplayMemberPath = "Name", SelectedIndex = subjects.Count > 0 ? 0 : -1 };
        var subjectId = new TextBox(); var subjectName = new TextBox(); var lead = new TextBox { Text = "20", Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
        subject.SelectionChanged += (_, _) => { if (subject.SelectedItem is Subject s) { subjectId.Text = s.Id; subjectName.Text = s.Name; } };
        var form = Stack(Text("课前登录规则", 17, bold: true), Text("在课程开始前触发；相同科目的连堂课不重复触发。", 12, "#738097", top: 8), new Border { Height = 18 });
        Label(form, "账号档案"); form.Children.Add(profiles);
        form.Children.Add(Button("读取 ClassIsland 课表中的科目…", () =>
        {
            var dialog = new OpenFileDialog { Filter = "ClassIsland 课表 (*.json)|*.json" };
            if (dialog.ShowDialog() != true) return;
            try { subjects = ClassIsland.ReadSubjects(dialog.FileName); ShowPage(2); Note("已读取科目，原课表未修改"); } catch { Note("所选文件不是有效的 ClassIsland 课表"); }
        })); form.Children.Add(subject);
        Label(form, "科目 ID（也可手动填写）"); form.Children.Add(subjectId); Label(form, "科目名称"); form.Children.Add(subjectName); Label(form, "提前秒数  0–600"); form.Children.Add(lead);
        if (subject.SelectedItem is Subject initial) { subjectId.Text = initial.Id; subjectName.Text = initial.Name; }
        form.Children.Add(Button("添加规则", () =>
        {
            if (profiles.SelectedItem is not AccountProfile p || !int.TryParse(lead.Text, out int seconds) || seconds is < 0 or > 600 || string.IsNullOrWhiteSpace(subjectId.Text)) { Note("请选择档案、填写科目和有效的提前秒数"); return; }
            settings.Rules.RemoveAll(x => x.SubjectId == subjectId.Text); settings.Rules.Add(new(Guid.NewGuid(), p.Id, subjectId.Text, subjectName.Text, seconds)); store.Save(settings); ShowPage(2);
        }, true)); content.Children.Add(Card(form));
        var list = Stack(Text($"已配置 {settings.Rules.Count} 条规则", 16, bold: true));
        foreach (var rule in settings.Rules)
        {
            var line = Row(Text($"{rule.SubjectName}   ·   提前 {rule.LeadSeconds} 秒", 13, top: 10), Button("移除", () => { settings.Rules.Remove(rule); store.Save(settings); ShowPage(2); })); line.Margin = new Thickness(0, 12, 0, 0); list.Children.Add(line);
        }
        list.Children.Add(new Border { Height = 20 });
        list.Children.Add(Button("导出 ClassIsland 自动化 JSON…", () =>
        {
            var dialog = new SaveFileDialog { FileName = "C30SuperLogin.json", Filter = "ClassIsland 自动化配置 (*.json)|*.json" };
            if (dialog.ShowDialog() != true) return;
            if (Process.GetProcesses().Any(p => { using (p) { try { return p.ProcessName is "ClassIsland" or "ClassIsland.Desktop"; } catch { return false; } } }))
            { Note("请先退出 ClassIsland，再导出配置，避免配置被运行中的程序覆盖"); return; }
            if (File.Exists(dialog.FileName)) File.Copy(dialog.FileName, dialog.FileName + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".bak");
            string data = ClassIsland.Export(settings.Rules, Environment.ProcessPath!);
            if (File.Exists(dialog.FileName)) data = ClassIsland.Merge(File.ReadAllText(dialog.FileName), data);
            File.WriteAllText(dialog.FileName, data); Note("自动化已导出；已有其他规则已保留，覆盖前已备份");
        }, true));
        list.Children.Add(Text("选择 ClassIsland 的 Config/Automations/<配置名>.json，或导出为独立配置。请在 ClassIsland 中选中该配置后验证一次触发。", 11, "#8491A7", top: 14)); content.Children.Add(Card(list));
    }
    private void Diagnostics()
    {
        Header("连接与诊断", "仅适配已核对的 C30 模块。版本不匹配时停止注入，不猜测地址。");
        var root = new TextBox { Text = settings.InstallRoot };
        content.Children.Add(Card(Stack(Text("C30 安装目录", 16, bold: true), root, Row(Button("保存路径", () => { settings.InstallRoot = root.Text; store.Save(settings); Note("安装目录已保存"); }), Button("检查注入连接", () => _ = Run(true), true)),
            Text("当前安装版本：" + Target.ReadInstallVersion(settings.InstallRoot), 12, "#738097", top: 16),
            Text("支持：1.3.1610.0 · DuiLib 哈希匹配 · x86 Windows 消息钩子", 11, "#8491A7", top: 8))));
        var quiet = new CheckBox { Content = "快速成功保持安静，仅在异常时显示托盘通知", IsChecked = settings.QuietSuccess };
        quiet.Click += (_, _) => { settings.QuietSuccess = quiet.IsChecked == true; store.Save(settings); };
        var launch = new CheckBox { Content = "没有检测到进程时启动 C30", IsChecked = settings.LaunchC30 };
        launch.Click += (_, _) => { settings.LaunchC30 = launch.IsChecked == true; store.Save(settings); };
        content.Children.Add(Card(Stack(quiet, launch, Button("打开本地诊断目录", () => Process.Start(new ProcessStartInfo("explorer.exe", store.Root) { UseShellExecute = true })))));
        var logs = Stack(Text("本次会话", 16, bold: true)); foreach (var line in history.TakeLast(12).Reverse()) logs.Children.Add(Text(line, 12, "#738097", top: 12));
        if (history.Count == 0) logs.Children.Add(Text("暂无操作记录。点击检查连接开始。", 12, "#8491A7", top: 15)); content.Children.Add(Card(logs));
    }
    private void Privacy()
    {
        Header("隐私与上报", "PostHog + Sentry 默认启用；失败时分别尝试，至少一路成功才记为已上传。");
        var posthog = new CheckBox { Content = "启用 PostHog：功能使用与耗时统计", IsChecked = settings.Telemetry.PostHogEnabled };
        var sentry = new CheckBox { Content = "启用 Sentry：注入与应用错误诊断", IsChecked = settings.Telemetry.SentryEnabled };
        var host = new TextBox { Text = settings.Telemetry.PostHogHost }; var key = new TextBox { Text = settings.Telemetry.PostHogKey }; var dsn = new TextBox { Text = settings.Telemetry.SentryDsn };
        var form = Stack(posthog, sentry, Text("项目配置留空时，诊断会限量保存在本机，等待你补充配置。", 12, "#738097"), new Border { Height = 20 });
        Label(form, "PostHog Host"); form.Children.Add(host); Label(form, "PostHog Project API Key"); form.Children.Add(key); Label(form, "Sentry DSN"); form.Children.Add(dsn);
        void Save() { settings.Telemetry.PostHogEnabled = posthog.IsChecked == true; settings.Telemetry.SentryEnabled = sentry.IsChecked == true;
            settings.Telemetry.PostHogHost = host.Text.Trim(); settings.Telemetry.PostHogKey = key.Text.Trim(); settings.Telemetry.SentryDsn = dsn.Text.Trim(); store.Save(settings); }
        form.Children.Add(Row(Button("保存配置", () => { Save(); Note("上报配置已保存"); }, true), Button("发送测试事件", async () =>
        { Save(); var result = await telemetry.PublishAsync(new(DiagnosticKind.TestEvent, DiagnosticStage.Application, Target.ReadInstallVersion(settings.InstallRoot))); deliveries.Text = string.Join("  ·  ", result.Select(x => x.Provider + ": " + DeliveryLabel(x.State))); })));
        deliveries.Margin = new Thickness(0, 18, 0, 0); form.Children.Add(deliveries); content.Children.Add(Card(form));
        content.Children.Add(Card(Stack(Text("只上传定位问题所需的字段", 16, bold: true), Text("C30 版本 · 适配器版本 · 失败阶段与原因码 · Windows 错误码 · 耗时 · 随机安装标识", 12, "#738097", top: 14),
            Text("不上传账号、密码、令牌、班级信息、页面内容、截图或会话录屏。不开启人物档案、网络请求抓取和自动 breadcrumb。", 12, "#738097", top: 12),
            Text("也可通过 C30_POSTHOG_KEY、C30_POSTHOG_HOST、C30_SENTRY_DSN 环境变量部署。", 11, "#8491A7", top: 16))));
    }
    private void About()
    {
        Header("为课堂留出时间", "C30 Super Login  ·  0.1.0 preview");
        content.Children.Add(Card(Stack(Text("原生桥，不走 OCR。", 21, bold: true), Text("界面保持安静，诊断保持清晰。首版专注账号档案、注入连接、ClassIsland 自动化和版本化错误上报。", 13, "#738097", top: 14),
            Text("参考 EasiAuto 的页面分组与档案绑定思路，独立实现 C30 注入适配。ClassIsland 配置采用公开动作与规则 ID。", 12, "#738097", top: 20),
            new Border { Height = 20 }, Row(Button("EasiAuto ↗", () => Open("https://github.com/hxabcd/EasiAuto")), Button("ClassIsland ↗", () => Open("https://classisland.tech"))))));
        content.Children.Add(Text("当前仅支持经哈希核对的 C30 1.3.1610.0。C30 授权检测与本地激活从桌面版「关于」页手动触发。", 12, "#8491A7"));
        content.Children.Add(Button("退出程序", () => { exiting = true; Close(); }));
    }
    private static void Open(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    private void Restore() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void Note(string message) { history.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + message); detail.Text = message; if (page == 3) ShowPage(3); }
    private static string DeliveryLabel(string state) => state switch { "sent" => "已上传", "not_configured" => "待配置", "disabled" => "已关闭", "not_an_error" => "本次非错误事件", _ => "上传失败（本地保留）" };
    public void RecordUnhandled() => queue.Add(new(DiagnosticKind.UnhandledError, DiagnosticStage.Application, Target.ReadInstallVersion(settings.InstallRoot)));
}
