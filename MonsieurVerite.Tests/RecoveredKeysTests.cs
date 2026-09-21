using System.IO;
using System.Text.Json.Nodes;

namespace MonsieurVerite.Tests;

/// <summary>
/// The recovered-keys file has keys.json's shape, which lets its entries paste straight across.
/// These pin that shape and the merge rules.
/// </summary>
public class RecoveredKeysTests : IDisposable
{
    private readonly string path = Path.Combine(
        Path.GetTempPath(), "MonsieurVerite.Tests", Path.GetRandomFileName(), "recovered_keys.json");

    public void Dispose()
    {
        if (Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private JsonArray List() => (JsonArray)JsonNode.Parse(File.ReadAllText(path))!["list"]!;

    [Fact]
    public void FirstKeyCreatesTheFileInKeysJsonShape()
    {
        RecoveredKeys.Add(path, "Cs_Boy", 42);

        var group = Assert.Single(List());
        Assert.Equal(42UL, group!["videoKey"]!.GetValue<ulong>());
        Assert.Equal(["Cs_Boy"], ((JsonArray)group["videos"]!).Select(v => v!.GetValue<string>()));
    }

    [Fact]
    public void FilesSharingAKeyShareAGroup()
    {
        RecoveredKeys.Add(path, "Cs_Boy", 42);
        RecoveredKeys.Add(path, "Cs_Girl", 42);
        RecoveredKeys.Add(path, "Other", 7);

        var list = List();
        Assert.Equal(2, list.Count);
        Assert.Equal(["Cs_Boy", "Cs_Girl"], ((JsonArray)list[0]!["videos"]!).Select(v => v!.GetValue<string>()));
        Assert.Equal(7UL, list[1]!["videoKey"]!.GetValue<ulong>());
    }

    [Fact]
    public void RecordingAStemAgainMovesItRatherThanDuplicating()
    {
        RecoveredKeys.Add(path, "Cs_Boy", 42);
        RecoveredKeys.Add(path, "Cs_Boy", 42);
        RecoveredKeys.Add(path, "Cs_Boy", 99);

        // The old group emptied out, leaving only the new key holding the stem once.
        var group = Assert.Single(List());
        Assert.Equal(99UL, group!["videoKey"]!.GetValue<ulong>());
        Assert.Equal(["Cs_Boy"], ((JsonArray)group["videos"]!).Select(v => v!.GetValue<string>()));
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
        // Somebody pasted a key in as a string. It is not our group, and the new key still lands.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"list":[{"videoKey":"42","videos":["Cs_Other"]}]}""");

        RecoveredKeys.Add(path, "Cs_Boy", 42);

        var list = List();
        Assert.Equal(2, list.Count);
        Assert.Equal("42", list[0]!["videoKey"]!.GetValue<string>());
        Assert.Equal(42UL, list[1]!["videoKey"]!.GetValue<ulong>());
    }

    [Fact]
    public void ACorruptFileIsReplacedNotFatal()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        RecoveredKeys.Add(path, "Cs_Boy", 42);

        Assert.Single(List());
    }
}
