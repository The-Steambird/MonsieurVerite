using System.Globalization;
using System.IO;
using MonsieurVerite.ViewModels;

namespace MonsieurVerite.Tests;

public class TranslationsTests : IDisposable
{
    // The app should never ship these, because a built-in file for the language under test would
    // change what Resolve and Save compare against.
    private const string Unshipped = "kl-GL";
    private const string AlsoUnshipped = "fo-FO";

    private readonly ScratchFolder scratch = new();

    public void Dispose()
    {
        scratch.Dispose();
        GC.SuppressFinalize(this);
    }

    private void WriteDownload(string language, string json)
    {
        var path = Translations.FilePath(scratch.Root, language);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    [Fact]
    public void ADownloadedStringReplacesTheBuiltInOneOnlyWhereItsPlaceholdersMatch()
    {
        var builtIn = new Dictionary<string, string>
        {
            ["OPEN_FOLDER"] = "Ordner öffnen",
            ["ALREADY_QUEUED_LOG"] = "{file} ist schon in der Warteschlange.",
        };
        var downloaded = new Dictionary<string, string>
        {
            ["OPEN_FOLDER"] = "Ordner auswählen",
            ["ALREADY_QUEUED_LOG"] = "{path} ist schon in der Warteschlange.",
            ["ADD_FILES"] = "",
            ["NOT_A_KEY_YET"] = "Neu",
        };

        var merged = Translations.Merge(builtIn, downloaded);

        Assert.Equal("Ordner auswählen", merged["OPEN_FOLDER"]);
        Assert.Equal("{file} ist schon in der Warteschlange.", merged["ALREADY_QUEUED_LOG"]);
        Assert.False(merged.ContainsKey("ADD_FILES"));
        Assert.False(merged.ContainsKey("NOT_A_KEY_YET"));
    }

    [Fact]
    public void ALanguageThatOnlyExistsAsADownloadIsStillFound()
    {
        WriteDownload(Unshipped, """{ "OPEN_FOLDER": "เปิดโฟลเดอร์" }""");

        var translation = Translations.Resolve(CultureInfo.GetCultureInfo(Unshipped), scratch.Root);

        Assert.Equal("เปิดโฟลเดอร์", translation?["OPEN_FOLDER"]);
    }

    [Fact]
    public void ANullStringInADownloadIsSkipped()
    {
        WriteDownload(Unshipped, """{ "OPEN_FOLDER": null, "ADD_FILES": "เพิ่มไฟล์" }""");

        var translation = Translations.Resolve(CultureInfo.GetCultureInfo(Unshipped), scratch.Root);

        Assert.Equal(["ADD_FILES"], translation?.Keys);
    }

    [Fact]
    public void AnUnreadableDownloadOrAStrayFolderIsIgnored()
    {
        WriteDownload(Unshipped, "not json");
        WriteDownload("old translations", "{}");

        Assert.Empty(Translations.Resolve(CultureInfo.GetCultureInfo(Unshipped), scratch.Root)!);
        Assert.Null(Translations.Resolve(CultureInfo.GetCultureInfo(AlsoUnshipped), scratch.Root));
    }

    [Fact]
    public void TheLanguageChoicesIncludeDownloads()
    {
        WriteDownload(Unshipped, """{ "OPEN_FOLDER": "Paasuuk" }""");

        var choices = Translations.Choices(scratch.Root);

        Assert.Contains(new Language("en-US", "English"), choices);
        Assert.Contains(new Language(Unshipped, "Kalaallisut"), choices);
    }

    [Fact]
    public void ALanguageNamesItsRegionOnlyWhenAnotherChoiceSharesIt()
    {
        string[] codes = ["en-US", "es-ES", "zh-CN", "zh-TW"];

        Assert.Equal("Español", Translations.NativeName("es-ES", codes));
        Assert.Equal(CultureInfo.GetCultureInfo("zh-CN").NativeName,
            Translations.NativeName("zh-CN", codes));
        Assert.NotEqual(Translations.NativeName("zh-CN", codes),
            Translations.NativeName("zh-TW", codes));
    }

    private const string Listing = """
        [
          {"name": "de-DE.json", "type": "file", "download_url": "https://raw/de-DE.json"},
          {"name": "en-US.json", "type": "file", "download_url": "https://raw/en-US.json"},
          {"name": "zh-TW.json", "type": "file", "download_url": "https://raw/zh-TW.json"},
          {"name": "notes.md", "type": "file", "download_url": "https://raw/notes.md"},
          {"name": "xx-QQ.json", "type": "file", "download_url": "https://raw/xx-QQ.json"},
          {"name": "old", "type": "dir", "download_url": null}
        ]
        """;

    [Theory]
    [InlineData("de-AT", "de-DE", "https://raw/de-DE.json")]
    [InlineData("zh-HK", "zh-TW", "https://raw/zh-TW.json")]
    public void TheClosestTranslationOnMasterIsPicked(string culture, string language, string url)
    {
        var picked = Translations.Pick(Listing, CultureInfo.GetCultureInfo(culture));

        Assert.Equal(new Translations.TranslationFile(language, url), picked);
    }

    [Theory]
    [InlineData("en-GB")]
    [InlineData("fr-FR")]
    public void NothingIsPickedForEnglishOrALanguageWithNoFile(string culture)
    {
        Assert.Null(Translations.Pick(Listing, CultureInfo.GetCultureInfo(culture)));
        Assert.Null(Translations.Pick("""{"message": "Not Found"}""", CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void ATranslationIsWrittenOnlyWhenItChanged()
    {
        const string first = """{ "OPEN_FOLDER": "Ordner öffnen" }""";
        const string second = """{ "OPEN_FOLDER": "Ordner auswählen" }""";

        Assert.True(Translations.Save(scratch.Root, Unshipped, first));
        Assert.False(Translations.Save(scratch.Root, Unshipped, first));
        Assert.True(Translations.Save(scratch.Root, Unshipped, second));

        Assert.Equal(second, File.ReadAllText(Translations.FilePath(scratch.Root, Unshipped)));
    }

    [Fact]
    public void ANullStringInADownloadCountsAsUntranslated()
    {
        Assert.False(Translations.Save(scratch.Root, Unshipped, """{ "OPEN_FOLDER": null }"""));
    }

    [Fact]
    public void AnUnreadableSavedTranslationIsReplaced()
    {
        WriteDownload(Unshipped, "not json");

        Assert.True(Translations.Save(scratch.Root, Unshipped, """{ "OPEN_FOLDER": "Ordner öffnen" }"""));
    }

    // Crowdin exports a language nobody has translated yet as {}, which counts as no file.
    [Fact]
    public void ADownloadTheBuildAlreadyHasRemovesTheFileAndItsEmptyFolders()
    {
        Translations.Save(scratch.Root, Unshipped, """{ "OPEN_FOLDER": "Ordner öffnen" }""");

        Assert.False(Translations.Save(scratch.Root, Unshipped, "{}"));

        Assert.False(Directory.Exists(scratch.File("locales")));
    }

    [Fact]
    public void ADownloadTheBuildAlreadyHasWritesNothingWhenNoneWasSaved()
    {
        Assert.False(Translations.Save(scratch.Root, Unshipped, "{}"));

        Assert.False(Directory.Exists(scratch.File("locales")));
    }
}
