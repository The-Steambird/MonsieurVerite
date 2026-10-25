using System.ComponentModel;
using System.Windows;
using System.Windows.Navigation;
using MonsieurVerite.ViewModels;

namespace MonsieurVerite.Views;

public class DialogWindow : Window
{
    public DialogWindow()
    {
        SetResourceReference(StyleProperty, "DialogWindow");
        // No style can set this, because it is not a dependency property.
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SourceInitialized += (_, _) => Chrome.Frost(this);
        IsVisibleChanged += (_, _) => (Owner as MainWindow)?.UpdateShade();
    }

    protected void Dismiss(object sender, RoutedEventArgs e) => Close();

    protected void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception failure)
        {
            var message = new Message(Strings.LINK_FAILED_TITLE, failure.Message, Detail: e.Uri.AbsoluteUri);
            MessageDialog.Show(this, message);
        }
    }
}
