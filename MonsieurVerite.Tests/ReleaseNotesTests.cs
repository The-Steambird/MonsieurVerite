using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using MonsieurVerite.Views;

namespace MonsieurVerite.Tests;

public class ReleaseNotesTests
{
    // This is the shape of a real release body. The image is on a domain that cannot resolve,
    // because creating it starts its download.
    private const string Release = """
        <div align="center">
        <img width="3000" height="4500" alt="art" src="https://example.invalid/art.jpg" />
        </div>

        Artist: [someone](https://www.pixiv.net/en/artworks/1)

        <br>

        Charlotte now speaks **7 languages** and has a `Load from game` button.
        A second line.

        ## What's Changed

        ### Engine

        * Translated https://github.com/The-Steambird/charlotte/commit/97dc2f3d4a3ede70fed208c66a1cf6188ddc73b7 https://github.com/The-Steambird/charlotte/commit/b1856f5a1e25a200f4795511a8dc962a9f34dc09
        * Add keys for `Cs_LY_EQ4025110_ZY`

        **Full Changelog**: https://github.com/The-Steambird/charlotte/compare/1.1.1...1.2.0
        """;

    [Fact]
    public void RendersAReleaseBody() => OnSta(() =>
    {
        var blocks = ReleaseNotes.Render(Release);

        Assert.IsType<Image>(blocks[0]);
        Assert.Equal(
            [
                "Artist: someone",
                "Charlotte now speaks 7 languages and has a Load from game button.\nA second line.",
                "What's Changed",
                "Engine",
                "•", "Translated 97dc2f3 b1856f5",
                "•", "Add keys for Cs_LY_EQ4025110_ZY",
                "Full Changelog: 1.1.1...1.2.0",
            ],
            blocks.Skip(1).SelectMany(Texts));
        Assert.Equal(
            [
                "https://www.pixiv.net/en/artworks/1",
                "https://github.com/The-Steambird/charlotte/commit/97dc2f3d4a3ede70fed208c66a1cf6188ddc73b7",
                "https://github.com/The-Steambird/charlotte/commit/b1856f5a1e25a200f4795511a8dc962a9f34dc09",
                "https://github.com/The-Steambird/charlotte/compare/1.1.1...1.2.0",
            ],
            blocks.SelectMany(Links).Select(link => link.NavigateUri.AbsoluteUri));
    });

    [Fact]
    public void ListsNumberNestAndContinue() => OnSta(() =>
    {
        var blocks = ReleaseNotes.Render("3. first\ncontinued\n   - nested\n4. second\n\nafter");

        Assert.Equal(["3.", "first\ncontinued", "•", "nested", "4.", "second", "after"], blocks.SelectMany(Texts));
        Assert.Equal(2, blocks.Count);
    });

    [Fact]
    public void QuotesCodeAndStrikethroughRender() => OnSta(() =>
    {
        var blocks = ReleaseNotes.Render("""
            > quoted ~~gone~~

            ```
            code <here>
            ```
            """);

        Assert.Equal(["quoted gone", "code <here>"], blocks.SelectMany(Texts));
    });

    [Fact]
    public void TablesAndTaskListsShowAsTheirText() => OnSta(() =>
    {
        var blocks = ReleaseNotes.Render("| Key | Value |\n| --- | ----- |\n\n- [x] done");

        Assert.Equal(["| Key | Value |\n| --- | ----- |", "\u2022", "[x] done"], blocks.SelectMany(Texts));
    });

    [Fact]
    public void TagsInCodeSpansAreKept() => OnSta(() =>
        Assert.Equal(["Pass --key <hex> here"], ReleaseNotes.Render("Pass `--key <hex>` here").SelectMany(Texts)));

    [Fact]
    public void LinkedImagesShowTheImage() => OnSta(() =>
    {
        var blocks = ReleaseNotes.Render(
            "[![shot](https://example.invalid/a.png)](https://example.com/video)\n\n" +
            "Built by [![ci](https://example.invalid/b.svg)](https://example.com/ci) today");

        Assert.IsType<Image>(blocks[0]);
        Assert.Equal(["Built by ci today"], Texts(blocks[1]));
        Assert.Equal(["https://example.com/ci"], Links(blocks[1]).Select(link => link.NavigateUri.AbsoluteUri));
    });

    [Fact]
    public void LinksWithoutTextShowTheirUrl() => OnSta(() =>
    {
        var blocks = ReleaseNotes.Render("See https://example.com/docs or [](https://example.com/faq).");

        Assert.Equal(["See https://example.com/docs or https://example.com/faq."], Texts(blocks[0]));
        Assert.Equal(
            ["https://example.com/docs", "https://example.com/faq"],
            Links(blocks[0]).Select(link => link.NavigateUri.AbsoluteUri));
    });

    [Fact]
    public void NotesWithNothingToShowRenderNothing() => OnSta(() =>
        Assert.Empty(ReleaseNotes.Render("<!-- template -->\n\n<div>\n<img src=\"assets/local.png\">\n</div>")));

    [Fact]
    public void OnlyWebLinksAreClickable() => OnSta(() =>
    {
        var blocks = ReleaseNotes.Render("[local](file:///C:/Windows) and [web](https://example.com)");

        Assert.Equal(["local and web"], Texts(blocks[0]));
        Assert.Equal(["https://example.com/"], Links(blocks[0]).Select(link => link.NavigateUri.AbsoluteUri));
    });

    [Theory]
    [InlineData("https://github.com/o/r/commit/0123456789abcdef", "0123456")]
    [InlineData("https://github.com/o/r/pull/42", "#42")]
    [InlineData("https://github.com/o/r/issues/7", "#7")]
    [InlineData("https://github.com/o/r/compare/1.0.0...1.1.0", "1.0.0...1.1.0")]
    [InlineData("https://github.com/o/r/releases", "https://github.com/o/r/releases")]
    public void GitHubLinksShowTheirShortName(string url, string expected) =>
        Assert.Equal(expected, ReleaseNotes.LinkText(url));

    private static IEnumerable<string> Texts(UIElement element) => element switch
    {
        TextBlock text when text.Inlines.Count > 0 => [string.Concat(text.Inlines.Select(TextOf))],
        TextBlock text => [text.Text],
        Panel panel => panel.Children.OfType<UIElement>().SelectMany(Texts),
        Border border when border.Child is not null => Texts(border.Child),
        _ => [],
    };

    private static string TextOf(Inline inline) => inline switch
    {
        Run run => run.Text,
        LineBreak => "\n",
        Span span => string.Concat(span.Inlines.Select(TextOf)),
        _ => "",
    };

    private static IEnumerable<Hyperlink> Links(UIElement element) => element switch
    {
        TextBlock text => text.Inlines.SelectMany(Links),
        Panel panel => panel.Children.OfType<UIElement>().SelectMany(Links),
        Border border when border.Child is not null => Links(border.Child),
        _ => [],
    };

    private static IEnumerable<Hyperlink> Links(Inline inline) => inline switch
    {
        Hyperlink link => [link],
        Span span => span.Inlines.SelectMany(Links),
        _ => [],
    };

    // WPF elements can only be made on an STA thread, and xunit runs tests on MTA ones.
    private static void OnSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }
}
