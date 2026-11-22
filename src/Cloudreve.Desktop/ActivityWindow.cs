using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Cloudreve.Desktop;

internal sealed record ScanRow(string Drive, ScanStatus Scan)
{
    public bool Failed { get { return Scan.Phase == ScanPhase.Failed; } }
    public string PhaseText
    {
        get
        {
            return Scan.Phase switch
            {
                ScanPhase.Local => DesktopText.Get("popup.scanLocal"),
                ScanPhase.Remote => DesktopText.Get("popup.scanRemote"),
                ScanPhase.Failed => DesktopText.Get("popup.scanRetry"),
                _ => throw new InvalidOperationException()
            };
        }
    }
    public string CountText { get { return DesktopText.Get("popup.scanItems").Replace("{{count}}", Scan.ItemCount.ToString()); } }
}

internal sealed record WaitingRow(string Drive, WaitingFile File)
{
    public string FileName { get { return Path.GetFileName(File.Key); } }
    public bool IsUpload { get { return File.Action == SyncAction.Upload; } }
    public bool IsDownload { get { return File.Action == SyncAction.Download; } }
    public bool IsComparison { get { return File.Action == SyncAction.CompareContents; } }
    public string Location
    {
        get
        {
            string? parent = Path.GetDirectoryName(File.Key);
            return string.IsNullOrEmpty(parent) ? Drive : $"{Drive} · {parent}";
        }
    }

    public string Operation
    {
        get
        {
            return File.Action switch
            {
                SyncAction.Upload => UiText.Get("上传"),
                SyncAction.Download => UiText.Get("下载"),
                SyncAction.CompareContents => UiText.Get("核对内容"),
                _ => DesktopText.Get("popup.waiting")
            };
        }
    }

    public string Description { get { return $"{Operation} · {Drive} · {File.Key}"; } }
}

internal sealed partial class ActivityWindow : Window
{
    private readonly WorkspaceHost host;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<MountedDrive, ActivitySnapshot> snapshots = [];
    private readonly TextDecorationCollection folderUnderline = [new TextDecoration { Location = TextDecorationLocation.Underline }];
    private ScanRow[] displayedScans = [];
    private WaitingRow[] displayedWaiting = [];
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

        var active = snapshots.SelectMany(s => s.Value.Active.Select(file => (Drive: s.Key, File: file))).ToArray();
        var recent = snapshots.SelectMany(s => s.Value.Recent.Select(e => (Drive: s.Key, Entry: e))).OrderByDescending(e => e.Entry.Time).Take(100).ToArray();
        int waitingCount = snapshots.Values.Sum(snapshot => snapshot.WaitingCount);
        string waitingLabel = DesktopText.Get("popup.queued").Replace("{{count}}", waitingCount.ToString());
        var scans = snapshots.Where(s => s.Value.Scan.Phase != ScanPhase.Idle)
            .Select(s => new ScanRow(s.Key.Account.Name, s.Value.Scan)).ToArray();
        bool scanning = scans.Any(row => !row.Failed);
        bool idle = scans.Length == 0 && active.Length == 0 && waitingCount == 0;
        Empty.IsVisible = idle && recent.Length == 0;
        TaskScroll.IsVisible = !Empty.IsVisible;
        ScanSection.IsVisible = scans.Length > 0;
        ScanProgress.IsVisible = scanning;
        if (!scans.SequenceEqual(displayedScans))
        {
            displayedScans = scans;
            ScanItems.ItemsSource = scans;
        }

        ActiveSection.IsVisible = active.Length > 0;
        ActiveRows.Children.Clear();
        foreach (var (drive, file) in active)
        {
            ActiveRows.Children.Add(Row(drive, file.Entry, true, file.Transfer));
        }

        WaitingSection.IsVisible = waitingCount > 0;
        WaitingTitle.Text = waitingLabel;
        var waiting = snapshots.SelectMany(s => s.Value.Waiting.Select(file => new WaitingRow(s.Key.Account.Name, file)))
            .Take(SyncActivity.Capacity).ToArray();
        if (!waiting.SequenceEqual(displayedWaiting))
        {
            displayedWaiting = waiting;
            WaitingItems.ItemsSource = waiting;
        }

