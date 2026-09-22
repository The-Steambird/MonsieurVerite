using System.IO;
using System.Windows;
using Microsoft.Win32;
using MonsieurVerite.ViewModels;

namespace MonsieurVerite;

public partial class SettingsDialog : Window
{
    private readonly Settings settings;
    private readonly RunOptions working;

    public SettingsDialog(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        InitializeComponent();
        SourceInitialized += (_, _) => Chrome.Frost(this);
        this.settings = settings;
        working = settings.Options.Clone();
        DataContext = working;
        EngineBox.Text = settings.EffectiveEnginePath;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        settings.Options.CopyFrom(working);
        settings.EnginePath = EngineBox.Text.Trim();
        DialogResult = true;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        working.CopyFrom(new RunOptions());
        EngineBox.Text = Settings.DefaultEnginePath;
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

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
