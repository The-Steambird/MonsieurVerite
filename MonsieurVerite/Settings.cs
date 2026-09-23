using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MonsieurVerite.Engine;
using MonsieurVerite.ViewModels;

namespace MonsieurVerite;

public sealed class Settings
{
    private static readonly JsonSerializerOptions
        SerializerOptions = new() { WriteIndented = true };

    public static string DefaultEnginePath { get; } =
        Path.Combine(AppContext.BaseDirectory, "charlotte-cli.exe");

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public string? SourceDirectory { get; set; }

    public string? OutputDirectory { get; set; }

    /// <summary>Null means the default, charlotte-cli.exe beside charlotte-gui.exe.</summary>
    public string? EnginePath
    {
        get;
        set => field = string.IsNullOrWhiteSpace(value)
                       || string.Equals(value, DefaultEnginePath,
                           StringComparison.OrdinalIgnoreCase)
            ? null
            : value;
    }

    public bool CheckForUpdatesOnStartup { get; set; } = true;

    public RunOptions Options { get; set; } = new();

    [JsonIgnore] public string EffectiveEnginePath => EnginePath ?? DefaultEnginePath;

    /// <summary>Beside the engine, which is where keys.json is.</summary>
    [JsonIgnore]
    public string RecoveredKeysPath =>
        Path.Combine(Path.GetDirectoryName(EffectiveEnginePath) ?? AppContext.BaseDirectory,
            "recovered_keys.json");

    /// <summary>Why the file on disk was not used, when it was there but unreadable.</summary>
    [JsonIgnore]
    public string? LoadError { get; private set; }

    public EngineLaunchProfile? ResolveEngine() =>
        File.Exists(EffectiveEnginePath) ? EngineLaunchProfile.Packaged(EffectiveEnginePath) : null;

    public static Settings Load() => Load(FilePath);

    public static Settings Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path),
                           SerializerOptions)
                       ?? new Settings();
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Settings { LoadError = $"Could not read {path}, using defaults: {e.Message}" };
        }

        return new Settings();
    }

    public void Save() => Save(FilePath);

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, SerializerOptions));
    }
}