        int remaining = waitingCount - waiting.Length;
        WaitingMore.IsVisible = remaining > 0;
        WaitingMore.Text = remaining > 0 ? DesktopText.Get("popup.queuedMore").Replace("{{count}}", remaining.ToString()) : null;

        RecentSection.IsVisible = recent.Length > 0;
        RecentRows.Children.Clear();
        foreach (var (drive, entry) in recent)
        {
            RecentRows.Children.Add(Row(drive, entry, false, null));
        }

        bool showFailure = active.Length == 0 && !scanning && scans.Length > 0;
        Footer.Text = active.Length switch
        {
            > 0 => DesktopText.Get("popup.syncingStatus").Replace("{{count}}", active.Length.ToString()),
            _ when scanning => DesktopText.Get("popup.scanning"),
            _ when showFailure => DesktopText.Get("popup.scanFailed"),
            _ when waitingCount > 0 => waitingLabel,
            _ => DesktopText.Get("popup.upToDate")
        };
        StatusIcon.Data = StreamGeometry.Parse(showFailure ? "M12 2 1 21h22z M11 8h2v7h-2z M11 17h2v2h-2z" :
            idle ? "M12 2a10 10 0 1 0 0 20a10 10 0 1 0 0-20 M10 17l-5-5 1.5-1.5 3.5 3.5 7-7 1.5 1.5z" : "M4 5v5h5L7 8a6 6 0 1 1-1 7H3a9 9 0 1 0 2-9z");
        StatusIcon.Classes.Set("failure", showFailure);
        StatusIcon.Classes.Set("healthy", idle);
    }

    private Grid Row(MountedDrive drive, ActivityEntry entry, bool active, ActivityTransfer? transfer)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("32,*"), ColumnSpacing = 8, Margin = new Thickness(16, 12) };
        var file = new PathIcon
        {
            Width = 22, Height = 26, Classes = { active ? "activityInk" : "mutedIcon" },
            Data = StreamGeometry.Parse("M4 1h10l6 6v16H4z M6 3v18h12V9h-6V3z")
        };
        row.Children.Add(file);
        var lines = new StackPanel { Spacing = 3 };
        Grid.SetColumn(lines, 1);
        row.Children.Add(lines);
        lines.Children.Add(new TextBlock { Text = Path.GetFileName(entry.Key), TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeight.Medium, LineHeight = 20 });
        if (active)
        {
            lines.Children.Add(new TextBlock
            {
                Classes = { "secondary" }, FontSize = 12, LineHeight = 18,
                Text = transfer == null
                    ? DesktopText.Get("popup.processing")
                    : $"{DesktopFiles.Bytes(transfer.Completed)} / {DesktopFiles.Bytes(transfer.Total)} - {DesktopFiles.Bytes(transfer.BytesPerSecond)}/s"
            });
            lines.Children.Add(new ProgressBar
                { Classes = { "syncProgress" }, Height = 4, Minimum = 0, Maximum = Math.Max(1, transfer?.Total ?? 1), Value = transfer?.Completed ?? 0, IsIndeterminate = transfer == null });
        }
        else if (drive.Pair != null)
        {
            var path = Path.Combine(drive.Pair.Local, entry.Key);
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 3 };
            line.Children.Add(new TextBlock
            {
                Classes = { "secondary" }, FontSize = 12, LineHeight = 18, VerticalAlignment = VerticalAlignment.Center,
                Text = (entry.Status == UiText.Get("失败") ? entry.Status : RelativeTime(entry.Time)) + " · "
            });
            var folderName = Path.GetFileName(Path.GetDirectoryName(path));
            var link = new Button
            {
                Content = new TextBlock { Text = folderName, TextDecorations = folderUnderline, TextTrimming = TextTrimming.CharacterEllipsis },
                Classes = { "tool", "driveLink" }, MinHeight = 0, FontSize = 12, Padding = new Thickness(0)
            };
            Avalonia.Automation.AutomationProperties.SetName(link, folderName);
            link.Click += (_, _) => { DesktopFiles.Reveal(path); };
            Grid.SetColumn(link, 1);
            line.Children.Add(link);
            lines.Children.Add(line);
        }

        return row;
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
