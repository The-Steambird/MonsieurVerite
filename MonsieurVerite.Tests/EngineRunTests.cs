using System.IO;

using MonsieurVerite.Engine;
using MonsieurVerite.ViewModels;

namespace MonsieurVerite.Tests;

public class EngineRunTests : ViewModelTest
{
    private static EngineRun NewRun(MainViewModel viewModel, int jobCount = 0) =>
        new(viewModel, viewModel.Engine!, jobCount);

    [Fact]
    public void ProbeFillsKeyAndSubtitleAvailability()
    {
        var viewModel = NewViewModel();
        var run = NewRun(viewModel);
        var keyed = Add(viewModel, "a.usm");
        var keyless = Add(viewModel, "b.usm");

        run.Apply(new ProbeEvent { File = "a.usm", Key = true, Version = "5.3", Subtitles = ["EN", "JP"] });
        run.Apply(new ProbeEvent { File = "b.usm", Key = false, Subtitles = [], StreamCipher = true });

        Assert.Equal(KeyState.Present, keyed.Key);
        Assert.Equal("5.3", keyed.Version);
        Assert.True(keyed.HasSubtitles);
        Assert.Equal("Cached subtitles: EN, JP", keyed.SubtitlesTip);
        Assert.False(keyed.StreamCipher);

        Assert.Equal(KeyState.Missing, keyless.Key);
        Assert.Null(keyless.Version);
        Assert.False(keyless.HasSubtitles);
        Assert.True(keyless.StreamCipher);
    }

    [Fact]
    public void ARunDrivesTheRowFromPendingToDone()
    {
        var viewModel = NewViewModel();
        var run = NewRun(viewModel);
        var item = Add(viewModel, "a.usm");
        Assert.Equal(ItemStatus.Pending, item.Status);

        run.Apply(new JobStartEvent { File = "a.usm" });
        Assert.Equal(ItemStatus.Running, item.Status);

        run.Apply(new StageEvent { Stage = "demux", Status = "start" });
        Assert.Equal("Demuxing", item.Detail);

        run.Apply(new ProgressEvent { Stage = "demux", Current = 5, Total = 10 });
        Assert.Equal(50, item.Progress);

        var output = Scratch.File(Path.Combine("out", "a", "a.mkv"));
        run.Apply(new ResultEvent { File = "a.usm", Output = output });
        Assert.Equal(ItemStatus.Done, item.Status);
        Assert.Equal(100, item.Progress);
        Assert.Equal(output, item.OutputPath);
    }

    [Fact]
    public void AFailedRowKeepsItsProgressAndSaysWhy()
    {
        var viewModel = NewViewModel();
        var run = NewRun(viewModel);
        var item = Add(viewModel, "a.usm");

        run.Apply(new JobStartEvent { File = "a.usm" });
        run.Apply(new ProgressEvent { Stage = "ffmpeg", Current = 3, Total = 4 });
        run.Apply(new ErrorEvent { File = "a.usm", Message = "bad chunk" });

        Assert.Equal(ItemStatus.Error, item.Status);
        Assert.Equal(75, item.Progress);
        Assert.Equal("bad chunk", item.Detail);
        Assert.Contains(new LogLine("a.usm: bad chunk", IsWarning: true), viewModel.Log);
    }

    [Fact]
    public void CancelMarksTheQueuedRemainderNotTheUntouchedRows()
    {
        var viewModel = NewViewModel();
        var run = NewRun(viewModel);
        var running = Add(viewModel, "a.usm");
        var queued = Add(viewModel, "b.usm");
        var untouched = Add(viewModel, "c.usm");
        running.MarkRunning();
        queued.MarkQueued();

        run.Apply(new CancelledEvent { File = "a.usm" });

        Assert.Equal(ItemStatus.Cancelled, running.Status);
        Assert.Equal(ItemStatus.Cancelled, queued.Status);
        Assert.Equal(ItemStatus.Pending, untouched.Status);
    }

