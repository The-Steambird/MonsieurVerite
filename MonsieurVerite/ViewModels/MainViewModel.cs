using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MonsieurVerite.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const int LogCapacity = 2000;
    private static readonly string SavedQueuePath = Path.Combine(AppContext.BaseDirectory, "queue.json");
    private readonly Settings settings;
    private CancellationTokenSource? cancellation;
    private EngineRun? run;

    public MainViewModel(string? engine, Settings settings)
    {
        this.settings = settings;
        Engine = engine;

        SourceDirectory = settings.SourceDirectory ?? "";
        OutputDirectory = settings.OutputDirectory ?? Path.Combine(settings.EngineFolder, "output");
        IsLogOpen = true;

        Items.CollectionChanged += (_, _) => RefreshQueue();
        Items.RowChanged += OnRowChanged;

        if (settings.LoadError is { } error)
        {
            AppendLog(error, warning: true);
        }

        if (engine is null)
        {
            AppendLog(NoEngineMessage, warning: true);
        }
    }

    private string NoEngineMessage => Strings.NO_ENGINE_LOG(EnginePath);

    public QueueItems Items { get; } = [];

    public ObservableCollection<LogLine> Log { get; } = [];

    public RunOptions Options => settings.Options;

    public Func<string, string?>? PickFolder { get; set; }

    public Func<string, IReadOnlyList<string>?>? PickFiles { get; set; }

    public Func<Settings, bool>? ShowSettings { get; set; }

    public Func<QueueItem, string?>? PromptKey { get; set; }

    public Action<KeyCapture>? ShowCapture { get; set; }

    public Action<string>? CopyText { get; set; }

    /// <summary>Shows a message and reports whether its primary button was pressed.</summary>
    public Func<Message, bool>? ShowMessage { get; set; }

    public Action? CloseWindow { get; set; }

    public string RecoveredKeysPath => settings.RecoveredKeysPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEngine), nameof(CanRunEngine))]
    public partial string? Engine { get; private set; }

    public bool HasEngine => Engine is not null;

    /// <summary>Where the engine is expected, whether or not it is there.</summary>
    public string EnginePath => settings.EffectiveEnginePath;

    /// <summary>Null until the engine has run once, because only session_start carries it.</summary>
    public string? EngineVersion { get; internal set; }

    [ObservableProperty] public partial string SourceDirectory { get; set; }

    [ObservableProperty] public partial string OutputDirectory { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanRunEngine))]
    public partial bool IsRunning { get; internal set; }

    public bool IsIdle => !IsRunning;

    /// <summary>An engine run was asked to cancel, and a second press kills it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancelLabel))]
    public partial bool CanForceStop { get; private set; }

    /// <summary>The engine's job object was terminated, and the run ends once its pipes close.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancelLabel))]
    public partial bool IsStopping { get; private set; }

    public string CancelLabel
    {
        get
        {
            if (IsStopping)
            {
                return Strings.STOPPING;
            }

            return CanForceStop ? Strings.FORCE_STOP : Strings.CANCEL;
        }
    }

    public bool CanRunEngine => HasEngine && !IsRunning;

    /// <summary>
    /// Started by a language restart. The previous instance handed over its queue and already
    /// checked for updates.
    /// </summary>
    public bool IsRestart { get; init; }

    [ObservableProperty] public partial bool IsLogOpen { get; set; }

    public bool? CheckState
    {
        get
        {
            if (Items.AllChecked)
            {
                return true;
            }

            if (Items.Checked.Any())
            {
                return null;
            }

            return false;
        }
    }

    public string StartLabel
    {
        get
        {
            var checkedCount = Items.Checked.Count();
            return checkedCount > 0 && checkedCount < Items.Count
                ? Strings.START_CHECKED(checkedCount)
                : Strings.START;
        }
    }

    public async Task LoadSourceAsync(string directory)
    {
        if (ListCutscenes(directory) is not { } paths)
        {
            return;
        }

        SourceDirectory = directory;
        Items.Clear();
        await AddFilesAsync(paths).ConfigureAwait(true);
    }

    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var added = new List<QueueItem>();
        foreach (var path in paths.SelectMany(Expand))
        {
            if (!string.Equals(Path.GetExtension(path), ".usm", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileName = Path.GetFileName(path);
            if (Items.Find(fileName) is not null)
            {
                AppendLog(Strings.ALREADY_QUEUED_LOG(fileName));
                continue;
            }

            var item = new QueueItem(path);
            Items.Add(item);
            added.Add(item);
        }

        if (added.Count == 0)
        {
            return;
        }

        if (SourceDirectory.Length == 0)
        {
            SourceDirectory = Path.GetDirectoryName(added[0].FullPath) ?? "";
        }

        if (HasEngine)
        {
            var (folder, files) = EngineFiles(added);
            await RunEngineAsync(["--probe", .. files], opensJobs: false, folder).ConfigureAwait(true);
        }
    }

    private IEnumerable<string> Expand(string path)
    {
        if (!Directory.Exists(path))
        {
            return [path];
        }

        return ListCutscenes(path) ?? [];
    }

    private List<string>? ListCutscenes(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.usm").Order().ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppendLog(Strings.FOLDER_UNREADABLE_LOG(directory, e.Message), warning: true);
            return null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task OpenFolderAsync()
    {
        if (PickFolder?.Invoke(SourceDirectory) is { } folder)
        {
            await LoadSourceAsync(folder).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task LoadFromGameAsync()
    {
        if (GameInstall.CutsceneFolder() is { } folder)
        {
            await LoadSourceAsync(folder).ConfigureAwait(true);
        }
        else
        {
            ShowMessage?.Invoke(new Message(Strings.LOAD_FROM_GAME, Strings.GAME_NOT_FOUND_MESSAGE));
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task BrowseFilesAsync()
    {
        if (PickFiles?.Invoke(SourceDirectory) is { } files)
        {
            await AddFilesAsync(files).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        if (PickFolder?.Invoke(OutputDirectory) is { } folder)
        {
            OutputDirectory = folder;
        }
    }

    [RelayCommand]
    private void EditSettings()
    {
        var previousPath = EnginePath;
        var previousLanguage = settings.Language;
        if (ShowSettings?.Invoke(settings) ?? false)
        {
            SaveSettings();
            OnPropertyChanged(nameof(Options));
            ApplyEngineSetting(previousPath);
            OfferRestartForLanguage(previousLanguage);
        }
    }

    // A new language needs a restart because a window reads its strings once. Switching them first
    // puts the question in the language the user just picked.
    internal void OfferRestartForLanguage(string previousLanguage)
    {
        if (settings.Language == previousLanguage)
        {
            return;
        }

        Strings.Use(settings.Language);

        if (IsRunning)
        {
            AppendLog(Strings.LANGUAGE_CHANGED_LOG);
            return;
        }

        var question = new Message(Strings.RESTART_TITLE, Strings.RESTART_MESSAGE,
            Strings.RESTART_NOW, Strings.LATER);
        if (ShowMessage?.Invoke(question) != true)
        {
            return;
        }

        try
        {
            Items.Save(SavedQueuePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The next instance probes the folder instead, as on any start.
        }

        Updater.Relaunch("--restart");
        CloseWindow?.Invoke();
    }

    // Resolved again even when the path is unchanged, because the file may have appeared there
    // since the last look.
    private void ApplyEngineSetting(string previousPath)
    {
        var engine = settings.ResolveEngine();
        if (engine == Engine && EnginePath == previousPath)
        {
            return;
        }

        Engine = engine;
        EngineVersion = null;
        if (engine is null)
        {
            AppendLog(NoEngineMessage, warning: true);
        }
        else
        {
            AppendLog(Strings.ENGINE_LOG(engine));
        }
    }

    public void SaveSettings()
    {
        settings.SourceDirectory = SourceDirectory;
        settings.OutputDirectory = OutputDirectory;
        try
        {
            settings.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppendLog(Strings.SETTINGS_UNSAVED_LOG(e.Message), warning: true);
        }
    }

    [RelayCommand]
    private void ToggleAll()
    {
        var target = !Items.AllChecked;
        foreach (var item in Items)
        {
            item.IsChecked = target;
        }
    }

    [RelayCommand]
    private void UncheckAll()
    {
        foreach (var item in Items)
        {
            item.IsChecked = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void RemoveChecked()
    {
        foreach (var item in Items.Checked.ToList())
        {
            Items.Remove(item);
        }
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        var output = Items.Checked.FirstOrDefault(item => File.Exists(item.OutputPath))?.OutputPath;
        if (output is not null)
        {
            Explore($"/select,\"{output}\"");
        }
        else if (Directory.Exists(OutputDirectory))
        {
            Explore($"\"{OutputDirectory}\"");
        }
        else
        {
            AppendLog(Strings.NO_OUTPUT_FOLDER_LOG(OutputDirectory));
        }
    }

    [RelayCommand]
    private void ShowRecoveredKeys()
    {
        if (File.Exists(RecoveredKeysPath))
        {
            Explore($"/select,\"{RecoveredKeysPath}\"");
        }
        else
        {
            AppendLog(Strings.NO_RECOVERED_KEYS_LOG(RecoveredKeysPath));
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunEngine))]
    private void CaptureKeys()
    {
        if (Engine is not { } engine)
        {
            return;
        }

        var capture = new KeyCapture(engine, settings) { CopyText = CopyText, AppendLog = AppendLog };
        ShowCapture?.Invoke(capture);
    }

    private static void Explore(string arguments) =>
        Process.Start("explorer.exe", arguments)?.Dispose();

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var targets = Items.Checked.ToList();
        if (targets.Count == 0)
        {
            targets = [.. Items];
        }

        await ConvertAsync(targets, []).ConfigureAwait(true);
    }

    private bool CanStart() => CanRunEngine && Items.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRetryFailed))]
    private async Task RetryFailedAsync()
    {
        var targets = Items.Where(item => item.Status == ItemStatus.Error).ToList();
        await ConvertAsync(targets, []).ConfigureAwait(true);
    }

    private bool CanRetryFailed() =>
        CanRunEngine && Items.Any(item => item.Status == ItemStatus.Error);

    [RelayCommand(CanExecute = nameof(CanSetKey))]
    private async Task SetKeyAsync()
    {
        if (Items.SingleChecked is { } item && PromptKey?.Invoke(item) is { } key)
        {
            await ConvertAsync([item], ["--key", key]).ConfigureAwait(true);
        }
    }

    private bool CanSetKey() => CanRunEngine && Items.SingleChecked is not null;

    [RelayCommand(CanExecute = nameof(CanCopyVideoKey))]
    private void CopyVideoKey()
    {
        if (Items.SingleChecked?.VideoKey is { } videoKey)
        {
            CopyText?.Invoke(videoKey.ToString(CultureInfo.InvariantCulture));
        }
    }

    private bool CanCopyVideoKey() => Items.SingleChecked?.VideoKey is not null;

    private async Task ConvertAsync(List<QueueItem> targets, IReadOnlyList<string> extraArguments)
    {
        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            AppendLog(Strings.CHOOSE_OUTPUT_LOG);
            return;
        }

        foreach (var item in targets)
        {
            item.MarkQueued();
            item.OutputPath = null;
        }

        // A relative output folder belongs to the engine's folder, where the engine no longer
        // starts.
        var output = Path.GetFullPath(OutputDirectory, settings.EngineFolder);
        var (folder, files) = EngineFiles(targets);
        await RunEngineAsync(
            ["--output", output, .. Options.ToArguments(), .. extraArguments, .. files],
            opensJobs: true,
            folder).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRecoverKeys))]
    private async Task RecoverKeysAsync()
    {
        var targets = Items.Where(IsRecoverable).ToList();
        var leftOut = Items.Checked.Count(item => item.StreamCipher);
        if (leftOut > 0)
        {
            AppendLog(Strings.LEFT_OUT_STREAM_CIPHER_LOG(leftOut));
        }

        foreach (var item in targets)
        {
            item.MarkQueued();
        }

        var (folder, files) = EngineFiles(targets);
        await RunEngineAsync(["--crack", .. files], opensJobs: true, folder).ConfigureAwait(true);
    }

    private bool CanRecoverKeys() => CanRunEngine && Items.Any(IsRecoverable);

    private static bool IsRecoverable(QueueItem item) => item.IsChecked && !item.StreamCipher;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (!CanForceStop)
        {
            cancellation?.Cancel();
            CanForceStop = true;
        }
        else
        {
            run?.ForceStop();
            IsStopping = true;
        }
    }

    private bool CanCancel() => IsRunning && !IsStopping;

    [RelayCommand(CanExecute = nameof(CanSkip))]
    private void Skip() => run?.Skip();

    private bool CanSkip() => run is { OpensJobs: true };

    public void Shutdown() => cancellation?.Cancel();

    // The folder loads before the first frame to keep the empty queue from flashing. The dialogs
    // wait for the window, because one opened earlier would appear alone on the desktop.
    public async Task StartupAsync(Task windowShown)
    {
        // Every start takes and deletes the file, including one left by a restart that never came up.
        var saved = QueueItems.TakeSaved(SavedQueuePath);
        if (IsRestart && saved is not null)
        {
            foreach (var row in saved)
            {
                Items.Add(QueueItem.Restore(row));
            }
        }
        else if (Directory.Exists(SourceDirectory))
        {
            await LoadSourceAsync(SourceDirectory).ConfigureAwait(true);
        }

        await windowShown.ConfigureAwait(true);
        if (Engine is null)
        {
            var detail = Strings.ENGINE_MISSING_DETAIL(EnginePath);
            ShowMessage?.Invoke(new Message(Strings.ENGINE_MISSING_TITLE,
                Strings.ENGINE_MISSING_MESSAGE, Detail: detail));
        }

        if (!IsRestart)
        {
            await CheckForUpdatesOnStartupAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunEngine))]
    private Task CheckForUpdatesAsync() => UpdateAsync(quiet: false);

    internal Task CheckForUpdatesOnStartupAsync() =>
        settings.CheckForUpdatesOnStartup && CanRunEngine
            ? UpdateAsync(quiet: true)
            : Task.CompletedTask;

    // Quiet leaves "up to date" and "could not check" to the log, because an offline start would
    // otherwise meet a dialog every time.
    private async Task UpdateAsync(bool quiet)
    {
        var check = await RunEngineAsync(["--update"], opensJobs: false).ConfigureAwait(true);
        if (check.Update is not { } update)
        {
            AppendLog(Strings.NO_UPDATE_RESULT_LOG, warning: true);
            return;
        }

        if (!update.Available && update.Reason is { Length: > 0 } reason)
        {
            AppendLog(Strings.UPDATE_CHECK_FAILED_LOG(reason), warning: true);
            if (!quiet)
            {
                ShowMessage?.Invoke(new Message(Strings.UPDATE_CHECK_FAILED_TITLE, reason));
            }

            return;
        }

        if (!update.Available)
        {
            AppendLog(Strings.UP_TO_DATE_LOG(update.Current));
            if (!quiet)
            {
                var text = Strings.UP_TO_DATE_MESSAGE(update.Current);
                ShowMessage?.Invoke(new Message(Strings.UP_TO_DATE_TITLE, text));
            }

            return;
        }

        AppendLog(Strings.UPDATE_AVAILABLE_LOG(update.Latest, update.Current));
        // A failed install needs nothing here, because the engine already logged its reason.
        if (check.Install is { Installed: true })
        {
            AppendLog(Strings.UPDATE_RESTARTING_LOG);
            Updater.Relaunch();
            CloseWindow?.Invoke();
        }
    }

    // Windows caps a command line at 32,767 characters, and the game's cutscenes overrun it as full
    // paths. The engine starts in the folder holding most of the files and gets those as bare names.
    private static (string? Folder, List<string> Files) EngineFiles(IReadOnlyList<QueueItem> items)
    {
        var folder = items
            .GroupBy(item => Path.GetDirectoryName(item.FullPath), StringComparer.OrdinalIgnoreCase)
            .MaxBy(group => group.Count())?.Key;
        if (!Directory.Exists(folder))
        {
            return (null, [.. items.Select(item => item.FullPath)]);
        }

        var files = new List<string>();
        foreach (var item in items)
        {
            var inFolder = string.Equals(Path.GetDirectoryName(item.FullPath), folder,
                StringComparison.OrdinalIgnoreCase);
            files.Add(inFolder ? item.FileName : item.FullPath);
        }

        return (folder, files);
    }

    /// <summary>
    /// This is the one owner of <see cref="IsRunning"/>, the cancellation source and the current
    /// run.
    /// </summary>
    private async Task<EngineRun> RunEngineAsync(
        IReadOnlyList<string> arguments, bool opensJobs, string? workingDirectory = null)
    {
        if (Engine is not { } engine)
        {
            throw new InvalidOperationException("No engine is configured.");
        }

        if (IsRunning)
        {
            throw new InvalidOperationException("A run is already in progress.");
        }

        var engineRun = new EngineRun(this, engine, opensJobs);
        using var source = new CancellationTokenSource();
        cancellation = source;
        run = engineRun;
        IsRunning = true;
        try
        {
            await engineRun.RunAsync(arguments, workingDirectory, source.Token).ConfigureAwait(true);
        }
        finally
        {
            cancellation = null;
            run = null;
            CanForceStop = false;
            IsStopping = false;
            IsRunning = false;
        }

        return engineRun;
    }

    public void AppendLog(string text, bool warning = false) => AppendLog(new LogLine(text, warning));

    public void AppendLog(LogLine line)
    {
        Log.Add(line);
        if (Log.Count > LogCapacity)
        {
            Log.RemoveAt(0);
        }
    }

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    partial void OnEngineChanged(string? value) => RefreshCommands();

    partial void OnIsRunningChanged(bool value) => RefreshCommands();

    partial void OnIsStoppingChanged(bool value) => RefreshCommands();

    // The skipped properties change on every progress and stage event, and nothing refreshed here
    // reads them.
    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(QueueItem.Progress) or nameof(QueueItem.Detail)
            or nameof(QueueItem.StatusLabel)))
        {
            RefreshQueue();
        }
    }

    private void RefreshQueue()
    {
        OnPropertyChanged(nameof(CheckState));
        OnPropertyChanged(nameof(StartLabel));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        OpenFolderCommand.NotifyCanExecuteChanged();
        LoadFromGameCommand.NotifyCanExecuteChanged();
        BrowseFilesCommand.NotifyCanExecuteChanged();
        RemoveCheckedCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        RetryFailedCommand.NotifyCanExecuteChanged();
        SetKeyCommand.NotifyCanExecuteChanged();
        CopyVideoKeyCommand.NotifyCanExecuteChanged();
        RecoverKeysCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged();
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        CaptureKeysCommand.NotifyCanExecuteChanged();
    }
}

public sealed record Message(
    string Title, string Text, string? Primary = null, string? Secondary = null,
    string? Detail = null, string? Notes = null, string? NotesUrl = null);
