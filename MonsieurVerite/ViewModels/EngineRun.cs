using System.ComponentModel;
using System.IO;
using MonsieurVerite.Engine;

namespace MonsieurVerite.ViewModels;

/// <summary>Drives one engine process and is the only place its events are read.</summary>
internal sealed class EngineRun(MainViewModel owner, string enginePath, int jobCount)
{
    private EngineClient? client;
    private QueueItem? current;
    private bool forceStopped;

    public UpdateEvent? Update { get; private set; }

    public UpdateInstallEvent? Install { get; private set; }

    // jobCount is how many job_start events the run will open, which a probe and an update check
    // never do. Zero disables Skip.
    public bool OpensJobs => jobCount > 0;

    public async Task RunAsync(
        IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken)
    {
        await using var engine = new EngineClient(enginePath);
        using var registration = cancellationToken.Register(engine.SendCancel);
        client = engine;

        string? failure = null;
        try
        {
            var exit = engine.Start(["--json", .. arguments], workingDirectory);
            // The token only asks the engine to stop, and its events are read until it has.
            await foreach (var evt in engine.Events.ReadAllAsync(CancellationToken.None)
                               .ConfigureAwait(true))
            {
                Apply(evt);
            }

            var exitCode = await exit.ConfigureAwait(true);
            if (forceStopped)
            {
                owner.AppendLog(Strings.ENGINE_STOPPED_LOG);
            }
            else if (exitCode != 0)
            {
                failure = Strings.ENGINE_EXITED_DETAIL(exitCode);
                owner.AppendLog(Strings.ENGINE_EXITED_LOG(exitCode), warning: true);

                // The engine exits 1 after a batch in which any file failed, and those rows
                // already carry the error events. The stderr tail is for a run that never opened
                // a job or died inside one.
                if (current is null or { Status: ItemStatus.Running })
                {
                    foreach (var line in engine.StandardErrorTail.Where(line => line.Length > 0))
                    {
                        owner.AppendLog($"  {line}", warning: true);
                    }
                }
            }
        }
        catch (Win32Exception e)
        {
            failure = Strings.ENGINE_NOT_STARTED_DETAIL;
            owner.AppendLog(Strings.ENGINE_START_FAILED_LOG(enginePath, e.Message), warning: true);
        }
        catch (Exception)
        {
            // A bug in Apply ends the run, and disposing the client kills the engine and waits
            // for it to exit. The unhandled-error dialog shows the exception.
            failure = Strings.UNEXPECTED_ERROR_DETAIL;
            throw;
        }
        finally
        {
            client = null;
            Settle(failure);
        }
    }

    // The skip names the file so the engine can drop it if that job already finished by the
    // time the command arrives, instead of skipping whichever file started next.
    public void Skip()
    {
        if (current is { Status: ItemStatus.Running } item)
        {
            client?.SendSkip(item.FileName);
            item.Detail = Strings.SKIPPING_DETAIL;
        }
    }

    /// <summary>Ends the engine and everything it started, or returns false if it already has.</summary>
    public bool ForceStop()
    {
        if (client is not { } engine || forceStopped)
        {
            return false;
        }

        forceStopped = true;
        engine.Kill();
        return true;
    }

    private void Settle(string? failure)
    {
        foreach (var item in owner.Items)
        {
            if (item.Status is not (ItemStatus.Queued or ItemStatus.Running))
            {
                continue;
            }

            if (forceStopped)
            {
                item.MarkCancelled();
            }
            else if (item.Status == ItemStatus.Running && failure is not null)
            {
                item.MarkFailed(failure);
            }
            else
            {
                item.MarkPending();
            }
        }
    }

