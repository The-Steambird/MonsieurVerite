using System.Windows;

namespace MonsieurVerite;

public class DialogWindow : Window
{
    public DialogWindow()
    {
        SetResourceReference(StyleProperty, "DialogWindow");
        // No style can set this, because it is not a dependency property.
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SourceInitialized += (_, _) => Chrome.Frost(this);
    }

    protected void Dismiss(object sender, RoutedEventArgs e) => Close();
}
