using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Cloudreve.Desktop;

internal sealed partial class WorkspaceWindow
{
    private async Task CreateCloudFolderAsync()
    {
        string? name = await AskAsync(UiText.Get("新建文件夹"), UiText.Get("文件夹名称"), UiText.Get("创建"), "");
        if (name == null || drive == null)
        {
            return;
        }

        await drive.CreateFolderAsync(directory, name, shutdown.Token);
        await RefreshAfterMutationAsync();
    }

    private async Task RenameCloudItemAsync()
    {
        if (drive == null || Files.SelectedItem is not DriveEntry item)
        {
            return;
        }

        string? name = await AskAsync(UiText.Get("重命名"), item.Name, UiText.Get("重命名"), item.Name);
        if (name == null || name == item.Name)
        {
            return;
        }

        await drive.RenameAsync(item, name, shutdown.Token);
        await RefreshAfterMutationAsync();
    }

    private async Task MoveCloudItemAsync()
    {
        if (drive == null || account == null || Files.SelectedItem is not DriveEntry item)
        {
            return;
        }

        string? path = await AskAsync(UiText.Format($"移动「{item.Name}」"), UiText.Get("目标文件夹（相对于授权目录，/ 表示根目录）"), UiText.Get("移动"), "/");
        if (path == null)
        {
            return;
        }

        string target = ResolveCloudDirectory(account.Root, path);
        await drive.FolderIdAsync(target, shutdown.Token);
        await drive.MoveAsync(item, target, shutdown.Token);
        await RefreshAfterMutationAsync();
    }

    internal static string ResolveCloudDirectory(string root, string path)
    {
        if (path == "/")
        {
            return root;
        }

        string result = root;
        foreach (string name in path.Trim('/').Split('/'))
        {
            result = DriveClient.ChildUri(result, name);
        }

        return result;
    }

    private async Task TrashCloudItemAsync()
    {
        if (drive == null || Files.SelectedItem is not DriveEntry item)
        {
            return;
        }

        string? answer = await AskAsync(UiText.Get("移到云端回收站"), UiText.Format($"将「{item.Name}」移到云端回收站？此更改也会同步到配对目录。"), UiText.Get("移到回收站"), null);
        if (answer == null)
        {
            return;
        }

        await drive.TrashAsync(item, shutdown.Token);
        await RefreshAfterMutationAsync();
    }

    private async Task RefreshAfterMutationAsync()
    {
        RequestSync();
        await LoadAsync(directory);
    }

    private async Task EditIgnoreRulesAsync()
    {
        if (pairStore == null || pair == null)
        {
            return;
        }

        string original = pairStore.ReadRules(pair);
        string? updated = await AskAsync(UiText.Get("配对忽略规则"), UiText.Get("每行一条，例如 *.tmp 或 build/。清空后恢复同步。"), UiText.Get("保存"), original, true);
        if (updated == null || updated == original)
        {
            return;
        }

        await StopPairLoopAsync();
        try
        {
            pairStore.SaveRules(pair, updated);
        }
        finally
        {
            if (!shutdown.IsCancellationRequested)
            {
                StartPairLoop();
            }
        }
    }

    private async Task<string?> AskAsync(string title, string label, string action, string? initial, bool multiline = false)
    {
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        var dialog = new Window
        {
            Title = title, Width = 380, CanResize = false, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel, ShowInTaskbar = false
        };
        var cancel = new Button { Content = UiText.Get("取消"), IsCancel = true };
        var confirm = new Button { Content = action, IsDefault = initial != null && !multiline };
        TextBox? input = null;
        if (initial != null)
        {
            input = new TextBox { Text = initial, AcceptsReturn = multiline };
            if (multiline)
            {
                input.Height = 160;
            }

            panel.Children.Add(input);
            confirm.IsEnabled = multiline || !string.IsNullOrWhiteSpace(input.Text);
            input.TextChanged += (_, _) => { confirm.IsEnabled = multiline || !string.IsNullOrWhiteSpace(input.Text); };
            dialog.Opened += (_, _) =>
            {
                input.Focus();
                input.SelectAll();
            };
        }
        else
        {
            dialog.Opened += (_, _) => { cancel.Focus(); };
        }

        cancel.Click += (_, _) => { dialog.Close(); };
        confirm.Click += (_, _) => { dialog.Close(input?.Text ?? "confirmed"); };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        panel.Children.Add(actions);
        return await dialog.ShowDialog<string?>(this);
    }
}