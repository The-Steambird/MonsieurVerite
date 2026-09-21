using System.Collections.Concurrent;
using System.IO;

using MonsieurVerite.Engine;

using Xunit.Abstractions;

namespace MonsieurVerite.Tests;

/// <summary>
/// Drives a real charlotte run end to end. The hand-written JSON in
/// <see cref="EngineEventTests"/> can drift from what the engine actually emits, and only a live
/// run catches that, which is why this exists despite depending on the environment.
/// <para>
/// Skips itself when the sibling charlotte checkout or its test cutscene is absent. The suite
/// then still passes on a machine that has only this repo.
/// </para>
/// </summary>
public class EngineClientTests(ITestOutputHelper output)
{
    private const string TestCutscene = "USM/6.3/Cs_NodKrai_AQ60161901_BSHMO_Boy.usm";

    [Fact]
    public async Task ProbeRunParsesCleanlyAgainstTheRealEngine()
    {
        // A skipped run is otherwise indistinguishable from a passing one, which would quietly
        // turn the drift guard into a no-op. The lines written here show up under
        // `dotnet test -v n`. Nothing sits beside the test binary, and ResolveEngine only ever
        // finds the sibling checkout.
        if (Settings.ResolveEngine() is not { } engine)
        {
            output.WriteLine("SKIPPED: no sibling charlotte checkout with main.py found.");
            return;
        }

        var cutscene = Path.Combine(engine.WorkingDirectory, TestCutscene.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(cutscene))
        {
            output.WriteLine($"SKIPPED: test cutscene missing at {cutscene}");
            return;
        }

        output.WriteLine($"Running against {engine.Description}");

        var events = new ConcurrentQueue<EngineEvent>();

        using var client = new EngineClient(engine);
        client.EventReceived += events.Enqueue;

        // A hard deadline rather than a cancellation token, because the token would only ask the
        // engine to stop politely and a hung engine would hang the test forever instead of
        // failing it. On timeout the client's disposal kills the process through its job object.
        var exitCode = await client.RunAsync(["--probe", "--json", cutscene]).WaitAsync(TimeSpan.FromMinutes(3));

        Assert.Equal(0, exitCode);

        // The engine announces itself first. A mismatch here means the protocol moved under us.
        var start = Assert.IsType<SessionStartEvent>(events.FirstOrDefault(e => e is SessionStartEvent));
        Assert.Equal(EngineEvent.ProtocolVersion, start.Protocol);

        var probe = Assert.IsType<ProbeEvent>(events.FirstOrDefault(e => e is ProbeEvent));
        Assert.Equal(Path.GetFileName(cutscene), probe.File);

        // This is the drift guard. Anything the engine emitted that this client could not name
        // fails here.
        var unrecognised = events.OfType<UnknownEvent>()
            .Where(e => !string.IsNullOrEmpty(e.Type))
            .Select(e => e.Type)
            .Distinct()
            .ToList();
        Assert.True(
            unrecognised.Count == 0,
            $"Engine emitted event kinds this client does not handle: {string.Join(", ", unrecognised)}");

        // Malformed lines would land as UnknownEvent with no Type at all.
        Assert.DoesNotContain(events, e => e is UnknownEvent { Type: "" });
    }

    [Theory]
    [InlineData("q0", true)]
    [InlineData("q1", false)]
    [InlineData("we\"ird\\id\n", true)]
    public void AnswerCommandIsOneJsonObjectTheEngineCanParse(string id, bool value)
    {
        // json.py matches `cmd.get("type") == "answer" and cmd.get("id") == question_id`, which
        // makes the three keys and their spelling the contract. Ids are engine-generated ("q0",
        // "q1"…) today, but the encoding must not depend on that. The command is also one line,
        // because the engine reads stdin line by line.
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
        // announced, so the name goes over verbatim, on one line.
        var command = EngineClient.SkipCommand(file);

        Assert.DoesNotContain('\n', command);
        using var parsed = System.Text.Json.JsonDocument.Parse(command);
        Assert.Equal("skip", parsed.RootElement.GetProperty("type").GetString());
        Assert.Equal(file, parsed.RootElement.GetProperty("file").GetString());
    }
}
