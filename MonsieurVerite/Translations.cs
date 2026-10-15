using System.Globalization;
using System.IO;
using System.Text.Json;
using MonsieurVerite.ViewModels;

namespace MonsieurVerite;

public static class Translations
{
    private const string ListingApi =
        "https://api.github.com/repos/The-Steambird/MonsieurVerite/contents/MonsieurVerite/Locales?ref=master";

    internal static Dictionary<string, string>? Resolve(CultureInfo culture, string appDirectory)
    {
        var language = Strings.Closest(culture, Strings.Languages.Union(Downloaded(appDirectory)));
        if (language is null or Strings.SourceLanguage)
        {
            return null;
        }

        return Merge(BuiltIn(language), Read(appDirectory, language));
    }

    internal static List<Language> Choices(string appDirectory)
    {
        var codes = Strings.Languages.Union(Downloaded(appDirectory)).ToList();
        return codes
            .Select(code => new Language(code, NativeName(code, codes)))
            .OrderBy(choice => choice.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    internal static string NativeName(string code, IReadOnlyCollection<string> codes)
    {
        var culture = CultureInfo.GetCultureInfo(code);
        var language = culture.TwoLetterISOLanguageName;
        var shared = codes.Count(other =>
            CultureInfo.GetCultureInfo(other).TwoLetterISOLanguageName == language) > 1;
        var name = shared || culture.IsNeutralCulture
            ? culture.NativeName
            : CultureInfo.GetCultureInfo(language).NativeName;
        return char.ToUpper(name[0], culture) + name[1..];
    }

    /// <summary>Reports whether a new file was saved for the next start to pick up.</summary>
    public static async Task<bool> RefreshAsync(
        string appDirectory, CultureInfo culture, CancellationToken cancellationToken)
    {
        if (culture.TwoLetterISOLanguageName == "en")
        {
            return false;
        }

        var listing = await GitHub.Http.GetStringAsync(ListingApi, cancellationToken)
            .ConfigureAwait(false);
        if (Pick(listing, culture) is not { } translation)
        {
            return false;
        }

        var json = await GitHub.Http.GetStringAsync(translation.Url, cancellationToken)
            .ConfigureAwait(false);
        return Save(appDirectory, translation.Language, json);
    }

    internal sealed record TranslationFile(string Language, string Url);

    internal static TranslationFile? Pick(string listingJson, CultureInfo culture)
    {
        using var document = JsonDocument.Parse(listingJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var urls = new Dictionary<string, string>();
        foreach (var file in document.RootElement.EnumerateArray())
        {
            var name = GitHub.Text(file, "name");
            var language = Path.GetFileNameWithoutExtension(name);
            var url = GitHub.Text(file, "download_url");
            if (Path.GetExtension(name) == ".json" && url.Length > 0 && IsCulture(language))
            {
                urls[language] = url;
            }
        }

        return Strings.Closest(culture, urls.Keys) is { } closest and not Strings.SourceLanguage
            ? new TranslationFile(closest, urls[closest])
            : null;
    }

    // A download the build already has is deleted, which lets a release that catches up clear it.
    internal static bool Save(string appDirectory, string language, string json)
    {
        var downloaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        if (SameStrings(downloaded, BuiltIn(language) ?? []))
        {
            Delete(appDirectory, language);
            return false;
        }

        var saved = Read(appDirectory, language);
        if (saved is not null && SameStrings(downloaded, saved))
        {
            return false;
        }

        var path = FilePath(appDirectory, language);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, json);
        return true;
    }

    // Called after an install, because a release carries every translation master had when it was
    // built.
    internal static void DeleteAll(string appDirectory)
    {
        var folder = Path.Combine(appDirectory, "locales");
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var directory in Directory.GetDirectories(folder))
        {
            var language = Path.GetFileName(directory);
            try
            {
                Delete(appDirectory, language);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Could not delete the {language} translation: {e.Message}");
            }
        }
    }

    internal static Dictionary<string, string> Merge(
        IReadOnlyDictionary<string, string>? builtIn,
        IReadOnlyDictionary<string, string>? downloaded)
    {
        var merged = builtIn is null ? [] : new Dictionary<string, string>(builtIn);
        if (downloaded is null)
        {
            return merged;
        }

        foreach (var (key, text) in downloaded)
        {
            if (FitsThisBuild(key, text))
            {
                merged[key] = text;
            }
        }

        return merged;
    }

    // Master can be ahead of this build, and a key there may be gone here or have renamed
    // placeholders.
    private static bool FitsThisBuild(string key, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (!Strings.English.TryGetValue(key, out var english))
        {
            return false;
        }

        return Strings.Placeholders(english).SetEquals(Strings.Placeholders(text));
    }

    private static Dictionary<string, string>? BuiltIn(string language) =>
        Strings.Languages.Contains(language) ? Strings.Load(language) : null;

    internal static string FilePath(string appDirectory, string language) =>
        Path.Combine(appDirectory, "locales", language, "gui.json");

    internal static Dictionary<string, string>? Read(string appDirectory, string language)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(FilePath(appDirectory, language)));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<string> Downloaded(string appDirectory)
    {
        var languages = new List<string>();
        var folder = Path.Combine(appDirectory, "locales");
        if (!Directory.Exists(folder))
        {
            return languages;
        }

        foreach (var directory in Directory.GetDirectories(folder))
        {
            var language = Path.GetFileName(directory);
            if (IsCulture(language) && File.Exists(FilePath(appDirectory, language)))
            {
                languages.Add(language);
            }
        }

        return languages;
    }

    private static void Delete(string appDirectory, string language)
    {
        var path = FilePath(appDirectory, language);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        DeleteIfEmpty(Path.GetDirectoryName(path)!);
        DeleteIfEmpty(Path.Combine(appDirectory, "locales"));
    }

    private static void DeleteIfEmpty(string folder)
    {
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
        {
            Directory.Delete(folder);
        }
    }

    private static bool SameStrings(
        IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second)
    {
        var left = Translated(first);
        var right = Translated(second);
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (key, text) in left)
        {
            if (right.GetValueOrDefault(key) != text)
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, string> Translated(
        IReadOnlyDictionary<string, string> strings) =>
        strings.Where(pair => !string.IsNullOrEmpty(pair.Value)).ToDictionary();

    internal static bool IsCulture(string name)
    {
        try
        {
            CultureInfo.GetCultureInfo(name, predefinedOnly: true);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }
}
