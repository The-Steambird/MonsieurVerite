using System.Windows;
using MonsieurVerite.ViewModels;

namespace MonsieurVerite;

public partial class SettingsDialog : Window
{
    private readonly RunOptions target;
    private readonly RunOptions working;

    public SettingsDialog(RunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        InitializeComponent();
        SourceInitialized += (_, _) => Chrome.Frost(this);
        target = options;
        working = options.Clone();
        DataContext = working;
        RecoveredKeysRun.Text = Settings.RecoveredKeysPath;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        target.CopyFrom(working);
        DialogResult = true;
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => working.CopyFrom(new RunOptions());
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
