using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    public string Language
    {
        get;
        set => field = Strings.Languages.Contains(value) ? value : Strings.SourceLanguage;
    } = Strings.SourceLanguage;

    public RunOptions Options { get; set; } = new();

    [JsonIgnore] public string EffectiveEnginePath => EnginePath ?? DefaultEnginePath;

    /// <summary>Beside the engine, which is where keys.json is.</summary>
    [JsonIgnore]
    public string RecoveredKeysPath =>
        Path.Combine(Path.GetDirectoryName(EffectiveEnginePath) ?? AppContext.BaseDirectory,
            "recovered_keys.json");

    // The file is read before the language is chosen, which is why the message is built only when
    // asked for.
    private (string Path, string Reason)? loadFailure;

    /// <summary>Why the file on disk was not used, when it was there but unreadable.</summary>
    [JsonIgnore]
    public string? LoadError =>
        loadFailure is { } failure
            ? Strings.SETTINGS_UNREADABLE_LOG(failure.Path, failure.Reason)
            : null;

    public string? ResolveEngine() => File.Exists(EffectiveEnginePath) ? EffectiveEnginePath : null;

    public static Settings Load() => Load(FilePath);

    public static Settings Load(string path)
    {
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
            return new Settings { loadFailure = (path, e.Message) };
        }

        return new Settings();
    }

    public void Save() => Save(FilePath);

    public void Save(string path) =>
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, SerializerOptions));
}
