using System.IO;
using System.Text;

namespace MonsieurVerite;

public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        var staging = path + ".tmp";
        using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write))
        {
            stream.Write(Encoding.UTF8.GetBytes(contents));
            stream.Flush(flushToDisk: true);
        }

        File.Move(staging, path, overwrite: true);
    }
}
