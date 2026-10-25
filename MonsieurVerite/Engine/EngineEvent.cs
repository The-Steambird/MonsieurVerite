using System.Text.Json;

namespace MonsieurVerite.Engine;

public abstract record EngineEvent
{
    public string Type { get; init; } = "";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static EngineEvent Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeProperty)
                || typeProperty.ValueKind != JsonValueKind.String)
            {
                return new UnknownEvent();
            }

            var type = typeProperty.GetString()!;
            return type switch
            {
                "session_start" => Read<SessionStartEvent>(root),
                "log" => Read<LogEvent>(root),
                "stage" => Read<StageEvent>(root),
                "progress" => Read<ProgressEvent>(root),
                "question" => Read<QuestionEvent>(root),
                "job_start" => Read<JobStartEvent>(root),
                "job_skipped" => Read<JobSkippedEvent>(root),
                "result" => Read<ResultEvent>(root),
                "error" => Read<ErrorEvent>(root),
                "cancelled" => Read<CancelledEvent>(root),
                "probe" => Read<ProbeEvent>(root),
                "crack" => Read<CrackEvent>(root),
                "crack_summary" => Read<CrackSummaryEvent>(root),
                "update" => Read<UpdateEvent>(root),
                "update_install" => Read<UpdateInstallEvent>(root),
                "capture_key" => Read<CaptureKeyEvent>(root),
                _ => new UnknownEvent { Type = type },
            };
        }
        catch (JsonException)
        {
            return new UnknownEvent();
        }
    }

    private static T Read<T>(JsonElement root) where T : EngineEvent =>
        root.Deserialize<T>(SerializerOptions)!;
}

public sealed record SessionStartEvent : EngineEvent
{
    public string? Version { get; init; }
}

public sealed record LogEvent : EngineEvent
{
    public string Level { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed record StageEvent : EngineEvent
{
    public string Stage { get; init; } = "";
    public string Status { get; init; } = "";
}

public sealed record ProgressEvent : EngineEvent
{
    public long Current { get; init; }
    public long Total { get; init; }
}

public sealed record QuestionEvent : EngineEvent
{
    public string Id { get; init; } = "";
    public string Prompt { get; init; } = "";
    public bool Default { get; init; }
}

public sealed record JobStartEvent : EngineEvent
{
    public string File { get; init; } = "";
}

public sealed record JobSkippedEvent : EngineEvent
{
    public string File { get; init; } = "";
    public string Reason { get; init; } = "";
}

public sealed record ResultEvent : EngineEvent
{
    public string File { get; init; } = "";
    public string Output { get; init; } = "";
}

public sealed record ErrorEvent : EngineEvent
{
    public string File { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed record CancelledEvent : EngineEvent
{
    public string File { get; init; } = "";
}

public sealed record ProbeEvent : EngineEvent
{
    public string File { get; init; } = "";
    public bool Key { get; init; }
    public string? Version { get; init; }
    public IReadOnlyList<string> Subtitles { get; init; } = [];
    public bool StreamCipher { get; init; }
}

public sealed record CrackEvent : EngineEvent
{
    public string File { get; init; } = "";
    public string Stem { get; init; } = "";
    public ulong? VideoKey { get; init; }
    public string? Reason { get; init; }
}

public sealed record CrackSummaryEvent : EngineEvent
{
    public int Recovered { get; init; }
    public int Unrecovered { get; init; }
}

public sealed record UpdateEvent : EngineEvent
{
    public string Current { get; init; } = "";
    public string? Latest { get; init; }
    public bool Available { get; init; }
    public string? Url { get; init; }
    public string? Notes { get; init; }
    public string? Reason { get; init; }
}

public sealed record UpdateInstallEvent : EngineEvent
{
    public bool Installed { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// An old-scheme key carries only <see cref="VideoKey"/>, and a 7.1 key only
/// <see cref="AudioKey"/> and <see cref="AesKey"/>.
/// </summary>
public sealed record CaptureKeyEvent : EngineEvent
{
    public ulong? VideoKey { get; init; }
    public ulong? AudioKey { get; init; }
    public string? AesKey { get; init; }
    public string? ParentQuestId { get; init; }
    public bool Known { get; init; }
    public IReadOnlyList<string> Videos { get; init; } = [];
}

public sealed record UnknownEvent : EngineEvent;
