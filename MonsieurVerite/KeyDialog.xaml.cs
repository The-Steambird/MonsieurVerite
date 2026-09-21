using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace MonsieurVerite;

public partial class KeyDialog : Window
{
    public KeyDialog(string fileName)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Chrome.Acrylic(this);
        Caption.Text =
            $"Convert {fileName} with a key that neither keys.json nor recovery supplied.";
    }

    public ulong VideoKey { get; private set; }

    private void KeyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        OkButton.IsEnabled = ulong.TryParse(KeyBox.Text.Trim(), NumberStyles.None,
            CultureInfo.InvariantCulture, out var videoKey);
        VideoKey = videoKey;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
