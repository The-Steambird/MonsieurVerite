using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace MonsieurVerite.Tests;

public class StringsTests
{
    // The generator matches keys with a regex because MSBuild gives it no JSON reader, and the app
    // reads the same file with a real one. The two must agree.
    [Fact]
    public void EveryEnglishKeyIsAMemberAndEveryMemberHasText()
    {
        var members = typeof(Strings)
            .GetMembers(BindingFlags.Public | BindingFlags.Static)
            .Select(member => member.Name)
            .Where(name => Regex.IsMatch(name, "^[A-Z][A-Z0-9_]*$"))
            .Distinct()
            .Order()
            .ToList();

        Assert.Equal(Strings.English.Keys.Order(), members);
        Assert.All(Strings.English, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value), pair.Key));
    }

    // x:Static resolves only public members, and only when the window that names one is opened,
    // so neither the build nor starting the app catches a string it cannot see.
    [Fact]
    public void EveryStringTheXamlNamesIsPublic()
    {
        var project = Path.Combine(RepositoryRoot(), "MonsieurVerite");
        var names = Directory.EnumerateFiles(project, "*.xaml", SearchOption.AllDirectories)
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"local:Strings\.(\w+)"))
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(names);
        Assert.All(names, name =>
            Assert.NotNull(typeof(Strings).GetProperty(name, BindingFlags.Public | BindingFlags.Static)));
    }

    // A translation that drops or renames a placeholder shows it raw, and only when that message
    // comes up.
    [Fact]
    public void TranslationsKeepTheEnglishKeysAndPlaceholders()
    {
        Assert.Contains("en-US", Strings.Languages);
        foreach (var language in Strings.Languages.Where(language => language != "en-US"))
        {
            CultureInfo.GetCultureInfo(language, predefinedOnly: true);
            foreach (var (key, text) in Strings.Load(language).Where(pair => pair.Value.Length > 0))
            {
                Assert.True(Strings.English.ContainsKey(key), $"{language}: {key} is not in en-US");
                Assert.Equal(Placeholders(Strings.English[key]), Placeholders(text));
            }
        }
    }

    [Fact]
    public void PlaceholdersAreFilledOnceSoAValueCannotFillAnother()
    {
        Assert.Equal("Could not read {error}: boom", Strings.FOLDER_UNREADABLE_LOG("{error}", "boom"));
    }

    [Fact]
    public void ALanguageNamesItsRegionOnlyWhenAnotherChoiceSharesIt()
    {
        string[] codes = ["en-US", "es-ES", "zh-CN", "zh-TW"];

        Assert.Equal("Español", Strings.NativeName("es-ES", codes));
        Assert.Equal(CultureInfo.GetCultureInfo("zh-CN").NativeName, Strings.NativeName("zh-CN", codes));
        Assert.NotEqual(Strings.NativeName("zh-CN", codes), Strings.NativeName("zh-TW", codes));
    }

    private static List<string> Placeholders(string text) =>
        Strings.Placeholder().Matches(text).Select(match => match.Value).Order().ToList();

    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(path))!;
}
