using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using C30SuperLogin.Core;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;

namespace C30SuperLogin.Desktop;

public sealed class MainWindow : AppWindow
{
    private readonly Store store;
    private readonly Settings settings;
    private readonly AccountBook accounts;
    private readonly Coordinator coordinator;
    private readonly Telemetry telemetry;
    private readonly DiagnosticQueue queue;
    private readonly AppUpdates updates;
    private readonly TextBlock updateStatus = T("还未检查更新", 12, muted: true);
    private readonly ContentControl host = new();
    private readonly NavigationView navigation = new() { PaneDisplayMode = NavigationViewPaneDisplayMode.Left, OpenPaneLength = 220, IsBackButtonVisible = false, IsPaneToggleButtonVisible = false, IsSettingsVisible = false };
    private readonly List<NavigationViewItem> nav = [];
    private readonly TextBlock connection = T("尚未连接", 12, muted: true);
    private readonly TextBlock notice = T("", 12, muted: true);
    private readonly TextBlock time = T("—", 13);
    private readonly TextBlock delivery = T("等待配置上报服务", 12, muted: true);
    private readonly TextBlock activationStatus = T("尚未检测 C30 授权状态", 12, muted: true);
    private ComboBox? selected;
    private bool busy, quitting;
    private int page;
    private DispatcherTimer? saveTimer;
    private Action? flushEditor;
    private TrayIcon? tray;
    private DispatcherTimer? runtimeWatcher;
    private IReadOnlyList<Subject> subjects = [];

