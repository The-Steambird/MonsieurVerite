using System.Windows.Navigation;
using MonsieurVerite.ViewModels;

namespace MonsieurVerite;

public partial class AboutDialog : DialogWindow
{
    public AboutDialog(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();

        TitleText.Text = $"charlotte {App.Version}";
        BuildText.Text = App.Build ?? "unknown";
        EngineText.Text = viewModel.Engine is null ? "none found"
            : viewModel.EngineVersion is { } version ? $"charlotte-cli {version}"
            : "charlotte-cli";
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        e.Handled = true;
    }
}
