using System.IO;

using MonsieurVerite.ViewModels;

using Xunit.Abstractions;

namespace MonsieurVerite.Tests;

public class MainViewModelTests(ITestOutputHelper output) : ViewModelTest
{
    // The engine asks this right after it reports an available release.
    private const string InstallQuestion =
        """@echo {"type":"question","id":"q0","prompt":"Download and install 1.1 now?","default":false}""";

    [Theory]
    [InlineData("\"available\":true", true, "Charlotte 1.1 is available (running 1.0).")]
    [InlineData("\"available\":false", false, "Charlotte 1.0 is up to date.")]
    [InlineData("\"available\":false,\"reason\":\"offline\"", false, "Update check failed: offline")]
    public async Task StartupCheckLogsTheResultAndAsksOnlyWhenAReleaseIsOut(
        string fields, bool asked, string logged)
    {
        string[] lines = [$$"""@echo {"type":"update","current":"1.0","latest":"1.1",{{fields}}}"""];
        var engine = FakeEngine(asked ? [.. lines, InstallQuestion] : lines);
        var viewModel = NewViewModel(engine);
        var shown = DeclineMessages(viewModel);

        await viewModel.CheckForUpdatesOnStartupAsync();

        Assert.Equal(asked, shown.Count > 0);
        Assert.Contains(logged, viewModel.Log);
        Assert.DoesNotContain(Strings.NO_UPDATE_RESULT_LOG, viewModel.Log);
    }

    [Fact]
    public async Task AnOfferedReleaseShowsItsNotesApartFromTheDetail()
    {
        var engine = FakeEngine(
            """@echo {"type":"update","current":"1.0","latest":"1.1","available":true,"url":"https://github.com/o/r/releases/tag/1.1","notes":"## Fixed"}""",
            InstallQuestion);
        var viewModel = NewViewModel(engine);
        var shown = DeclineMessages(viewModel);

        await viewModel.CheckForUpdatesCommand.ExecuteAsync(null);

        var offer = Assert.Single(shown);
        Assert.Equal(Strings.UPDATE_AVAILABLE_TITLE("1.1"), offer.Title);
        Assert.Equal("## Fixed", offer.Notes);
        Assert.Equal("https://github.com/o/r/releases/tag/1.1", offer.NotesUrl);
        Assert.Null(offer.Detail);
    }

    [Theory]
    [InlineData("\"available\":false", false)]
    [InlineData("\"available\":false,\"reason\":\"offline\"", true)]
    public async Task ACheckFromTheMenuSaysWhenThereIsNothingToInstall(string fields, bool failed)
    {
        var engine = FakeEngine(
            $$"""@echo {"type":"update","current":"1.0","latest":"1.1",{{fields}}}""");
        var viewModel = NewViewModel(engine);
        var shown = DeclineMessages(viewModel);

        await viewModel.CheckForUpdatesCommand.ExecuteAsync(null);

        var expected = failed ? Strings.UPDATE_CHECK_FAILED_TITLE : Strings.UP_TO_DATE_TITLE;
        Assert.Equal(expected, Assert.Single(shown).Title);
    }

    // Offering a restart switches the strings, and English is the only switch that leaves them
    // as every other test expects.
    [Theory]
    [InlineData("en-US", "ja-JP", true)]
    [InlineData("ja-JP", "ja-JP", false)]
    public void ARestartIsOfferedOnlyForANewLanguage(
        string chosen, string previous, bool offered)
    {
        var viewModel = new MainViewModel(null, new Settings { Language = chosen });
        var shown = DeclineMessages(viewModel);

        viewModel.OfferRestartForLanguage(previous);

        string[] expected = offered ? [Strings.RESTART_TITLE] : [];
        Assert.Equal(expected, shown.Select(message => message.Title));
    }

    [Fact]
    public void ANewLanguageChosenMidRunIsOnlyLogged()
    {
        var viewModel = new MainViewModel(null, new Settings { Language = "en-US" })
        {
            IsRunning = true,
        };
        var shown = DeclineMessages(viewModel);

        viewModel.OfferRestartForLanguage("ja-JP");

        Assert.Empty(shown);
        Assert.Contains(Strings.LANGUAGE_CHANGED_LOG, viewModel.Log);
    }

