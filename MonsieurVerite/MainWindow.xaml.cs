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

        viewModel = new MainViewModel(Settings.ResolveEngine(), Settings.Load(),
            SynchronizationContext.Current)
        {
            PickFolder = PickFolder,
            PickFiles = PickFiles,
            EditOptions = EditOptions,
            ConfirmKeyOverwrite =
                prompt => MessageDialog.Show(this, "charlotte", prompt, "Yes", "No"),
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

        Loaded += OnLoaded;
        ContentRendered += OnContentRendered;
        Closing += OnClosing;
        Activated += (_, _) => Fade(ModalShade, false);
        Deactivated += (_, _) =>
            Fade(ModalShade, OwnedWindows.Cast<Window>().Any(window => window.IsVisible));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Chrome.Solid(this);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(SnapLayouts);
        PaintBleed(VisualTreeHelper.GetDpi(this));
    }

    private IntPtr SnapLayouts(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int hitTest = 0x0084, buttonDown = 0x00A1, buttonUp = 0x00A2, mouseLeave = 0x02A2;
        const int maxButton = 9;
        switch (msg)
        {
            case hitTest when MaximizeButton.IsVisible:
                var packed = lParam.ToInt64();
                var screen = new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
                var over = new Rect(MaximizeButton.RenderSize)
                    .Contains(MaximizeButton.PointFromScreen(screen));
                MaximizeButton.Tag = over ? "hot" : null;
                handled = over;
                return over ? maxButton : IntPtr.Zero;
            case buttonDown when wParam == maxButton:
                handled = true;
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
                "charlotte-cli.exe has to sit beside charlotte-gui.exe. Converting and key recovery are unavailable until it does.",
                detail: $"Expected: {Settings.BundledEnginePath}");
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(viewModel.SourceDirectory))
        {
            await viewModel.LoadSourceAsync(viewModel.SourceDirectory);
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
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

    private void ShowRecoveredKeys_Click(object sender, RoutedEventArgs e)
    {
        var path = viewModel.RecoveredKeysPath;
        if (File.Exists(path))
        {
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        else
        {
            viewModel.AppendLog($"No keys recovered yet. They will be written to {path}");
        }
    }

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

    private bool EditOptions(RunOptions options) =>
        new SettingsDialog(options) { Owner = this }.ShowDialog() == true;

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var engine = viewModel.Engine?.Description ?? "none found";
        MessageDialog.Show(
            this,
            $"charlotte {App.Version}",
            "MonsieurVerite, the front end for charlotte, the Genshin Impact cutscene converter.",
            detail: $"Engine: {engine}");
    }

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
            $"You have {update.Current}. Download and install the new release now? The app restarts afterwards.",
            "Install", "Not now", notes);
    }

    private void Restart()
    {
        if (Environment.ProcessPath is { } exe)
        {
            var processId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            Process.Start(new ProcessStartInfo(exe, ["--wait-for", processId])
                { WorkingDirectory = AppContext.BaseDirectory });
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

    private async void SetKey_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SingleChecked is not { } item)
        {
            return;
        }

        var dialog = new KeyDialog(item.FileName) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await viewModel.ConvertWithKeyAsync(item, dialog.VideoKey);
        }
    }

    private void CopyVideoKey_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SingleChecked?.VideoKey is { } videoKey)
        {
            CopyToClipboard(videoKey.ToString(CultureInfo.InvariantCulture));
        }
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
        !viewModel.IsRunning && e.Data.GetDataPresent(DataFormats.FileDrop);

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
        if (viewModel.IsRunning ||
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
    /// The clipboard is a shared resource, and Clipboard.SetText throws when another process is
    /// holding it, even after WPF's own retries.
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

    private void OnLogChanged(object sender, ItemsChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
        {
            return;
        }

        logScroller ??= LogList.Template.FindName("PART_ContentHost", LogList) as ScrollViewer;
        var wasAtBottom = logScroller is null
                          || logScroller.VerticalOffset >= logScroller.ScrollableHeight - 1;
        if (wasAtBottom)
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }
    }
}
