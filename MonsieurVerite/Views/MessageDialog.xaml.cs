using System.Windows;
using System.Windows.Input;
using MonsieurVerite.ViewModels;

namespace MonsieurVerite.Views;

public partial class MessageDialog : DialogWindow
{
    private MessageDialog()
    {
        InitializeComponent();
    }

    public static bool Show(Window owner, Message message)
    {
        var dialog = new MessageDialog { Owner = owner, Title = message.Title };
        dialog.TitleText.Text = message.Title;
        dialog.MessageText.Text = message.Text;
        dialog.PrimaryButton.Content = message.Primary ?? Strings.OK;

        if (message.Secondary is { } secondary)
        {
            dialog.SecondaryButton.Content = secondary;
            dialog.SecondaryButton.Visibility = Visibility.Visible;
        }

        if (message.Detail is { Length: > 0 } detail)
        {
            dialog.DetailText.Text = detail;
            dialog.DetailPanel.Visibility = Visibility.Visible;
        }

        var blocks = ReleaseNotes.Render(message.Notes ?? "");
        if (blocks.Count > 0)
        {
            foreach (var block in blocks)
            {
                dialog.NotesContent.Children.Add(block);
            }

            dialog.NotesPanel.Visibility = Visibility.Visible;
            dialog.Width = 560;
        }

        if (ReleaseNotes.WebLink(message.NotesUrl) is { } link)
        {
            dialog.NotesLink.NavigateUri = link;
            dialog.NotesLinkText.Visibility = Visibility.Visible;
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
