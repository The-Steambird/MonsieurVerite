using MonsieurVerite.Engine;
using MonsieurVerite.ViewModels;

namespace MonsieurVerite.Tests;

public class KeyCaptureTests
{
    private static readonly CaptureKeyEvent Old = new()
    {
        Type = "capture_key", VideoKey = 9602627639787478, ParentQuestId = "602", Known = true,
        Videos = ["Cs_A_Boy", "Cs_A_Girl"],
    };

    private static readonly CaptureKeyEvent Unlisted = new()
    {
        Type = "capture_key", AudioKey = 5281416956962385,
        AesKey = "f9e9e1c5cf3a68deffa0a4d3df665836", ParentQuestId = "7099",
    };

    private static KeyCapture NewCapture(bool newKeysOnly) =>
        new("charlotte-cli.exe", new Settings { CaptureNewKeysOnly = newKeysOnly });

    /// <summary>The text is what the user pastes into keys.json, which fixes its field order.</summary>
    [Fact]
    public void EachSchemePrintsAsTheKeysJsonGroupItBelongsIn()
    {
        Assert.Equal(
            """
            {
              "videoKey": 9602627639787478,
              "parentQuestId": "602",
              "videos": [
                "Cs_A_Boy",
                "Cs_A_Girl"
              ]
            }
            """.ReplaceLineEndings("\n"),
            KeyCapture.Group(Old));
        Assert.Equal(
            """
            {
              "audioKey": 5281416956962385,
              "aesKey": "f9e9e1c5cf3a68deffa0a4d3df665836",
              "parentQuestId": "7099",
              "videos": []
            }
            """.ReplaceLineEndings("\n"),
            KeyCapture.Group(Unlisted));
    }

    /// <summary>
    /// Every key is kept, which is what lets the filter change after a capture without another.
    /// </summary>
    [Fact]
    public void TheFilterHidesKnownKeysAndCanBeFlippedAfterwards()
    {
        var capture = NewCapture(newKeysOnly: true);
        capture.Apply(Old);
        capture.Apply(Unlisted);

        Assert.Equal(KeyCapture.Group(Unlisted), capture.Text);

        capture.NewKeysOnly = false;
        Assert.Equal($"{KeyCapture.Group(Old)},\n{KeyCapture.Group(Unlisted)}", capture.Text);
    }

    /// <summary>A login after "log in again" has to clear that warning, as the user did what it said.</summary>
    [Fact]
    public void TheStatusIsTheLatestLineAndALaterOneClearsAWarning()
    {
        var capture = NewCapture(newKeysOnly: true);
        capture.Apply(new LogEvent { Type = "log", Level = "warning", Message = "Log in again." });
        Assert.Equal(new LogLine("Log in again.", IsWarning: true), capture.Status);

        capture.Apply(new LogEvent { Type = "log", Level = "info", Message = "Login seen." });
        Assert.Equal(new LogLine("Login seen."), capture.Status);
    }

    /// <summary>charlotte sends an error only as an event, without a log line of the same text.</summary>
    [Fact]
    public void LogLinesAndErrorsReachTheStatusAndTheMainLog()
    {
        var log = new List<LogLine>();
        var capture = NewCapture(newKeysOnly: true);
        capture.AppendLog = log.Add;
        capture.Apply(new LogEvent { Type = "log", Level = "info", Message = "Login seen." });
        capture.Apply(new ErrorEvent { Type = "error", Message = "boom" });

        Assert.Equal([new LogLine("Login seen."), new LogLine("boom", IsWarning: true)], log);
        Assert.Equal(new LogLine("boom", IsWarning: true), capture.Status);
    }

    /// <summary>charlotte's tests/test_main.py pins the same arguments from the engine's side.</summary>
    [Fact]
    public void TheCaptureCommandIsTheOneTheEngineParses()
    {
        Assert.Equal(
            ["--capture", "--json", "--pipe", "charlotte-capture-1", "--lang", "ja-JP"],
            CaptureClient.Arguments("charlotte-capture-1", "ja-JP"));
    }
}
