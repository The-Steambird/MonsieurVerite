using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using MonsieurVerite.ViewModels;

namespace MonsieurVerite.Views;

public partial class MainWindow : Window
{
    private const double LogMinHeight = 60;
    private const double QueueMinHeight = 160;
    private static readonly Duration OverlayFade = new(TimeSpan.FromMilliseconds(150));
    private readonly MainViewModel viewModel;
    private readonly TaskCompletionSource shown =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ScrollViewer? logScroller;
    private int dragDepth;
    private QueueItem? anchor;
    private double logHeight;

    public MainWindow(Settings settings, bool restart)
    {
        InitializeComponent();
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width - 48);
        Height = Math.Min(Height, area.Height - 48);

        viewModel = new MainViewModel(settings.ResolveEngine(), settings)
        {
            IsRestart = restart,
            PickFolder = PickFolder,
            PickFiles = PickFiles,
            ShowSettings = ShowSettings,
            PromptKey = PromptKey,
            ShowCapture = capture => new CaptureDialog(capture) { Owner = this }.ShowDialog(),
            CopyText = CopyToClipboard,
            ShowMessage = message => MessageDialog.Show(this, message),
            CloseWindow = Close,
            FlashWindow = FlashIfInactive,
        };
        ResizeLog(132);
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
        ContentRendered += (_, _) => shown.TrySetResult();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Chrome.Solid(this);
        Chrome.HookCaption(this, MaximizeButton);
        PaintBleed(VisualTreeHelper.GetDpi(this));
    }

    private Window? Dialog =>
        OwnedWindows.Cast<Window>().FirstOrDefault(window => window.IsVisible);

    internal void UpdateShade() => Fade(ModalShade, Dialog is not null);

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => Chrome.ToggleMaximized(this);

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

    private void PaintBleed(DpiScale dpi)
    {
        var canvas = (Color)FindResource("WindowColor");
        var accent = (Color)FindResource("AccentColor");
        Bleed.Fill = Chrome.Bleed(canvas, accent, dpi);
    }

    // Without this, an exception from an async void handler or a command closes the app with no
    // trace. Only the log takes it under a dialog, because a repeating failure would stack them.
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var exception = e.Exception;
        Debug.WriteLine(exception);
        var line = Strings.UNEXPECTED_ERROR_LOG(exception.GetType().Name, exception.Message);
        viewModel.AppendLog(line, warning: true);
        if (Dialog is null)
        {
            var message = new Message(Strings.UNEXPECTED_ERROR_TITLE, exception.Message,
                Detail: exception.ToString());
            MessageDialog.Show(this, message);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) =>
        await viewModel.StartupAsync(shown.Task);

    // A dialog disables this window, but a WM_CLOSE from outside (taskkill without /f) still
    // arrives, and closing here would pull the dialog out from under its ShowDialog caller.
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (Dialog is { } dialog)
        {
            e.Cancel = true;
            dialog.Activate();
            return;
        }

        if (viewModel.IsRunning)
        {
            var question = new Message(Strings.QUIT_TITLE, Strings.QUIT_MESSAGE, Strings.QUIT,
                Strings.KEEP_RUNNING);
            if (!MessageDialog.Show(this, question))
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
            Filter = $"{Strings.USM_FILES} (*.usm)|*.usm|{Strings.ALL_FILES}|*.*",
            Title = Strings.ADD_FILES_TITLE,
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

    private void FlashIfInactive()
    {
        if (!IsActive)
        {
            Chrome.Flash(this);
        }
    }

    private void About_Click(object sender, RoutedEventArgs e) =>
        new AboutDialog(viewModel.HasEngine, viewModel.EngineVersion) { Owner = this }.ShowDialog();

    // Shift+click follows the sorted view, because the range the user sees is the one they mean.
    private void Queue_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || FindAncestor<DataGridRow>(source) is not { DataContext: QueueItem item })
        {
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Shift && anchor is not null)
        {
            var rows = Queue.Items.Cast<QueueItem>().ToList();
            MainViewModel.CheckRange(rows, anchor, item);
            e.Handled = true;
        }
        else if (FindAncestor<CheckBox>(source) is null)
        {
            item.IsChecked = !item.IsChecked;
            e.Handled = true;
        }

        anchor = item;
    }

    // The menu acts on the checked rows. Right-clicking a row outside them makes it the only one,
    // as Explorer does with a selection.
    private void Queue_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && FindAncestor<DataGridRow>(source) is { DataContext: QueueItem { IsChecked: false } item })
        {
            viewModel.CheckOnly(item);
            anchor = item;
        }
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
        if (viewModel.IsRunning
            || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } dropped)
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

    private void LogToggle_Checked(object sender, RoutedEventArgs e) => SlideLog(logHeight, 200);

    private void LogToggle_Unchecked(object sender, RoutedEventArgs e) => SlideLog(0, 180);

    private void SlideLog(double height, double milliseconds)
    {
        var slide = new DoubleAnimation(height, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = (IEasingFunction)FindResource("EaseOut"),
        };
        LogPane.BeginAnimation(HeightProperty, slide);
    }

    private void LogGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var tallest = Math.Max(LogMinHeight, logHeight + QueueArea.ActualHeight - QueueMinHeight);
        ResizeLog(Math.Clamp(logHeight - e.VerticalChange, LogMinHeight, tallest));
        LogPane.BeginAnimation(HeightProperty, null);
        LogPane.Height = logHeight;
    }

    // The list keeps its own height while the pane slides. The slide then reveals its lines
    // instead of reflowing them.
    private void ResizeLog(double height)
    {
        logHeight = height;
        LogList.Height = height - 12;
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var lines = LogList.SelectedItems.Count > 0
            ? LogList.SelectedItems.Cast<LogLine>()
            : viewModel.Log;
        CopyToClipboard(string.Join(Environment.NewLine, lines.Select(line => line.Text)));
    }

    private void CopyAllLog_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard(string.Join(Environment.NewLine, viewModel.Log.Select(line => line.Text)));

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
            viewModel.AppendLog(Strings.CLIPBOARD_FAILED_LOG(e.Message), warning: true);
        }
    }

    // ScrollIntoView looks the item up by value and would send a repeated log line to its first
    // occurrence instead of the bottom.
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
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
