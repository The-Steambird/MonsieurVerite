using MonsieurVerite.Engine;

namespace MonsieurVerite.Tests;

/// <summary>
/// Pins the wire format. There is no schema file, and charlotte's utils/reporter/json.py and
/// Engine/EngineEvent.cs together are the protocol spec, which leaves these tests as the only
/// thing keeping the two halves honest at the field level.
/// </summary>
public class EngineEventTests
{
    /// <summary>
    /// Every kind the engine can emit, with the full field set it sends. That includes fields the
    /// records do not declare, which proves they are ignored rather than fatal. A new kind must
    /// be added here and to the parser.
    /// </summary>
    public static TheoryData<string, Type> KnownKinds() => new()
    {
        { """{"type":"session_start","protocol":1,"version":"1.0.0"}""", typeof(SessionStartEvent) },
        { """{"type":"log","level":"info","message":"x"}""", typeof(LogEvent) },
        { """{"type":"stage","stage":"demux","status":"start","total":10,"unit":"chunk"}""", typeof(StageEvent) },
        { """{"type":"progress","stage":"demux","current":5,"total":10}""", typeof(ProgressEvent) },
        { """{"type":"question","id":"q0","prompt":"overwrite?","default":false}""", typeof(QuestionEvent) },
        { """{"type":"job_start","file":"a.usm","stem":"a"}""", typeof(JobStartEvent) },
        { """{"type":"job_skipped","file":"a.usm","reason":"exists"}""", typeof(JobSkippedEvent) },
        { """{"type":"result","file":"a.usm","stem":"a","output":"out.mkv","status":"ok"}""", typeof(ResultEvent) },
        { """{"type":"error","file":"a.usm","message":"boom"}""", typeof(ErrorEvent) },
        { """{"type":"cancelled","file":"a.usm"}""", typeof(CancelledEvent) },
        { """{"type":"probe","file":"a.usm","stem":"a","key":true,"version":"5.3","subtitles":[],"vs_script":null}""", typeof(ProbeEvent) },
        { """{"type":"crack","file":"a.usm","stem":"a","key":null,"video_key":null,"reason":"x"}""", typeof(CrackEvent) },
        { """{"type":"crack_summary","recovered":1,"unrecovered":0}""", typeof(CrackSummaryEvent) },
        { """{"type":"update","current":"0.4.0","latest":null,"available":false,"url":null,"notes":null,"download":null,"reason":"x"}""", typeof(UpdateEvent) },
    };

    [Theory]
    [MemberData(nameof(KnownKinds))]
    public void ParsesEveryKnownKindIntoItsOwnRecord(string line, Type expected)
    {
        var parsed = EngineEvent.Parse(line);
        Assert.IsType(expected, parsed);
    }

    [Fact]
    public void ProbeKeyIsABooleanNotTheKeyItself()
    {
        // probe_usm sends `find_key_from_file(...) is not None`, which means this field answers
        // "does keys.json have an entry" and never "what is the key".
        var probe = Assert.IsType<ProbeEvent>(EngineEvent.Parse(
            """{"type":"probe","file":"a.usm","stem":"a","key":true,"version":"5.3","subtitles":["EN","JP"],"vs_script":"vs/a.py"}"""));

        Assert.True(probe.Key);
        Assert.Equal("5.3", probe.Version);
        Assert.Equal(["EN", "JP"], probe.Subtitles);
        Assert.Equal("vs/a.py", probe.VsScript);
    }

    [Fact]
    public void CrackVideoKeyIsUnsignedAndTheCombinedKeyIsIgnored()
    {
        // The combined key uses the full 64 bits and is not declared on the record. It must be
        // skipped rather than choke the parse. The videoKey is 56 bits, which fits ulong and
        // never long.
        const ulong combined = 0xFFFF_FFFF_FFFF_FFFEUL;
        var crack = Assert.IsType<CrackEvent>(EngineEvent.Parse(
            $$"""{"type":"crack","file":"a.usm","stem":"a","key":{{combined}},"video_key":72057594037927934,"reason":""}"""));

        Assert.Equal(72057594037927934UL, crack.VideoKey);
        Assert.Equal("a", crack.Stem);
    }

    [Fact]
    public void UnknownKindFallsThroughInsteadOfThrowing()
    {
        // Event kinds are additive because a newer engine must not break an older GUI.
        var parsed = EngineEvent.Parse("""{"type":"something_new","whatever":1}""");

        var unknown = Assert.IsType<UnknownEvent>(parsed);
        Assert.Equal("something_new", unknown.Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"unterminated\": ")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a bare string\"")]
    [InlineData("{\"no_type_field\":1}")]
    [InlineData("{\"type\":42}")]
    public void MalformedLinesBecomeUnknownRatherThanThrowing(string line)
    {
        Assert.IsType<UnknownEvent>(EngineEvent.Parse(line));
    }

    [Fact]
    public void NullLineIsTolerated()
    {
        Assert.IsType<UnknownEvent>(EngineEvent.Parse(null));
    }

    [Fact]
    public void ProtocolVersionMatchesTheEngine()
    {
        // Bumped only on incompatible changes. Additive kinds keep it at 1.
        Assert.Equal(1, EngineEvent.ProtocolVersion);
    }
}
