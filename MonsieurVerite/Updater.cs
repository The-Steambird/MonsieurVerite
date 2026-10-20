using System.Globalization;
using System.IO;

namespace MonsieurVerite;

/// <summary>
/// The engine installs updates, including this exe. What stays here is restarting into the new
/// version and clearing the files the install moved aside.
/// </summary>
public static class Updater
{
    public static void DeleteStaleFiles(string appDirectory)
    {
        // Only the top level is swept, because the bundle is flat and the app folder also holds the
        // user's output and subtitle cache.
        foreach (var stale in Directory.EnumerateFiles(appDirectory, "*.old"))
        {
            try
            {
                File.Delete(stale);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Could not delete {stale}: {e.Message}");
            }
        }
    }

    public static void Relaunch()
    {
        if (Environment.ProcessPath is not { } exe)
        {
            return;
        }

        var processId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        var successor = new ProcessStartInfo(exe, ["--wait-for", processId])
        {
            WorkingDirectory = AppContext.BaseDirectory,
        };
        Process.Start(successor)?.Dispose();
    }

    /// <summary>
    /// After an update the old instance starts this one before exiting, and its exe is one of the
    /// *.old files. Waiting lets the sweep delete it now rather than on the next launch.
    /// </summary>
    public static void WaitForPredecessor(string[] args)
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
