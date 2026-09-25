using MonsieurVerite.Engine;

namespace MonsieurVerite.Tests;

/// <summary>
/// There is no schema file, which leaves these and charlotte's utils/reporter/json.py as the
/// only field-level check on the protocol.
/// </summary>
public class EngineEventTests
{
    /// <summary>Carries fields the records do not declare, which must be ignored.</summary>
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
        Assert.IsType(expected, EngineEvent.Parse(line));
    }

    [Fact]
    public void ProbeKeyIsABooleanNotTheKeyItself()
    {
        var probe = Assert.IsType<ProbeEvent>(EngineEvent.Parse(
            """{"type":"probe","file":"a.usm","stem":"a","key":true,"version":"5.3","subtitles":["EN","JP"],"vs_script":"vs/a.py","stream_cipher":true}"""));

        Assert.True(probe.Key);
        Assert.Equal("5.3", probe.Version);
        Assert.Equal(["EN", "JP"], probe.Subtitles);
        Assert.Equal("vs/a.py", probe.VsScript);
        Assert.True(probe.StreamCipher);
    }

    [Fact]
    public void CrackVideoKeyIsUnsignedAndTheCombinedKeyIsIgnored()
    {
        const ulong combined = 0xFFFF_FFFF_FFFF_FFFEUL;
        var crack = Assert.IsType<CrackEvent>(EngineEvent.Parse(
            $$"""{"type":"crack","file":"a.usm","stem":"a","key":{{combined}},"video_key":72057594037927934,"reason":""}"""));

        Assert.Equal(72057594037927934UL, crack.VideoKey);
        Assert.Equal("a", crack.Stem);
    }

    [Fact]
    public void UnknownKindFallsThroughInsteadOfThrowing()
    {
        var unknown = Assert.IsType<UnknownEvent>(EngineEvent.Parse("""{"type":"something_new","whatever":1}"""));

        Assert.Equal("something_new", unknown.Type);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"unterminated\": ")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a bare string\"")]
    [InlineData("{\"no_type_field\":1}")]
    [InlineData("{\"type\":42}")]
    public void MalformedLinesBecomeUnknownRatherThanThrowing(string? line)
    {
        Assert.IsType<UnknownEvent>(EngineEvent.Parse(line));
    }
}
