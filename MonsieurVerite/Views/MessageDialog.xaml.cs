using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace MonsieurVerite.Views;

public partial class MessageDialog : DialogWindow
{
    private MessageDialog()
    {
        InitializeComponent();
    }

    public static bool Show(
        Window owner, string title, string message,
        string? primary = null, string? secondary = null, string? detail = null,
        string? imageUri = null)
    {
        var dialog = new MessageDialog { Owner = owner, Title = title };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.PrimaryButton.Content = primary ?? Strings.OK;
        
        if (imageUri is not null)
        {
            dialog.Banner.Source = new BitmapImage(new Uri(imageUri, UriKind.Absolute));
            dialog.Banner.Visibility = Visibility.Visible;
        }
        
        if (secondary is not null)
        {
            dialog.SecondaryButton.Content = secondary;
            dialog.SecondaryButton.Visibility = Visibility.Visible;
        }

        if (detail is { Length: > 0 })
        {
            dialog.DetailText.Text = detail;
            dialog.DetailPanel.Visibility = Visibility.Visible;
        }

        return dialog.ShowDialog() == true;
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SecondaryButton.Visibility != Visibility.Visible)
        {
            e.Handled = true;
            Close();
        }
    }
}
