using System.IO;
using System.Text.Json;
using MonsieurVerite.Engine;
using MonsieurVerite.ViewModels;

namespace MonsieurVerite;

public sealed class Settings
{
    private static readonly JsonSerializerOptions
        SerializerOptions = new() { WriteIndented = true };

    public string? SourceDirectory { get; set; }

    public string? OutputDirectory { get; set; }

    public RunOptions Options { get; set; } = new();

    private static string Folder =>
        ResolveEngine()?.WorkingDirectory ?? AppContext.BaseDirectory;

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static string RecoveredKeysPath => Path.Combine(Folder, "recovered_keys.json");

    public static string BundledEnginePath =>
        Path.Combine(AppContext.BaseDirectory, "charlotte-cli.exe");

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

    public static EngineLaunchProfile? ResolveEngine()
    {
        if (File.Exists(BundledEnginePath))
        {
            return EngineLaunchProfile.Packaged(BundledEnginePath);
        }

        return FindSiblingCharlotte() is { } sibling ? EngineLaunchProfile.Dev(sibling) : null;
    }

    private static string? FindSiblingCharlotte()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "MonsieurVerite.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory?.Parent is not { } parent)
        {
            return null;
        }

        var sibling = Path.Combine(parent.FullName, "charlotte");
        return File.Exists(Path.Combine(sibling, "main.py")) ? sibling : null;
    }
}