    internal void Apply(EngineEvent evt)
    {
        var items = owner.Items;
        switch (evt)
        {
            case SessionStartEvent session:
                owner.EngineVersion = session.Version;
                if (session.Version != App.Version)
                {
                    owner.AppendLog(Strings.VERSION_MISMATCH_LOG, warning: true);
                }

                break;

            case LogEvent log:
                owner.AppendLog(LogLine.From(log));
                break;

            case JobStartEvent job:
                // --crack never closes a job, and the next job_start is the only sign that the
                // previous file is done.
                if (current is { Status: ItemStatus.Running } previous)
                {
                    previous.MarkPending();
                }

                current = items.Find(job.File);
                current?.MarkRunning();
                break;

            case StageEvent stage when stage.Status == "start":
                current?.EnterStage(StageName(stage.Stage));
                break;

            case ProgressEvent progress when progress.Total > 0 && current is not null:
                current.Progress = progress.Current * 100.0 / progress.Total;

                break;

            case ResultEvent result:
                items.Find(result.File)?.MarkDone(result.Output);
                break;

            case ErrorEvent error:
                items.Find(error.File)?.MarkFailed(error.Message);
                owner.AppendLog(error.File.Length > 0 ? $"{error.File}: {error.Message}" : error.Message,
                    warning: true);
                break;

            case JobSkippedEvent skipped:
                items.Find(skipped.File)?.MarkSkipped(SkipReason(skipped.Reason));
                break;

            case CancelledEvent cancelled:
                items.Find(cancelled.File)?.MarkCancelled();
                foreach (var item in items.Where(item => item.Status == ItemStatus.Queued))
                {
                    item.MarkCancelled();
                }

                break;

            case ProbeEvent probe:
                items.Find(probe.File)?.ApplyProbe(probe);
                break;

            case CrackEvent crack when items.Find(crack.File) is { } cracked:
                cracked.ApplyCrack(crack.VideoKey);
                if (crack.VideoKey is { } videoKey)
                {
                    owner.AppendLog($"{crack.File}: videoKey={videoKey}");
                    RecordRecoveredKey(crack.Stem, videoKey);
                }
                else
                {
                    owner.AppendLog(Strings.KEY_NOT_RECOVERABLE_LOG(crack.File, crack.Reason));
                }

                break;

            case CrackSummaryEvent summary:
                owner.AppendLog(Strings.CRACK_SUMMARY_LOG(summary.Recovered, summary.Unrecovered));
                break;

            case UpdateEvent update:
                Update = update;
                break;

            case UpdateInstallEvent install:
                Install = install;
                break;

            case QuestionEvent question:
                var answer = owner.ShowMessage?.Invoke(Prompt(question)) ?? question.Default;
                client?.SendAnswer(question.Id, answer);
                break;

            case UnknownEvent unknown when unknown.Type.Length > 0:
                owner.AppendLog(Strings.UNKNOWN_EVENT_LOG(unknown.Type));
                break;
        }
    }

    // An update run asks only whether to install the release it just reported, which is why that
    // question gets the release notes instead of a plain yes or no.
    private Message Prompt(QuestionEvent question)
    {
        if (Update is { Available: true } update)
        {
            var title = Strings.UPDATE_AVAILABLE_TITLE(update.Latest);
            var text = Strings.UPDATE_AVAILABLE_MESSAGE(update.Current, update.Latest);
            return new Message(title, text, Strings.UPDATE, Strings.CANCEL,
                Notes: update.Notes, NotesUrl: update.Url);
        }

        return new Message(Strings.QUESTION_TITLE, question.Prompt, Strings.YES, Strings.NO);
    }

    private static string StageName(string stage) => stage switch
    {
        "demux" => Strings.STAGE_DEMUX,
        "crack" => Strings.STAGE_CRACK,
        "ffmpeg" => Strings.STAGE_FFMPEG,
        "subtitles" => Strings.STAGE_SUBTITLES,
        "download" => Strings.STAGE_DOWNLOAD,
        _ => stage,
    };

    private static string SkipReason(string reason) => reason switch
    {
        "exists" => Strings.SKIP_EXISTS_DETAIL,
        "no_key" => Strings.SKIP_NO_KEY_DETAIL,
        "requested" => Strings.SKIP_REQUESTED_DETAIL,
        _ => reason,
    };

    private void RecordRecoveredKey(string stem, ulong videoKey)
    {
        var path = owner.RecoveredKeysPath;
        try
        {
            if (RecoveredKeys.Add(path, stem, videoKey) is { } setAside)
            {
                owner.AppendLog(Strings.RECOVERED_KEYS_SET_ASIDE_LOG(path, setAside), warning: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            owner.AppendLog(Strings.RECOVERED_KEYS_UNWRITTEN_LOG(path, e.Message), warning: true);
        }
    }
}
