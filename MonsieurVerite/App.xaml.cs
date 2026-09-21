using System.Globalization;
using System.Reflection;
using System.Windows;

namespace MonsieurVerite;

public partial class App
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        WaitForPredecessor(e.Args);
        Updater.DeleteStaleFiles(AppContext.BaseDirectory);
        ThemeMode = ThemeMode.Dark;
    }

    /// <summary>
    /// After an update the old instance launches this one before it has exited, and its exe is
    /// one of the *.old files. Waiting for it lets the sweep delete that file now instead of on
    /// the launch after this one.
    /// </summary>
    private static void WaitForPredecessor(string[] args)
    {
        if (args is not ["--wait-for", var text]
            || !int.TryParse(text, CultureInfo.InvariantCulture, out var processId))
        {
            return;
        }

        try
        {
            using var predecessor = Process.GetProcessById(processId);
            predecessor.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }
}
