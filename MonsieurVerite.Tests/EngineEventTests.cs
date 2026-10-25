using MonsieurVerite.Engine;

namespace MonsieurVerite.Tests;

/// <summary>
/// There is no schema file. These tests and charlotte's utils/reporter/json.py are the only
/// field-level check on the protocol.
/// </summary>
public class EngineEventTests
{
    /// <summary>Carries fields the records do not declare, which must be ignored.</summary>
    public static TheoryData<string, Type> KnownKinds() => new()
    {
        { """{"type":"session_start","version":"1.0.0"}""", typeof(SessionStartEvent) },
        { """{"type":"log","level":"info","message":"x"}""", typeof(LogEvent) },
        { """{"type":"stage","stage":"demux","status":"start","total":10,"unit":"chunk"}""", typeof(StageEvent) },
        { """{"type":"progress","stage":"demux","current":5,"total":10}""", typeof(ProgressEvent) },
        { """{"type":"question","id":"q0","prompt":"overwrite?","default":false}""", typeof(QuestionEvent) },
        { """{"type":"job_start","file":"a.usm","stem":"a"}""", typeof(JobStartEvent) },
        { """{"type":"job_skipped","file":"a.usm","reason":"exists"}""", typeof(JobSkippedEvent) },
        { """{"type":"result","file":"a.usm","stem":"a","output":"out.mkv","status":"ok"}""", typeof(ResultEvent) },
        { """{"type":"error","file":"a.usm","message":"boom"}""", typeof(ErrorEvent) },
        { """{"type":"cancelled","file":"a.usm"}""", typeof(CancelledEvent) },
        { """{"type":"probe","file":"a.usm","stem":"a","key":true,"version":"5.3","subtitles":[]}""", typeof(ProbeEvent) },
        { """{"type":"crack","file":"a.usm","stem":"a","key":null,"video_key":null,"reason":"x"}""", typeof(CrackEvent) },
        { """{"type":"crack_summary","recovered":1,"unrecovered":0}""", typeof(CrackSummaryEvent) },
        { """{"type":"update","current":"0.4.0","latest":null,"available":false,"url":null,"notes":null,"download":null,"reason":"x"}""", typeof(UpdateEvent) },
        { """{"type":"update_install","installed":false,"reason":"x"}""", typeof(UpdateInstallEvent) },
        { """{"type":"capture_key","video_key":null,"audio_key":1,"aes_key":"ab","parent_quest_id":null,"known":false,"videos":[]}""", typeof(CaptureKeyEvent) },
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
            """{"type":"probe","file":"a.usm","stem":"a","key":true,"version":"5.3","subtitles":["EN","JP"],"stream_cipher":true}"""));

        Assert.True(probe.Key);
        Assert.Equal("5.3", probe.Version);
        Assert.Equal(["EN", "JP"], probe.Subtitles);
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

    /// <summary>Copied from capture/run.py's capture_key, for an old key and a 7.1 one.</summary>
    [Fact]
    public void CaptureKeysCarryEitherSchemeAndTheirFiles()
    {
        var old = Assert.IsType<CaptureKeyEvent>(EngineEvent.Parse(
            """{"type":"capture_key","video_key":9602627639787478,"audio_key":null,"aes_key":null,"parent_quest_id":"602","known":true,"videos":["Cs_A_Boy","Cs_A_Girl"]}"""));
        var stream = Assert.IsType<CaptureKeyEvent>(EngineEvent.Parse(
            """{"type":"capture_key","video_key":null,"audio_key":72057594037927935,"aes_key":"f9e9e1c5cf3a68deffa0a4d3df665836","parent_quest_id":null,"known":false,"videos":[]}"""));

        Assert.Equal(9602627639787478UL, old.VideoKey);
        Assert.Null(old.AudioKey);
        Assert.Equal("602", old.ParentQuestId);
        Assert.Equal(["Cs_A_Boy", "Cs_A_Girl"], old.Videos);
        Assert.True(old.Known);

        Assert.Null(stream.VideoKey);
        Assert.Equal(72057594037927935UL, stream.AudioKey);
        Assert.Equal("f9e9e1c5cf3a68deffa0a4d3df665836", stream.AesKey);
        Assert.Null(stream.ParentQuestId);
    }

    [Fact]
    public void UnknownKindFallsThroughInsteadOfThrowing()
    {
        var unknown = Assert.IsType<UnknownEvent>(EngineEvent.Parse("""{"type":"something_new","whatever":1}"""));

        Assert.Equal("something_new", unknown.Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"no_type_field\":1}")]
    [InlineData("{\"type\":42}")]
    public void MalformedLinesBecomeUnknownRatherThanThrowing(string line)
    {
        Assert.IsType<UnknownEvent>(EngineEvent.Parse(line));
    }
}
