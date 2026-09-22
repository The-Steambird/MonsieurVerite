using System.IO;

namespace MonsieurVerite.Tests;

internal static class Charlotte
{
    public static string? Checkout { get; } = Find();

    private static string? Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "MonsieurVerite.slnx")))
        {
            directory = directory.Parent;
        }

        var sibling = directory?.Parent is { } parent
            ? Path.Combine(parent.FullName, "charlotte")
            : null;
        return Directory.Exists(sibling) ? sibling : null;
    }
}
