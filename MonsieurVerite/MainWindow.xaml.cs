using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using MonsieurVerite.Engine;
using MonsieurVerite.ViewModels;

namespace MonsieurVerite;

public partial class MainWindow : Window
{
    public static RoutedUICommand AddFilesCommand { get; } =
        new("Add files", nameof(AddFilesCommand), typeof(MainWindow));

    public static RoutedUICommand SettingsCommand { get; } =
        new("Settings", nameof(SettingsCommand), typeof(MainWindow));

    private static readonly Duration OverlayFade = new(TimeSpan.FromMilliseconds(150));
    private readonly Settings settings;
    private readonly MainViewModel viewModel;
    private ScrollViewer? logScroller;
    private int dragDepth;

    public MainWindow()
    {
        InitializeComponent();

        settings = Settings.Load();
        viewModel = new MainViewModel(Settings.ResolveEngine(), settings,
            SynchronizationContext.Current)
        {
            ConfirmKeyOverwrite =
                prompt => MessageDialog.Show(this, "charlotte", prompt, "Yes", "No"),
            ConfirmUpdate = ConfirmUpdate,
            RestartRequested = Restart,
        };
        DataContext = viewModel;
        viewModel.Log.CollectionChanged += OnLogChanged;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsRunning))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        };
        CommandManager.InvalidateRequerySuggested();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!viewModel.HasEngine)
        {
            MessageDialog.Show(
                this,
                "Could not find charlotte",
                "The engine has to sit beside MonsieurVerite.exe. Converting and key recovery are unavailable until it does.",
                detail: $"Expected: {Settings.BundledEnginePath}");
        }

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

        settings.SourceDirectory = viewModel.SourceDirectory;
        settings.OutputDirectory = viewModel.OutputDirectory;
        SaveSettings();
        viewModel.Shutdown();
    }

    private void SaveSettings()
    {
        try
        {
            settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a few remembered values is not worth refusing to close over.
            viewModel.AppendLog($"Could not save settings: {ex.Message}");
        }
    }

    private void WhenIdle_CanExecute(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = viewModel?.IsIdle ?? false;

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (MoreButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = MoreButton;
            menu.Placement = PlacementMode.Custom;
            menu.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [
                new CustomPopupPlacement(
                    new Point(targetSize.Width - popupSize.Width, targetSize.Height + 4),
                    PopupPrimaryAxis.Horizontal)
            ];
            menu.IsOpen = true;
        }
    }

    private async void OpenFolder_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (PickFolder(viewModel.SourceDirectory) is { } folder)
        {
            await viewModel.LoadSourceAsync(folder);
        }
    }

    private async void AddFiles_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "USM cutscenes (*.usm)|*.usm|All files|*.*",
            Title = "Add files to the queue",
        };
        if (Directory.Exists(viewModel.SourceDirectory))
        {
            dialog.InitialDirectory = viewModel.SourceDirectory;
        }

        if (dialog.ShowDialog(this) == true)
        {
            await viewModel.AddFilesAsync(dialog.FileNames);
        }
    }

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

    private void Settings_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var dialog = new SettingsDialog(settings.Options) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            SaveSettings();
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev";
        var engine = viewModel.Engine?.Description ?? "none found";
        MessageDialog.Show(
            this,
            $"MonsieurVerite {version}",
            "A front end for charlotte, the Genshin Impact cutscene converter.",
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
            Process.Start(new ProcessStartInfo(exe)
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
            Clipboard.SetText(videoKey.ToString(CultureInfo.InvariantCulture));
        }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder(viewModel.OutputDirectory) is { } folder)
        {
            viewModel.OutputDirectory = folder;
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

    private string? PickFolder(string initial)
    {
        var dialog = new OpenFolderDialog();
        if (Directory.Exists(initial))
        {
            dialog.InitialDirectory = initial;
        }

        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private bool CanAccept(DragEventArgs e) =>
        !viewModel.IsRunning && e.Data.GetDataPresent(DataFormats.FileDrop);

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        dragDepth++;
        if (CanAccept(e))
        {
            ShowDropOverlay(true);
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
                ShowDropOverlay(false);
            }
        });
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = CanAccept(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void ShowDropOverlay(bool visible) =>
        DropOverlay.BeginAnimation(OpacityProperty,
            new DoubleAnimation(visible ? 1 : 0, OverlayFade));

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        dragDepth = 0;
        ShowDropOverlay(false);
        if (viewModel.IsRunning ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } dropped)
        {
            return;
        }

        if (dropped is [var only] && Directory.Exists(only))
        {
            await viewModel.LoadSourceAsync(only);
            return;
        }

        var files = new List<string>();
        foreach (var path in dropped)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    files.AddRange(Directory.EnumerateFiles(path, "*.usm").Order());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    viewModel.AppendLog($"Could not read {path}: {ex.Message}");
                }
            }
            else
            {
                files.Add(path);
            }
        }

        await viewModel.AddFilesAsync(files);
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var lines = LogList.SelectedItems.Count > 0
            ? LogList.SelectedItems.Cast<string>()
            : viewModel.Log;
        CopyLines(lines);
    }

    private void CopyAllLog_Click(object sender, RoutedEventArgs e) => CopyLines(viewModel.Log);

    private static void CopyLines(IEnumerable<string> lines)
    {
        var text = string.Join(Environment.NewLine, lines);
        if (text.Length > 0)
        {
            Clipboard.SetText(text);
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null ||
            e.NewItems.Count == 0)
        {
            return;
        }

        logScroller ??= FindScrollViewer(LogList);
        var wasAtBottom = logScroller is null
                          || logScroller.VerticalOffset >= logScroller.ScrollableHeight - 1;
        if (wasAtBottom)
        {
            LogList.ScrollIntoView(e.NewItems[^1]);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer found)
        {
            return found;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } child)
            {
                return child;
            }
        }

        return null;
    }
}
