using System.IO;

using MonsieurVerite.Engine;
using MonsieurVerite.ViewModels;

using Xunit.Abstractions;

namespace MonsieurVerite.Tests;

/// <summary>
/// Every view model here gets no SynchronizationContext, which applies engine events on the
/// thread that read them and lets each assertion follow its call directly. xUnit installs a
/// context of its own on the test thread, and capturing that one would post the events to the
/// thread pool and race the assertions.
/// </summary>
public class MainViewModelTests(ITestOutputHelper output) : IDisposable
{
    private readonly ScratchFolder scratch = new();

    public void Dispose()
    {
        scratch.Dispose();
        GC.SuppressFinalize(this);
    }

    // The engine path puts recovered_keys.json in the scratch folder, beside where the engine
    // would be.
    private MainViewModel NewViewModel(EngineLaunchProfile? engine) =>
        new(engine, new Settings { EnginePath = scratch.File("charlotte-cli.exe") }, uiContext: null);

    private MainViewModel NewViewModel() => NewViewModel(EngineLaunchProfile.Packaged(scratch.File("charlotte-cli.exe")));

    private MainViewModel NewEnginelessViewModel() => NewViewModel(null);

    private QueueItem Add(MainViewModel viewModel, string name)
    {
        var item = new QueueItem(scratch.File(Path.Combine("usm", name)));
        viewModel.Items.Add(item);
        return item;
    }

    private EngineLaunchProfile FakeEngine(params string[] lines)
    {
        var script = scratch.File(Path.GetRandomFileName() + ".cmd");
        File.WriteAllLines(script, lines);
        return new EngineLaunchProfile
        {
            FileName = "cmd.exe",
            BaseArguments = ["/c", script],
            WorkingDirectory = scratch.Root,
        };
    }

    [Fact]
    public void ProbeFillsKeyAndSubtitleAvailability()
    {
        var viewModel = NewViewModel();
        var keyed = Add(viewModel, "a.usm");
        var keyless = Add(viewModel, "b.usm");

        viewModel.Apply(new ProbeEvent { File = "a.usm", Key = true, Version = "5.3", Subtitles = ["EN", "JP"], VsScript = "vs/a.py" });
        viewModel.Apply(new ProbeEvent { File = "b.usm", Key = false, Subtitles = [], VsScript = null });

        Assert.Equal(KeyState.Present, keyed.Key);
        Assert.Equal("5.3", keyed.Version);
        Assert.True(keyed.HasSubtitles);
        Assert.Equal("EN, JP", keyed.SubtitleLanguages);
        Assert.True(keyed.HasVsScript);

        Assert.Equal(KeyState.Missing, keyless.Key);
        Assert.Null(keyless.Version);
        Assert.False(keyless.HasSubtitles);
        Assert.False(keyless.HasVsScript);
    }

    [Fact]
    public void ARunDrivesTheRowFromPendingToDone()
    {
        var viewModel = NewViewModel();
        var item = Add(viewModel, "a.usm");
        Assert.Equal(ItemStatus.Pending, item.Status);

        viewModel.Apply(new JobStartEvent { File = "a.usm" });
        Assert.Equal(ItemStatus.Running, item.Status);

        viewModel.Apply(new StageEvent { Stage = "demux", Status = "start" });
        Assert.Equal("demux", item.Detail);

        viewModel.Apply(new ProgressEvent { Stage = "demux", Current = 5, Total = 10 });
        Assert.Equal(50, item.Progress);
        // No run position in the text, because only a real run sets runTotal.
        Assert.Equal("demux · 50%", viewModel.StageText);

        var output = scratch.File(Path.Combine("out", "a", "a.mkv"));
        viewModel.Apply(new ResultEvent { File = "a.usm", Output = output });
        Assert.Equal(ItemStatus.Done, item.Status);
        Assert.Equal(100, item.Progress);
        Assert.Equal(output, item.OutputPath);
    }

    [Fact]
    public void CancelMarksTheQueuedRemainderNotTheUntouchedRows()
    {
        var viewModel = NewViewModel();
        var running = Add(viewModel, "a.usm");
        var queued = Add(viewModel, "b.usm");
        var untouched = Add(viewModel, "c.usm");
        running.Status = ItemStatus.Running;
        queued.Status = ItemStatus.Queued;

        viewModel.Apply(new CancelledEvent { File = "a.usm" });

        Assert.Equal(ItemStatus.Cancelled, running.Status);
        Assert.Equal(ItemStatus.Cancelled, queued.Status);
        Assert.Equal(ItemStatus.Pending, untouched.Status);
    }

