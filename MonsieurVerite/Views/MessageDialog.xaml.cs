using System.Windows;
using System.Windows.Input;

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
        string? notes = null, string? notesUrl = null)
    {
        var dialog = new MessageDialog { Owner = owner, Title = title };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.PrimaryButton.Content = primary ?? Strings.OK;

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

        var blocks = ReleaseNotes.Render(notes ?? "");
        if (blocks.Count > 0)
        {
            foreach (var block in blocks)
            {
                dialog.NotesContent.Children.Add(block);
            }

            dialog.NotesPanel.Visibility = Visibility.Visible;
            dialog.Width = 560;
        }

        if (ReleaseNotes.WebLink(notesUrl) is { } link)
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