    [Fact]
    public void RecoveryMidRunFlipsTheKeyColumnAndRecordsTheKey()
    {
        var viewModel = NewViewModel();
        var run = NewRun(viewModel);
        var item = Add(viewModel, "a.usm");
        item.Key = KeyState.Missing;

        run.Apply(new CrackEvent { File = "a.usm", Stem = "a", VideoKey = 7, Reason = "" });

        Assert.Equal(KeyState.Recovered, item.Key);
        Assert.Equal(7UL, item.VideoKey);
        Assert.Contains(viewModel.Log, line => line.Text.Contains("videoKey=7", StringComparison.Ordinal));
        Assert.Contains("\"a\"", File.ReadAllText(viewModel.RecoveredKeysPath), StringComparison.Ordinal);
    }

    [Fact]
    public void ACrackBatchReturnsEachRowToPendingWhenTheNextOneStarts()
    {
        var viewModel = NewViewModel();
        var run = NewRun(viewModel);
        var first = Add(viewModel, "a.usm");
        var second = Add(viewModel, "b.usm");

        run.Apply(new JobStartEvent { File = "a.usm" });
        run.Apply(new CrackEvent { File = "a.usm", Stem = "a", VideoKey = null, Reason = "x" });
        Assert.Equal(ItemStatus.Running, first.Status);

        run.Apply(new JobStartEvent { File = "b.usm" });

        Assert.Equal(ItemStatus.Pending, first.Status);
        Assert.Equal(KeyState.Missing, first.Key);
        Assert.Equal(ItemStatus.Running, second.Status);
    }

    [Fact]
    public void FailedRecoveryStaysMissingAndSaysWhy()
    {
        var viewModel = NewViewModel();
        var run = NewRun(viewModel);
        var item = Add(viewModel, "a.usm");

        run.Apply(new CrackEvent { File = "a.usm", Stem = "a", VideoKey = null, Reason = "single distinct payload" });

        Assert.Equal(KeyState.Missing, item.Key);
        Assert.Contains(viewModel.Log, line => line.Text.Contains("single distinct payload", StringComparison.Ordinal));
    }

    [Fact]
    public void FailedRecoveryKeepsAKeyTheProbeFound()
    {
        var viewModel = NewViewModel();
        var run = NewRun(viewModel);
        var item = Add(viewModel, "a.usm");
        item.Key = KeyState.Present;

        run.Apply(new CrackEvent { File = "a.usm", Stem = "a", VideoKey = null, Reason = "x" });

        Assert.Equal(KeyState.Present, item.Key);
    }

    [Theory]
    [InlineData("exists", "Already exists")]
    [InlineData("no_key", "No key")]
    [InlineData("requested", "Skipped on request")]
    public void SkipReasonsBecomeReadableDetail(string reason, string expected)
    {
        var viewModel = NewViewModel();
        var run = NewRun(viewModel);
        var item = Add(viewModel, "a.usm");

        run.Apply(new JobSkippedEvent { File = "a.usm", Reason = reason });

        Assert.Equal(ItemStatus.Skipped, item.Status);
        Assert.Equal(expected, item.Detail);
    }

    [Fact]
    public void QuestionsGoThroughTheViewsDelegate()
    {
        var viewModel = NewViewModel();
        var run = NewRun(viewModel);
        var shown = DeclineMessages(viewModel);

        run.Apply(new QuestionEvent { Id = "q0", Prompt = "Overwrite keys.json?", Default = false });

        var question = Assert.Single(shown);
        Assert.Equal("Overwrite keys.json?", question.Text);
        Assert.Equal(Strings.YES, question.Primary);
        Assert.Equal(Strings.NO, question.Secondary);
    }

    [Fact]
    public void UnknownKindsAreLoggedNotDropped()
    {
        var viewModel = NewViewModel();
        var run = NewRun(viewModel);

        run.Apply(new UnknownEvent { Type = "something_new" });

        Assert.Contains(viewModel.Log, line => line.Text.Contains("something_new", StringComparison.Ordinal));
    }

    [Fact]
    public void ProgressOutsideAnyJobLeavesTheRowsAlone()
    {
        var viewModel = NewViewModel();
        var run = NewRun(viewModel);
        var item = Add(viewModel, "a.usm");

        run.Apply(new StageEvent { Stage = "subtitles", Status = "start" });
        run.Apply(new ProgressEvent { Stage = "subtitles", Current = 1, Total = 4 });

        Assert.Equal("", item.Detail);
        Assert.Equal(0, item.Progress);
        Assert.Equal(ItemStatus.Pending, item.Status);
    }

