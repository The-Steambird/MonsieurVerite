using System.IO;
using System.Windows;
using Microsoft.Win32;
using MonsieurVerite.ViewModels;

namespace MonsieurVerite;

public partial class SettingsDialog : DialogWindow
{
    private readonly Settings settings;
    private RunOptions working;

    public SettingsDialog(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        InitializeComponent();
        this.settings = settings;
        working = settings.Options.Clone();
        DataContext = working;
        EngineBox.Text = settings.EffectiveEnginePath;
        UpdateCheck.IsChecked = settings.CheckForUpdatesOnStartup;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        settings.Options = working;
        settings.EnginePath = EngineBox.Text.Trim();
        settings.CheckForUpdatesOnStartup = UpdateCheck.IsChecked == true;
        DialogResult = true;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        working = new RunOptions();
        DataContext = working;
        EngineBox.Text = Settings.DefaultEnginePath;
        UpdateCheck.IsChecked = true;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "charlotte-cli.exe|charlotte-cli.exe|Programs (*.exe)|*.exe",
            Title = "Choose the engine",
        };
        if (Path.GetDirectoryName(EngineBox.Text.Trim()) is { } folder && Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog(this) == true)
        {
            EngineBox.Text = dialog.FileName;
        }
    }
}
