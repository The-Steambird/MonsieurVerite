using System.IO;

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
        using var client = new EngineClient(engine);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var exit = client.Start(["--probe", "--json", cutscene]);
        await foreach (var evt in client.Events.ReadAllAsync(deadline.Token))
        {
            events.Add(evt);
        }

        Assert.Equal(0, await exit);

        var start = Assert.IsType<SessionStartEvent>(events.FirstOrDefault(e => e is SessionStartEvent));
        Assert.Equal(EngineEvent.ProtocolVersion, start.Protocol);
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

    [Theory]
    [InlineData("q0", true)]
    [InlineData("q1", false)]
    [InlineData("we\"ird\\id\n", true)]
    public void AnswerCommandIsOneJsonObjectTheEngineCanParse(string id, bool value)
    {
        // json.py matches `cmd.get("type") == "answer" and cmd.get("id") == question_id` on one
        // line of stdin, which makes the three keys and the single line the contract.
        var command = EngineClient.AnswerCommand(id, value);

        Assert.DoesNotContain('\n', command);
        using var parsed = System.Text.Json.JsonDocument.Parse(command);
        Assert.Equal("answer", parsed.RootElement.GetProperty("type").GetString());
        Assert.Equal(id, parsed.RootElement.GetProperty("id").GetString());
        Assert.Equal(value, parsed.RootElement.GetProperty("value").GetBoolean());
    }

    [Theory]
    [InlineData("Cs_A.usm")]
    [InlineData("we\"ird\\name\n.usm")]
    public void SkipCommandNamesTheFileTheEngineMustMatch(string file)
    {
        // json.py honors a skip only while `cmd.get("file")` equals the file its last job_start
        // announced.
        var command = EngineClient.SkipCommand(file);

        Assert.DoesNotContain('\n', command);
        using var parsed = System.Text.Json.JsonDocument.Parse(command);
        Assert.Equal("skip", parsed.RootElement.GetProperty("type").GetString());
        Assert.Equal(file, parsed.RootElement.GetProperty("file").GetString());
    }
}