    [Fact]
    public async Task AnEngineThatCannotStartFailsTheRunWithoutLeavingRowsQueued()
    {
        var viewModel = NewViewModel(Scratch.File("missing.exe"));
        var first = Add(viewModel, "a.usm");
        var second = Add(viewModel, "b.usm");

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsRunning);
        Assert.Contains(viewModel.Log, line => line.Text.Contains("Could not start the engine", StringComparison.Ordinal));
        Assert.Equal(ItemStatus.Pending, first.Status);
        Assert.Equal(ItemStatus.Pending, second.Status);
    }

    [Theory]
    [InlineData(3, ItemStatus.Error, "Engine exited with code 3")]
    [InlineData(0, ItemStatus.Pending, "")]
    public async Task ARowTheEngineLeftRunningIsSettledByHowTheEngineEnded(int exitCode, ItemStatus expected, string detail)
    {
        // A clean exit that never closed the job is how --crack ends, which is why the row goes
        // back to Pending instead of to Error.
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

        Assert.Contains(viewModel.Log, line => line.Text.Contains("exited with code 1", StringComparison.Ordinal));
        Assert.Contains(viewModel.Log, line => line.Text.Contains("something broke", StringComparison.Ordinal));
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
        Assert.Contains(viewModel.Log, line => line.Text.Contains("exited with code 1", StringComparison.Ordinal));
        Assert.DoesNotContain(viewModel.Log, line => line.Text.Contains("Failed to process", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASecondCancelKillsAnEngineThatIgnoresTheFirst()
    {
        // The cancel goes unanswered because cmd never reads stdin, and ping is a child the kill
        // has to reach through the job object.
        var engine = FakeEngine(
            """@echo {"type":"job_start","file":"a.usm","stem":"a"}""",
            "@ping -n 60 127.0.0.1 >nul");
        var viewModel = NewViewModel(engine);
        var running = Add(viewModel, "a.usm");
        var queued = Add(viewModel, "b.usm");
        // The Skip button only learns its state when the command says it changed.
        var skippable = false;
        viewModel.SkipCommand.CanExecuteChanged +=
            (_, _) => skippable = viewModel.SkipCommand.CanExecute(null);
        // Without asynchronous continuations the cancels below would run inside Apply.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        running.PropertyChanged += (_, _) =>
        {
            if (running.Status == ItemStatus.Running)
            {
                started.TrySetResult();
            }
        };

        var run = viewModel.StartCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(skippable);
        viewModel.CancelCommand.Execute(null);
        Assert.Equal("Force stop", viewModel.CancelLabel);
        viewModel.CancelCommand.Execute(null);
        Assert.Equal("Stopping…", viewModel.CancelLabel);
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(viewModel.IsRunning);
        Assert.False(skippable);
        Assert.Equal("Cancel", viewModel.CancelLabel);
        Assert.Equal(ItemStatus.Cancelled, running.Status);
        Assert.Equal(ItemStatus.Cancelled, queued.Status);
        Assert.Contains(viewModel.Log, line => line.Text == "Engine stopped.");
    }

    [Fact]
    public async Task AnErrorInApplyEndsTheRunAndMarksTheRunningRow()
    {
        // The ping would hold the run for a minute if the throw did not also end the engine.
        var engine = FakeEngine(
            """@echo {"type":"job_start","file":"a.usm","stem":"a"}""",
            """@echo {"type":"question","id":"q0","prompt":"overwrite?","default":false}""",
            "@ping -n 60 127.0.0.1 >nul");
        var viewModel = NewViewModel(engine);
        viewModel.ShowMessage = _ => throw new InvalidOperationException("boom");
        var running = Add(viewModel, "a.usm");
        var queued = Add(viewModel, "b.usm");

        var run = viewModel.StartCommand.ExecuteAsync(null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.False(viewModel.IsRunning);
        Assert.Equal(ItemStatus.Error, running.Status);
        Assert.Equal("Stopped by an unexpected error", running.Detail);
        Assert.Equal(ItemStatus.Pending, queued.Status);
    }
}
