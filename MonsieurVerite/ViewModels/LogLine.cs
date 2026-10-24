using MonsieurVerite.Engine;

namespace MonsieurVerite.ViewModels;

/// <summary>
/// The level shows as colour rather than as a "[warning]" prefix, because the engine's level
/// names are not translated.
/// </summary>
public sealed record LogLine(string Text, bool IsWarning = false)
{
    public static LogLine From(LogEvent log) => new(log.Message, log.Level is "warning" or "error");
}
