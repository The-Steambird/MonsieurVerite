using System.IO;
using System.IO.Compression;

namespace MonsieurVerite.Tests;

/// <summary>
/// Only the install half is covered, because the network half is two GET calls that are not
/// worth mocking and PickZipUrl covers the parsing.
/// </summary>
public class UpdaterTests : IDisposable
{
    private readonly ScratchFolder scratch = new();

    public UpdaterTests() => Directory.CreateDirectory(App(""));

    public void Dispose()
    {
        scratch.Dispose();
        GC.SuppressFinalize(this);
    }

    private string Zip(params (string Name, string Content)[] entries)
    {
        var path = scratch.File(Path.GetRandomFileName() + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }

        return path;
    }

    private string App(string relative) => scratch.File(Path.Combine("app", relative));

    [Fact]
    public void PicksTheZipAssetAndIgnoresTheRest()
    {
        const string release = """
            {"tag_name":"v1.2","assets":[
              {"name":"charlotte-cli.exe","browser_download_url":"https://x/charlotte-cli.exe"},
              {"name":"charlotte-1.2.zip","browser_download_url":"https://x/bundle.zip"}]}
            """;

        Assert.Equal("https://x/bundle.zip", Updater.PickZipUrl(release));
        Assert.Null(Updater.PickZipUrl("""{"assets":[{"name":"charlotte-cli.exe","browser_download_url":"u"}]}"""));
        Assert.Null(Updater.PickZipUrl("{}"));
    }

    [Fact]
    public void InstallReplacesFilesAndKeepsTheOldOnesAsideUntilTheNextLaunch()
    {
        File.WriteAllText(App("charlotte-gui.exe"), "old gui");
        File.WriteAllText(App("charlotte-cli.exe"), "old engine");
        var zip = Zip(("charlotte-gui.exe", "new gui"), ("charlotte-cli.exe", "new engine"), ("extra.dll", "lib"));

        var written = Updater.Install(zip, App(""));

        Assert.Equal(3, written.Count);
        Assert.Equal("new gui", File.ReadAllText(App("charlotte-gui.exe")));
        Assert.Equal("new engine", File.ReadAllText(App("charlotte-cli.exe")));
        Assert.Equal("lib", File.ReadAllText(App("extra.dll")));
        Assert.Equal("old gui", File.ReadAllText(App("charlotte-gui.exe.old")));
        Assert.Equal("old engine", File.ReadAllText(App("charlotte-cli.exe.old")));

        Updater.DeleteStaleFiles(App(""));
        Assert.False(File.Exists(App("charlotte-gui.exe.old")));
        Assert.False(File.Exists(App("charlotte-cli.exe.old")));
        Assert.True(File.Exists(App("charlotte-gui.exe")));
    }

    [Fact]
    public void AnAppFolderWithATrailingSeparatorIsStillTheAppFolder()
    {
        var zip = Zip(("charlotte-cli.exe", "engine"));

        Updater.Install(zip, App("") + Path.DirectorySeparatorChar);

        Assert.Equal("engine", File.ReadAllText(App("charlotte-cli.exe")));
    }

    [Theory]
    [InlineData('/')]
    [InlineData('\\')]
    public void ASingleWrappingFolderIsStrippedWhicheverSeparatorTheArchiverWrote(char separator)
    {
        var zip = Zip(
            ($"charlotte-1.2{separator}charlotte-cli.exe", "engine"),
            ($"charlotte-1.2{separator}font{separator}ja.ttf", "font"));

        Updater.Install(zip, App(""));

        Assert.Equal("engine", File.ReadAllText(App("charlotte-cli.exe")));
        Assert.Equal("font", File.ReadAllText(App(Path.Combine("font", "ja.ttf"))));
        Assert.False(Directory.Exists(App("charlotte-1.2")));
    }

    [Fact]
    public void AnEntryEscapingTheAppFolderIsRefusedAndNothingChanges()
    {
        File.WriteAllText(App("charlotte-cli.exe"), "old engine");
        var zip = Zip(("charlotte-cli.exe", "new engine"), ("../outside.txt", "escape"));

        Assert.Throws<InvalidDataException>(() => Updater.Install(zip, App("")));

        Assert.Equal("old engine", File.ReadAllText(App("charlotte-cli.exe")));
        Assert.False(File.Exists(App("charlotte-cli.exe.old")));
        Assert.False(File.Exists(scratch.File("outside.txt")));
    }

    [Fact]
    public void AnEmptyArchiveIsAnError()
    {
        Assert.Throws<InvalidDataException>(() => Updater.Install(Zip(), App("")));
    }
}
