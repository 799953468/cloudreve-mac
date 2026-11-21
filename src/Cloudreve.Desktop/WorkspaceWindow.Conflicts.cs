using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Cloudreve.Desktop;

internal sealed partial class WorkspaceWindow
{
    private sealed record Resolution(FileConflict File, ConflictChoice Choice);

    private async Task ResolveSelectedConflictAsync()
    {
        if (pair == null || pairStore == null || drive == null || fileConflicts.Count == 0)
        {
            return;
        }

        var selection = await SelectConflictAsync(fileConflicts.ToArray());
        if (selection == null || shutdown.IsCancellationRequested)
        {
            return;
        }

        await StopPairLoopAsync();
        try
        {
            var engine = new FolderPairSync(pairStore, pair, drive);
            await Task.Run(() => engine.ResolveConflictAsync(selection.File, selection.Choice, shutdown.Token), shutdown.Token);
            StatusText.Text = UiText.Get("已处理冲突：") + selection.File.Key;
        }
        finally
        {
            fileConflicts = [];
            if (!shutdown.IsCancellationRequested)
            {
                StartPairLoop();
            }
        }
    }

    private Task<Resolution?> SelectConflictAsync(FileConflict[] items)
    {
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        var dialog = new Window
        {
            Title = UiText.Get("处理同步冲突"), Width = 480, SizeToContent = SizeToContent.Height, CanResize = false,
            Content = panel, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var list = new ListBox { ItemsSource = items.Select(item => item.Key).ToArray(), Height = 190 };
        var detail = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var local = new Button { Content = UiText.Get("保留本地") };
        var remote = new Button { Content = UiText.Get("保留云端") };
        var both = new Button { Content = UiText.Get("保留两份") };
        var cancel = new Button { Content = UiText.Get("取消"), IsCancel = true };
        list.SelectionChanged += (_, _) =>
        {
            int index = list.SelectedIndex;
            local.IsEnabled = remote.IsEnabled = index >= 0;
            both.IsEnabled = index >= 0 && items[index].LocalHash != null && items[index].Remote != null;
            if (index < 0)
            {
                detail.Text = "";
            }
            else if (items[index].LocalHash == null)
            {
                detail.Text = UiText.Get("本地已删除。保留本地会将云端文件移入回收站；保留云端会重新下载。");
            }
            else if (items[index].Remote == null)
            {
                detail.Text = UiText.Get("云端已删除。保留本地会重新上传；保留云端会把本地文件移入恢复目录。");
            }
            else
            {
                detail.Text = UiText.Get("保留两份会把本地版本另存为 local-copy 文件，并将云端版本保留在原路径。");
            }
        };
        list.SelectedIndex = 0;

        void Choose(ConflictChoice choice)
        {
            if (list.SelectedIndex >= 0)
            {
                dialog.Close(new Resolution(items[list.SelectedIndex], choice));
            }
        }

        local.Click += (_, _) => { Choose(ConflictChoice.KeepLocal); };
        remote.Click += (_, _) => { Choose(ConflictChoice.KeepRemote); };
        both.Click += (_, _) => { Choose(ConflictChoice.KeepBoth); };
        cancel.Click += (_, _) => { dialog.Close(); };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        actions.Children.Add(cancel);
        actions.Children.Add(local);
        actions.Children.Add(remote);
        actions.Children.Add(both);
        panel.Children.Add(list);
        panel.Children.Add(detail);
        panel.Children.Add(actions);
        return dialog.ShowDialog<Resolution?>(this);
    }
}