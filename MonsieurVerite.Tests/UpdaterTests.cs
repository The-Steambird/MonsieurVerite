using System.IO;
using System.IO.Compression;

namespace MonsieurVerite.Tests;

/// <summary>
/// The install half of updating, against a scratch folder. The network half is two GET calls
/// that are not worth mocking; PickZipUrl covers the parsing.
/// </summary>
public class UpdaterTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "MonsieurVerite.Tests", Path.GetRandomFileName());

    public UpdaterTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Zip(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(folder, Path.GetRandomFileName() + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }

        return path;
    }

    private string App(string relative) => Path.Combine(folder, "app", relative);

    [Fact]
    public void PicksTheZipAssetAndIgnoresTheRest()
    {
        const string release = """
            {"tag_name":"v1.2","assets":[
              {"name":"charlotte.exe","browser_download_url":"https://x/charlotte.exe"},
              {"name":"MonsieurVerite-1.2.zip","browser_download_url":"https://x/bundle.zip"}]}
            """;

        Assert.Equal("https://x/bundle.zip", Updater.PickZipUrl(release));
        Assert.Null(Updater.PickZipUrl("""{"assets":[{"name":"charlotte.exe","browser_download_url":"u"}]}"""));
        Assert.Null(Updater.PickZipUrl("{}"));
    }

    [Fact]
    public void InstallReplacesFilesAndKeepsTheOldOnesAside()
    {
        Directory.CreateDirectory(App(""));
        File.WriteAllText(App("MonsieurVerite.exe"), "old gui");
        File.WriteAllText(App("charlotte.exe"), "old engine");
        var zip = Zip(("MonsieurVerite.exe", "new gui"), ("charlotte.exe", "new engine"), ("extra.dll", "lib"));

        var written = Updater.Install(zip, App(""));

        Assert.Equal(3, written.Count);
        Assert.Equal("new gui", File.ReadAllText(App("MonsieurVerite.exe")));
        Assert.Equal("new engine", File.ReadAllText(App("charlotte.exe")));
        Assert.Equal("lib", File.ReadAllText(App("extra.dll")));
        // The replaced binaries are renamed, not deleted: a running exe can only be renamed,
        // and the next launch removes them.
        Assert.Equal("old gui", File.ReadAllText(App("MonsieurVerite.exe.old")));
        Assert.Equal("old engine", File.ReadAllText(App("charlotte.exe.old")));

        Updater.DeleteStaleFiles(App(""));
        Assert.False(File.Exists(App("MonsieurVerite.exe.old")));
        Assert.False(File.Exists(App("charlotte.exe.old")));
        Assert.True(File.Exists(App("MonsieurVerite.exe")));
    }

    [Fact]
    public void AnAppFolderWithATrailingSeparatorIsStillTheAppFolder()
    {
        // AppContext.BaseDirectory, the real caller's argument, ends in a separator. The
        // containment check must not turn that into "C:\app\\" and refuse every entry.
        Directory.CreateDirectory(App(""));
        var zip = Zip(("charlotte.exe", "engine"));

        Updater.Install(zip, App("") + Path.DirectorySeparatorChar);

        Assert.Equal("engine", File.ReadAllText(App("charlotte.exe")));
    }

    [Fact]
    public void ASingleWrappingFolderIsStripped()
    {
        Directory.CreateDirectory(App(""));
        var zip = Zip(("MonsieurVerite-1.2/charlotte.exe", "engine"), ("MonsieurVerite-1.2/font/ja.ttf", "font"));

        Updater.Install(zip, App(""));

        Assert.Equal("engine", File.ReadAllText(App("charlotte.exe")));
        Assert.Equal("font", File.ReadAllText(App(Path.Combine("font", "ja.ttf"))));
        Assert.False(Directory.Exists(App("MonsieurVerite-1.2")));
    }

    [Fact]
    public void AnEntryEscapingTheAppFolderIsRefusedAndNothingChanges()
    {
        Directory.CreateDirectory(App(""));
        File.WriteAllText(App("charlotte.exe"), "old engine");
        var zip = Zip(("charlotte.exe", "new engine"), ("../outside.txt", "escape"));

        Assert.Throws<InvalidDataException>(() => Updater.Install(zip, App("")));

        // Rolled back: the first entry had already been swapped when the second was refused.
        Assert.Equal("old engine", File.ReadAllText(App("charlotte.exe")));
        Assert.False(File.Exists(App("charlotte.exe.old")));
        Assert.False(File.Exists(Path.Combine(folder, "outside.txt")));
    }

    [Fact]
    public void AnEmptyArchiveIsAnError()
    {
        Directory.CreateDirectory(App(""));
        Assert.Throws<InvalidDataException>(() => Updater.Install(Zip(), App("")));
    }
}
