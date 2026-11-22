using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Serilog;
using Serilog.Events;

namespace Cloudreve.Desktop;

internal sealed partial class SettingsWindow : Window
{
    private readonly WorkspaceHost host;
    private readonly ILoginItemService loginItem;
    private Action? refreshLoginItem;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private (MountedDrive Drive, bool Expired, bool Lost, (long Used, long Total)? Space, FolderPair? Pair, string Name)[] shown = [];
    private bool rendered;

    internal SettingsWindow(WorkspaceHost host, ILoginItemService? loginItem = null)
    {
        this.host = host;
        this.loginItem = loginItem ?? new MacLoginItem();
        InitializeComponent();
        AddButton.Content = "+  " + DesktopText.Get("popup.newDrive");
        AddButton.Click += (_, _) => { host.OpenAdd(); };
        Navigation.SelectionChanged += (_, _) =>
        {
            DrivePage.IsVisible = Navigation.SelectedIndex == 0;
            GeneralPage.IsVisible = Navigation.SelectedIndex == 1;
            AboutPage.IsVisible = Navigation.SelectedIndex == 2;
            ErrorMessage.IsVisible = false;
            Scroll.Offset = default;
            if (GeneralPage.IsVisible)
            {
                BuildGeneral();
            }
        };
        var version = typeof(SettingsWindow).Assembly.GetName().Version!;
        VersionLabel.Text = $"v{version.ToString(3)}" + (version.Major == 0 ? "   " + DesktopText.Get("about.preview") : "");
        const string repositoryUrl = "https://github.com/799953468/cloudreve-mac";
        var aboutLinks = new[]
        {
            (DesktopText.Get("about.homepage"), repositoryUrl),
            (DesktopText.Get("about.releases"), repositoryUrl + "/releases"),
            (DesktopText.Get("about.reportIssue"), repositoryUrl + "/issues/new/choose")
        };
        foreach (var (label, url) in aboutLinks)
        {
            AboutPage.Children.Add(ActionButton(label, "Open project link", async () => { await Launcher.LaunchUriAsync(new Uri(url)); }));
        }

        host.Changed += RefreshDrives;
        Activated += (_, _) =>
        {
            if (GeneralPage.IsVisible)
            {
                refreshLoginItem?.Invoke();
            }
        };
        timer.Tick += (_, _) =>
        {
            if (DrivePage.IsVisible)
            {
                RefreshDrives();
            }
        };
        Opened += (_, _) =>
        {
            RefreshDrives();
            timer.Start();
        };
        Closed += (_, _) =>
        {
            timer.Stop();
            host.Changed -= RefreshDrives;
        };
        RefreshDrives();
    }

    private Button ActionButton(string label, string operation, Func<Task> action)
    {
        var button = new Button { Content = label, Classes = { "tool" } };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                ErrorMessage.IsVisible = false;
                await action();
            }
            catch (Exception ex)
            {
                Log.Logger.WriteFailure(LogEventLevel.Warning, operation, ex);
                ErrorMessage.Text = ex.Message;
                ErrorMessage.IsVisible = true;
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        return button;
    }

