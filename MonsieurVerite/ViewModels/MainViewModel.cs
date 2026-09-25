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
    private readonly Settings settings;
    private readonly HashSet<QueueItem> watched = [];
    private EngineClient? client;
    private CancellationTokenSource? cancellation;
    private bool forceStopped;
    private QueueItem? current;
    private int runIndex;
    private int runTotal;

    public MainViewModel(EngineLaunchProfile? engine, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = settings;
        Engine = engine;

        SourceDirectory = settings.SourceDirectory ?? "";
        OutputDirectory = settings.OutputDirectory
                          ?? Path.Combine(engine?.WorkingDirectory ?? AppContext.BaseDirectory,
                              "output");
        IsLogOpen = true;
        StageText = "Idle";

        Items.CollectionChanged += OnItemsChanged;

        if (settings.LoadError is { } error)
        {
            AppendLog(error);
        }

        if (engine is null)
        {
            AppendLog(NoEngineMessage);
        }
    }

    private string NoEngineMessage =>
        $"No engine at {EnginePath}. Point Settings > Engine at charlotte-cli.exe.";

    public ObservableCollection<QueueItem> Items { get; } = [];

    public ObservableCollection<string> Log { get; } = [];

    public RunOptions Options => settings.Options;

    public Func<string, string?>? PickFolder { get; set; }

    public Func<string, IReadOnlyList<string>?>? PickFiles { get; set; }

    public Func<Settings, bool>? ShowSettings { get; set; }

    public Func<QueueItem, string?>? PromptKey { get; set; }

    public Action<string>? CopyText { get; set; }

    public Func<string, bool>? AnswerQuestion { get; set; }

    public Func<UpdateEvent, bool>? ConfirmUpdate { get; set; }

    public Action? RestartRequested { get; set; }

    public UpdateEvent? LatestUpdate { get; private set; }

    public string RecoveredKeysPath => settings.RecoveredKeysPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEngine), nameof(CanRunEngine))]
    [NotifyCanExecuteChangedFor(
        nameof(StartCommand), nameof(RetryFailedCommand), nameof(RecoverKeysCommand),
        nameof(SetKeyCommand), nameof(CheckForUpdatesCommand))]
    public partial EngineLaunchProfile? Engine { get; private set; }

    public bool HasEngine => Engine is not null;

    /// <summary>Where the engine is expected, whether or not it is there.</summary>
    public string EnginePath => settings.EffectiveEnginePath;

    /// <summary>Null until the engine has run once, because only session_start carries it.</summary>
    public string? EngineVersion { get; private set; }

    [ObservableProperty] public partial string SourceDirectory { get; set; }

    [ObservableProperty] public partial string OutputDirectory { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanRunEngine))]
    [NotifyCanExecuteChangedFor(
        nameof(StartCommand), nameof(CancelCommand), nameof(SkipCommand),
        nameof(RetryFailedCommand), nameof(RecoverKeysCommand), nameof(SetKeyCommand),
        nameof(RemoveCheckedCommand),
        nameof(CheckForUpdatesCommand), nameof(OpenFolderCommand), nameof(BrowseFilesCommand))]
    public partial bool IsRunning { get; internal set; }

    public bool IsIdle => !IsRunning;

    /// <summary>An engine run was asked to cancel, which makes a second press kill it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancelLabel))]
    public partial bool CanForceStop { get; private set; }

    public string CancelLabel => CanForceStop ? "Force stop" : "Cancel";

    public bool CanRunEngine => HasEngine && !IsRunning;

    [ObservableProperty] public partial bool IsLogOpen { get; set; }

    [ObservableProperty] public partial string StageText { get; set; }

    public bool AllChecked => Items.Count > 0 && Items.All(item => item.IsChecked);

    public bool? CheckState => AllChecked ? true : Items.Any(item => item.IsChecked) ? null : false;

    public QueueItem? SingleChecked =>
        Items.Where(item => item.IsChecked).Take(2).ToList() is [var only] ? only : null;

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
            var unsubtitled = Items.Count(item => item.HasSubtitles == false);
            var summary =
                $"{Items.Count} files · {done} done · {missing} missing key · {unsubtitled} without subtitles";
            if (Options.UseVapourSynth)
            {
                summary += $" · {Items.Count(item => !item.HasVsScript)} unfiltered";
            }

            return summary;
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

            var fileName = Path.GetFileName(path);
            if (Find(fileName) is not null)
            {
                AppendLog($"{fileName} is already in the queue.");
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
        var previousPath = EnginePath;
        if (ShowSettings?.Invoke(settings) ?? false)
        {
            SaveSettings();
            OnPropertyChanged(nameof(Options));
            OnPropertyChanged(nameof(Summary));
            ApplyEngineSetting(previousPath);
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
        AppendLog(engine is null ? NoEngineMessage : $"Engine: {engine.FileName}");
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
        foreach (var item in Items.Where(item => item.IsChecked).ToList())
        {
            Items.Remove(item);
        }
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        var output = Items.FirstOrDefault(item => item.IsChecked && File.Exists(item.OutputPath))
            ?.OutputPath;
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
            AppendLog($"Output folder does not exist yet: {OutputDirectory}");
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
            AppendLog($"No keys recovered yet. They will be written to {RecoveredKeysPath}");
        }
    }

    private static void Explore(string arguments) =>
        Process.Start("explorer.exe", arguments)?.Dispose();

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
        if (SingleChecked is { } item && PromptKey?.Invoke(item) is { } key)
        {
            await ConvertAsync([item], ["--key", key]).ConfigureAwait(true);
        }
    }

    private bool CanSetKey() => CanRunEngine && SingleChecked is not null;

    [RelayCommand(CanExecute = nameof(CanCopyVideoKey))]
    private void CopyVideoKey()
    {
        if (SingleChecked?.VideoKey is { } videoKey)
        {
            CopyText?.Invoke(videoKey.ToString(CultureInfo.InvariantCulture));
        }
    }

    private bool CanCopyVideoKey() => SingleChecked?.VideoKey is not null;

    private async Task ConvertAsync(List<QueueItem> targets, IReadOnlyList<string> extraArguments)
    {
        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            AppendLog("Choose an output folder before converting.");
            return;
        }

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
        var targets = Items.Where(IsRecoverable).ToList();
        var leftOut = Items.Count(item => item.IsChecked && item.StreamCipher);
        if (leftOut > 0)
        {
            AppendLog($"Left out {leftOut} 7.1 file(s), whose keys cannot be recovered.");
        }

        Enqueue(targets);
        await RunEngineAsync(["--crack", .. targets.Select(item => item.FullPath)], targets.Count)
            .ConfigureAwait(true);
    }

    private bool CanRecoverKeys() => CanRunEngine && Items.Any(IsRecoverable);

    private static bool IsRecoverable(QueueItem item) => item.IsChecked && !item.StreamCipher;

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
        if (cancellation is not { } source)
        {
            return;
        }

        if (!source.IsCancellationRequested)
        {
            source.Cancel();
            CanForceStop = client is not null;
            StageText = "Cancelling…";
        }
        else if (client is { } engine && !forceStopped)
        {
            forceStopped = true;
            engine.Kill();
            StageText = "Stopping…";
        }
    }

    // The skip names the file so the engine can drop it if that job already finished by the
    // time the command arrives, instead of skipping whichever file started next.
    [RelayCommand(CanExecute = nameof(CanSkip))]
    private void Skip()
    {
        if (current is { Status: ItemStatus.Running } item)
        {
            client?.SendSkip(item.FileName);
            item.Detail = "skipping…";
        }
    }

    private bool CanSkip() => IsRunning && runTotal > 0;

    public void Shutdown() => cancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(CanRunEngine))]
    private Task CheckForUpdatesAsync() => RunUpdateCheckAsync(quiet: false);

    public Task CheckForUpdatesOnStartupAsync() =>
        settings.CheckForUpdatesOnStartup && CanRunEngine
            ? RunUpdateCheckAsync(quiet: true)
            : Task.CompletedTask;

    // Quiet leaves "up to date" and "could not check" to the log, because an offline start would
    // otherwise meet a dialog every time.
    private async Task RunUpdateCheckAsync(bool quiet)
    {
        LatestUpdate = null;
        await RunEngineAsync(["--update"], 0).ConfigureAwait(true);
        if (LatestUpdate is not { } update)
        {
            AppendLog("The engine did not report an update result.");
            return;
        }

        if (quiet && !update.Available)
        {
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
        var installed = false;
        await RunExclusiveAsync(async token =>
        {
            try
            {
                var status = new Progress<string>(text => StageText = text);
                var written = await Updater
                    .InstallLatestAsync(AppContext.BaseDirectory, status, token)
                    .ConfigureAwait(true);
                AppendLog($"Installed {written.Count} file(s). Restarting.");
                installed = true;
            }
            catch (OperationCanceledException)
            {
                AppendLog(token.IsCancellationRequested ? "Update cancelled." : "Update timed out.");
            }
            catch (Exception e) when (e is HttpRequestException or IOException
                                          or InvalidDataException or UnauthorizedAccessException
                                          or JsonException)
            {
                AppendLog($"Update failed: {e.Message}");
            }
        }).ConfigureAwait(true);
        return installed;
    }

    /// <summary>The one owner of <see cref="IsRunning"/>, the cancellation source and Cancel.</summary>
    private async Task RunExclusiveAsync(Func<CancellationToken, Task> work)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("A run is already in progress.");
        }

        using var source = new CancellationTokenSource();
        cancellation = source;
        forceStopped = false;
        IsRunning = true;
        try
        {
            await work(source.Token).ConfigureAwait(true);
        }
        finally
        {
            cancellation = null;
            CanForceStop = false;
            IsRunning = false;
            StageText = "Idle";
        }
    }

    // jobCount is how many job_start events the run will open, which a probe and an update check
    // never do. Zero hides the run position and disables Skip.
    private Task RunEngineAsync(IReadOnlyList<string> arguments, int jobCount)
    {
        if (Engine is not { } profile)
        {
            throw new InvalidOperationException("No engine is configured.");
        }

        return RunExclusiveAsync(token => DriveEngineAsync(profile, arguments, jobCount, token));
    }

    private async Task DriveEngineAsync(
        EngineLaunchProfile profile, IReadOnlyList<string> arguments, int jobCount,
        CancellationToken cancellationToken)
    {
        using var engine = new EngineClient(profile);
        using var registration = cancellationToken.Register(engine.SendCancel);
        client = engine;
        current = null;
        runIndex = 0;
        runTotal = jobCount;

        string? failure = null;
        try
        {
            var exit = engine.Start(["--json", .. arguments]);
            // The token only asks the engine to stop, and its events are read until it has.
            await foreach (var evt in engine.Events.ReadAllAsync(CancellationToken.None)
                               .ConfigureAwait(true))
            {
                Apply(evt);
            }

            var exitCode = await exit.ConfigureAwait(true);
            if (forceStopped)
            {
                AppendLog("Engine stopped.");
            }
            else if (exitCode != 0)
            {
                failure = $"engine exited with code {exitCode}";
                AppendLog($"Engine exited with code {exitCode}.");

                // The engine exits 1 after a batch in which any file failed, and those rows
                // already carry the error events. The stderr tail is for a run that never opened
                // a job or died inside one.
                if (current is null or { Status: ItemStatus.Running })
                {
                    foreach (var line in engine.StandardErrorTail.Where(line => line.Length > 0))
                    {
                        AppendLog($"  {line}");
                    }
                }
            }
        }
        catch (Win32Exception e)
        {
            failure = "engine could not be started";
            AppendLog($"Could not start the engine '{profile.FileName}': {e.Message}");
        }
        catch (Exception)
        {
            // A bug in Apply ends the run, and disposing the client kills the engine. The
            // unhandled-error dialog shows the exception.
            failure = "stopped by an unexpected error";
            throw;
        }
        finally
        {
            client = null;
            current = null;
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
                    item.Status = forceStopped ? ItemStatus.Cancelled : ItemStatus.Pending;
                    break;
                case ItemStatus.Running when forceStopped:
                    item.Status = ItemStatus.Cancelled;
                    item.Detail = "";
                    break;
                case ItemStatus.Running:
                    item.Status = failure is null ? ItemStatus.Pending : ItemStatus.Error;
                    item.Detail = failure ?? "";
                    break;
            }
        }
    }

    internal void Apply(EngineEvent evt)
    {
        switch (evt)
        {
            case SessionStartEvent session:
                EngineVersion = session.Version;
                if (session.Protocol != EngineEvent.ProtocolVersion)
                {
                    AppendLog(
                        $"Engine speaks protocol {session.Protocol}; this GUI expects {EngineEvent.ProtocolVersion}.");
                }

                break;

            case LogEvent log:
                AppendLog($"[{log.Level}] {log.Message}");
                break;

            case JobStartEvent job:
                // --crack never closes a job, and the next job_start is the only sign that the
                // previous file is done.
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

                AppendLog(error.File.Length > 0 ? $"{error.File}: {error.Message}" : error.Message);
                break;

            case JobSkippedEvent skipped when Find(skipped.File) is { } skippedItem:
                skippedItem.Status = ItemStatus.Skipped;
                skippedItem.Detail = skipped.Reason switch
                {
                    "exists" => "already exists",
                    "no_key" => "no key",
                    "requested" => "skipped on request",
                    _ => skipped.Reason,
                };
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
                probed.Subtitles = probe.Subtitles;
                probed.HasVsScript = probe.VsScript is not null;
                probed.StreamCipher = probe.StreamCipher;
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
                    // A key the probe found in keys.json stays, because a decline says nothing
                    // about keys.json.
                    if (cracked.Key == KeyState.Unknown)
                    {
                        cracked.Key = KeyState.Missing;
                    }

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
                var answer = AnswerQuestion?.Invoke(question.Prompt) ?? question.Default;
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
            if (RecoveredKeys.Add(RecoveredKeysPath, stem, videoKey) is { } setAside)
            {
                AppendLog($"{RecoveredKeysPath} could not be read. It is now {setAside}, and a new file was started.");
            }
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

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Clear raises Reset and names no OldItems, which is why the rows are tracked here too.
        var removed = e.Action == NotifyCollectionChangedAction.Reset
            ? watched.ToList()
            : e.OldItems?.Cast<QueueItem>() ?? [];
        foreach (var item in removed)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
            watched.Remove(item);
        }

        foreach (var item in e.NewItems?.Cast<QueueItem>() ?? [])
        {
            item.PropertyChanged += OnItemPropertyChanged;
            watched.Add(item);
        }

        OnCheckedChanged();
        OnPropertyChanged(nameof(Summary));
        StartCommand.NotifyCanExecuteChanged();
        RetryFailedCommand.NotifyCanExecuteChanged();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(QueueItem.IsChecked):
                OnCheckedChanged();
                break;
            case nameof(QueueItem.VideoKey):
                CopyVideoKeyCommand.NotifyCanExecuteChanged();
                break;
            case nameof(QueueItem.Status) or nameof(QueueItem.Key)
                or nameof(QueueItem.Subtitles) or nameof(QueueItem.HasVsScript):
                OnPropertyChanged(nameof(Summary));
                RetryFailedCommand.NotifyCanExecuteChanged();
                break;
        }
    }

    private void OnCheckedChanged()
    {
        OnPropertyChanged(nameof(AllChecked));
        OnPropertyChanged(nameof(CheckState));
        OnPropertyChanged(nameof(StartLabel));
        OnPropertyChanged(nameof(SingleChecked));
        RecoverKeysCommand.NotifyCanExecuteChanged();
        SetKeyCommand.NotifyCanExecuteChanged();
        CopyVideoKeyCommand.NotifyCanExecuteChanged();
    }
}
