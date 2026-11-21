using Avalonia.Controls;
using Serilog;
using Serilog.Events;

namespace Cloudreve.Desktop;

internal static class PreferencesMenu
{
    internal static NativeMenuItem Create(DesktopPreferences settings, Action<string> reportFailure)
    {
        var menu = new NativeMenu();
        var startup = new NativeMenuItem(UiText.Get("登录时打开"));
        var alerts = new NativeMenuItem(UiText.Get("冲突提醒"));
        var levels = new NativeMenu();
        var languages = new NativeMenu();
        var levelItems = new Dictionary<LogEventLevel, NativeMenuItem>();
        var chinese = new NativeMenuItem();
        var english = new NativeMenuItem();
        chinese.Click += (_, _) => { Change(() => settings.Save(settings.Current with { Language = UiLanguage.Chinese })); };
        english.Click += (_, _) => { Change(() => settings.Save(settings.Current with { Language = UiLanguage.English })); };
        languages.Items.Add(chinese);
        languages.Items.Add(english);
        foreach (var level in new[] { LogEventLevel.Debug, LogEventLevel.Information, LogEventLevel.Warning, LogEventLevel.Error })
        {
            var item = new NativeMenuItem(level.ToString());
            item.Click += (_, _) => { Change(() => settings.Save(settings.Current with { LogLevel = level })); };
            levels.Items.Add(item);
            levelItems.Add(level, item);
        }

        menu.Items.Add(startup);
        menu.Items.Add(alerts);
        menu.Items.Add(new NativeMenuItem(UiText.Get("界面语言（重启生效）")) { Menu = languages });
        menu.Items.Add(new NativeMenuItem(UiText.Get("日志级别")) { Menu = levels });
        startup.Click += (_, _) => { Change(MacLoginItem.Toggle); };
        alerts.Click += (_, _) => { Change(() => settings.Save(settings.Current with { ConflictAlerts = !settings.Current.ConflictAlerts })); };
        menu.Opening += (_, _) => { Refresh(); };
        Refresh();
        return new NativeMenuItem(UiText.Get("设置")) { Menu = menu };

        void Change(Action action)
        {
            try
            {
                action();
                Refresh();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Logger.WriteFailure(LogEventLevel.Warning, "Update desktop preferences", ex);
                reportFailure(ex.Message);
            }
        }

        void Refresh()
        {
            var status = MacLoginItem.Status();
            startup.IsEnabled = status != LoginItemStatus.NotFound;
            startup.Header = status switch
            {
                LoginItemStatus.Enabled => UiText.Get("登录时打开：开"),
                LoginItemStatus.RequiresApproval => UiText.Get("登录时打开：前往系统设置批准…"),
                LoginItemStatus.NotFound => UiText.Get("登录时打开：不可用"),
                _ => UiText.Get("登录时打开：关")
            };
            alerts.Header = settings.Current.ConflictAlerts ? UiText.Get("窗口冲突提醒：开") : UiText.Get("窗口冲突提醒：关");
            chinese.Header = settings.Current.Language == UiLanguage.Chinese ? "✓ 简体中文" : "简体中文";
            english.Header = settings.Current.Language == UiLanguage.English ? "✓ English" : "English";
            foreach (var (level, item) in levelItems)
            {
                item.Header = settings.Current.LogLevel == level ? UiText.Format($"{level}（当前）") : level.ToString();
            }
        }
    }
}