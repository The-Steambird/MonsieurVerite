using System.IO;
using System.Windows;
using Microsoft.Win32;
using MonsieurVerite.ViewModels;

namespace MonsieurVerite.Views;

public partial class SettingsDialog : DialogWindow
{
    private readonly Settings settings;
    private readonly List<Language> languages = Strings.Choices();
    private RunOptions working;

    public SettingsDialog(Settings settings)
    {
        InitializeComponent();
        this.settings = settings;
        working = settings.Options.Clone();
        DataContext = working;
        EngineBox.Text = settings.EffectiveEnginePath;
        UpdateCheck.IsChecked = settings.CheckForUpdatesOnStartup;
        LanguageBox.ItemsSource = languages;
        LanguageBox.SelectedItem = languages.Find(language => language.Code == settings.Language);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        settings.Options = working;
        settings.EnginePath = EngineBox.Text.Trim();
        settings.CheckForUpdatesOnStartup = UpdateCheck.IsChecked == true;
        if (LanguageBox.SelectedItem is Language language)
        {
            settings.Language = language.Code;
        }

        DialogResult = true;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        working = new RunOptions();
        DataContext = working;
        EngineBox.Text = Settings.DefaultEnginePath;
        UpdateCheck.IsChecked = true;
        LanguageBox.SelectedItem = languages.Find(language => language.Code == Strings.SourceLanguage);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = $"charlotte-cli.exe|charlotte-cli.exe|{Strings.PROGRAMS} (*.exe)|*.exe",
            Title = Strings.CHOOSE_ENGINE_TITLE,
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
