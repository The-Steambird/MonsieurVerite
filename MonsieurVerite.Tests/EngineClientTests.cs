using System.IO;
using System.Text.Json;
using MonsieurVerite.Engine;
using Xunit.Abstractions;

namespace MonsieurVerite.Tests;

public class EngineClientTests(ITestOutputHelper output)
{
    private const string TestCutscene = "USM/6.3/Cs_NodKrai_AQ60161901_BSHMO_Boy.usm";

    /// <summary>
    /// The hand-written JSON in <see cref="EngineEventTests"/> can drift from what the engine
    /// actually emits, and only a live run catches that.
    /// </summary>
    [Fact]
    public async Task ProbeRunParsesCleanlyAgainstTheRealEngine()
    {
        if (Charlotte.LiveEngine(output) is not { } engine)
        {
            return;
        }

        var cutscene = Charlotte.Checkout is { } checkout
            ? Path.Combine(checkout, TestCutscene.Replace('/', Path.DirectorySeparatorChar))
            : null;
        if (!File.Exists(cutscene))
        {
            output.WriteLine($"SKIPPED: test cutscene missing at {cutscene ?? "../charlotte"}");
            return;
        }

        var events = new List<EngineEvent>();
        await using var client = new EngineClient(engine);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var exit = client.Start(["--probe", "--json", cutscene]);
        await foreach (var evt in client.Events.ReadAllAsync(deadline.Token))
        {
            events.Add(evt);
        }

        Assert.Equal(0, await exit);

        var start = Assert.IsType<SessionStartEvent>(events.FirstOrDefault(e => e is SessionStartEvent));
        Assert.Matches(@"^\d+\.\d+\.\d+", start.Version);

        var probe = Assert.IsType<ProbeEvent>(events.FirstOrDefault(e => e is ProbeEvent));
        Assert.Equal(Path.GetFileName(cutscene), probe.File);

        var unrecognised = events.OfType<UnknownEvent>()
            .Where(e => !string.IsNullOrEmpty(e.Type))
            .Select(e => e.Type)
            .Distinct()
            .ToList();
        Assert.True(
            unrecognised.Count == 0,
            $"Engine emitted event kinds this client does not handle: {string.Join(", ", unrecognised)}");

        // A malformed line lands as an UnknownEvent with no Type at all.
        Assert.DoesNotContain(events, e => e is UnknownEvent { Type: "" });
    }

    [Fact]
    public async Task TheEngineIsToldTheUiLanguage()
    {
        using var scratch = new ScratchFolder();
        var script = scratch.File("engine.cmd");
        File.WriteAllText(script, """@echo {"type":"log","level":"info","message":"%CHARLOTTE_LANG%"}""");
        await using var client = new EngineClient(script);

        var exit = client.Start([]);
        var events = await client.Events.ReadAllAsync().ToListAsync();
        await exit;

        Assert.Equal(Strings.Language, Assert.IsType<LogEvent>(Assert.Single(events)).Message);
    }

    [Theory]
    [InlineData("q0", true)]
    [InlineData("q1", false)]
    public void AnswerCommandIsOneJsonObjectTheEngineCanParse(string id, bool value)
    {
        // json.py matches `cmd.get("type") == "answer" and cmd.get("id") == question_id` on one
        // line of stdin. The three keys and the single line are the contract.
        var command = EngineClient.AnswerCommand(id, value);

        Assert.DoesNotContain('\n', command);
        using var parsed = JsonDocument.Parse(command);
        Assert.Equal("answer", parsed.RootElement.GetProperty("type").GetString());
        Assert.Equal(id, parsed.RootElement.GetProperty("id").GetString());
        Assert.Equal(value, parsed.RootElement.GetProperty("value").GetBoolean());
    }

    [Fact]
    public void SkipCommandNamesTheFileTheEngineMustMatch()
    {
        // json.py honors a skip only while `cmd.get("file")` equals the file its last job_start
        // announced.
        var command = EngineClient.SkipCommand("Cs_A.usm");

        Assert.DoesNotContain('\n', command);
        using var parsed = JsonDocument.Parse(command);
        Assert.Equal("skip", parsed.RootElement.GetProperty("type").GetString());
        Assert.Equal("Cs_A.usm", parsed.RootElement.GetProperty("file").GetString());
    }
}
