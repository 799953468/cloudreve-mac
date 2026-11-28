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
                ScanPhase.Local => UiText.Get(Scan.IsScoped ? "popup.scanLocalChanges" : "popup.scanLocal"),
                ScanPhase.Remote => UiText.Get(Scan.IsScoped ? "popup.scanRemoteChanges" : "popup.scanRemote"),
                ScanPhase.Failed => UiText.Get("popup.scanRetry"),
                _ => throw new InvalidOperationException()
            };
        }
    }
    public string CountText { get { return UiText.Get("popup.scanItems", ("count", Scan.ItemCount)); } }
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
            string? parent = PairKey.Parent(File.Key);
            return string.IsNullOrEmpty(parent) ? Drive : $"{Drive} · {parent}";
        }
    }

    public string Operation
    {
        get
        {
            return File.Action switch
            {
                SyncAction.Upload => UiText.Get("activity.upload"),
                SyncAction.Download => UiText.Get("activity.download"),
                SyncAction.CompareContents => UiText.Get("activity.compare"),
                _ => UiText.Get("popup.waiting")
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
    // Conflict rows are rebuilt whenever a drive's list changes; resolution state outlives the rows.
    private const int MaxConflictRows = 50;
    private readonly Dictionary<MountedDrive, int> conflictRevisions = [];
    private readonly HashSet<(MountedDrive Drive, string Key)> resolving = [];
    private readonly Dictionary<(MountedDrive Drive, string Key), string> resolveErrors = [];

    private sealed class ActivityRow(MountedDrive drive, ActivityEntry entry) : Grid
    {
        internal MountedDrive Drive { get; } = drive;
        internal ActivityEntry Entry { get; } = entry;
        internal TextBlock? Detail { get; set; }
        internal ProgressBar? Progress { get; set; }
        private DateTime nextTimeUpdate;

        internal void Update(ActivityTransfer? transfer)
        {
            if (Detail == null)
            {
                return;
            }
            if (Progress != null)
            {
                Detail.Text = transfer == null ? UiText.Get("popup.processing") :
                    $"{DesktopFiles.Bytes(transfer.Completed)} / {DesktopFiles.Bytes(transfer.Total)} - {DesktopFiles.Bytes(transfer.BytesPerSecond)}/s";
                Progress.Maximum = Math.Max(1, transfer?.Total ?? 1);
                Progress.Value = transfer?.Completed ?? 0;
                Progress.IsIndeterminate = transfer == null;
            }
            else if (DateTime.Now >= nextTimeUpdate)
            {
                Detail.Text = (Entry.Status == UiText.Get("activity.failed") ? Entry.Status : RelativeTime(Entry.Time)) + " · ";
                var now = DateTime.Now;
                long ageTicks = Math.Max(0, (now - Entry.Time).Ticks);
                nextTimeUpdate = now.AddTicks(TimeSpan.TicksPerMinute - ageTicks % TimeSpan.TicksPerMinute);
            }
        }
    }

    internal ActivityWindow(WorkspaceHost host)
    {
        this.host = host;
        InitializeComponent();
        Motion.EnableTransitions(this);
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
                    Motion.Pop(Root);
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
        conflictRevisions.Clear();
        Chips.Children.Clear();
        AddFilter(null, UiText.Get("popup.allDrives"));
        foreach (var drive in host.Drives)
        {
            AddFilter(drive, drive.Account.Name);
        }

        var add = new Button { Content = "+  " + UiText.Get("popup.newDrive"), Classes = { "chip", "add" } };
        add.Click += (_, _) => { host.OpenAdd(); };
        Chips.Children.Add(add);
        Refresh(true);
    }

    private void AddFilter(MountedDrive? drive, string label)
    {
        var button = new Button { Content = label, Classes = { "chip" }, Tag = drive };
        button.Classes.Set("selected", selected == drive);
        button.Click += (_, _) =>
        {
            if (selected == drive)
            {
                return;
            }

            // Update the chips in place so their colors can transition instead of being rebuilt.
            selected = drive;
            foreach (var chip in Chips.Children.OfType<Button>().Where(c => c.Classes.Contains("chip") && !c.Classes.Contains("add")))
            {
                chip.Classes.Set("selected", chip.Tag == drive);
            }

            snapshots.Clear();
            conflictRevisions.Clear();
            Refresh(true);
            TaskScroll.Offset = default;
        };
        Chips.Children.Add(button);
    }

    internal void Refresh(bool force = false)
    {
        bool changed = force;
        bool conflictsChanged = force;
        foreach (var drive in host.Drives.Where(d => selected == null || d == selected))
        {
            int conflictRevision = drive.ConflictRevision;
            if (!conflictRevisions.TryGetValue(drive, out int shown) || shown != conflictRevision)
            {
                conflictRevisions[drive] = conflictRevision;
                conflictsChanged = changed = true;
            }

            var snapshot = drive.Activity.ChangesSince(snapshots.GetValueOrDefault(drive)?.Revision ?? -1);
            if (snapshot != null)
            {
                snapshots[drive] = snapshot;
                changed = true;
            }
        }

        if (!changed)
        {
            foreach (ActivityRow row in RecentRows.Children)
            {
                row.Update(null);
            }
            return;
        }

        var active = snapshots.SelectMany(s => s.Value.Active.Select(file => (Drive: s.Key, File: file))).ToArray();
        var recent = snapshots.SelectMany(s => s.Value.Recent.Select(e => (Drive: s.Key, Entry: e))).OrderByDescending(e => e.Entry.Time).Take(100).ToArray();
        int waitingCount = snapshots.Values.Sum(snapshot => snapshot.WaitingCount);
        string waitingLabel = UiText.Get("popup.queued", ("count", waitingCount));
        var scans = snapshots.Where(s => s.Value.Scan.Phase != ScanPhase.Idle)
            .Select(s => new ScanRow(s.Key.Account.Name, s.Value.Scan)).ToArray();
        bool scanning = scans.Any(row => !row.Failed);
        int conflictCount = conflictRevisions.Keys.Sum(drive => drive.Conflicts.Count);
        bool quiet = scans.Length == 0 && active.Length == 0 && waitingCount == 0;
        bool idle = quiet && conflictCount == 0;
        bool attention = quiet && conflictCount > 0;
        if (conflictsChanged)
        {
            RenderConflicts();
        }

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
        UpdateRows(ActiveRows, active.Select(item => (item.Drive, item.File.Entry, item.File.Transfer)), active: true, animate: !force);

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
        WaitingMore.Text = remaining > 0 ? UiText.Get("popup.queuedMore", ("count", remaining)) : null;

        RecentSection.IsVisible = recent.Length > 0;
        UpdateRows(RecentRows, recent.Select(item => (item.Drive, item.Entry, (ActivityTransfer?)null)), active: false, animate: !force);

        bool showFailure = active.Length == 0 && !scanning && scans.Length > 0;
        Footer.Text = active.Length switch
        {
            > 0 => UiText.Get("popup.syncingStatus", ("count", active.Length)),
            _ when scanning => UiText.Get("popup.scanning"),
            _ when showFailure => UiText.Get("popup.scanFailed"),
            _ when waitingCount > 0 => waitingLabel,
            _ when conflictCount > 0 => UiText.Get("popup.conflictStatus", ("count", conflictCount)),
            _ => UiText.Get("popup.upToDate")
        };
        StatusIcon.Data = StreamGeometry.Parse(showFailure || attention ? "M12 2 1 21h22z M11 8h2v7h-2z M11 17h2v2h-2z" :
            idle ? "M12 2a10 10 0 1 0 0 20a10 10 0 1 0 0-20 M10 17l-5-5 1.5-1.5 3.5 3.5 7-7 1.5 1.5z" : "M4 5v5h5L7 8a6 6 0 1 1-1 7H3a9 9 0 1 0 2-9z");
        StatusIcon.Classes.Set("failure", showFailure);
        StatusIcon.Classes.Set("warning", attention && !showFailure);
        StatusIcon.Classes.Set("healthy", idle);
        StatusIcon.Classes.Set("spinning", active.Length > 0 || scanning);
    }

    private void RenderConflicts()
    {
        var items = conflictRevisions.Keys.SelectMany(drive => drive.Conflicts.Select(conflict => (Drive: drive, Conflict: conflict))).ToArray();
        // Forget state for conflicts that a scan or another resolution already cleared.
        var present = items.Select(item => (item.Drive, item.Conflict.Key)).ToHashSet();
        foreach (var stale in resolveErrors.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            resolveErrors.Remove(stale);
        }

        ConflictSection.IsVisible = items.Length > 0;
        ConflictTitle.Text = UiText.Get("popup.conflicts", ("count", items.Length));
        ConflictRows.Children.Clear();
        foreach (var (drive, conflict) in items.Take(MaxConflictRows))
        {
            ConflictRows.Children.Add(ConflictRow(drive, conflict));
        }

        int remaining = items.Length - MaxConflictRows;
        ConflictMore.IsVisible = remaining > 0;
        ConflictMore.Text = remaining > 0 ? UiText.Get("popup.conflictsMore", ("count", remaining)) : null;
    }

    private Control ConflictRow(MountedDrive drive, PairConflict conflict)
    {
        var id = (drive, conflict.Key);
        bool busy = resolving.Contains(id);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("32,*"), ColumnSpacing = 8, Margin = new Thickness(16, 10) };
        var icon = FileIcons.Create(conflict.Key, false);
        icon.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(icon);
        var lines = new StackPanel { Spacing = 3 };
        Grid.SetColumn(lines, 1);
        row.Children.Add(lines);
        lines.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(conflict.Key), TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeight.Medium, LineHeight = 20
        });
        string? parent = PairKey.Parent(conflict.Key);
        string where = string.IsNullOrEmpty(parent) ? drive.Account.Name : $"{drive.Account.Name} · {parent}";
        lines.Children.Add(new TextBlock { Text = where, Classes = { "secondary" }, FontSize = 12, LineHeight = 16, TextTrimming = TextTrimming.CharacterEllipsis });
        ToolTip.SetTip(row, conflict.Key);
        string reason = UiText.Get(conflict.Cause switch
        {
            ConflictCause.DeletedLocally => "popup.conflictDeletedLocally",
            ConflictCause.DeletedInCloud => "popup.conflictDeletedInCloud",
            ConflictCause.TypeMismatch => "popup.conflictTypeMismatch",
            ConflictCause.UnsupportedItem => "popup.conflictUnsupportedItem",
            ConflictCause.NameCollision => "popup.conflictNameCollision",
            ConflictCause.DirectoryReplaced => "popup.conflictDirectoryReplaced",
            ConflictCause.CloudFolderReplaced => "popup.conflictCloudFolderReplaced",
            ConflictCause.DirectoryNotEmpty => "popup.conflictDirectoryNotEmpty",
            ConflictCause.UncertainMove => "popup.conflictUncertainMove",
            ConflictCause.UnverifiedIdentity => "popup.conflictUnverifiedIdentity",
            _ => "popup.conflictContentsDiffer"
        });
        lines.Children.Add(new TextBlock { Text = reason, Classes = { "warning" }, FontSize = 12, LineHeight = 16, TextWrapping = TextWrapping.Wrap });
        if (resolveErrors.TryGetValue(id, out string? error))
        {
            lines.Children.Add(new TextBlock
            {
                Text = UiText.Get("popup.resolveFailed", ("message", error)), Classes = { "failure" }, FontSize = 12, LineHeight = 16, TextWrapping = TextWrapping.Wrap
            });
        }

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        lines.Children.Add(actions);
        if (busy)
        {
            actions.Children.Add(new TextBlock { Text = UiText.Get("popup.processing"), Classes = { "secondary" }, FontSize = 12 });
            return row;
        }

        if (conflict.Choice != null)
        {
            actions.Children.Add(ResolveButton(drive, conflict, ConflictChoice.KeepLocal, "popup.keepLocal",
                conflict.Cause == ConflictCause.DeletedLocally ? "popup.keepLocalTrashTip" : "popup.keepLocalUploadTip"));
            actions.Children.Add(ResolveButton(drive, conflict, ConflictChoice.KeepRemote, "popup.keepRemote", conflict.Cause switch
            {
                ConflictCause.DeletedLocally => "popup.keepRemoteRestoreTip",
                ConflictCause.DeletedInCloud => "popup.keepRemoteRecoverTip",
                _ => "popup.keepRemoteDownloadTip"
            }));
            if (conflict.CanKeepBoth)
            {
                actions.Children.Add(ResolveButton(drive, conflict, ConflictChoice.KeepBoth, "popup.keepBoth", "popup.keepBothTip"));
            }
        }
        else if (drive.Pair != null)
        {
            var show = ActionButton(UiText.Get("popup.showInFolder"), null);
            string path = Path.Combine(drive.Pair.Local, conflict.Key);
            show.Click += (_, _) => { DesktopFiles.Reveal(path); };
            actions.Children.Add(show);
        }

        return row;
    }

    private static Button ActionButton(string label, string? tip)
    {
        var button = new Button
        {
            Content = label, Classes = { "tool", "subtle" }, FontSize = 12, MinHeight = 26, Padding = new Thickness(10, 2), Margin = new Thickness(0, 0, 6, 4)
        };
        if (tip != null)
        {
            ToolTip.SetTip(button, tip);
        }

        Avalonia.Automation.AutomationProperties.SetName(button, label);
        return button;
    }

    private Button ResolveButton(MountedDrive drive, PairConflict conflict, ConflictChoice choice, string labelKey, string tipKey)
    {
        var button = ActionButton(UiText.Get(labelKey), UiText.Get(tipKey));
        button.Click += async (_, _) => { await ResolveAsync(drive, conflict, choice); };
        return button;
    }

    private async Task ResolveAsync(MountedDrive drive, PairConflict conflict, ConflictChoice choice)
    {
        var id = (drive, conflict.Key);
        if (!resolving.Add(id))
        {
            return;
        }

        resolveErrors.Remove(id);
        RenderConflicts();
        try
        {
            await drive.ResolveConflictAsync(conflict, choice);
        }
        catch (Exception ex)
        {
            // MountedDrive already logged the failure with its details.
            resolveErrors[id] = ex.Message;
        }
        finally
        {
            resolving.Remove(id);
        }

        Refresh(true);
    }

    private void UpdateRows(StackPanel panel, IEnumerable<(MountedDrive Drive, ActivityEntry Entry, ActivityTransfer? Transfer)> items, bool active, bool animate)
    {
        int index = 0;
        foreach (var (drive, entry, transfer) in items)
        {
            int match = index;
            while (match < panel.Children.Count)
            {
                var existing = (ActivityRow)panel.Children[match];
                if (existing.Drive == drive && ReferenceEquals(existing.Entry, entry))
                {
                    break;
                }
                match++;
            }
            ActivityRow row;
            if (match == panel.Children.Count)
            {
                row = Row(drive, entry, active);
                panel.Children.Insert(index, row);
                if (animate)
                {
                    Motion.Enter(row, offsetY: -6);
                }
            }
            else
            {
                row = (ActivityRow)panel.Children[match];
                if (match != index)
                {
                    panel.Children.Move(match, index);
                }
            }
            row.Update(transfer);
            index++;
        }
        while (panel.Children.Count > index)
        {
            panel.Children.RemoveAt(panel.Children.Count - 1);
        }
    }

    private ActivityRow Row(MountedDrive drive, ActivityEntry entry, bool active)
    {
        var row = new ActivityRow(drive, entry) { ColumnDefinitions = new ColumnDefinitions("32,*"), ColumnSpacing = 8, Margin = new Thickness(16, 12) };
        row.Children.Add(FileIcons.Create(entry.Key, active));
        var lines = new StackPanel { Spacing = 3 };
        Grid.SetColumn(lines, 1);
        row.Children.Add(lines);
        lines.Children.Add(new TextBlock { Text = Path.GetFileName(entry.Key), TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeight.Medium, LineHeight = 20 });
        if (active)
        {
            row.Detail = new TextBlock { Classes = { "secondary" }, FontSize = 12, LineHeight = 18 };
            row.Progress = new ProgressBar { Classes = { "syncProgress" }, Height = 4, Minimum = 0 };
            lines.Children.Add(row.Detail);
            lines.Children.Add(row.Progress);
        }
        else if (drive.Pair != null)
        {
            var path = Path.Combine(drive.Pair.Local, entry.Key);
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 3 };
            row.Detail = new TextBlock
            {
                Classes = { "secondary" }, FontSize = 12, LineHeight = 18, VerticalAlignment = VerticalAlignment.Center
            };
            line.Children.Add(row.Detail);
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
            return UiText.Get("timeAgo.justNow");
        }

        var (unit, count) = age.TotalDays >= 1 ? ("day", (int)age.TotalDays) : age.TotalHours >= 1 ? ("hour", (int)age.TotalHours) : ("minute", (int)age.TotalMinutes);
        return UiText.Get($"timeAgo.{unit}{(count == 1 ? "" : "s")}Ago", ("value", count));
    }
}
