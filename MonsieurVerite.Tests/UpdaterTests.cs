using System.IO;
using System.IO.Compression;

namespace MonsieurVerite.Tests;

/// <summary>
/// These cover the parsing and the file work around the GET calls, which are not worth mocking.
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
              {"name":"charlotte-cli.exe","browser_download_url":"https://x/charlotte-cli.exe","digest":"sha256:00"},
              {"name":"charlotte-1.2.zip","browser_download_url":"https://x/bundle.zip","digest":"sha256:ab12"}]}
            """;

        Assert.Equal(new Updater.ReleaseAsset("https://x/bundle.zip", "ab12"), Updater.PickZip(release));
        Assert.Null(Updater.PickZip("""{"assets":[{"name":"charlotte-cli.exe","browser_download_url":"u"}]}"""));
        Assert.Null(Updater.PickZip("{}"));
    }

    [Theory]
    [InlineData("""{"name":"a.zip","browser_download_url":"u"}""")]
    [InlineData("""{"name":"a.zip","browser_download_url":"u","digest":null}""")]
    [InlineData("""{"name":"a.zip","browser_download_url":"u","digest":"md5:ab12"}""")]
    public void AZipWithoutASha256DigestHasNothingToVerifyAgainst(string asset)
    {
        Assert.Equal(new Updater.ReleaseAsset("u", null), Updater.PickZip($$"""{"assets":[{{asset}}]}"""));
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
    public void TheCharlotteFolderTheBundleIsWrappedInIsDropped()
    {
        File.WriteAllText(App("charlotte-cli.exe"), "old engine");
        var zip = Zip(
            ("charlotte/", ""),
            ("charlotte/charlotte-cli.exe", "new engine"),
            ("charlotte/font/ja-jp.ttf", "font"));

        Updater.Install(zip, App(""));

        Assert.Equal("new engine", File.ReadAllText(App("charlotte-cli.exe")));
        Assert.True(File.Exists(App(Path.Combine("font", "ja-jp.ttf"))));
        Assert.False(Directory.Exists(App("charlotte")));
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
    public void AFolderEntryIsNotAFileWhicheverSeparatorTheArchiverWrote(char separator)
    {
        var zip = Zip(
            ("charlotte-cli.exe", "engine"),
            ($"font{separator}", ""),
            ($"font{separator}ja.ttf", "font"));

        Updater.Install(zip, App(""));

        Assert.Equal("engine", File.ReadAllText(App("charlotte-cli.exe")));
        Assert.Equal("font", File.ReadAllText(App(Path.Combine("font", "ja.ttf"))));
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
    public void InstallDeletesTheDownloadedTranslations()
    {
        Translations.Save(App(""), "de-DE", """{ "OPEN_FOLDER": "Ordner öffnen" }""");
        Translations.Save(App(""), "th-TH", """{ "OPEN_FOLDER": "เปิดโฟลเดอร์" }""");

        Updater.Install(Zip(("charlotte-gui.exe", "new gui")), App(""));

        Assert.False(Directory.Exists(App("locales")));
    }

    [Fact]
    public void AFailedInstallKeepsTheDownloadedTranslations()
    {
        Translations.Save(App(""), "de-DE", """{ "OPEN_FOLDER": "Ordner öffnen" }""");
        var zip = Zip(("charlotte-gui.exe", "new gui"), ("../outside.txt", "escape"));

        Assert.Throws<InvalidDataException>(() => Updater.Install(zip, App("")));

        Assert.True(File.Exists(Translations.FilePath(App(""), "de-DE")));
    }
}
