using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using MonsieurVerite.ViewModels;

namespace MonsieurVerite;

/// <summary>
/// Every key in Locales/en-US.json is a generated member. English fills any gap in the translation.
/// </summary>
public static partial class Strings
{
    private const string Prefix = "Locales/";

    internal const string SourceLanguage = "en-US";

    private static Dictionary<string, string>? translation;

    internal static IReadOnlyDictionary<string, string> English { get; } = Load(SourceLanguage);

    internal static IReadOnlyList<string> Languages { get; } =
        typeof(Strings).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(name => Path.GetFileNameWithoutExtension(name[Prefix.Length..]))
            .ToList();

    /// <summary>The language picked in Settings, passed to the engine as CHARLOTTE_LANG.</summary>
    public static string Language { get; private set; } = SourceLanguage;

    public static void Use(string language)
    {
        Language = language;
        translation = language == SourceLanguage ? null : Load(language);
    }

    internal static List<Language> Choices() =>
        Languages
            .Select(code => new Language(code, NativeName(code, Languages)))
            .OrderBy(choice => choice.Name, StringComparer.CurrentCulture)
            .ToList();

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

    /// <summary>A string with its placeholders left in, for text that puts links in them.</summary>
    public static string Template(string key) => Text(key);

    internal static Dictionary<string, string> Load(string language)
    {
        var name = $"{Prefix}{language}.json";
        using var stream = typeof(Strings).Assembly.GetManifestResourceStream(name)
                           ?? throw new FileNotFoundException($"No {name} in the build.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }

    [GeneratedRegex(@"\{([a-z][a-z0-9_]*)\}")]
    internal static partial Regex Placeholder();

    private static string Text(string key) =>
        translation?.GetValueOrDefault(key) is { Length: > 0 } text ? text : English[key];

    // One pass leaves a value such as a file name alone even when it contains "{name}".
    private static string Fill(string key, params (string Name, object? Value)[] values) =>
        Placeholder().Replace(Text(key), match =>
        {
            var found = values.FirstOrDefault(value => value.Name == match.Groups[1].Value);
            return found.Name is null
                ? match.Value
                : Convert.ToString(found.Value, CultureInfo.CurrentCulture) ?? "";
        });
}