    [Fact]
    public async Task StartupCheckCanBeTurnedOff()
    {
        var engine = FakeEngine(
            """@echo {"type":"update","current":"1.0","latest":"1.1","available":true}""");
        var settings = new Settings
        {
            EnginePath = Scratch.File("charlotte-cli.exe"),
            CheckForUpdatesOnStartup = false,
        };
        var viewModel = new MainViewModel(engine, settings);
        var shown = DeclineMessages(viewModel);

        await viewModel.CheckForUpdatesOnStartupAsync();

        Assert.Empty(shown);
        Assert.DoesNotContain(viewModel.Log, line => line.Contains("1.1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartupLoadsTheLastFolderBeforeTheWindowIsShownAndTheEngineNoticeAfter()
    {
        var folder = Scratch.File("usm");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.usm"), "");
        var settings = new Settings
        {
            EnginePath = Scratch.File("charlotte-cli.exe"),
            SourceDirectory = folder,
        };
        var viewModel = new MainViewModel(null, settings);
        var shown = DeclineMessages(viewModel);
        var windowShown = new TaskCompletionSource();

        var startup = viewModel.StartupAsync(windowShown.Task);
        Assert.Equal("a.usm", Assert.Single(viewModel.Items).FileName);
        Assert.Empty(shown);

        windowShown.SetResult();
        await startup;
        var notice = Assert.Single(shown);
        Assert.Equal(Strings.ENGINE_MISSING_TITLE, notice.Title);
        Assert.Contains(viewModel.EnginePath, notice.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SetKeyNeedsExactlyOneCheckedRow()
    {
        var viewModel = NewViewModel();
        var first = Add(viewModel, "a.usm");
        var second = Add(viewModel, "b.usm");
        Assert.False(viewModel.SetKeyCommand.CanExecute(null));

        first.IsChecked = true;
        Assert.True(viewModel.SetKeyCommand.CanExecute(null));
        Assert.Same(first, viewModel.Items.SingleChecked);

        second.IsChecked = true;
        Assert.False(viewModel.SetKeyCommand.CanExecute(null));
        Assert.Null(viewModel.Items.SingleChecked);
    }

    [Fact]
    public void CopyVideoKeyWaitsForARecoveredKey()
    {
        var viewModel = NewViewModel();
        var item = Add(viewModel, "a.usm");
        string? copied = null;
        viewModel.CopyText = text => copied = text;
        item.IsChecked = true;
        Assert.False(viewModel.CopyVideoKeyCommand.CanExecute(null));

        item.ApplyCrack(7);
        Assert.True(viewModel.CopyVideoKeyCommand.CanExecute(null));

        viewModel.CopyVideoKeyCommand.Execute(null);
        Assert.Equal("7", copied);
    }

    [Fact]
    public void RecoverKeyNeedsACheckedRowThatIsNotStreamCipher()
    {
        var viewModel = NewViewModel();
        var old = Add(viewModel, "a.usm");
        var streamCipher = Add(viewModel, "b.usm");
        streamCipher.StreamCipher = true;
        Assert.False(viewModel.RecoverKeysCommand.CanExecute(null));

        streamCipher.IsChecked = true;
        Assert.False(viewModel.RecoverKeysCommand.CanExecute(null));

        old.IsChecked = true;
        Assert.True(viewModel.RecoverKeysCommand.CanExecute(null));
    }

    [Fact]
    public void RowChangesTheCommandsReadTellTheButtonsToRequery()
    {
        // The test counts the event because CanExecute is computed on every call and cannot show
        // whether a button would update.
        var viewModel = NewViewModel();
        var item = Add(viewModel, "a.usm");
        var raised = 0;
        viewModel.RecoverKeysCommand.CanExecuteChanged += (_, _) => raised++;

        item.Progress = 50;
        item.Detail = "Demuxing";
        Assert.Equal(0, raised);

        item.IsChecked = true;
        Assert.Equal(1, raised);

        item.StreamCipher = true;
        Assert.Equal(2, raised);
        Assert.False(viewModel.RecoverKeysCommand.CanExecute(null));
    }

    [Fact]
    public void CheckStateIsMixedWhileSomeRowsAreChecked()
    {
        var viewModel = NewViewModel();
        var first = Add(viewModel, "a.usm");
        var second = Add(viewModel, "b.usm");
        Assert.False(viewModel.CheckState);

        first.IsChecked = true;
        Assert.Null(viewModel.CheckState);

        second.IsChecked = true;
        Assert.True(viewModel.CheckState);
    }

    [Fact]
    public void RetryFailedIsOfferedOnlyWhileAnErrorRowExists()
    {
        var viewModel = NewViewModel();
        var failed = Add(viewModel, "a.usm");
        Add(viewModel, "b.usm").MarkDone(Scratch.File("b.mkv"));
        Assert.False(viewModel.RetryFailedCommand.CanExecute(null));

        failed.MarkFailed("x");
        Assert.True(viewModel.RetryFailedCommand.CanExecute(null));

        viewModel.Items.Remove(failed);
        Assert.False(viewModel.RetryFailedCommand.CanExecute(null));
    }

    [Fact]
    public async Task SkipIsOfferedOnlyDuringAFileRun()
    {
        // An update check has nothing to skip because it is an engine run with no files.
        var viewModel = NewViewModel(FakeEngine("@ping -n 60 127.0.0.1 >nul"));

        var check = viewModel.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsRunning);
        Assert.False(viewModel.SkipCommand.CanExecute(null));

        viewModel.CancelCommand.Execute(null);
        viewModel.CancelCommand.Execute(null);
        await check.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void SummaryCountsDoneMissingKeysAndNoSubtitles()
    {
        var viewModel = NewViewModel();
        Add(viewModel, "a.usm").MarkDone(Scratch.File("a.mkv"));
        Add(viewModel, "b.usm").Key = KeyState.Missing;
        Add(viewModel, "c.usm").Subtitles = [];

        Assert.Equal("3 files · 1 done · 1 missing key · 1 without subtitles", viewModel.Summary);
    }

    [Fact]
    public void ToggleAllChecksEverythingThenNothing()
    {
        var viewModel = NewViewModel();
        Add(viewModel, "a.usm");
        Add(viewModel, "b.usm");

        viewModel.ToggleAllCommand.Execute(null);
        Assert.True(viewModel.Items.AllChecked);

        viewModel.ToggleAllCommand.Execute(null);
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
        var viewModel = NewViewModel(null);
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
    public async Task LoadingAMissingFolderLogsInsteadOfThrowing()
    {
        var viewModel = NewViewModel(null);
        var kept = Add(viewModel, "a.usm");

        await viewModel.LoadSourceAsync(Scratch.File("missing"));

        Assert.Same(kept, Assert.Single(viewModel.Items));
        Assert.Contains(viewModel.Log, line => line.Contains("Could not read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddedFilesAreFilteredToUsmAndDedupedByBareName()
    {
        var viewModel = NewViewModel(null);
        var one = Scratch.File("one");
        var two = Scratch.File("two");

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
    public void AnUnreadableSettingsFileIsReportedInTheLog()
    {
        var path = Scratch.File("settings.json");
        File.WriteAllText(path, "{ not json");

        var viewModel = new MainViewModel(null, Settings.Load(path));

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
