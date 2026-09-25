using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using MonsieurVerite.Engine;
using MonsieurVerite.ViewModels;

namespace MonsieurVerite;

public partial class MainWindow : Window
{
    private static readonly Duration OverlayFade = new(TimeSpan.FromMilliseconds(150));
    private readonly MainViewModel viewModel;
    private ScrollViewer? logScroller;
    private int dragDepth;

    public MainWindow()
    {
        InitializeComponent();
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width - 48);
        Height = Math.Min(Height, area.Height - 48);

        var settings = Settings.Load();
        viewModel = new MainViewModel(settings.ResolveEngine(), settings)
        {
            PickFolder = PickFolder,
            PickFiles = PickFiles,
            ShowSettings = ShowSettings,
            PromptKey = PromptKey,
            CopyText = CopyToClipboard,
            AnswerQuestion = prompt => MessageDialog.Show(this, "charlotte", prompt, "Yes", "No"),
            ConfirmUpdate = ConfirmUpdate,
            RestartRequested = Restart,
        };
        DataContext = viewModel;
        LogList.ItemContainerGenerator.ItemsChanged += OnLogChanged;

        MoreMenu.PlacementTarget = MoreButton;
        MoreMenu.Placement = PlacementMode.Custom;
        MoreMenu.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
        [
            new CustomPopupPlacement(
                new Point(targetSize.Width - popupSize.Width, targetSize.Height + 4),
                PopupPrimaryAxis.Horizontal)
        ];

