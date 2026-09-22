using System.IO;

namespace MonsieurVerite.Tests;

internal sealed class ScratchFolder : IDisposable
{
    public string Root { get; } = Path.Combine(
        Path.GetTempPath(), "MonsieurVerite.Tests", Path.GetRandomFileName());

    public ScratchFolder() => Directory.CreateDirectory(Root);

    public string File(string relative) => Path.Combine(Root, relative);

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
