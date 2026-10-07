using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MonsieurVerite.Engine;

namespace MonsieurVerite.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const int LogCapacity = 2000;
    private readonly Settings settings;
    private CancellationTokenSource? cancellation;
    private EngineRun? run;

    public MainViewModel(EngineLaunchProfile? engine, Settings settings)
    {
        this.settings = settings;
        Engine = engine;

        SourceDirectory = settings.SourceDirectory ?? "";
        OutputDirectory = settings.OutputDirectory
                          ?? Path.Combine(engine?.WorkingDirectory ?? AppContext.BaseDirectory,
                              "output");
        IsLogOpen = true;
        StageText = Strings.IDLE;

        Items.CollectionChanged += (_, _) => RefreshQueue();
        Items.RowChanged += OnRowChanged;

        if (settings.LoadError is { } error)
        {
            AppendLog(error);
        }

        if (engine is null)
        {
            AppendLog(NoEngineMessage);
        }
    }

    private string NoEngineMessage => Strings.NO_ENGINE_LOG(EnginePath);

    public QueueItems Items { get; } = [];

    public ObservableCollection<string> Log { get; } = [];

    public RunOptions Options => settings.Options;

    public Func<string, string?>? PickFolder { get; set; }

    public Func<string, IReadOnlyList<string>?>? PickFiles { get; set; }

    public Func<Settings, bool>? ShowSettings { get; set; }

    public Func<QueueItem, string?>? PromptKey { get; set; }

    public Action<string>? CopyText { get; set; }

    /// <summary>Shows a message and reports whether its primary button was pressed.</summary>
    public Func<Message, bool>? ShowMessage { get; set; }

    public Action? CloseWindow { get; set; }

    public string RecoveredKeysPath => settings.RecoveredKeysPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEngine), nameof(CanRunEngine))]
    public partial EngineLaunchProfile? Engine { get; private set; }

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

    /// <summary>An engine run was asked to cancel, which makes a second press kill it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancelLabel))]
    public partial bool CanForceStop { get; private set; }

    public string CancelLabel => CanForceStop ? Strings.FORCE_STOP : Strings.CANCEL;

    public bool CanRunEngine => HasEngine && !IsRunning;

    [ObservableProperty] public partial bool IsLogOpen { get; set; }

    [ObservableProperty] public partial string StageText { get; set; }

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

    public string Summary
    {
        get
        {
            var done = Items.Count(item => item.Status == ItemStatus.Done);
            var missing = Items.Count(item => item.Key == KeyState.Missing);
            var unsubtitled = Items.Count(item => item.HasSubtitles == false);
            var summary = Strings.SUMMARY(Items.Count, done, missing, unsubtitled);
            if (Options.UseVapourSynth)
            {
                summary += " · " + Strings.SUMMARY_UNFILTERED(
                    Items.Count(item => !item.HasVsScript));
            }

            return summary;
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
            await RunEngineAsync(["--probe", .. added.Select(item => item.FullPath)], 0)
                .ConfigureAwait(true);
        }
    }

    private IEnumerable<string> Expand(string path) =>
        Directory.Exists(path) ? ListCutscenes(path) ?? [] : [path];

    private List<string>? ListCutscenes(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.usm").Order().ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppendLog(Strings.FOLDER_UNREADABLE_LOG(directory, e.Message));
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
            OnPropertyChanged(nameof(Summary));
            ApplyEngineSetting(previousPath);
            OfferRestartForLanguage(previousLanguage);
        }
    }

    // A window reads its strings once, which is why a new language needs a restart. Switching
    // them first puts the question in the language the user just picked.
    internal void OfferRestartForLanguage(string previousLanguage)
    {
        if (settings.Language == previousLanguage)
        {
            return;
        }

        Strings.Use(settings.Language, AppContext.BaseDirectory);

        if (IsRunning)
        {
            AppendLog(Strings.LANGUAGE_CHANGED_LOG);
            return;
        }

        var restart = ShowMessage?.Invoke(new Message(Strings.RESTART_TITLE,
            Strings.RESTART_MESSAGE, Strings.RESTART_NOW, Strings.LATER)) ?? false;
        if (restart)
        {
            Updater.Relaunch();
            CloseWindow?.Invoke();
        }
    }

    // Resolved again even when the path is unchanged, because the file may have appeared there
    // since the last look.
    private void ApplyEngineSetting(string previousPath)
    {
        var engine = settings.ResolveEngine();
        if (engine?.FileName == Engine?.FileName && EnginePath == previousPath)
        {
            return;
        }

        Engine = engine;
        EngineVersion = null;
        AppendLog(engine is null ? NoEngineMessage : Strings.ENGINE_LOG(engine.FileName));
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
            AppendLog(Strings.SETTINGS_UNSAVED_LOG(e.Message));
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

        await RunEngineAsync(
            [
                "--output", OutputDirectory, .. Options.ToArguments(), .. extraArguments,
                .. targets.Select(item => item.FullPath),
            ],
            targets.Count).ConfigureAwait(true);
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

        await RunEngineAsync(["--crack", .. targets.Select(item => item.FullPath)], targets.Count)
            .ConfigureAwait(true);
    }

    private bool CanRecoverKeys() => CanRunEngine && Items.Any(IsRecoverable);

    private static bool IsRecoverable(QueueItem item) => item.IsChecked && !item.StreamCipher;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel()
    {
        if (cancellation is not { } source)
        {
            return;
        }

        if (!source.IsCancellationRequested)
        {
            source.Cancel();
            CanForceStop = run is not null;
            StageText = Strings.CANCELLING;
        }
        else if (run?.ForceStop() == true)
        {
            StageText = Strings.STOPPING;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSkip))]
    private void Skip() => run?.Skip();

    private bool CanSkip() => run is { OpensJobs: true };

    public void Shutdown() => cancellation?.Cancel();

    // The folder loads before the first frame, which keeps the empty queue from flashing, and a
    // dialog opened before the window is on screen would appear alone on the desktop.
    public async Task StartupAsync(Task windowShown)
    {
        if (Directory.Exists(SourceDirectory))
        {
            await LoadSourceAsync(SourceDirectory).ConfigureAwait(true);
        }

        await windowShown.ConfigureAwait(true);
        if (Engine is null)
        {
            ShowMessage?.Invoke(new Message(Strings.ENGINE_MISSING_TITLE,
                Strings.ENGINE_MISSING_MESSAGE, Detail: Strings.ENGINE_MISSING_DETAIL(EnginePath)));
        }

        var translation = RefreshTranslationOnStartupAsync();
        await CheckForUpdatesOnStartupAsync().ConfigureAwait(true);
        await translation.ConfigureAwait(true);
    }

    private async Task RefreshTranslationOnStartupAsync()
    {
        if (!settings.CheckForUpdatesOnStartup)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            if (await Translations.RefreshAsync(AppContext.BaseDirectory,
                    CultureInfo.GetCultureInfo(Strings.Language), timeout.Token).ConfigureAwait(true))
            {
                AppendLog(Strings.TRANSLATION_UPDATED_LOG);
            }
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException
                                      or IOException or JsonException
                                      or UnauthorizedAccessException)
        {
            Debug.WriteLine(e);
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
        var check = await RunEngineAsync(["--update"], 0).ConfigureAwait(true);
        if (check.Update is not { } update)
        {
            AppendLog(Strings.NO_UPDATE_RESULT_LOG);
            return;
        }

        if (!update.Available && update.Reason is { Length: > 0 } reason)
        {
            AppendLog(Strings.UPDATE_CHECK_FAILED_LOG(reason));
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
                ShowMessage?.Invoke(new Message(Strings.UP_TO_DATE_TITLE,
                    Strings.UP_TO_DATE_MESSAGE(update.Current)));
            }

            return;
        }

        AppendLog(Strings.UPDATE_AVAILABLE_LOG(update.Latest, update.Current));
        var notes = update.Notes is { Length: > 1200 } text ? text[..1200] + "…" : update.Notes;
        var accepted = ShowMessage?.Invoke(new Message(
            Strings.UPDATE_AVAILABLE_TITLE(update.Latest),
            Strings.UPDATE_AVAILABLE_MESSAGE(update.Current, update.Latest),
            Strings.UPDATE, Strings.CANCEL, notes,
                   ImageUri: "pack://application:,,,/Assets/logo.png")) ?? false;
        if (accepted && await InstallUpdateAsync().ConfigureAwait(true))
        {
            Updater.Relaunch();
            CloseWindow?.Invoke();
        }
    }

    private async Task<bool> InstallUpdateAsync()
    {
        var installed = false;
        await RunExclusiveAsync(null, async token =>
        {
            try
            {
                var status = new Progress<string>(text => StageText = text);
                var written = await Updater
                    .InstallLatestAsync(AppContext.BaseDirectory, status, token)
                    .ConfigureAwait(true);
                AppendLog(Strings.UPDATE_INSTALLED_LOG(written.Count));
                installed = true;
            }
            catch (OperationCanceledException)
            {
                AppendLog(token.IsCancellationRequested
                    ? Strings.UPDATE_CANCELLED_LOG
                    : Strings.UPDATE_TIMED_OUT_LOG);
            }
            catch (Exception e) when (e is HttpRequestException or IOException
                                          or InvalidDataException or UnauthorizedAccessException
                                          or JsonException)
            {
                AppendLog(Strings.UPDATE_FAILED_LOG(e.Message));
            }
        }).ConfigureAwait(true);
        return installed;
    }

    private async Task<EngineRun> RunEngineAsync(IReadOnlyList<string> arguments, int jobCount)
    {
        if (Engine is not { } profile)
        {
            throw new InvalidOperationException("No engine is configured.");
        }

        var engineRun = new EngineRun(this, profile, jobCount);
        await RunExclusiveAsync(engineRun, token => engineRun.RunAsync(arguments, token))
            .ConfigureAwait(true);
        return engineRun;
    }

    /// <summary>
    /// This is the one owner of <see cref="IsRunning"/>, the cancellation source and the current
    /// run. An update install passes null because it has no engine run.
    /// </summary>
    private async Task RunExclusiveAsync(EngineRun? engineRun, Func<CancellationToken, Task> work)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("A run is already in progress.");
        }

        using var source = new CancellationTokenSource();
        cancellation = source;
        run = engineRun;
        IsRunning = true;
        try
        {
            await work(source.Token).ConfigureAwait(true);
        }
        finally
        {
            cancellation = null;
            run = null;
            CanForceStop = false;
            IsRunning = false;
            StageText = Strings.IDLE;
        }
    }

    public void AppendLog(string line)
    {
        Log.Add(line);
        if (Log.Count > LogCapacity)
        {
            Log.RemoveAt(0);
        }
    }

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    partial void OnEngineChanged(EngineLaunchProfile? value) => RefreshCommands();

    partial void OnIsRunningChanged(bool value) => RefreshCommands();

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
        OnPropertyChanged(nameof(Summary));
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
    }
}

public sealed record Message(
    string Title, string Text, string? Primary = null, string? Secondary = null,
    string? Detail = null, string? ImageUri = null);
