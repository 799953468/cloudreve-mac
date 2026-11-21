using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Cloudreve.Desktop;

internal sealed partial class WorkspaceWindow
{
    private async Task ShowActivityAsync()
    {
        var activity = pairActivity;
        var current = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var recent = new ListBox();
        var close = new Button { Content = UiText.Get("关闭"), IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        var pause = new Button { Content = UiText.Get("暂停当前文件"), IsEnabled = false };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(pause);
        buttons.Children.Add(close);
        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 10, Margin = new Thickness(16) };
        Grid.SetRow(recent, 1);
        Grid.SetRow(buttons, 2);
        panel.Children.Add(current);
        panel.Children.Add(recent);
        panel.Children.Add(buttons);
        var window = new Window
        {
            Title = UiText.Get("同步活动"), Width = 520, Height = 360, MinWidth = 340, MinHeight = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Content = panel
        };
        long revision = -1;
        ActivityTransfer? shown = null;
        pause.Click += (_, _) =>
        {
            if (shown != null)
            {
                activity.SetPaused(shown.Id, !shown.Paused);
                Refresh();
            }
        };

        void Refresh()
        {
            var changes = activity.ChangesSince(revision);
            if (changes == null)
            {
                return;
            }

            revision = changes.Revision;
            current.Text = changes.Current == null ? UiText.Get("没有正在处理的文件") : $"{changes.Current.Operation}：{changes.Current.Key}";
            shown = changes.Transfer;
            pause.IsEnabled = shown != null;
            pause.Content = shown?.Paused == true ? UiText.Get("继续当前文件") : UiText.Get("暂停当前文件");
            if (shown != null)
            {
                current.Text += $"\n{shown.Phase}  {shown.Completed:N0} / {shown.Total:N0} B";
            }

            recent.ItemsSource = changes.Recent.Select(entry => $"{entry.Time:HH:mm:ss}  {entry.Operation} · {entry.Status}  {entry.Key}").ToArray();
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => { Refresh(); };
        close.Click += (_, _) => { window.Close(); };
        Refresh();
        timer.Start();
        try
        {
            await window.ShowDialog(this);
        }
        finally
        {
            timer.Stop();
        }
    }
}