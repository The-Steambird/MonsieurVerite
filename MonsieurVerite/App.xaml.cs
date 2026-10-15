using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MonsieurVerite.Views;

namespace MonsieurVerite;

public partial class App
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static string? Build { get; } = ShortCommit();

    protected override void OnStartup(StartupEventArgs e)
    {
        Updater.WaitForPredecessor(e.Args);
        var settings = Settings.Load();
        Strings.Use(settings.Language, AppContext.BaseDirectory);
        base.OnStartup(e);
        Updater.DeleteStaleFiles(AppContext.BaseDirectory);
        ThemeMode = ThemeMode.Dark;
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent,
            new MouseButtonEventHandler(BlurOnClickOutside));
        new MainWindow(settings).Show();
    }

    // The SDK appends the commit hash to the informational version after a plus sign.
    private static string? ShortCommit()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var plus = version.IndexOf('+');
        if (plus < 0)
        {
            return null;
        }

        var commit = version[(plus + 1)..];
        return commit[..Math.Min(7, commit.Length)];
    }

    private static void BlurOnClickOutside(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.FocusedElement is not TextBoxBase box || sender is not Window window)
        {
            return;
        }

        var node = e.OriginalSource as DependencyObject;
        while (node is not null and not Visual)
        {
            node = LogicalTreeHelper.GetParent(node);
        }

        var clickedBox = node is Visual visual && (visual == box || box.IsAncestorOf(visual));
        if (!clickedBox)
        {
            Blur(window);
        }
    }

    public static void Blur(Window window)
    {
        FocusManager.SetFocusedElement(window, null);
        Keyboard.Focus(window);
    }
}
