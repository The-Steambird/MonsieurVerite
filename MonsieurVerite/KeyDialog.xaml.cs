using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace MonsieurVerite;

public partial class KeyDialog : Window
{
    private readonly bool streamCipher;

    public KeyDialog(string fileName, bool streamCipher)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Chrome.Frost(this);
        this.streamCipher = streamCipher;
        Caption.Text = $"Apply your own key to {fileName}.";
        if (streamCipher)
        {
            Heading.Text = "Decryption keys";
            KeyLabel.Text = "Audio key";
            AesKeyPanel.Visibility = Visibility.Visible;
        }
    }

    public string Key { get; private set; } = "";

    private void KeyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var key = KeyBox.Text.Trim();
        var valid = ulong.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture,
            out var value);
        if (streamCipher)
        {
            var aesKey = AesKeyBox.Text.Trim();
            key = $"{key}:{aesKey}";
            valid = valid && value < 1UL << 56 && aesKey.Length == 32 &&
                    aesKey.All(char.IsAsciiHexDigit);
        }

        Key = key;
        OkButton.IsEnabled = valid;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
