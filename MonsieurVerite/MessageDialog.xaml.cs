using System.Windows;
using System.Windows.Input;

namespace MonsieurVerite;

public partial class MessageDialog : Window
{
    private MessageDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Chrome.Frost(this);
    }

    public static bool Show(
        Window owner, string title, string message,
        string primary = "OK", string? secondary = null, string? detail = null)
    {
        var dialog = new MessageDialog { Owner = owner, Title = title };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.PrimaryButton.Content = primary;

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

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
