using MonsieurVerite.ViewModels;

namespace MonsieurVerite.Views;

public partial class CaptureDialog : DialogWindow
{
    public CaptureDialog(KeyCapture capture)
    {
        InitializeComponent();
        DataContext = capture;
        Closing += (_, _) => capture.Abandon();
    }
}
