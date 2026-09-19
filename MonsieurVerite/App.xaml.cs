using System.Reflection;
using System.Windows;

namespace MonsieurVerite;

public partial class App : Application
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Updater.DeleteStaleFiles(AppContext.BaseDirectory);
        ThemeMode = ThemeMode.Dark;
    }
}
