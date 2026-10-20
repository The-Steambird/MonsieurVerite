using System.IO;

namespace MonsieurVerite.Tests;

public class UpdaterTests : IDisposable
{
    private readonly ScratchFolder scratch = new();

    public void Dispose()
    {
        scratch.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void DeleteStaleFilesRemovesOnlyTheFilesAnInstallMovedAside()
    {
        File.WriteAllText(scratch.File("charlotte-gui.exe"), "new gui");
        File.WriteAllText(scratch.File("charlotte-gui.exe.old"), "old gui");
        File.WriteAllText(scratch.File("charlotte-cli.exe.old"), "old engine");

        Updater.DeleteStaleFiles(scratch.Root);

        Assert.Equal(["charlotte-gui.exe"], Directory.GetFiles(scratch.Root).Select(Path.GetFileName));
    }
}
