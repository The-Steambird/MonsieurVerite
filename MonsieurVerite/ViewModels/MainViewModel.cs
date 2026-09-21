using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    private const int StderrTailCapacity = 20;
    private readonly Settings settings;
    private readonly SynchronizationContext? uiContext;
    private EngineClient? client;
    private CancellationTokenSource? cancellation;
    private QueueItem? current;
    private int runIndex;
    private int runTotal;

    public MainViewModel(EngineLaunchProfile? engine, Settings settings,
        SynchronizationContext? uiContext)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = settings;
        this.uiContext = uiContext;
        Engine = engine;

        SourceDirectory = settings.SourceDirectory ?? "";
        OutputDirectory = settings.OutputDirectory
                          ?? Path.Combine(engine?.WorkingDirectory ?? AppContext.BaseDirectory,
                              "output");
        IsLogOpen = true;
        StageText = "Idle";
        RecoveredKeysPath = Settings.RecoveredKeysPath;

        Items.CollectionChanged += OnItemsChanged;

        if (engine is null)
        {
            AppendLog("No engine found. charlotte-cli.exe must sit next to charlotte-gui.exe.");
        }
    }

    public ObservableCollection<QueueItem> Items { get; } = [];

    public ObservableCollection<string> Log { get; } = [];

    public RunOptions Options => settings.Options;

    public Func<string, string?>? PickFolder { get; set; }

    public Func<string, IReadOnlyList<string>?>? PickFiles { get; set; }

    public Func<RunOptions, bool>? EditOptions { get; set; }

    public Func<string, bool>? ConfirmKeyOverwrite { get; set; }

    public Func<UpdateEvent, bool>? ConfirmUpdate { get; set; }

    public Action? RestartRequested { get; set; }

    public UpdateEvent? LatestUpdate { get; private set; }

    public string RecoveredKeysPath { get; set; }

    public EngineLaunchProfile? Engine { get; }

    public bool HasEngine => Engine is not null;

    [ObservableProperty] public partial string SourceDirectory { get; set; }

    [ObservableProperty] public partial string OutputDirectory { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanRunEngine), nameof(CanSetKey))]
    [NotifyCanExecuteChangedFor(
        nameof(StartCommand), nameof(CancelCommand), nameof(RecoverKeysCommand),
        nameof(RemoveCheckedCommand), nameof(CheckForUpdatesCommand),
        nameof(OpenFolderCommand), nameof(BrowseFilesCommand))]
    public partial bool IsRunning { get; set; }

    public bool IsIdle => !IsRunning;

    public bool CanRunEngine => HasEngine && !IsRunning;

    [ObservableProperty] public partial bool IsLogOpen { get; set; }

    [ObservableProperty] public partial string StageText { get; set; }

    public bool AllChecked => Items.Count > 0 && Items.All(item => item.IsChecked);

    public bool? CheckState => AllChecked ? true : Items.Any(item => item.IsChecked) ? null : false;

    public QueueItem? SingleChecked =>
        Items.Where(item => item.IsChecked).Take(2).ToList() is [var only] ? only : null;

    public bool CanSetKey => CanRunEngine && SingleChecked is not null;

    public bool CanCopyVideoKey => SingleChecked?.VideoKey is not null;

    public string StartLabel
    {
        get
        {
            var checkedCount = Items.Count(item => item.IsChecked);
            return checkedCount > 0 && checkedCount < Items.Count
                ? $"Start ({checkedCount} checked)"
                : "Start";
        }
    }

    public string Summary
    {
        get
        {
            var done = Items.Count(item => item.Status == ItemStatus.Done);
            var missing = Items.Count(item => item.Key == KeyState.Missing);
            return $"{Items.Count} files · {done} done · {missing} needing key recovery";
        }
    }

    public async Task LoadSourceAsync(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (IsRunning)
        {
            AppendLog("Wait for the current run to finish before changing the source folder.");
            return;
        }

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
        ArgumentNullException.ThrowIfNull(paths);
        if (IsRunning)
        {
            AppendLog("Wait for the current run to finish before adding files.");
            return;
        }

        var added = new List<QueueItem>();
        foreach (var path in paths.SelectMany(Expand))
        {
            if (!string.Equals(Path.GetExtension(path), ".usm", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var item = new QueueItem(path);
            if (Find(item.FileName) is not null)
            {
                AppendLog($"{item.FileName} is already in the queue.");
                continue;
            }

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
            await RunEngineAsync(["--probe", .. added.Select(item => item.FullPath)], added.Count)
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
            AppendLog($"Could not read {directory}: {e.Message}");
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
        if (EditOptions?.Invoke(Options) ?? false)
        {
            SaveSettings();
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
            AppendLog($"Could not save settings: {e.Message}");
        }
    }

    [RelayCommand]
    private void ToggleAll()
    {
        var target = !AllChecked;
        foreach (var item in Items)
        {
            item.IsChecked = target;
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var item in Items)
        {
            item.IsChecked = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void RemoveChecked()
    {
        foreach (var item in Items.Where(item => item.IsChecked).ToList())
        {
            Items.Remove(item);
        }
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        var item = Items.FirstOrDefault(item => item.IsChecked && item.OutputPath is not null);
        if (item is not null)
        {
            Process.Start("explorer.exe", $"/select,\"{item.OutputPath}\"");
        }
        else if (Directory.Exists(OutputDirectory))
        {
            Process.Start("explorer.exe", $"\"{OutputDirectory}\"");
        }
        else
        {
            AppendLog($"Output folder does not exist yet: {OutputDirectory}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var targets = Items.Where(item => item.IsChecked).ToList();
        if (targets.Count == 0)
        {
            targets = [.. Items];
        }

        await ConvertAsync(targets, []).ConfigureAwait(true);
    }

    private bool CanStart() => CanRunEngine && Items.Count > 0;

    public Task ConvertWithKeyAsync(QueueItem item, ulong videoKey)
    {
        ArgumentNullException.ThrowIfNull(item);
        return ConvertAsync([item], ["--key", videoKey.ToString(CultureInfo.InvariantCulture)]);
    }

    private async Task ConvertAsync(List<QueueItem> targets, IReadOnlyList<string> extraArguments)
    {
        Enqueue(targets);
        foreach (var item in targets)
        {
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
        var targets = Items.Where(item => item.IsChecked).ToList();
        Enqueue(targets);
        await RunEngineAsync(["--crack", .. targets.Select(item => item.FullPath)], targets.Count)
            .ConfigureAwait(true);
    }

    private bool CanRecoverKeys() => CanRunEngine && Items.Any(item => item.IsChecked);

    private static void Enqueue(IEnumerable<QueueItem> targets)
    {
        foreach (var item in targets)
        {
            item.Status = ItemStatus.Queued;
            item.Progress = 0;
            item.Detail = "";
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel()
    {
        if (cancellation is { IsCancellationRequested: false } source)
        {
            source.Cancel();
            StageText = "Cancelling…";
        }
    }

    public void Shutdown() => cancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(CanRunEngine))]
    private async Task CheckForUpdatesAsync()
    {
        LatestUpdate = null;
        await RunEngineAsync(["--update"], 0).ConfigureAwait(true);
        if (LatestUpdate is not { } update)
        {
            AppendLog("The engine did not report an update result.");
            return;
        }

        var install = ConfirmUpdate?.Invoke(update) ?? false;
        if (install && update.Available && await InstallUpdateAsync().ConfigureAwait(true))
        {
            RestartRequested?.Invoke();
        }
    }

    private async Task<bool> InstallUpdateAsync()
    {
        using var source = new CancellationTokenSource();
        cancellation = source;
        IsRunning = true;
        try
        {
            var status = new Progress<string>(text => StageText = text);
            var written = await Updater
                .InstallLatestAsync(AppContext.BaseDirectory, status, source.Token)
                .ConfigureAwait(true);
            AppendLog($"Installed {written.Count} file(s). Restarting.");
            return true;
        }
        catch (OperationCanceledException)
        {
            AppendLog(source.IsCancellationRequested ? "Update cancelled." : "Update timed out.");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException
                                      or UnauthorizedAccessException or JsonException)
        {
            AppendLog($"Update failed: {e.Message}");
        }
        finally
        {
            cancellation = null;
            IsRunning = false;
            StageText = "Idle";
        }

        return false;
    }

    private async Task RunEngineAsync(IReadOnlyList<string> arguments, int fileCount)
    {
        if (Engine is not { } profile)
        {
            throw new InvalidOperationException("No engine is configured.");
        }

        if (IsRunning)
        {
            throw new InvalidOperationException("An engine run is already in progress.");
        }

        using var source = new CancellationTokenSource();
        using var engine = new EngineClient(profile);
        engine.EventReceived += evt => OnUiThread(() => Apply(evt));

        // Anything the engine says outside the protocol (a usage error, a Python traceback) lands
        // on stderr. Only its tail is worth showing, and only when the run failed, because a
        // healthy run's stderr is the console logger repeating the log events.
        var stderrTail = new Queue<string>();
        engine.StandardErrorReceived += line =>
        {
            Debug.WriteLine(line);
            stderrTail.Enqueue(line);
            if (stderrTail.Count > StderrTailCapacity)
            {
                stderrTail.Dequeue();
            }
        };

        cancellation = source;
        client = engine;
        current = null;
        runIndex = 0;
        runTotal = fileCount;
        IsRunning = true;

        string? failure = null;
        try
        {
            var exitCode = await engine.RunAsync(["--json", .. arguments], source.Token)
                .ConfigureAwait(true);
            if (exitCode != 0)
            {
                failure = $"engine exited with code {exitCode}";
                AppendLog($"Engine exited with code {exitCode}.");
                foreach (var line in stderrTail.Where(line => line.Length > 0))
                {
                    AppendLog($"  {line}");
                }
            }
        }
        catch (Win32Exception e)
        {
            failure = "engine could not be started";
            AppendLog($"Could not start the engine '{profile.FileName}': {e.Message}");
        }
        finally
        {
            cancellation = null;
            client = null;
            current = null;
            IsRunning = false;
            StageText = "Idle";
            SettleRows(failure);
        }
    }

    private void SettleRows(string? failure)
    {
        foreach (var item in Items)
        {
            switch (item.Status)
            {
                case ItemStatus.Queued:
                    item.Status = ItemStatus.Pending;
                    break;
                case ItemStatus.Running:
                    item.Status = failure is null ? ItemStatus.Pending : ItemStatus.Error;
                    item.Detail = failure ?? "";
                    break;
            }
        }
    }

    public void Apply(EngineEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        switch (evt)
        {
            case SessionStartEvent session when session.Protocol != EngineEvent.ProtocolVersion:
                AppendLog(
                    $"Engine speaks protocol {session.Protocol}; this GUI expects {EngineEvent.ProtocolVersion}.");
                break;

            case LogEvent log:
                AppendLog($"[{log.Level}] {log.Message}");
                break;

            case JobStartEvent job:
                // --crack never closes a job. Outcome is the Key column, and the next
                // job_start is the only signal that the previous file is done.
                if (current is { Status: ItemStatus.Running } previous)
                {
                    previous.Status = ItemStatus.Pending;
                }

                runIndex++;
                current = Find(job.File);
                if (current is not null)
                {
                    current.Status = ItemStatus.Running;
                    current.Progress = 0;
                }

                break;

            case StageEvent stage when stage.Status == "start":
                ShowStage(Describe(stage.Stage, null));
                if (current is not null)
                {
                    current.Detail = stage.Stage;
                    current.Progress = 0;
                }

                break;

            case ProgressEvent progress when progress.Total > 0:
                var percent = progress.Current * 100.0 / progress.Total;
                ShowStage(Describe(progress.Stage, (int)percent));
                if (current is not null)
                {
                    current.Progress = percent;
                }

                break;

            case ResultEvent result when Find(result.File) is { } finished:
                finished.Status = ItemStatus.Done;
                finished.Progress = 100;
                finished.Detail = "";
                finished.OutputPath = result.Output;
                break;

            case ErrorEvent error:
                if (Find(error.File) is { } failed)
                {
                    failed.Status = ItemStatus.Error;
                    failed.Detail = error.Message;
                }

                AppendLog($"{error.File}: {error.Message}");
                break;

            case JobSkippedEvent skipped when Find(skipped.File) is { } skippedItem:
                skippedItem.Status = ItemStatus.Skipped;
                skippedItem.Detail = skipped.Reason == "exists" ? "already exists" : "no key";
                break;

            case CancelledEvent cancelled:
                if (Find(cancelled.File) is { } cancelledItem)
                {
                    cancelledItem.Status = ItemStatus.Cancelled;
                    cancelledItem.Detail = "";
                }

                foreach (var item in Items.Where(item => item.Status == ItemStatus.Queued))
                {
                    item.Status = ItemStatus.Cancelled;
                }

                break;

            case ProbeEvent probe when Find(probe.File) is { } probed:
                probed.Key = probe.Key ? KeyState.Present : KeyState.Missing;
                probed.Version = probe.Version;
                probed.HasSubtitles = probe.Subtitles.Count > 0;
                probed.HasVsScript = probe.VsScript is not null;
                break;

            case CrackEvent crack when Find(crack.File) is { } cracked:
                if (crack.VideoKey is { } videoKey)
                {
                    cracked.Key = KeyState.Recovered;
                    cracked.VideoKey = videoKey;
                    AppendLog($"{crack.File}: videoKey={videoKey}");
                    RecordRecoveredKey(crack.Stem, videoKey);
                }
                else
                {
                    cracked.Key = KeyState.Missing;
                    AppendLog($"{crack.File}: key not recoverable — {crack.Reason}");
                }

                break;

            case CrackSummaryEvent summary:
                AppendLog(
                    $"Recovered {summary.Recovered} key(s), {summary.Unrecovered} not recoverable.");
                break;

            case UpdateEvent update:
                AppendLog(update.Available
                    ? $"charlotte {update.Latest} is available (running {update.Current})."
                    : update.Reason is { Length: > 0 } reason
                        ? $"Update check failed: {reason}"
                        : $"charlotte {update.Current} is up to date.");
                LatestUpdate = update;
                break;

            case QuestionEvent question:
                var answer = ConfirmKeyOverwrite?.Invoke(question.Prompt) ?? question.Default;
                client?.SendAnswer(question.Id, answer);
                break;

            case UnknownEvent unknown when unknown.Type.Length > 0:
                AppendLog($"Engine sent an event kind this GUI does not know: {unknown.Type}");
                break;
        }
    }

    private void ShowStage(string text)
    {
        if (cancellation is not { IsCancellationRequested: true })
        {
            StageText = text;
        }
    }

    private QueueItem? Find(string fileName) =>
        Items.FirstOrDefault(item => item.FileName == fileName);

    private void RecordRecoveredKey(string stem, ulong videoKey)
    {
        try
        {
            RecoveredKeys.Add(RecoveredKeysPath, stem, videoKey);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppendLog($"Could not write {RecoveredKeysPath}: {e.Message}");
        }
    }

    private string Describe(string stage, int? percent)
    {
        var text = percent is { } value ? $"{stage} · {value}%" : stage;
        return current is null || runTotal == 0 ? text : $"{runIndex}/{runTotal} · {text}";
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

    private void OnUiThread(Action action)
    {
        if (uiContext is null)
        {
            action();
        }
        else
        {
            uiContext.Post(_ => action(), null);
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (QueueItem item in e.OldItems)
            {
                item.PropertyChanged -= OnItemPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (QueueItem item in e.NewItems)
            {
                item.PropertyChanged += OnItemPropertyChanged;
            }
        }

        OnCheckedChanged();
        OnPropertyChanged(nameof(Summary));
        StartCommand.NotifyCanExecuteChanged();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not QueueItem item || !Items.Contains(item))
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(QueueItem.IsChecked):
                OnCheckedChanged();
                break;
            case nameof(QueueItem.VideoKey):
                OnPropertyChanged(nameof(CanCopyVideoKey));
                break;
            case nameof(QueueItem.Status) or nameof(QueueItem.Key):
                OnPropertyChanged(nameof(Summary));
                break;
        }
    }

    private void OnCheckedChanged()
    {
        OnPropertyChanged(nameof(AllChecked));
        OnPropertyChanged(nameof(CheckState));
        OnPropertyChanged(nameof(StartLabel));
        OnPropertyChanged(nameof(SingleChecked));
        OnPropertyChanged(nameof(CanSetKey));
        OnPropertyChanged(nameof(CanCopyVideoKey));
        RecoverKeysCommand.NotifyCanExecuteChanged();
    }
}