    public MainWindow(Store store, Settings settings, bool background = false)
    {
        this.store = store; this.settings = settings; accounts = new(store, settings);
        coordinator = new(store, settings, Path.Combine(AppContext.BaseDirectory, "C30Native.exe"));
        telemetry = new(settings.Telemetry); queue = new(store, telemetry);
        updates = new(store, settings);
        updates.Changed += state => Dispatcher.UIThread.Post(() => {
            updateStatus.Text = UpdateText(state);
            if (state == "available") Note("有新版本了，可在「关于」中更新。");
        });
        coordinator.DiagnosticReady += queue.Add;
        coordinator.Progress += message => Dispatcher.UIThread.Post(() => connection.Text = Human(message));
        queue.Delivered += result => Dispatcher.UIThread.Post(() => delivery.Text = string.Join("  ·  ", result.Select(x => x.Provider + "：" + Delivery(x.State))));
        Title = "C30 Super Login"; Width = 1120; Height = 760; MinWidth = 980; MinHeight = 660;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        TitleBar.Height = 36;
        TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.None];
        var brand = Stack(T("C30", 26, bold: true), T("登录助手", 12, muted: true)); brand.Spacing = 3; brand.Margin = new Thickness(24, 20, 16, 22);
        navigation.PaneHeader = brand;
        string[] names = ["概览", "账号档案", "课程自动化", "连接与诊断", "设置", "关于"];
        string[] glyphs = ["\uE80F", "\uE77B", "\uE787", "\uE9D9", "\uE713", "\uE946"];
        for (int i = 0; i < names.Length; i++) {
            int target = i;
            var item = new NavigationItem { Content = names[i], Tag = i, IconSource = Glyph(glyphs[i]), Navigate = () => Show(target) };
            AutomationProperties.SetName(item, names[i]); AutomationProperties.SetAutomationId(item, "Navigation" + i);
            nav.Add(item); if (i < 4) navigation.MenuItems.Add(item); else navigation.FooterMenuItems.Add(item);
        }
        navigation.SelectionChanged += (_, _) => { if (navigation.SelectedItem is NavigationViewItem { Tag: int target } && target != page) Show(target); };
        navigation.ItemInvoked += (_, args) => { if (args.InvokedItemContainer is NavigationViewItem { Tag: int target } && target != page) Show(target); };
        var surface = new Border { CornerRadius = new CornerRadius(8, 0, 0, 0), Child = host }; surface.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
        navigation.Content = surface; Content = navigation;
        Closing += (_, args) => {
            if (quitting) return;
            flushEditor?.Invoke(); args.Cancel = true;
            if (settings.RuntimeMode == RuntimeMode.Resident) Hide(); else _ = Quit();
        };
        Opened += async (_, _) => {
            SetupTray(); ApplyRuntimeMode(); _ = updates.CheckAsync();
            runtimeWatcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            runtimeWatcher.Tick += (_, _) => {
                try {
                    var latest = store.Load();
                    if (RuntimeAccess.BrokerBusy) return;
                    settings.RuntimeMode = latest.RuntimeMode; settings.UseAdministrator = latest.UseAdministrator;
                    ApplyRuntimeMode();
                    if (background && (settings.RuntimeMode != RuntimeMode.Resident || !settings.UseAdministrator)) _ = Quit();
                } catch { /* Atomic settings writes may be briefly unavailable; keep the current mode. */ }
            };
            runtimeWatcher.Start();
            if (background) { if (!RuntimeAccess.BrokerRunning) _ = Quit(); else Hide(); return; }
            if (RuntimeAccess.RequiresAdministrator(settings)) {
                if (settings.RuntimeMode == RuntimeMode.Resident) await EnsureResident();
                return; // Do not start an unrequested second elevation for account import.
            }
            if (Target.Find(Installation.Discover(settings).Root).Count > 0) await Run(readAccounts: true, silent: true);
        };
        Show(0); queue.Add(new(DiagnosticKind.AppStarted, DiagnosticStage.Startup, Target.CurrentVersion(settings)));
    }
    private static ToggleSwitch Switch(bool value) => new() { IsChecked = value, OnContent = "", OffContent = "" };
    private static FontIconSource Glyph(string glyph) => new() { Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") };
    private static TextBlock T(string text, double size = 14, bool bold = false, bool muted = false) { var block = new TextBlock {
        Text = text, FontSize = size, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center
    }; block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush"); return block; }
    private static StackPanel Stack(params Control[] children) { var p = new StackPanel { Spacing = 12 }; foreach (var c in children) p.Children.Add(c); return p; }
    private static StackPanel Row(params Control[] children) { var p = new StackPanel { Spacing = 10, Orientation = Orientation.Horizontal }; foreach (var c in children) p.Children.Add(c); return p; }
    private static Button B(string text, Action click, bool accent = false) {
        var b = new Button { Content = text }; if (accent) b.Classes.Add("accent");
        AutomationProperties.SetName(b, text); b.Click += (_, _) => click(); return b;
    }
    private static Border Card(Control child, double padding = 20) { var card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(padding), Child = child }; card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush"); card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush"); return card; }
    private static TextBox Field(string id, string hint = "", bool password = false) {
        var b = new TextBox { Watermark = hint }; if (password) b.PasswordChar = '●'; AutomationProperties.SetAutomationId(b, id); return b;
    }
    private static Control Labeled(string label, Control field) => new StackPanel { Spacing = 6, Children = { T(label, 12), field } };
    private static Grid Columns(Control left, Control right, string sizes = "*,*") {
        var grid = new Grid { ColumnDefinitions = new(sizes), ColumnSpacing = 18 }; grid.Children.Add(left); Grid.SetColumn(right, 1); grid.Children.Add(right); return grid;
    }
    private void Show(int index) {
        var flush = flushEditor; flushEditor = null; flush?.Invoke();
        saveTimer?.Stop(); page = index; selected = null;
        navigation.SelectedItem = nav[index];
        foreach (var control in new Control[] { connection, notice, time, delivery, updateStatus, activationStatus }) if (control.Parent is Panel parent) parent.Children.Remove(control);
        string[] titles = ["概览", "账号档案", "课程自动化", "连接与诊断", "设置", "关于"];
        string[] descriptions = ["课前准备，简单一点。", "填好自动保存，同一账号只留一份。", "跟着课表走，到点自动登录。", "没连上？先在这里检查。", "按你的习惯来。", "上课前，少点几下。"];
        var content = new StackPanel { Spacing = 18, Margin = new Thickness(32, 24, 32, 28), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch };
        var pageTitle = T(titles[index], 28); AutomationProperties.SetAutomationId(pageTitle, "PageTitle");
        var header = Stack(pageTitle, T(descriptions[index], 13, muted: true)); header.Spacing = 6; header.Margin = new Thickness(0, 0, 0, 8); content.Children.Add(header);
        switch (index) { case 0: Home(content); break; case 1: Profiles(content); break; case 2: Automation(content); break; case 3: Diagnostics(content); break; case 4: Preferences(content); break; default: About(content); break; }
        notice.Margin = new Thickness(2, 0); content.Children.Add(notice);
        host.Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    private void Home(StackPanel content) {
        selected = new ComboBox { ItemsSource = settings.Profiles, DisplayMemberBinding = new Avalonia.Data.Binding("Label"), SelectedIndex = settings.Profiles.Count > 0 ? 0 : -1, PlaceholderText = "选择账号", MinWidth = 230 };
        var badge = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(12), Child = new FontIcon { Glyph = "\uE8F1", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 26 } }; badge.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        var intro = Stack(T("开始今天的课堂", 20, bold: true), connection); intro.Spacing = 5;
        var heading = Row(badge, intro); heading.Spacing = 16;
        var actions = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 10, Margin = new Thickness(0, 8, 0, 0) }; actions.Children.Add(selected);
        var login = B("立即登录", () => _ = Run(), true); Grid.SetColumn(login, 1); actions.Children.Add(login);
        var probe = B("检查连接", () => _ = Run(probe: true)); Grid.SetColumn(probe, 2); actions.Children.Add(probe);
        content.Children.Add(Card(Stack(heading, actions), 24));
        content.Children.Add(T("常用功能", 14, bold: true));
        content.Children.Add(Setting("\uE77B", "账号档案", $"已保存 {settings.Profiles.Count} 个账号，随时切换。", B("管理账号", () => Show(1))));
        content.Children.Add(Setting("\uE787", "课前自动登录", "在 ClassIsland 里跟随课程运行。", B("去设置", () => Show(2))));
        content.Children.Add(Setting("\uE769", "这次先不登录", "临时跳过，不影响之后的课程。", B("跳过下一次", () => { store.SetSkipNext(); Note("下一次自动登录会跳过。"); })));
        var version = Installation.Discover(settings).LatestInstalledVersion;
        content.Children.Add(Row(T("C30  " + (version == "unknown" ? "尚未检测到" : version), 12, muted: true), T("·   上次用时", 12, muted: true), time));
    }
    private static Control Setting(string glyph, string title, string description, Control action) {
        var setting = new SettingsExpander { Header = title, Description = description, IconSource = Glyph(glyph), Footer = action };
        AutomationProperties.SetHelpText(setting, description);
        return setting;
    }
    private void Profiles(StackPanel content) {
        var list = new ListBox { MinHeight = 260, MaxHeight = 320 }; list.Classes.Add("accounts");
        var name = Field("ProfileLabel", "给账号起个名字（选填）"); var username = Field("ProfileUsername", "手机号或教师账号"); var password = Field("ProfilePassword", "输入密码", true);
        var id = Field("ProfileId", "保存后自动分配"); id.IsReadOnly = true; id.FontSize = 15;
        var saveState = T("填完后会自动保存", 11, muted: true); Guid? editing = null; bool filling = false, dirty = false;
        string cleanName = "", cleanUsername = "";
        bool IsDirty() => (name.Text ?? "") != cleanName || (username.Text ?? "") != cleanUsername || !string.IsNullOrEmpty(password.Text);
        void Refresh() {
            list.Items.Clear();
            foreach (var record in accounts.Read()) {
                var initial = T(record.Label.Length > 0 ? record.Label[..1] : "C", bold: true); initial.HorizontalAlignment = HorizontalAlignment.Center;
                var mark = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(9), Child = initial }; mark.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                var text = Stack(T(record.Label + "  ·  ID " + record.Number, 13, bold: true), T(record.Username + "  ·  " + (record.CanLogin ? record.Source : "需要补充密码"), 11, muted: true)); text.Spacing = 4;
                var item = new ListBoxItem { Tag = record, Content = Row(mark, text) };
                AutomationProperties.SetAutomationId(item, "AccountRecord_" + record.Id);
                list.Items.Add(item);
            }
        }
        void New() { if (dirty) Save(); saveTimer?.Stop(); filling = true; dirty = false; cleanName = cleanUsername = ""; editing = null; id.Text = ""; name.Text = username.Text = password.Text = ""; list.SelectedIndex = -1; filling = false; saveState.Text = "填完后会自动保存"; }
        Guid? Save(bool refresh = true) {
            if (filling || !dirty) return editing;
            if (string.IsNullOrWhiteSpace(username.Text) || (editing is null && string.IsNullOrEmpty(password.Text))) return null;
            try {
                var saved = accounts.Save(editing, string.IsNullOrWhiteSpace(name.Text) ? username.Text! : name.Text!, username.Text!, password.Text ?? "", editing.HasValue);
                filling = true; editing = saved.Id; id.Text = saved.Number.ToString(); password.Text = ""; cleanName = name.Text ?? ""; cleanUsername = username.Text ?? ""; filling = false; dirty = false; if (refresh) Refresh();
                saveState.Text = "已自动保存  ·  " + DateTime.Now.ToString("HH:mm"); return saved.Id;
            } catch { saveState.Text = "还没保存，请检查账号、密码或已有记录。"; return null; }
        }
        saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) }; saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(); };
        foreach (var box in new[] { name, username, password }) box.TextChanged += (_, _) => { if (!filling && IsDirty()) { dirty = true; saveState.Text = "正在编辑…"; saveTimer.Stop(); saveTimer.Start(); } };
        list.SelectionChanged += (_, _) => {
            if (list.SelectedItem is not ListBoxItem { Tag: AccountRecord record }) return;
            saveTimer.Stop(); Save(refresh: false);
            try { filling = true; editing = record.Id; name.Text = record.Label; username.Text = accounts.Read().First(x => x.Id == record.Id).Username; password.Text = ""; id.Text = record.Number.ToString(); saveState.Text = record.Source + "  ·  修改后自动保存"; }
            finally { cleanName = name.Text ?? ""; cleanUsername = username.Text ?? ""; filling = false; dirty = false; }
        };
        var lookup = Field("LoginProfileId", "输入数字 ID，例如 1"); lookup.FontSize = 14;
        flushEditor = () => Save();
        foreach (var box in new[] { name, username, password }) box.LostFocus += (_, _) => { if (!filling) Save(); };
        var left = Stack(Row(B("新建账号", New, true), B("从 C30 读取", () => _ = Run(readAccounts: true)), B("刷新", () => { var latest = store.Load(); settings.Profiles = latest.Profiles; settings.ProfileAliases = latest.ProfileAliases; settings.ProfileNumberAliases = latest.ProfileNumberAliases; settings.NextProfileNumber = latest.NextProfileNumber; accounts.Deduplicate(); Refresh(); })), list,
            T("列表会合并相同账号，不会越导越多。", 11, muted: true), new Separator(), T("通过 ID 登录", 12, bold: true), lookup,
            B("按 ID 登录", () => { if (accounts.Resolve(lookup.Text) is Guid found) _ = Run(profile: found); else Note("没有这个 ID，请先保存账号。"); })); left.Spacing = 12;
        var right = Stack(T("账号信息", 15, bold: true), Labeled("名称", name), Labeled("登录账号", username), Labeled("密码", password), saveState,
            Row(B("登录此账号", () => { saveTimer?.Stop(); if (Save() is Guid saved) _ = Run(profile: saved); }, true), B("保存档案", () => Save())),
            new Separator(), Labeled("数字 ID", id), Row(B("复制 ID", async () => { if (Clipboard is not null && !string.IsNullOrEmpty(id.Text)) await Clipboard.SetTextAsync(id.Text); }),
                B("删除", () => { if (editing is Guid current) { saveTimer.Stop(); dirty = false; store.DeleteSecret(current); settings.Profiles.RemoveAll(x => x.Id == current); settings.Rules.RemoveAll(x => x.ProfileId == current); store.Save(settings); New(); Refresh(); } })),
            T("C30 已记住密码的账号可以直接用；没记住的，补一次密码就好。", 11, muted: true)); right.Spacing = 12;
        content.Children.Add(Columns(Card(left, 18), Card(right, 20), "1.1*,*")); Refresh(); New();
    }
    private void Automation(StackPanel content) {
        var profile = new ComboBox { ItemsSource = settings.Profiles, DisplayMemberBinding = new Avalonia.Data.Binding("Label"), SelectedIndex = settings.Profiles.Count > 0 ? 0 : -1, PlaceholderText = "选择账号" };
        var subject = Field("SubjectId", "科目 ID"); var title = Field("SubjectName", "例如：数学"); var lead = new NumericUpDown { Minimum = 0, Maximum = 600, Value = 20, FormatString = "0" };
        var choices = new ComboBox { ItemsSource = subjects, DisplayMemberBinding = new Avalonia.Data.Binding("Name"), PlaceholderText = "从课表选择科目" };
        choices.SelectionChanged += (_, _) => { if (choices.SelectedItem is Subject value) { subject.Text = value.Id; title.Text = value.Name; } };
        var form = Stack(T("添加课前任务", 15, bold: true), Labeled("使用账号", profile), B("读取 ClassIsland 课表", async () => {
            var paths = await StorageProvider.OpenFilePickerAsync(new() { Title = "选择课表", AllowMultiple = false }); if (paths.Count == 0) return;
            try { subjects = ClassIsland.ReadSubjects(paths[0].Path.LocalPath); Show(2); } catch { Note("这个文件不是有效课表。请重新选择。"); }
        }), choices, Columns(Labeled("科目名称", title), Labeled("提前几秒", lead)), Labeled("科目 ID", subject), B("添加任务", () => {
            if (profile.SelectedItem is not AccountProfile p || string.IsNullOrWhiteSpace(subject.Text)) { Note("先选账号和科目。"); return; }
            settings.Rules.RemoveAll(x => x.SubjectId == subject.Text); settings.Rules.Add(new(Guid.NewGuid(), p.Id, subject.Text, title.Text ?? "", (int)(lead.Value ?? 20))); store.Save(settings); Show(2);
        }, true));
        var tasks = Stack(T("已安排的任务", 15, bold: true));
        foreach (var rule in settings.Rules) tasks.Children.Add(Setting("\uE787", rule.SubjectName, $"提前 {rule.LeadSeconds} 秒", B("移除", () => { settings.Rules.Remove(rule); store.Save(settings); Show(2); })));
        if (settings.Rules.Count == 0) tasks.Children.Add(T("还没有任务。添加后，导出到 ClassIsland 即可。", 12, muted: true));
        tasks.Children.Add(B("导出到 ClassIsland", async () => {
            if (System.Diagnostics.Process.GetProcessesByName("ClassIsland").Length > 0) { Note("先退出 ClassIsland，避免配置被覆盖。"); return; }
            var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "导出自动化", SuggestedFileName = "C30SuperLogin.json" }); if (file is null) return;
            try { accounts.EnsureNumbers(); string path = file.Path.LocalPath; string data = ClassIsland.Export(settings.Rules, Environment.ProcessPath!, settings.Profiles); if (File.Exists(path)) { File.Copy(path, path + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".bak"); data = ClassIsland.Merge(File.ReadAllText(path), data); } File.WriteAllText(path, data); Note("已导出。请在 ClassIsland 中选择这份配置。"); } catch { Note("导出没成功，原文件已保留。"); }
        }, true)); content.Children.Add(Columns(Card(form), Card(tasks)));
        var pause = Switch(settings.AutomationPaused); pause.Content = "暂停自动登录"; pause.IsCheckedChanged += (_, _) => { settings.AutomationPaused = pause.IsChecked == true; store.Save(settings); }; content.Children.Add(pause);
    }
    private void Diagnostics(StackPanel content) {
        var installation = Installation.Discover(settings);
        var root = Field("InstallRoot", "C30 安装位置"); root.Text = installation.Root;
        content.Children.Add(Card(Stack(T("C30 在哪里？", 15, bold: true), T(settings.AutoDetectInstall ? "自动识别中，每次连接前都会重新检查。" : "使用手动位置，也会检查其中的新版本。", 12, muted: true), root,
            Row(B("立即检测目录", () => {
                    var detected = Installation.Discover(settings, forceAutoDetect: true);
                    if (detected.LatestDirectory is null) { Note("没有找到 C30，原有设置未更改。"); return; }
                    settings.InstallRoot = detected.Root; settings.AutoDetectInstall = true; store.Save(settings); Show(3);
                    Note($"已找到 C30 {detected.LatestInstalledVersion}，并启用后续自动识别。");
                }),
                B("自动识别", () => { settings.AutoDetectInstall = true; store.Save(settings); Show(3); }),
                B("保存位置", () => { settings.InstallRoot = root.Text ?? ""; settings.AutoDetectInstall = false; store.Save(settings); Note("已保存手动位置。新版本文件夹仍会自动识别。"); }),
                B("检查连接", () => _ = Run(probe: true), true)), T("已安装版本：" + installation.LatestInstalledVersion, 12, muted: true), connection)));
        content.Children.Add(Setting("\uE8F2", "先用最快的方式", "优先直接连接；没提交时，才尝试备用控件登录。", T("自动选择", 12, muted: true)));
        var launch = Switch(settings.LaunchC30); launch.Content = "需要时自动打开 C30"; launch.IsCheckedChanged += (_, _) => { settings.LaunchC30 = launch.IsChecked == true; store.Save(settings); }; content.Children.Add(Card(launch, 16));
        content.Children.Add(new Expander { Header = "查看详细信息", Content = T("当前适配 C30 1.3.1610.0。\n首选：x86 注入。备用：Windows 控件填写，不用 OCR。\n已提交但未收到回执时停止，不重复点登录。\nWindows 错误 5：请让本程序和 C30 使用相同运行权限。", 12, muted: true) });
    }
    private void Preferences(StackPanel content) {
        var theme = new ComboBox { ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = settings.Theme == "Light" ? 1 : settings.Theme == "Dark" ? 2 : 0, MinWidth = 145 };
        AutomationProperties.SetAutomationId(theme, "ThemeMode");
        theme.SelectionChanged += (_, _) => { settings.Theme = theme.SelectedIndex == 1 ? "Light" : theme.SelectedIndex == 2 ? "Dark" : "System"; store.Save(settings); App.ApplyTheme(settings.Theme); };
        content.Children.Add(T("外观", 14, bold: true));
        content.Children.Add(Setting("\uE790", "应用主题", "选择浅色、深色，或跟随系统。", theme));
        content.Children.Add(T("运行方式", 14, bold: true));
        var runtime = new ComboBox { ItemsSource = new[] { "后台常驻", "仅调用时运行" }, SelectedIndex = settings.RuntimeMode == RuntimeMode.OnDemand ? 1 : 0, MinWidth = 180 };
        AutomationProperties.SetAutomationId(runtime, "RuntimeMode");
        runtime.SelectionChanged += (_, _) => {
            settings.RuntimeMode = runtime.SelectedIndex == 1 ? RuntimeMode.OnDemand : RuntimeMode.Resident;
            store.Save(settings); ApplyRuntimeMode();
            Note(settings.RuntimeMode == RuntimeMode.Resident ? "关闭窗口后留在托盘，CI 可复用后台。" : "关闭窗口后直接退出；之后由 CLI 按需启动和提权。");
        };
        content.Children.Add(Setting("\uE7F4", "后台还是按需", "开启 UAC 弹窗时推荐常驻；按需模式每次调用可能需要确认。", runtime));
        content.Children.Add(T("连接 C30 必须使用管理员权限，由 Windows 正常授权；CI 自身无需管理员权限。", 12, muted: true));
        content.Children.Add(Row(B("启动管理员后台", () => _ = EnsureResident()), B("重新查看首次引导", () => {
            var guide = new OnboardingWindow(store, settings, exitOnCancel: false); guide.Completed += () => { ApplyRuntimeMode(); Show(4); }; guide.Show(this);
        })));
        var warning = new ComboBox { ItemsSource = new[] { "不提醒", "提前 5 秒", "提前 10 秒" }, SelectedIndex = settings.PreLoginWarningSeconds <= 0 ? 0 : settings.PreLoginWarningSeconds >= 10 ? 2 : 1, MinWidth = 145 };
        AutomationProperties.SetAutomationId(warning, "PreLoginWarning");
        warning.SelectionChanged += (_, _) => { settings.PreLoginWarningSeconds = warning.SelectedIndex == 0 ? 0 : warning.SelectedIndex == 2 ? 10 : 5; store.Save(settings); };
        content.Children.Add(T("自动登录", 14, bold: true));
        content.Children.Add(Setting("\uE7BA", "运行前提醒", "只提醒课表触发的登录，可跳过或推迟一分钟。", warning));
        var automaticUpdates = Switch(settings.Updates.AutoCheck);
        AutomationProperties.SetAutomationId(automaticUpdates, "AutoCheckUpdates"); AutomationProperties.SetName(automaticUpdates, "自动检查更新");
        automaticUpdates.IsCheckedChanged += (_, _) => { settings.Updates.AutoCheck = automaticUpdates.IsChecked == true; store.Save(settings); };
        content.Children.Add(Setting("\uE895", "自动检查更新", "后台检查；下载和重启由你决定。", automaticUpdates));
        content.Children.Add(T("隐私", 14, bold: true));
        var ph = Switch(settings.Telemetry.PostHogEnabled);
        var se = Switch(settings.Telemetry.SentryEnabled);
        AutomationProperties.SetName(ph, "使用情况统计"); AutomationProperties.SetName(se, "出错时自动报告");
        AutomationProperties.SetAutomationId(ph, "UsageStatistics"); AutomationProperties.SetAutomationId(se, "ErrorReports");
        ph.IsCheckedChanged += (_, _) => { settings.Telemetry.PostHogEnabled = ph.IsChecked == true; store.Save(settings); };
        se.IsCheckedChanged += (_, _) => { settings.Telemetry.SentryEnabled = se.IsChecked == true; store.Save(settings); };
        content.Children.Add(Setting("\uE9D9", "使用情况统计", "帮助了解功能的使用次数和用时。", ph));
        content.Children.Add(Setting("\uE946", "出错时自动报告", "只报告与本程序有关的问题。", se));
        content.Children.Add(T("账号、密码和课堂内容不会上传。", 12, muted: true));
    }
    private void About(StackPanel content) {
        content.Children.Add(Card(Stack(T("C30 Super Login", 22, bold: true), T(BuildVersion.Current + "  ·  Preview", 12, muted: true), T("保存账号，快速登录。\n少一点操作，多一点从容。", 14))));
        content.Children.Add(Card(Stack(T("C30 本地授权", 15, bold: true), activationStatus,
            Row(B("检测授权状态", () => _ = RunActivation("status")), B("本地激活（本次运行）", () => _ = RunActivation("local"), true),
                B("打开官方激活", () => _ = RunActivation("open"))),
            T("本地激活只修改当前 C30 进程内存，不写许可证文件、不修改云端绑定；重启 C30 后需要重新手动触发。", 12, muted: true))));
        updateStatus.Text = UpdateText(updates.State);
        content.Children.Add(Card(Stack(T("版本与更新", 15, bold: true), updateStatus,
            Row(B("检查更新", () => _ = updates.CheckAsync(manual: true)), B("下载更新", () => _ = DownloadUpdate()),
                B("重启并更新", () => { flushEditor?.Invoke(); updates.ApplyAndRestart(busy); })),
            T("更新只替换助手，不修改 C30；登录进行中会等待。", 12, muted: true))));
        content.Children.Add(T("相关链接", 14, bold: true));
        content.Children.Add(RelatedLink("EasiAuto", "参考项目：了解其他教学软件自动化的思路。", "查看参考项目 ↗", "https://github.com/hxabcd/EasiAuto"));
        content.Children.Add(RelatedLink("ClassIsland", "课表与课前自动化工具，可配合本助手使用。", "了解 ClassIsland ↗", "https://classisland.tech"));
        content.Children.Add(B("退出程序", () => _ = Quit()));
    }
    private async Task RunActivation(string action) {
        if (busy) return; busy = true;
        try {
            activationStatus.Text = action == "local" ? "正在应用本地授权…" : action == "open" ? "正在打开 C30 激活窗口…" : "正在读取 C30 状态…";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            string[] args = ["activation", action];
            var routed = await RuntimeAccess.RouteAsync(args, store, settings);
            ActivationResult result;
            if (routed?.Activation is { } elevated) result = elevated;
            else if (routed is not null) result = new(routed.Result.Code, routed.Result.Message, C30Version: routed.Result.C30Version, Win32Error: routed.Result.Win32Error);
            else {
                var client = new ActivationClient(settings, Path.Combine(AppContext.BaseDirectory, "C30Native.exe"));
                result = action switch {
                    "local" => await client.ApplyLocalAsync(timeout.Token),
                    "open" => await client.OpenOfficialDialogAsync(timeout.Token),
                    _ => await client.QueryAsync(timeout.Token)
                };
            }
            activationStatus.Text = result.Authorized == true
                ? $"已授权  ·  C30 {result.C30Version}" + (string.IsNullOrWhiteSpace(result.Time) ? "" : "  ·  " + result.Time)
                : result.Code == "activation_requested" ? "已打开 C30 官方激活窗口。" : result.Message;
            File.WriteAllText(Path.Combine(store.Root, "last-activation.json"), Json.Serialize(result));
            Note(result.Message);
        } catch (OperationCanceledException) { activationStatus.Text = "操作超时，请确认 C30 正在运行。"; }
        finally { busy = false; }
    }
    private static Control RelatedLink(string title, string description, string action, string url) {
        using var stream = Avalonia.Platform.AssetLoader.Open(new Uri($"avares://C30SuperLogin/Assets/{title}.png"));
        var image = new Image { Source = new Avalonia.Media.Imaging.Bitmap(stream), Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(image, title + "Logo"); AutomationProperties.SetName(image, title + " 项目图标");
        AutomationProperties.SetHelpText(image, description);
        var row = new Grid { ColumnDefinitions = new("48,*,Auto"), ColumnSpacing = 12 };
        row.Children.Add(image);
        var text = Stack(T(title, 14, bold: true), T(description, 12, muted: true)); text.Spacing = 4; text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1); row.Children.Add(text);
        var link = B(action, () => Open(url)); link.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(link, 2); row.Children.Add(link);
        var card = Card(row, 16); AutomationProperties.SetHelpText(card, description); return card;
    }
    private async Task DownloadUpdate() { using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)); await updates.DownloadAsync(timeout.Token); }
    private string UpdateText(string state) => state switch {
        "idle" => "还未检查更新", "disabled" => "自动检查已关闭，可手动检查。", "not_configured" => "预览版暂未开放在线更新。",
        "invalid_source" => "更新地址有误，请联系维护者。", "portable" => "当前是解压版；安装版支持自动更新。",
        "checking" => "正在检查更新…", "up_to_date" => "已经是最新版本。", "available" => "发现新版本 " + updates.AvailableVersion,
        "downloading" => "正在下载更新…", "downloaded" => "下载完成，方便时重启更新即可。",
        "download_cancelled" => "下载已取消，可以稍后重试。", "busy" => "有登录任务正在进行，请结束后再更新。",
        "not_ready" => "先检查并下载更新，再重启安装。", "download_failed" => "下载没成功，当前版本可继续使用。",
        "apply_failed" => "更新没成功，当前版本和账号已保留。", _ => "暂时没查到更新，稍后再试。"
    };
    private async Task Run(bool probe = false, bool readAccounts = false, Guid? profile = null, bool silent = false) {
        if (busy) return; busy = true;
        try {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            Guid? chosen = profile ?? (selected?.SelectedItem as AccountProfile)?.Id;
            string[] requestArgs = readAccounts ? ["accounts", "sync"] : probe ? ["probe"] : ["login", "--id", chosen?.ToString() ?? ""];
            var routed = await RuntimeAccess.RouteAsync(requestArgs, store, settings);
            var result = routed?.Result ?? await coordinator.RunAsync(chosen, probe, false, timeout.Token, readAccounts);
            connection.Text = result.Submitted ? "请求已发出" : result.Code == "ready" ? "C30 已连接" : result.Code == "accounts_synced" ? "账号已同步" : "等待连接";
            time.Text = result.ElapsedMs < 1000 ? $"{result.ElapsedMs:F0} ms" : $"{result.ElapsedMs / 1000:F1} 秒";
            if (!silent || result.Code == "accounts_synced") Note(result.Code switch {
                "not_running" => "先打开 C30，再试一次。", "accounts_synced" => "账号已同步，重复记录已合并。", "ready" => "已连接，可以登录了。",
                "submitted" => "已发出登录请求，结果请看 C30。", "password_required" => "这个账号没记住密码，补填一次就好。",
                "unsupported_version" => "这个 C30 版本还没适配。", _ => Human(result.Message)
            });
            File.WriteAllText(Path.Combine(store.Root, "last-operation.json"), Json.Serialize(result));
            if (readAccounts && page == 1) Show(1);
            if (probe && result.Code == "ready") { busy = false; await Run(readAccounts: true, silent: true); }
        } finally { busy = false; }
    }
    private void Note(string text) => notice.Text = text;
    private void ApplyRuntimeMode() {
        if (RuntimeAccess.BrokerBusy) return;
        if (settings.RuntimeMode == RuntimeMode.Resident && settings.UseAdministrator && RuntimeAccess.IsAdministrator)
            RuntimeAccess.StartBroker(store.Root, async request => await Dispatcher.UIThread.InvokeAsync(() => App.ExecuteAdminRequest(request, store)));
        else RuntimeAccess.StopBroker();
    }
    private async Task EnsureResident() {
        if (settings.RuntimeMode != RuntimeMode.Resident || !settings.UseAdministrator) { Note("请先选择后台常驻，并开启管理员权限。"); return; }
        if (RuntimeAccess.IsAdministrator) { ApplyRuntimeMode(); Note("管理员后台已就绪。"); return; }
        try {
            // A connected host must be verified, but starting it must not perform a probe/login.
            await Task.Run(() => RuntimeAccess.StartElevated(["--admin-host", "--background", "--data-dir", store.Root]));
            Note("已请求启动管理员后台，授权后 CI 就能复用。");
        } catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { Note("已取消授权，运行方式没有改变。"); }
        catch { Note("后台没有启动，请稍后重试。"); }
    }
    private static string Human(string value) => value.Contains("错误码 5") || value.Contains("权限") ? "暂时没连上。请让本程序和 C30 使用相同运行权限。" : value.Contains("正在注入") ? "正在连接 C30…" : value.Contains("回执") ? "请求已发出，请查看 C30 的登录结果。" : value;
    private static string Delivery(string state) => state switch { "sent" => "已发送", "not_configured" => "待配置", "disabled" => "已关闭", "not_an_error" => "无需发送", _ => "暂未发送，稍后重试" };
    private static void Open(string url) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    private void SetupTray() {
        try {
            tray = new TrayIcon { ToolTipText = "C30 Super Login" };
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://C30SuperLogin/Assets/app.ico"));
            var windowIcon = new WindowIcon(stream); ((Window)this).Icon = windowIcon; tray.Icon = windowIcon;
            using var png = Avalonia.Platform.AssetLoader.Open(new Uri("avares://C30SuperLogin/Assets/app.png")); Icon = new Avalonia.Media.Imaging.Bitmap(png);
            tray.Clicked += (_, _) => { Show(); Activate(); };
            var menu = new NativeMenu(); var show = new NativeMenuItem("打开窗口"); show.Click += (_, _) => { Show(); Activate(); }; menu.Items.Add(show);
            var skip = new NativeMenuItem("跳过下一次"); skip.Click += (_, _) => store.SetSkipNext(); menu.Items.Add(skip);
            var exit = new NativeMenuItem("退出"); exit.Click += (_, _) => _ = Quit(); menu.Items.Add(exit); tray.Menu = menu;
            TrayIcon.SetIcons(Application.Current!, new TrayIcons { tray });
        } catch { /* A missing tray icon must not affect login. */ }
    }
    private async Task Quit() { if (quitting) return; if (busy || RuntimeAccess.BrokerBusy) { Note("登录任务正在进行，结束后再退出。"); return; } quitting = true; runtimeWatcher?.Stop(); RuntimeAccess.StopBroker(); flushEditor?.Invoke(); saveTimer?.Stop(); tray?.Dispose(); await queue.DisposeAsync(); telemetry.Dispose(); Close(); (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(); }
}