        Dispatcher.UnhandledException += OnUnhandledException;
        Loaded += OnLoaded;
        ContentRendered += OnContentRendered;
        Closing += OnClosing;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Chrome.Solid(this);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(Caption);
        PaintBleed(VisualTreeHelper.GetDpi(this));
    }

    /// <summary>Snap layouts need the maximize button reported as HTMAXBUTTON.</summary>
    private IntPtr Caption(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int hitTest = 0x0084, buttonDown = 0x00A1, buttonUp = 0x00A2, mouseLeave = 0x02A2;
        const int caption = 2, maxButton = 9;
        switch (msg)
        {
            case hitTest when MaximizeButton.IsVisible:
                var over = Over(MaximizeButton, lParam);
                MaximizeButton.Tag = over ? "hot" : null;
                handled = over;
                return over ? maxButton : IntPtr.Zero;
            case buttonDown when wParam == maxButton:
                handled = true;
                return IntPtr.Zero;
            case buttonDown when wParam == caption:
                // The click never reaches WPF's mouse events, because the toolbar is caption.
                App.Blur(this);
                return IntPtr.Zero;
            case buttonUp when wParam == maxButton:
                handled = true;
                Maximize_Click(this, new RoutedEventArgs());
                return IntPtr.Zero;
            case mouseLeave:
                MaximizeButton.Tag = null;
                return IntPtr.Zero;
            default:
                return IntPtr.Zero;
        }
    }

    private static bool Over(FrameworkElement element, IntPtr packedScreenPoint)
    {
        var packed = packedScreenPoint.ToInt64();
        var screen = new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
        return new Rect(element.RenderSize).Contains(element.PointFromScreen(screen));
    }

    private Window? Dialog =>
        OwnedWindows.Cast<Window>().FirstOrDefault(window => window.IsVisible);

    internal void UpdateShade() => Fade(ModalShade, Dialog is not null);

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        Root.Margin = WindowState == WindowState.Maximized
            ? Chrome.MaximizedInset(VisualTreeHelper.GetDpi(this))
            : default;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        PaintBleed(newDpi);
    }

    private void PaintBleed(DpiScale dpi) =>
        Bleed.Fill = Chrome.Bleed((Color)FindResource("WindowColor"),
            (Color)FindResource("AccentColor"), dpi);

    private void OnContentRendered(object? sender, EventArgs e)
    {
        if (!viewModel.HasEngine)
        {
            MessageDialog.Show(
                this,
                "Could not find charlotte",
                "Converting and key recovery are unavailable until charlotte-cli.exe is where the engine setting points. Pick it under Settings > Engine, or put it beside charlotte-gui.exe.",
                detail: $"Expected: {viewModel.EnginePath}");
        }
    }

    // Without this, an exception from an async void handler or a command closes the app with no
    // trace. Only the log takes it under a dialog, because a repeating failure would stack them.
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Debug.WriteLine(e.Exception);
        viewModel.AppendLog($"Unexpected error: {e.Exception.GetType().Name}: {e.Exception.Message}");
        if (Dialog is null)
        {
            MessageDialog.Show(this, "Something went wrong", e.Exception.Message,
                detail: e.Exception.ToString());
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(viewModel.SourceDirectory))
        {
            await viewModel.LoadSourceAsync(viewModel.SourceDirectory);
        }

        await viewModel.CheckForUpdatesOnStartupAsync();
    }

    // A dialog disables this window, but a WM_CLOSE from outside (taskkill without /f) still
    // arrives, and closing here would pull the dialog out from under its ShowDialog caller.
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (Dialog is { } dialog)
        {
            e.Cancel = true;
            dialog.Activate();
            return;
        }

        if (viewModel.IsRunning)
        {
            var quit = MessageDialog.Show(
                this,
                "A run is still in progress",
                "Quit anyway? The engine will be stopped, and partial output may be left behind.",
                "Quit", "Keep running");
            if (!quit)
            {
                e.Cancel = true;
                return;
            }
        }

        viewModel.SaveSettings();
        viewModel.Shutdown();
    }

    private void More_Click(object sender, RoutedEventArgs e) => MoreMenu.IsOpen = true;

    private string? PickFolder(string initial)
    {
        var dialog = new OpenFolderDialog();
        if (Directory.Exists(initial))
        {
            dialog.InitialDirectory = initial;
        }

        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private string[]? PickFiles(string initial)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "USM cutscenes (*.usm)|*.usm|All files|*.*",
            Title = "Add files to the queue",
        };
        if (Directory.Exists(initial))
        {
            dialog.InitialDirectory = initial;
        }

        return dialog.ShowDialog(this) == true ? dialog.FileNames : null;
    }

    private bool ShowSettings(Settings settings) =>
        new SettingsDialog(settings) { Owner = this }.ShowDialog() == true;

    private string? PromptKey(QueueItem item)
    {
        var dialog = new KeyDialog(item.FileName, item.StreamCipher) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Key : null;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e) =>
        new AboutDialog(viewModel) { Owner = this }.ShowDialog();

    private bool ConfirmUpdate(UpdateEvent update)
    {
        if (!update.Available)
        {
            if (update.Reason is { Length: > 0 } reason)
            {
                MessageDialog.Show(this, "Could not check for updates", reason);
            }
            else
            {
                MessageDialog.Show(this, "Up to date",
                    $"charlotte {update.Current} is the latest release.");
            }

            return false;
        }

        var notes = update.Notes is { Length: > 1200 } text ? text[..1200] + "…" : update.Notes;
        return MessageDialog.Show(
            this,
            $"charlotte {update.Latest} is available",
            $"Update from {update.Current} → {update.Latest}? Charlotte will automatically restart when done.",
            "Update", "Cancel", notes);
    }

    private void Restart()
    {
        if (Environment.ProcessPath is { } exe)
        {
            var processId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            var successor = new ProcessStartInfo(exe, ["--wait-for", processId])
            {
                WorkingDirectory = AppContext.BaseDirectory,
            };
            Process.Start(successor)?.Dispose();
        }

        Close();
    }

    private void Queue_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || FindAncestor<CheckBox>(source) is not null
            || FindAncestor<DataGridRow>(source) is not { DataContext: QueueItem item })
        {
            return;
        }

        item.IsChecked = !item.IsChecked;
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var node = start; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is T found)
            {
                return found;
            }
        }

        return null;
    }

    private void OutputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            e.Handled = true;
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Queue.Focus();
        }
    }

    private bool CanAccept(DragEventArgs e) =>
        !viewModel.IsRunning && Dialog is null && e.Data.GetDataPresent(DataFormats.FileDrop);

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        dragDepth++;
        if (CanAccept(e))
        {
            Fade(DropOverlay, true);
        }
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        dragDepth--;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (dragDepth <= 0)
            {
                dragDepth = 0;
                Fade(DropOverlay, false);
            }
        });
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = CanAccept(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static void Fade(UIElement element, bool visible) =>
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? 1 : 0, OverlayFade));

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        dragDepth = 0;
        Fade(DropOverlay, false);
        if (viewModel.IsRunning || Dialog is not null ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } dropped)
        {
            return;
        }

        if (dropped is [var only] && Directory.Exists(only))
        {
            await viewModel.LoadSourceAsync(only);
        }
        else
        {
            await viewModel.AddFilesAsync(dropped);
        }
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var lines = LogList.SelectedItems.Count > 0
            ? LogList.SelectedItems.Cast<string>()
            : viewModel.Log;
        CopyToClipboard(string.Join(Environment.NewLine, lines));
    }

    private void CopyAllLog_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard(string.Join(Environment.NewLine, viewModel.Log));

    /// <summary>
    /// Clipboard.SetText throws while another process holds the clipboard, despite WPF's retries.
    /// </summary>
    private void CopyToClipboard(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException e)
        {
            viewModel.AppendLog($"Could not copy to the clipboard: {e.Message}");
        }
    }

    // ScrollIntoView looks the item up by value, which would send a repeated log line to its
    // first occurrence instead of the bottom.
    private void OnLogChanged(object sender, ItemsChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
        {
            return;
        }

        logScroller ??= FindDescendant<ScrollViewer>(LogList);
        if (logScroller is { } scroller && scroller.VerticalOffset >= scroller.ScrollableHeight - 1)
        {
            scroller.ScrollToEnd();
        }
    }

    private static T? FindDescendant<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if ((child as T ?? FindDescendant<T>(child)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
