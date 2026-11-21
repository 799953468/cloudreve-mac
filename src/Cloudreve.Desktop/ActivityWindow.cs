using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Cloudreve.Desktop;

internal sealed partial class ActivityWindow : Window
{
    private readonly WorkspaceHost host;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<MountedDrive, ActivitySnapshot> snapshots = [];
    private MountedDrive? selected;

    internal ActivityWindow(WorkspaceHost host)
    {
        this.host = host;
        InitializeComponent();
        SettingsButton.Click += (_, _) => { host.OpenSettings(); };
        host.Changed += RebuildFilters;
        timer.Tick += (_, _) => { Refresh(); };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                if (IsVisible)
                {
                    timer.Start();
                    Refresh(true);
                }
                else
                {
                    timer.Stop();
                }
            }
        };
        Deactivated += (_, _) =>
        {
            if (Program.Preferences.Current.FastPopup)
            {
                Hide();
            }
            else
            {
                Close();
            }
        };
        Closed += (_, _) =>
        {
            timer.Stop();
            host.Changed -= RebuildFilters;
        };
        RebuildFilters();
    }

    private void RebuildFilters()
    {
        if (selected != null && !host.Drives.Contains(selected))
        {
            selected = null;
        }

        snapshots.Clear();
        Chips.Children.Clear();
        AddFilter(null, DesktopText.Get("popup.allDrives"));
        foreach (var drive in host.Drives)
        {
            AddFilter(drive, drive.Account.Name);
        }

        var add = new Button { Content = "+  " + DesktopText.Get("popup.newDrive"), Classes = { "chip", "add" } };
        add.Click += (_, _) => { host.OpenAdd(); };
        Chips.Children.Add(add);
        Refresh(true);
    }

    private void AddFilter(MountedDrive? drive, string label)
    {
        var button = new Button { Content = label, Classes = { "chip" } };
        button.Classes.Set("selected", selected == drive);
        button.Click += (_, _) =>
        {
            selected = drive;
            RebuildFilters();
        };
        Chips.Children.Add(button);
    }

    internal void Refresh(bool force = false)
    {
        bool changed = force;
        foreach (var drive in host.Drives.Where(d => selected == null || d == selected))
        {
            var snapshot = drive.Activity.ChangesSince(snapshots.GetValueOrDefault(drive)?.Revision ?? -1);
            if (snapshot != null)
            {
                snapshots[drive] = snapshot;
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        Tasks.Children.Clear();
        var active = snapshots.Where(s => s.Value.Current != null).ToArray();
        var recent = snapshots.SelectMany(s => s.Value.Recent.Select(e => (Drive: s.Key, Entry: e))).OrderByDescending(e => e.Entry.Time).Take(100).ToArray();
        Empty.IsVisible = active.Length == 0 && recent.Length == 0;
        TaskScroll.IsVisible = !Empty.IsVisible;
        if (active.Length > 0)
        {
            Section("popup.syncing");
            foreach (var (drive, state) in active)
            {
                Row(drive, state.Current!, true, state.Transfer);
            }
        }

        if (recent.Length > 0)
        {
            Section("popup.recent");
            foreach (var (drive, entry) in recent)
            {
                Row(drive, entry, false, null);
            }
        }

        Footer.Text = active.Length == 0 ? DesktopText.Get("popup.upToDate") : DesktopText.Get("popup.syncingStatus").Replace("{{count}}", active.Length.ToString());
        StatusIcon.Data = StreamGeometry.Parse(active.Length == 0 ? "M12 2a10 10 0 1 0 0 20a10 10 0 1 0 0-20 M10 17l-5-5 1.5-1.5 3.5 3.5 7-7 1.5 1.5z" : "M4 5v5h5L7 8a6 6 0 1 1-1 7H3a9 9 0 1 0 2-9z");
        StatusIcon.Foreground = active.Length == 0 ? Brushes.SeaGreen : Brushes.DodgerBlue;
    }

    private void Section(string key)
    {
        Tasks.Children.Add(new TextBlock { Text = DesktopText.Get(key), FontWeight = FontWeight.Medium, Margin = new Thickness(16, 12, 16, 6) });
    }

    private void Row(MountedDrive drive, ActivityEntry entry, bool active, ActivityTransfer? transfer)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("32,*"), ColumnSpacing = 8, Margin = new Thickness(16, 8) };
        var file = new PathIcon
        {
            Width = 22, Height = 26, Foreground = active ? Brushes.SteelBlue : Brushes.Gray,
            Data = StreamGeometry.Parse("M4 1h10l6 6v16H4z M6 3v18h12V9h-6V3z")
        };
        row.Children.Add(file);
        var lines = new StackPanel { Spacing = 2 };
        Grid.SetColumn(lines, 1);
        row.Children.Add(lines);
        lines.Children.Add(new TextBlock { Text = Path.GetFileName(entry.Key), TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeight.Medium });
        if (active)
        {
            lines.Children.Add(new TextBlock
            {
                Classes = { "secondary" }, FontSize = 12,
                Text = transfer == null ? DesktopText.Get("popup.processing") : $"{DesktopFiles.Bytes(transfer.Completed)} / {DesktopFiles.Bytes(transfer.Total)} - {DesktopFiles.Bytes(transfer.BytesPerSecond)}/s"
            });
            lines.Children.Add(new ProgressBar { Height = 4, Minimum = 0, Maximum = Math.Max(1, transfer?.Total ?? 1), Value = transfer?.Completed ?? 0, IsIndeterminate = transfer == null });
        }
        else if (drive.Pair != null)
        {
            var path = Path.Combine(drive.Pair.Local, entry.Key);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            line.Children.Add(new TextBlock { Classes = { "secondary" }, FontSize = 12, Text = (entry.Status == UiText.Get("失败") ? entry.Status : RelativeTime(entry.Time)) + " · " });
            var link = new Button { Content = Path.GetFileName(Path.GetDirectoryName(path)), Classes = { "tool" }, MinHeight = 0, FontSize = 12, Padding = new Thickness(0) };
            link.Click += (_, _) => { DesktopFiles.Reveal(path); };
            line.Children.Add(link);
            lines.Children.Add(line);
        }

        Tasks.Children.Add(row);
    }

    private static string RelativeTime(DateTime time)
    {
        var age = DateTime.Now - time;
        if (age.TotalMinutes < 1)
        {
            return DesktopText.Get("timeAgo.justNow");
        }

        var (unit, count) = age.TotalDays >= 1 ? ("day", (int)age.TotalDays) : age.TotalHours >= 1 ? ("hour", (int)age.TotalHours) : ("minute", (int)age.TotalMinutes);
        return DesktopText.Get($"timeAgo.{unit}{(count == 1 ? "" : "s")}Ago").Replace("{{value}}", count.ToString());
    }
}