using System.IO;

namespace MonsieurVerite.Engine;

public sealed record EngineLaunchProfile
{
    public required string FileName { get; init; }

    public required IReadOnlyList<string> BaseArguments { get; init; }

    public required string WorkingDirectory { get; init; }

    public static EngineLaunchProfile At(string executablePath) => new()
    {
        FileName = executablePath,
        BaseArguments = [],
        WorkingDirectory = Path.GetDirectoryName(executablePath) ?? ".",
    };
}
