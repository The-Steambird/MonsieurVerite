using System.IO;

using MonsieurVerite.Engine;

using Xunit.Abstractions;

namespace MonsieurVerite.Tests;

/// <summary>
/// The live tests need a charlotte-cli.exe beside the test binary, the app's own default
/// location, and the sibling charlotte checkout for its test cutscenes. They skip themselves
/// without either, which keeps the suite passing on a machine that has only this repo, and say
/// so under <c>dotnet test -v n</c> because a silent skip looks like a pass.
/// </summary>
internal static class Charlotte
{
    public static string? Checkout { get; } = Find();

    public static EngineLaunchProfile? LiveEngine(ITestOutputHelper output)
    {
        var engine = new Settings().ResolveEngine();
        if (engine is null)
        {
            output.WriteLine($"SKIPPED: no engine at {Settings.DefaultEnginePath}");
        }

        return engine;
    }

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
