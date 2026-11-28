using Avalonia.Controls;
using Serilog;
using Serilog.Events;

namespace Cloudreve.Desktop;

internal sealed partial class IgnoreRulesWindow : Window
{
    internal IgnoreRulesWindow(string driveName, string rules, Action<string> saveRules)
    {
        InitializeComponent();
        Motion.EnableTransitions(this);
        DriveName.Text = driveName;
        RulesEditor.Text = rules;
        Opened += (_, _) => { RulesEditor.Focus(); };
        CancelButton.Click += (_, _) => { Close(); };
        SaveButton.Click += (_, _) =>
        {
            try
            {
                saveRules(RulesEditor.Text ?? string.Empty);
            }
            catch (Exception ex)
            {
                Log.Logger.WriteFailure(LogEventLevel.Warning, "Save ignore rules", ex);
                SaveError.Text = ex.Message;
                SaveError.IsVisible = true;
                RulesEditor.Focus();
                return;
            }

            Close();
        };
    }
}
