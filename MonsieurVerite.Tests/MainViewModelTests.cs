using System.IO;

using MonsieurVerite.Engine;
using MonsieurVerite.ViewModels;

namespace MonsieurVerite.Tests;

/// <summary>
/// Every view model here is given no SynchronizationContext, which applies engine events on the
/// thread that read them and lets every assertion follow the call directly. xUnit installs a
/// context of its own on the test thread, and capturing that one would post the events to the
/// thread pool and race the assertions.
/// </summary>
public class MainViewModelTests
{
    private static string ScratchKeysPath() =>
        Path.Combine(Path.GetTempPath(), "MonsieurVerite.Tests", Path.GetRandomFileName(), "recovered_keys.json");

    private static MainViewModel NewViewModel(EngineLaunchProfile? engine) =>
        new(engine, new Settings(), uiContext: null) { RecoveredKeysPath = ScratchKeysPath() };

    private static MainViewModel NewViewModel() => NewViewModel(EngineLaunchProfile.Dev(Path.GetTempPath()));

    private static MainViewModel NewEnginelessViewModel() => NewViewModel(null);

    private static QueueItem Add(MainViewModel viewModel, string name)
    {
        var item = new QueueItem(Path.Combine(@"C:\usm", name));
        viewModel.Items.Add(item);
        return item;
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
    public void ARunDrivesTheRowFromQueuedToDone()
    {
        var viewModel = NewViewModel();
        var item = Add(viewModel, "a.usm");
        Assert.Equal(ItemStatus.Pending, item.Status);

        viewModel.Apply(new JobStartEvent { File = "a.usm" });
        Assert.Equal(ItemStatus.Running, item.Status);

        // Progress and stage events name no file and belong to the job that started last.
        viewModel.Apply(new StageEvent { Stage = "demux", Status = "start" });
        Assert.Equal("demux", item.Detail);

        viewModel.Apply(new ProgressEvent { Stage = "demux", Current = 5, Total = 10 });
        Assert.Equal(50, item.Progress);
        // The text has no run position because runTotal is only set by a real run, and this test
        // feeds events directly.
        Assert.Equal("demux · 50%", viewModel.StageText);

        viewModel.Apply(new ResultEvent { File = "a.usm", Output = @"C:\out\a\a.mkv" });
        Assert.Equal(ItemStatus.Done, item.Status);
        Assert.Equal(100, item.Progress);
        // The path is kept as sent because it is what "open output folder" reveals, whatever the
        // Flat toggle says now.
        Assert.Equal(@"C:\out\a\a.mkv", item.OutputPath);
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

        // The engine names only the file it was on when it stopped.
        viewModel.Apply(new CancelledEvent { File = "a.usm" });

        Assert.Equal(ItemStatus.Cancelled, running.Status);
        Assert.Equal(ItemStatus.Cancelled, queued.Status);
        Assert.Equal(ItemStatus.Pending, untouched.Status);
    }

    [Fact]
    public void RecoveryMidRunFlipsTheKeyColumn()
    {
        var viewModel = NewViewModel();
        var item = Add(viewModel, "a.usm");
        item.Key = KeyState.Missing;

        viewModel.Apply(new CrackEvent { File = "a.usm", Stem = "a", VideoKey = 7, Reason = "" });

        Assert.Equal(KeyState.Recovered, item.Key);
        Assert.Equal(7UL, item.VideoKey);
        Assert.Contains(viewModel.Log, line => line.Contains("videoKey=7", StringComparison.Ordinal));

        // It also lands in the recovered-keys file under the stem, which is how keys.json wants it.
        Assert.Contains("\"a\"", File.ReadAllText(viewModel.RecoveredKeysPath), StringComparison.Ordinal);
        Directory.Delete(Path.GetDirectoryName(viewModel.RecoveredKeysPath)!, recursive: true);
    }

    [Fact]
    public void UpdateCheckIsRememberedAndLogged()
    {
        var viewModel = NewViewModel();

        viewModel.Apply(new UpdateEvent { Current = "1.0", Latest = "1.1", Available = true });

        // CheckForUpdates reads this once the engine has exited, then asks the view whether to install.
        Assert.NotNull(viewModel.LatestUpdate);
        Assert.Equal("1.1", viewModel.LatestUpdate.Latest);
        Assert.Contains(viewModel.Log, line => line.Contains("1.1 is available", StringComparison.Ordinal));
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
        // --crack closes no job, so the row is still Running here and only the next job_start
        // says the file is done. Without that the whole batch pulses until the engine exits.
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
        // An update check runs the engine too, but has no file to skip.
        var viewModel = NewViewModel();
        viewModel.IsRunning = true;

        Assert.False(viewModel.SkipCommand.CanExecute(null));
    }

    [Fact]
    public void QuestionsGoThroughTheViewsDelegate()
    {
        var viewModel = NewViewModel();
        string? asked = null;
        viewModel.ConfirmKeyOverwrite = prompt => { asked = prompt; return true; };

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

        viewModel.Apply(new LogEvent { Level = "info", Message = "tick" });

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

        // Everything checked is the same run as nothing checked, which is why the qualifier goes.
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

        await viewModel.LoadSourceAsync(Path.GetTempPath());

        Assert.Same(kept, Assert.Single(viewModel.Items));
        Assert.Contains(viewModel.Log, line => line.Contains("Wait for the current run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadingAMissingFolderLogsInsteadOfThrowing()
    {
        var viewModel = NewEnginelessViewModel();
        var kept = Add(viewModel, "a.usm");
        var missing = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        await viewModel.LoadSourceAsync(missing);

        Assert.Same(kept, Assert.Single(viewModel.Items));
        Assert.Contains(viewModel.Log, line => line.Contains("Could not read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddedFilesAreFilteredToUsmAndDedupedByBareName()
    {
        var viewModel = NewEnginelessViewModel();

        await viewModel.AddFilesAsync([
            @"C:\one\a.usm",
            @"C:\one\notes.txt",
            @"C:\two\a.usm",
            @"C:\two\B.USM",
        ]);

        // A second a.usm could never be told apart, because every engine event names a file by
        // bare name.
        Assert.Equal(["a.usm", "B.USM"], viewModel.Items.Select(item => item.FileName));
        Assert.Equal(@"C:\one", viewModel.SourceDirectory);
        Assert.Contains(viewModel.Log, line => line.Contains("already in the queue", StringComparison.Ordinal));
    }

    [Fact]
    public void ClearingTheQueueStopsListeningToTheOldRows()
    {
        var viewModel = NewViewModel();
        var old = Add(viewModel, "a.usm");
        viewModel.Items.Clear();
        Add(viewModel, "b.usm");

        // A stale handler would flip AllChecked from a row that is no longer in the queue.
        old.IsChecked = true;

        Assert.False(viewModel.AllChecked);
    }

    [Fact]
    public void ProgressOutsideAnyJobStillDrivesTheStatusBar()
    {
        // The subtitle sync runs before the file loop and its progress names no job. It should
        // still show in the status bar even though it has no row to land on.
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
        // A launcher that does not exist makes Process.Start throw before any event can arrive,
        // which is the only way to drive RunEngineAsync's failure path without a real engine.
        var missing = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "charlotte-cli.exe");
        var viewModel = NewViewModel(EngineLaunchProfile.Packaged(missing));
        var first = Add(viewModel, "a.usm");
        var second = Add(viewModel, "b.usm");

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsRunning);
        Assert.Equal("Idle", viewModel.StageText);
        Assert.Contains(viewModel.Log, line => line.Contains("Could not start the engine", StringComparison.Ordinal));
        // Neither row was ever reached, and neither is an error for it. They simply go back to
        // resting.
        Assert.Equal(ItemStatus.Pending, first.Status);
        Assert.Equal(ItemStatus.Pending, second.Status);
    }

    [Theory]
    [InlineData(3, ItemStatus.Error, "engine exited with code 3")]
    [InlineData(0, ItemStatus.Pending, "")]
    public async Task ARowTheEngineLeftRunningIsSettledByHowTheEngineEnded(int exitCode, ItemStatus expected, string detail)
    {
        // A crash mid-file leaves the row Running with nothing more coming, and that row is the
        // failure. A clean exit that never closed the job is --crack, whose outcome is the Key
        // column, and the row just goes back to resting. Either way nothing stays Queued.
        var (engine, script) = FakeEngine(
            """@echo {"type":"job_start","file":"a.usm","stem":"a"}""",
            $"@exit /b {exitCode}");
        try
        {
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
        finally
        {
            File.Delete(script);
        }
    }

    [Fact]
    public async Task AFailedRunShowsWhatTheEngineSaidOnStderr()
    {
        // A usage error or a traceback never becomes a log event; stderr is the only place it
        // goes, and "exited with code 1" alone would leave the user with nothing to act on.
        var (engine, script) = FakeEngine(
            "@echo Traceback: something broke>&2",
            "@exit /b 1");
        try
        {
            var viewModel = NewViewModel(engine);
            Add(viewModel, "a.usm");

            await viewModel.StartCommand.ExecuteAsync(null);

            Assert.Contains(viewModel.Log, line => line.Contains("exited with code 1", StringComparison.Ordinal));
            Assert.Contains(viewModel.Log, line => line.Contains("something broke", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(script);
        }
    }

    /// <summary>
    /// A batch script that prints the given lines to stdout and ignores its arguments, standing in
    /// for the engine. The caller deletes the script.
    /// </summary>
    private static (EngineLaunchProfile Engine, string Script) FakeEngine(params string[] lines)
    {
        var script = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".cmd");
        File.WriteAllLines(script, lines);
        var engine = new EngineLaunchProfile
        {
            FileName = "cmd.exe",
            BaseArguments = ["/c", script],
            WorkingDirectory = Path.GetTempPath(),
        };
        return (engine, script);
    }

    [Fact]
    public async Task LoadingAFolderProbesEveryFile()
    {
        if (Settings.ResolveEngine() is not { } engine)
        {
            return;
        }

        var folder = Path.Combine(engine.WorkingDirectory, "USM", "6.3");
        if (!Directory.Exists(folder))
        {
            return;
        }

        var viewModel = new MainViewModel(engine, new Settings(), uiContext: null);
        await viewModel.LoadSourceAsync(folder).WaitAsync(TimeSpan.FromMinutes(3));

        Assert.NotEmpty(viewModel.Items);
        Assert.False(viewModel.IsRunning);
        Assert.All(viewModel.Items, item => Assert.NotEqual(KeyState.Unknown, item.Key));
    }
}