    [Fact]
    public void RecoveryMidRunFlipsTheKeyColumnAndRecordsTheKey()
    {
        var viewModel = NewViewModel();
        var item = Add(viewModel, "a.usm");
        item.Key = KeyState.Missing;

        viewModel.Apply(new CrackEvent { File = "a.usm", Stem = "a", VideoKey = 7, Reason = "" });

        Assert.Equal(KeyState.Recovered, item.Key);
        Assert.Equal(7UL, item.VideoKey);
        Assert.Contains(viewModel.Log, line => line.Contains("videoKey=7", StringComparison.Ordinal));
        Assert.Contains("\"a\"", File.ReadAllText(viewModel.RecoveredKeysPath), StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateCheckIsRememberedAndLogged()
    {
        var viewModel = NewViewModel();

        viewModel.Apply(new UpdateEvent { Current = "1.0", Latest = "1.1", Available = true });

        Assert.NotNull(viewModel.LatestUpdate);
        Assert.Equal("1.1", viewModel.LatestUpdate.Latest);
        Assert.Contains(viewModel.Log, line => line.Contains("1.1 is available", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task StartupCheckAsksOnlyWhenAReleaseIsOut(bool available, bool asked)
    {
        var engine = FakeEngine(
            $$"""@echo {"type":"update","current":"1.0","latest":"1.1","available":{{available.ToString().ToLowerInvariant()}}}""");
        var viewModel = NewViewModel(engine);
        var confirmed = false;
        viewModel.ConfirmUpdate = _ =>
        {
            confirmed = true;
            return false;
        };

        await viewModel.CheckForUpdatesOnStartupAsync();

        Assert.NotNull(viewModel.LatestUpdate);
        Assert.Equal(asked, confirmed);
    }

    [Fact]
    public async Task StartupCheckCanBeTurnedOff()
    {
        var engine = FakeEngine(
            """@echo {"type":"update","current":"1.0","latest":"1.1","available":true}""");
        var settings = new Settings
        {
            EnginePath = scratch.File("charlotte-cli.exe"),
            CheckForUpdatesOnStartup = false,
        };
        var viewModel = new MainViewModel(engine, settings, uiContext: null);

        await viewModel.CheckForUpdatesOnStartupAsync();

        Assert.Null(viewModel.LatestUpdate);
    }

    [Fact]
    public void SetKeyNeedsExactlyOneCheckedRow()
    {
        var viewModel = NewViewModel();
        var first = Add(viewModel, "a.usm");
        var second = Add(viewModel, "b.usm");
        Assert.False(viewModel.CanSetKey);

        first.IsChecked = true;
        Assert.True(viewModel.CanSetKey);
        Assert.Same(first, viewModel.SingleChecked);

        second.IsChecked = true;
        Assert.False(viewModel.CanSetKey);
        Assert.Null(viewModel.SingleChecked);
    }

    [Fact]
    public void ACrackBatchRestsEachRowWhenTheNextOneStarts()
    {
        var viewModel = NewViewModel();
        var first = Add(viewModel, "a.usm");
        var second = Add(viewModel, "b.usm");

        viewModel.Apply(new JobStartEvent { File = "a.usm" });
        viewModel.Apply(new CrackEvent { File = "a.usm", Stem = "a", VideoKey = null, Reason = "x" });
        Assert.Equal(ItemStatus.Running, first.Status);

        viewModel.Apply(new JobStartEvent { File = "b.usm" });

        Assert.Equal(ItemStatus.Pending, first.Status);
        Assert.Equal(KeyState.Missing, first.Key);
        Assert.Equal(ItemStatus.Running, second.Status);
    }

    [Fact]
    public void FailedRecoveryStaysMissingAndSaysWhy()
    {
        var viewModel = NewViewModel();
        var item = Add(viewModel, "a.usm");

        viewModel.Apply(new CrackEvent { File = "a.usm", Stem = "a", VideoKey = null, Reason = "single distinct payload" });

        Assert.Equal(KeyState.Missing, item.Key);
        Assert.Contains(viewModel.Log, line => line.Contains("single distinct payload", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("exists", "already exists")]
    [InlineData("no_key", "no key")]
    [InlineData("requested", "skipped on request")]
    public void SkipReasonsBecomeReadableDetail(string reason, string expected)
    {
        var viewModel = NewViewModel();
        var item = Add(viewModel, "a.usm");

        viewModel.Apply(new JobSkippedEvent { File = "a.usm", Reason = reason });

        Assert.Equal(ItemStatus.Skipped, item.Status);
        Assert.Equal(expected, item.Detail);
    }

    [Fact]
    public void RetryFailedIsOfferedOnlyWhileAnErrorRowExists()
    {
        var viewModel = NewViewModel();
        var failed = Add(viewModel, "a.usm");
        Add(viewModel, "b.usm").Status = ItemStatus.Done;
        Assert.False(viewModel.RetryFailedCommand.CanExecute(null));

        failed.Status = ItemStatus.Error;
        Assert.True(viewModel.RetryFailedCommand.CanExecute(null));

        viewModel.Items.Remove(failed);
        Assert.False(viewModel.RetryFailedCommand.CanExecute(null));
    }

    [Fact]
    public void SkipIsOfferedOnlyDuringAFileRun()
    {
        // Running with no files is an update check, which has nothing to skip.
        var viewModel = NewViewModel();
        viewModel.IsRunning = true;

        Assert.False(viewModel.SkipCommand.CanExecute(null));
    }

    [Fact]
    public void QuestionsGoThroughTheViewsDelegate()
    {
        var viewModel = NewViewModel();
        string? asked = null;
        viewModel.AnswerQuestion = prompt => { asked = prompt; return true; };

        viewModel.Apply(new QuestionEvent { Id = "q0", Prompt = "Overwrite keys.json?", Default = false });

        Assert.Equal("Overwrite keys.json?", asked);
    }

    [Fact]
    public void UnknownKindsAreLoggedNotDropped()
    {
        var viewModel = NewViewModel();

        viewModel.Apply(new UnknownEvent { Type = "something_new" });

        Assert.Contains(viewModel.Log, line => line.Contains("something_new", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "1 KB")]
    [InlineData(640L * 1024 * 1024, "640 MB")]
    [InlineData(1_503_238_553, "1.4 GB")]
    public void SizeIsFormattedForTheQueue(long bytes, string expected)
    {
        Assert.Equal(expected, QueueItem.FormatSize(bytes));
    }

    [Fact]
    public void SummaryCountsDoneAndMissing()
    {
        var viewModel = NewViewModel();
        Add(viewModel, "a.usm").Status = ItemStatus.Done;
        Add(viewModel, "b.usm").Key = KeyState.Missing;
        Add(viewModel, "c.usm").Subtitles = [];

        Assert.Equal("3 files · 1 done · 1 missing key · 1 without subtitles", viewModel.Summary);
    }

    [Fact]
    public void SummaryCountsUnfilteredOnlyWhenVapourSynthIsOn()
    {
        var viewModel = NewViewModel();
        Add(viewModel, "a.usm").HasVsScript = false;
        Add(viewModel, "b.usm");

        Assert.DoesNotContain("unfiltered", viewModel.Summary, StringComparison.Ordinal);

        viewModel.Options.UseVapourSynth = true;
        Assert.EndsWith("· 1 unfiltered", viewModel.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ToggleAllChecksEverythingThenNothing()
    {
        var viewModel = NewViewModel();
        Add(viewModel, "a.usm");
        Add(viewModel, "b.usm");

        viewModel.ToggleAllCommand.Execute(null);
        Assert.True(viewModel.AllChecked);

        viewModel.ToggleAllCommand.Execute(null);
        Assert.False(viewModel.AllChecked);
        Assert.All(viewModel.Items, item => Assert.False(item.IsChecked));
    }

    [Fact]
    public void StartLabelSaysWhenOnlyCheckedRowsWillRun()
    {
        var viewModel = NewViewModel();
        var first = Add(viewModel, "a.usm");
        var second = Add(viewModel, "b.usm");
        Assert.Equal("Start", viewModel.StartLabel);

        first.IsChecked = true;
        Assert.Equal("Start (1 checked)", viewModel.StartLabel);

        second.IsChecked = true;
        Assert.Equal("Start", viewModel.StartLabel);
    }

    [Fact]
    public void WithoutAnEngineTheEngineBackedCommandsAreDisabled()
    {
        var viewModel = NewEnginelessViewModel();
        Add(viewModel, "a.usm").IsChecked = true;

        Assert.False(viewModel.HasEngine);
        Assert.False(viewModel.StartCommand.CanExecute(null));
        Assert.False(viewModel.RecoverKeysCommand.CanExecute(null));
        Assert.False(viewModel.CheckForUpdatesCommand.CanExecute(null));
        Assert.True(viewModel.RemoveCheckedCommand.CanExecute(null));
        Assert.Contains(viewModel.Log, line => line.Contains("No engine", StringComparison.Ordinal));
    }

    [Fact]
    public void RunningDisablesEverythingThatWouldChangeTheQueue()
    {
        var viewModel = NewViewModel();
        Add(viewModel, "a.usm").IsChecked = true;

        viewModel.IsRunning = true;

        Assert.False(viewModel.IsIdle);
        Assert.False(viewModel.StartCommand.CanExecute(null));
        Assert.False(viewModel.RecoverKeysCommand.CanExecute(null));
        Assert.False(viewModel.RemoveCheckedCommand.CanExecute(null));
        Assert.True(viewModel.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task LoadingIsRefusedWhileRunning()
    {
        var viewModel = NewEnginelessViewModel();
        var kept = Add(viewModel, "a.usm");
        viewModel.IsRunning = true;

        await viewModel.LoadSourceAsync(scratch.Root);

        Assert.Same(kept, Assert.Single(viewModel.Items));
        Assert.Contains(viewModel.Log, line => line.Contains("Wait for the current run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadingAMissingFolderLogsInsteadOfThrowing()
    {
        var viewModel = NewEnginelessViewModel();
        var kept = Add(viewModel, "a.usm");

        await viewModel.LoadSourceAsync(scratch.File("missing"));

        Assert.Same(kept, Assert.Single(viewModel.Items));
        Assert.Contains(viewModel.Log, line => line.Contains("Could not read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddedFilesAreFilteredToUsmAndDedupedByBareName()
    {
        var viewModel = NewEnginelessViewModel();
        var one = scratch.File("one");
        var two = scratch.File("two");

        await viewModel.AddFilesAsync([
            Path.Combine(one, "a.usm"),
            Path.Combine(one, "notes.txt"),
            Path.Combine(two, "a.usm"),
            Path.Combine(two, "B.USM"),
        ]);

        Assert.Equal(["a.usm", "B.USM"], viewModel.Items.Select(item => item.FileName));
        Assert.Equal(one, viewModel.SourceDirectory);
        Assert.Contains(viewModel.Log, line => line.Contains("already in the queue", StringComparison.Ordinal));
    }

    [Fact]
    public void ClearingTheQueueStopsListeningToTheOldRows()
    {
        var viewModel = NewViewModel();
        var old = Add(viewModel, "a.usm");
        viewModel.Items.Clear();
        Add(viewModel, "b.usm");

        old.IsChecked = true;

        Assert.False(viewModel.AllChecked);
    }

    [Fact]
    public void ProgressOutsideAnyJobStillDrivesTheStatusBar()
    {
        var viewModel = NewViewModel();
        var item = Add(viewModel, "a.usm");

        viewModel.Apply(new StageEvent { Stage = "subtitles", Status = "start" });
        viewModel.Apply(new ProgressEvent { Stage = "subtitles", Current = 1, Total = 4 });

        Assert.Equal("subtitles · 25%", viewModel.StageText);
        Assert.Equal(0, item.Progress);
        Assert.Equal(ItemStatus.Pending, item.Status);
    }

    [Fact]
    public void RecoverKeysNeedsACheckedRow()
    {
        var viewModel = NewViewModel();
        var item = Add(viewModel, "a.usm");
        Assert.False(viewModel.RecoverKeysCommand.CanExecute(null));

        item.IsChecked = true;
        Assert.True(viewModel.RecoverKeysCommand.CanExecute(null));

        viewModel.Items.Clear();
        Assert.False(viewModel.RecoverKeysCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnEngineThatCannotStartFailsTheRunWithoutLeavingRowsQueued()
    {
        // A launcher that does not exist is the only way to reach RunEngineAsync's failure path
        // without a real engine, because Process.Start throws before any event can arrive.
        var viewModel = NewViewModel(EngineLaunchProfile.Packaged(scratch.File("missing.exe")));
        var first = Add(viewModel, "a.usm");
        var second = Add(viewModel, "b.usm");

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsRunning);
        Assert.Equal("Idle", viewModel.StageText);
        Assert.Contains(viewModel.Log, line => line.Contains("Could not start the engine", StringComparison.Ordinal));
        Assert.Equal(ItemStatus.Pending, first.Status);
        Assert.Equal(ItemStatus.Pending, second.Status);
    }

    [Theory]
    [InlineData(3, ItemStatus.Error, "engine exited with code 3")]
    [InlineData(0, ItemStatus.Pending, "")]
    public async Task ARowTheEngineLeftRunningIsSettledByHowTheEngineEnded(int exitCode, ItemStatus expected, string detail)
    {
        // A clean exit that never closed the job is --crack, whose outcome is the Key column, and
        // the row goes back to resting rather than to Error.
        var engine = FakeEngine(
            """@echo {"type":"job_start","file":"a.usm","stem":"a"}""",
            $"@exit /b {exitCode}");
        var viewModel = NewViewModel(engine);
        var opened = Add(viewModel, "a.usm");
        var unreached = Add(viewModel, "b.usm");
        opened.IsChecked = true;
        unreached.IsChecked = true;

        await viewModel.RecoverKeysCommand.ExecuteAsync(null);

        Assert.Equal(expected, opened.Status);
        Assert.Equal(detail, opened.Detail);
        Assert.Equal(ItemStatus.Pending, unreached.Status);
        Assert.False(viewModel.IsRunning);
    }

    [Fact]
    public async Task AFailedRunShowsWhatTheEngineSaidOnStderr()
    {
        var engine = FakeEngine(
            "@echo Traceback: something broke>&2",
            "@exit /b 1");
        var viewModel = NewViewModel(engine);
        Add(viewModel, "a.usm");

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Contains(viewModel.Log, line => line.Contains("exited with code 1", StringComparison.Ordinal));
        Assert.Contains(viewModel.Log, line => line.Contains("something broke", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailureTheEventsAlreadyExplainedDoesNotEchoStderr()
    {
        // The engine exits 1 after a batch with a failed file, and the error event on the row
        // already explains it, which is why the console logger's copy stays out of the log.
        var engine = FakeEngine(
            """@echo {"type":"job_start","file":"a.usm","stem":"a"}""",
            """@echo {"type":"error","file":"a.usm","message":"bad chunk"}""",
            "@echo [12:00:00] ERROR Failed to process a.usm: bad chunk>&2",
            "@exit /b 1");
        var viewModel = NewViewModel(engine);
        var failed = Add(viewModel, "a.usm");

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Equal(ItemStatus.Error, failed.Status);
        Assert.Equal("bad chunk", failed.Detail);
        Assert.Contains(viewModel.Log, line => line.Contains("exited with code 1", StringComparison.Ordinal));
        Assert.DoesNotContain(viewModel.Log, line => line.Contains("Failed to process", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnreadableSettingsFileIsReportedInTheLog()
    {
        var path = scratch.File("settings.json");
        File.WriteAllText(path, "{ not json");

        var viewModel = new MainViewModel(null, Settings.Load(path), uiContext: null);

        Assert.Contains(viewModel.Log, line => line.Contains("using defaults", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadingAFolderProbesEveryFile()
    {
        if (Charlotte.LiveEngine(output) is not { } engine)
        {
            return;
        }

        var folder = Charlotte.Checkout is { } checkout ? Path.Combine(checkout, "USM", "6.3") : null;
        if (!Directory.Exists(folder))
        {
            output.WriteLine($"SKIPPED: test cutscenes missing at {folder ?? "../charlotte"}");
            return;
        }

        var viewModel = NewViewModel(engine);
        await viewModel.LoadSourceAsync(folder).WaitAsync(TimeSpan.FromMinutes(3));

        Assert.NotEmpty(viewModel.Items);
        Assert.False(viewModel.IsRunning);
        Assert.All(viewModel.Items, item => Assert.NotEqual(KeyState.Unknown, item.Key));
    }
}