    private void RefreshDrives()
    {
        var next = host.Drives.Select(d => (d, d.Expired, d.PushLost, d.Capacity, d.Pair, d.Account.Name)).ToArray();
        if (rendered && shown.SequenceEqual(next))
        {
            return;
        }

        shown = next;
        rendered = true;
        Cards.Children.Clear();
        if (shown.Length == 0)
        {
            Cards.Children.Add(new TextBlock { Classes = { "secondary" }, Text = DesktopText.Get("settings.noDrives"), TextWrapping = TextWrapping.Wrap });
        }

        foreach (var (drive, expired, lost, space, pair, name) in shown)
        {
            var content = new StackPanel { Spacing = 8 };
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("48,*"), ColumnSpacing = 16 };
            head.Children.Add(new Image { Source = DriveLogo, Width = 40, Height = 40, Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Top });
            var details = new StackPanel { Spacing = 4 };
            Grid.SetColumn(details, 1);
            head.Children.Add(details);
            var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 2) };
            title.Children.Add(new TextBlock { Text = name, FontSize = 16, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var status = new TextBlock
            {
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                Classes = { expired ? "failure" : lost ? "warning" : "healthy" },
                Text = "● " + DesktopText.Get("settings.driveStatus." + (expired ? "credentialExpired" : lost ? "eventPushLost" : "active"))
            };
            Grid.SetColumn(status, 1);
            title.Children.Add(status);
            details.Children.Add(title);
            var site = ActionButton(drive.Account.Site.TrimEnd('/'), "Open drive site", async () => { await Launcher.LaunchUriAsync(new Uri(drive.Account.Site)); });
            StyleLink(site, "SiteIcon");
            details.Children.Add(site);
            if (pair != null)
            {
                var folder = ActionButton(Path.GetFileName(pair.Local), "Open local sync folder", () => DesktopFiles.OpenFolderAsync(this, pair.Local));
                ToolTip.SetTip(folder, pair.Local);
                StyleLink(folder, "FolderOutline");
                details.Children.Add(folder);
            }

            content.Children.Add(head);
            if (space is { } quota && quota.Total > 0)
            {
                double pct = 100d * quota.Used / quota.Total;
                var usage = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8, Margin = new Thickness(0, 8, 0, 0) };
                usage.Children.Add(new TextBlock { Classes = { "secondary" }, Text = DesktopText.Get("settings.storage"), FontSize = 12 });
                var label = new TextBlock
                {
                    Classes = { "secondary" }, FontSize = 12, TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.Wrap,
                    Text = $"{DesktopFiles.Bytes(quota.Used)} / {DesktopFiles.Bytes(quota.Total)} ({pct:0.#}%)"
                };
                Grid.SetColumn(label, 1);
                usage.Children.Add(label);
                details.Children.Add(usage);
                details.Children.Add(new ProgressBar { Classes = { "syncProgress" }, Height = 6, Maximum = 100, Value = pct });
            }

            content.Children.Add(new Separator { Classes = { "cardDivider" } });
            var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 4 };
            footer.Children.Add(actions);
            if (expired)
            {
                var reauthorize = ActionButton(DesktopText.Get("settings.reauthorize"), "Reauthorize drive", () =>
                {
                    host.OpenAdd(drive);
                    return Task.CompletedTask;
                });
                reauthorize.Classes.Add("subtle");
                actions.Children.Add(reauthorize);
            }

            if (pair != null)
            {
                var rules = ActionButton(DesktopText.Get("settings.ignorePatterns"), "Edit ignore rules", () => EditRulesAsync(drive));
                rules.Classes.Add("subtle");
                IconLabel(rules, "FilterIcon");
                actions.Children.Add(rules);
            }

            var delete = ActionButton(DesktopText.Get("settings.deleteDrive"), "Remove drive", async () =>
            {
                string message = DesktopText.Get("settings.deleteDriveConfirm").Replace("{{name}}", name);
                if (await ConfirmDeleteAsync(message))
                {
                    await host.RemoveAsync(drive);
                }
            });
            delete.Classes.Add("danger");
            IconLabel(delete, "TrashIcon");
            Grid.SetColumn(delete, 1);
            footer.Children.Add(delete);
            content.Children.Add(footer);
            Cards.Children.Add(new Border { Classes = { "card" }, Child = content });
        }
    }

    private static readonly Avalonia.Media.Imaging.Bitmap DriveLogo = LoadLogo();

    private static Avalonia.Media.Imaging.Bitmap LoadLogo()
    {
        using (var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://CloudreveMac/Assets/cloudreve.png")))
        {
            return new Avalonia.Media.Imaging.Bitmap(stream);
        }
    }

    private void IconLabel(Button button, string icon)
    {
        if (!this.TryFindResource(icon, out var geometry) || geometry is not Geometry data)
        {
            return;
        }

        string label = button.Content?.ToString() ?? "";
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6 };
        line.Children.Add(new PathIcon { Data = data, Width = 14, Height = 14 });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(text, 1);
        line.Children.Add(text);
        Avalonia.Automation.AutomationProperties.SetName(button, label);
        button.Content = line;
    }

    private void StyleLink(Button button, string icon)
    {
        button.Padding = new Thickness(0);
        button.FontSize = 12;
        button.MinHeight = 20;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.Classes.Add("driveLink");
        IconLabel(button, icon);
    }

    private async Task EditRulesAsync(MountedDrive drive)
    {
        var dialog = new IgnoreRulesWindow(drive.Account.Name, drive.PairStore.ReadRules(drive.Pair!), drive.SaveRules);
        await dialog.ShowDialog(this);
    }

    private async Task<bool> ConfirmDeleteAsync(string message)
    {
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        var dialog = new Window
            { Title = DesktopText.Get("settings.deleteDrive"), Width = 440, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = body };
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        body.Children.Add(buttons);
        var cancel = new Button { Content = DesktopText.Get("settings.cancel"), IsCancel = true };
        var ok = new Button { Content = dialog.Title };
        cancel.Click += (_, _) => { dialog.Close(false); };
        ok.Click += (_, _) => { dialog.Close(true); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        return await dialog.ShowDialog<bool>(this);
    }

    private void BuildGeneral()
    {
        var prefs = Program.Preferences;
        GeneralPage.Children.Clear();
        GeneralPage.Children.Add(new TextBlock { Classes = { "pageTitle" }, Text = DesktopText.Get("settings.general") });
        var launch = Group("launchSettings");
        AddLoginItemSetting(launch);
        Toggle(launch, "fastPopupLaunch", prefs.Current.FastPopup, value => prefs.Save(prefs.Current with { FastPopup = value }));
        var languages = new[] { UiLanguage.SystemDefault, UiLanguage.Chinese, UiLanguage.English };
        Select(Group("languageSettings"), "language", [DesktopText.Get("settings.languageAuto"), "简体中文", "English"], Array.IndexOf(languages, prefs.Current.Language),
            i => prefs.Save(prefs.Current with { Language = languages[i] }));
        var notifications = Group("notificationSettings");
        Toggle(notifications, "notifyCredentialExpired", prefs.Current.CredentialAlerts, v => prefs.Save(prefs.Current with { CredentialAlerts = v }));
        Toggle(notifications, "notifyFileConflict", prefs.Current.ConflictAlerts, v => prefs.Save(prefs.Current with { ConflictAlerts = v }));
        var logs = Group("logSettings");
        var open = ActionButton(DesktopText.Get("settings.openFolder"), "Open log folder", async () =>
        {
            Directory.CreateDirectory(DesktopFiles.LogDirectory);
            await DesktopFiles.OpenFolderAsync(this, DesktopFiles.LogDirectory);
        });
        SettingRow(logs, "logFolder", open, DesktopFiles.LogDirectory);
        Toggle(logs, "logToFile", prefs.Current.LogToFile, v => prefs.Save(prefs.Current with { LogToFile = v }));
        LogEventLevel[] levels = [LogEventLevel.Verbose, LogEventLevel.Debug, LogEventLevel.Information, LogEventLevel.Warning, LogEventLevel.Error];
        Select(logs, "logLevel", ["Trace", "Debug", "Info", "Warn", "Error"], Array.IndexOf(levels, prefs.Current.LogLevel), i => prefs.Save(prefs.Current with { LogLevel = levels[i] }));
        int[] counts = [3, 5, 7, 10];
        Select(logs, "logMaxFiles", counts.Select(x => x.ToString()).ToArray(), Array.IndexOf(counts, prefs.Current.LogMaxFiles), i => prefs.Save(prefs.Current with { LogMaxFiles = counts[i] }));
    }

    private StackPanel Group(string key)
    {
        GeneralPage.Children.Add(new TextBlock { Text = DesktopText.Get("settings." + key), FontWeight = FontWeight.Medium });
        var group = new StackPanel();
        GeneralPage.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(0), Child = group });
        return group;
    }

    private static TextBlock SettingRow(StackPanel group, string key, Control input, string? detail = null)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12, Margin = new Thickness(16, 10) };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = DesktopText.Get("settings." + key) });
        var description = new TextBlock { Classes = { "secondary" }, Text = detail ?? DesktopText.Get("settings." + key + "Description"), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        text.Children.Add(description);
        input.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(input, 1);
        row.Children.Add(text);
        row.Children.Add(input);
        if (group.Children.Count > 0)
        {
            group.Children.Add(new Separator());
        }

        group.Children.Add(row);
        return description;
    }

    private void AddLoginItemSetting(StackPanel group)
    {
        var toggle = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0 };
        var description = SettingRow(group, "autoStart", toggle);
        bool refreshing = false;

        void Refresh()
        {
            var status = loginItem.Status();
            refreshing = true;
            toggle.IsChecked = status == LoginItemStatus.Enabled;
            refreshing = false;
            toggle.IsEnabled = status != LoginItemStatus.Unavailable;
            description.Text = status switch
            {
                LoginItemStatus.Unavailable => UiText.Get("请在 macOS 13 或更高版本从已安装的 Cloudreve.app 管理登录项。"),
                LoginItemStatus.RequiresApproval => DesktopText.Get("settings.autoStartApproval"),
                _ => DesktopText.Get("settings.autoStartDescription")
            };
        }

        refreshLoginItem = Refresh;
        Refresh();
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (refreshing)
            {
                return;
            }

            string? error = ChangeLoginItem(toggle.IsChecked == true);
            Refresh();
            if (error != null)
            {
                description.Text = error;
            }
        };
    }

    private string? ChangeLoginItem(bool enabled)
    {
        var status = loginItem.Status();
        var target = enabled ? LoginItemStatus.Enabled : LoginItemStatus.NotRegistered;
        if (status == LoginItemStatus.Unavailable || status == target || (!enabled && status == LoginItemStatus.NotFound))
        {
            return null;
        }

        string? error = null;
        if (!enabled || status != LoginItemStatus.RequiresApproval)
        {
            error = loginItem.SetRegistered(enabled);
            status = loginItem.Status();
        }

        if (enabled && status == LoginItemStatus.RequiresApproval)
        {
            loginItem.OpenSettings();
            return null;
        }

        if (status == target || (!enabled && status == LoginItemStatus.NotFound))
        {
            return null;
        }

        return error ?? UiText.Get("macOS 未更改登录项，请在系统设置的登录项中检查。");
    }

    private ToggleSwitch Toggle(StackPanel group, string key, bool value, Action<bool> save)
    {
        var toggle = new ToggleSwitch { IsChecked = value, OnContent = "", OffContent = "", MinWidth = 0 };
        toggle.IsCheckedChanged += (_, _) => { Save(() => save(toggle.IsChecked == true)); };
        SettingRow(group, key, toggle);
        return toggle;
    }

    private void Select(StackPanel group, string key, string[] values, int selected, Action<int> save)
    {
        var combo = new ComboBox { Classes = { "preference" }, ItemsSource = values, SelectedIndex = selected };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
            {
                Save(() => save(combo.SelectedIndex));
            }
        };
        SettingRow(group, key, combo);
    }

    private void Save(Action action)
    {
        try
        {
            action();
            ErrorMessage.IsVisible = false;
        }
        catch (Exception ex)
        {
            Log.Logger.WriteFailure(LogEventLevel.Warning, "Apply desktop setting", ex);
            ErrorMessage.Text = ex.Message;
            ErrorMessage.IsVisible = true;
            BuildGeneral();
        }
    }
}
