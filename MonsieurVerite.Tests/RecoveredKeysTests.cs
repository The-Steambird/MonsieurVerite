using System.IO;
using System.Text.Json.Nodes;

namespace MonsieurVerite.Tests;

public class RecoveredKeysTests : IDisposable
{
    private readonly ScratchFolder scratch = new();
    private readonly string path;

    public RecoveredKeysTests() => path = scratch.File("recovered_keys.json");

    public void Dispose()
    {
        scratch.Dispose();
        GC.SuppressFinalize(this);
    }

    private JsonArray List() => (JsonArray)JsonNode.Parse(File.ReadAllText(path))!["list"]!;

    private static IEnumerable<string> Videos(JsonNode? group) =>
        ((JsonArray)group!["videos"]!).Select(v => v!.GetValue<string>());

    [Fact]
    public void FirstKeyCreatesTheFileInKeysJsonShape()
    {
        RecoveredKeys.Add(path, "Cs_Boy", 42);

        var group = Assert.Single(List());
        Assert.Equal(42UL, group!["videoKey"]!.GetValue<ulong>());
        Assert.Equal(["Cs_Boy"], Videos(group));
    }

    [Fact]
    public void FilesSharingAKeyShareAGroup()
    {
        RecoveredKeys.Add(path, "Cs_Boy", 42);
        RecoveredKeys.Add(path, "Cs_Girl", 42);
        RecoveredKeys.Add(path, "Other", 7);

        var list = List();
        Assert.Equal(2, list.Count);
        Assert.Equal(["Cs_Boy", "Cs_Girl"], Videos(list[0]));
        Assert.Equal(7UL, list[1]!["videoKey"]!.GetValue<ulong>());
    }

    [Fact]
    public void RecordingAStemAgainMovesItRatherThanDuplicating()
    {
        RecoveredKeys.Add(path, "Cs_Boy", 42);
        RecoveredKeys.Add(path, "Cs_Boy", 42);
        RecoveredKeys.Add(path, "Cs_Boy", 99);

        var group = Assert.Single(List());
        Assert.Equal(99UL, group!["videoKey"]!.GetValue<ulong>());
        Assert.Equal(["Cs_Boy"], Videos(group));
    }

    [Fact]
    public void KeysUseTheFull56BitRange()
    {
        const ulong big = 0xFF_FFFF_FFFF_FFFFUL;
        RecoveredKeys.Add(path, "Cs_Boy", big);

        Assert.Equal(big, List()[0]!["videoKey"]!.GetValue<ulong>());
    }

    [Fact]
    public void AHandEditedGroupWithAnOddKeyIsLeftAloneNotFatal()
    {
        File.WriteAllText(path, """{"list":[{"videoKey":"42","videos":["Cs_Other"]}]}""");

        RecoveredKeys.Add(path, "Cs_Boy", 42);

        var list = List();
        Assert.Equal(2, list.Count);
        Assert.Equal("42", list[0]!["videoKey"]!.GetValue<string>());
        Assert.Equal(42UL, list[1]!["videoKey"]!.GetValue<ulong>());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    public void AnUnreadableFileIsSetAsideNotOverwritten(string corrupt)
    {
        File.WriteAllText(path, corrupt);

        var setAside = RecoveredKeys.Add(path, "Cs_Boy", 42);

        Assert.NotNull(setAside);
        Assert.Equal(corrupt, File.ReadAllText(setAside));
        Assert.Single(List());
        Assert.Null(RecoveredKeys.Add(path, "Cs_Girl", 7));
    }
}
