using System.IO;

namespace MonsieurVerite.Engine;

public sealed record EngineLaunchProfile
{
    public required string FileName { get; init; }

    public required IReadOnlyList<string> BaseArguments { get; init; }

    public required string WorkingDirectory { get; init; }

    public string Description =>
        BaseArguments.Count == 0 ? FileName : $"{FileName} {string.Join(' ', BaseArguments)} in {WorkingDirectory}";

    public static EngineLaunchProfile Packaged(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        return new EngineLaunchProfile
        {
            FileName = executablePath,
            BaseArguments = [],
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? ".",
        };
    }
}
