using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Block = Markdig.Syntax.Block;
using Inline = System.Windows.Documents.Inline;
using MdInline = Markdig.Syntax.Inlines.Inline;
using Table = Markdig.Extensions.Tables.Table;
using TableCell = Markdig.Extensions.Tables.TableCell;
using TableRow = Markdig.Extensions.Tables.TableRow;

namespace MonsieurVerite.Views;

/// <summary>Renders GitHub release notes as WPF elements, with Markdig doing the parsing.</summary>
internal static partial class ReleaseNotes
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseTaskLists()
        .UseAutoLinks()
        .UseEmojiAndSmiley(enableSmileys: false)
        .Build();

    public static List<FrameworkElement> Render(string markdown) => Blocks(Markdown.Parse(markdown, Pipeline));

    private static List<FrameworkElement> Blocks(ContainerBlock container)
    {
        var blocks = new List<FrameworkElement>();
        foreach (var block in container)
        {
            blocks.AddRange(Block(block));
        }

        return blocks;
    }

    private static List<FrameworkElement> Block(Block block) => block switch
    {
        HeadingBlock heading => [Heading(heading)],
        ParagraphBlock paragraph => Paragraph(paragraph),
        ListBlock list => [Items(list)],
        QuoteBlock quote => [Quote(quote)],
        HtmlBlock html => Html(html.Lines.ToString()),
        CodeBlock code => [Code(code)],
        ThematicBreakBlock => [Rule()],
        Table table => [TableGrid(table)],
        ContainerBlock container => Blocks(container),
        _ => [],
    };

    private static TextBlock Heading(HeadingBlock heading)
    {
        var block = Text(Inlines(heading.Inline));
        block.FontSize = heading.Level <= 2 ? 15 : 13;
        block.FontWeight = FontWeights.SemiBold;
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        block.Margin = new Thickness(0, 10, 0, 6);
        return block;
    }

    private static List<FrameworkElement> Paragraph(ParagraphBlock paragraph)
    {
        if (Pictures(paragraph.Inline) is { } pictures)
        {
            return pictures;
        }

        var block = Text(Inlines(paragraph.Inline));
        block.Margin = new Thickness(0, 0, 0, 10);
        block.SetResourceReference(TextBlock.ForegroundProperty, "DimBrush");
        return [block];
    }

    // GitHub shows a paragraph that holds nothing but images as the images themselves, which is
    // how a screenshot or a linked badge on its own line is meant to look.
    private static List<FrameworkElement>? Pictures(ContainerInline? container)
    {
        if (container is null)
        {
            return null;
        }

        var inlines = container.Where(inline => !IsSpace(inline)).ToList();
        if (inlines.Count == 0 || !inlines.All(IsPicture))
        {
            return null;
        }

        var pictures = new List<FrameworkElement>();
        foreach (var inline in inlines)
        {
            if (Picture(inline) is { } picture)
            {
                pictures.Add(picture);
            }
        }

        return pictures;
    }

    private static bool IsSpace(MdInline inline) => inline switch
    {
        LineBreakInline => true,
        LiteralInline literal => literal.Content.IsEmptyOrWhitespace(),
        HtmlInline html => !ImageTag().IsMatch(html.Tag),
        _ => false,
    };

    private static bool IsPicture(MdInline inline) => inline switch
    {
        LinkInline { IsImage: true } => true,
        LinkInline { FirstChild: LinkInline { IsImage: true } image } link => image == link.LastChild,
        HtmlInline html => ImageTag().IsMatch(html.Tag),
        _ => false,
    };

    private static Image? Picture(MdInline inline) => inline switch
    {
        LinkInline { IsImage: true } image => Picture(image.Url, null),
        LinkInline { FirstChild: LinkInline image } link => Picture(image.Url, link.Url),
        HtmlInline html => Picture(Source(html.Tag), null),
        _ => null,
    };

    private static StackPanel Items(ListBlock list)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var number = 1;
        if (int.TryParse(list.OrderedStart, out var start))
        {
            number = start;
        }

        foreach (var item in list.OfType<ListItemBlock>())
        {
            var marker = "•";
            if (list.IsOrdered)
            {
                marker = $"{number++}{list.OrderedDelimiter}";
            }

            panel.Children.Add(Item(item, marker, list.IsLoose));
        }

        return panel;
    }

    private static Grid Item(ListItemBlock item, string marker, bool loose)
    {
        var bullet = Text([new Run(marker)]);
        if (item.Count > 0 && item[0] is ParagraphBlock { Inline.FirstChild: TaskList task })
        {
            bullet = Text([new Run(task.Checked ? "\uE73A" : "\uE739")]);
            bullet.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        }

        bullet.MinWidth = 18;
        bullet.Margin = new Thickness(0, 0, 4, 0);
        bullet.SetResourceReference(TextBlock.ForegroundProperty, "FaintBrush");

        var content = new StackPanel();
        foreach (var block in Blocks(item))
        {
            if (!loose)
            {
                block.Margin = new Thickness(0);
            }

            content.Children.Add(block);
        }

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(bullet);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    private static Border Quote(QuoteBlock quote)
    {
        var content = new StackPanel();
        foreach (var block in Blocks(quote))
        {
            content.Children.Add(block);
        }

        var border = new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, 10),
            Child = content,
        };
        border.SetResourceReference(Border.BorderBrushProperty, "LineStrongBrush");
        return border;
    }

    // An HTML block keeps its images and its text, and its tags are dropped, because release notes
    // use HTML for layout such as a centered image and not for anything a dialog could show.
    private static List<FrameworkElement> Html(string html)
    {
        var blocks = new List<FrameworkElement>();
        foreach (Match image in ImageTag().Matches(html))
        {
            if (Picture(Source(image.Value), null) is { } picture)
            {
                blocks.Add(picture);
            }
        }

        var text = ImageTag().Replace(html, "");
        text = BreakTag().Replace(text, "\n");
        text = Tag().Replace(text, "");
        text = WebUtility.HtmlDecode(text).Trim();
        if (text.Length > 0)
        {
            var block = Text([new Run(text)]);
            block.Margin = new Thickness(0, 0, 0, 10);
            block.SetResourceReference(TextBlock.ForegroundProperty, "DimBrush");
            blocks.Add(block);
        }

        return blocks;
    }

    private static Border Code(CodeBlock code)
    {
        var text = new TextBlock { Text = code.Lines.ToString(), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        text.SetResourceReference(TextBlock.ForegroundProperty, "DimBrush");

        var border = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 10),
            Child = text,
        };
        border.SetResourceReference(Border.BackgroundProperty, "LayerBrush");
        return border;
    }

    private static Border Rule()
    {
        var rule = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 14) };
        rule.SetResourceReference(Border.BackgroundProperty, "LineBrush");
        return rule;
    }

    private static Grid TableGrid(Table table)
    {
        var rows = table.OfType<TableRow>().ToList();
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        var columns = rows.Max(row => row.Count);
        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        }

        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cells = rows[r].OfType<TableCell>().ToList();
            for (var c = 0; c < cells.Count; c++)
            {
                var cell = Cell(cells[c], rows[r].IsHeader);
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        return grid;
    }

    private static Border Cell(TableCell cell, bool header)
    {
        var content = new StackPanel();
        foreach (var block in Blocks(cell))
        {
            block.Margin = new Thickness(0);
            content.Children.Add(block);
        }

        if (header)
        {
            TextElement.SetFontWeight(content, FontWeights.SemiBold);
        }

        var border = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 6, 12, 6),
            Child = content,
        };
        border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        return border;
    }

    private static Image? Picture(string? source, string? link)
    {
        if (WebLink(source) is not { } uri)
        {
            return null;
        }

        var image = new Image
        {
            Stretch = Stretch.Uniform,
            MaxHeight = 280,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        if (WebLink(link) is { } target)
        {
            image.Cursor = Cursors.Hand;
            image.MouseLeftButtonUp += (_, _) => image.RaiseEvent(
                new RequestNavigateEventArgs(target, null) { RoutedEvent = Hyperlink.RequestNavigateEvent });
        }

        _ = LoadAsync(image, uri);
        return image;
    }

    // The image is shown at its pixel size like a browser shows it, because the DPI in the file is
    // arbitrary. Decoding stops at 1024 pixels wide, which keeps a 4K screenshot from taking 50 MB.
    private static async Task LoadAsync(Image image, Uri source)
    {
        try
        {
            var bytes = await GitHub.Http.GetByteArrayAsync(source).ConfigureAwait(true);
            var width = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None).Frames[0].PixelWidth;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.DecodePixelWidth = Math.Min(width, 1024);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            image.MaxWidth = width;
            image.Source = bitmap;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or NotSupportedException
                                      or FormatException or IOException)
        {
            // An image that cannot be fetched or decoded stays empty and takes no space.
        }
    }

    private static TextBlock Text(List<Inline> inlines)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        block.Inlines.AddRange(inlines);
        return block;
    }

    private static List<Inline> Inlines(ContainerInline? container)
    {
        var inlines = new List<Inline>();
        if (container is null)
        {
            return inlines;
        }

        foreach (var inline in container)
        {
            if (Inline(inline) is { } converted)
            {
                inlines.Add(converted);
            }
        }

        return inlines;
    }

    // A soft line break becomes a real one because GitHub renders release notes that way.
    private static Inline? Inline(MdInline inline) => inline switch
    {
        LiteralInline { PreviousSibling: TaskList } literal => new Run(literal.Content.ToString().TrimStart()),
        LiteralInline literal => new Run(literal.Content.ToString()),
        HtmlEntityInline entity => new Run(entity.Transcoded.ToString()),
        CodeInline code => CodeSpan(code.Content),
        LineBreakInline => new LineBreak(),
        HtmlInline html when BreakTag().IsMatch(html.Tag) => new LineBreak(),
        EmphasisInline emphasis => Emphasis(emphasis),
        LinkInline { IsImage: true } image => Group(Inlines(image)),
        LinkInline link => Link(link.Url, link.IsAutoLink, Inlines(link)),
        AutolinkInline { IsEmail: false } autolink => Link(autolink.Url, true, []),
        ContainerInline container => Group(Inlines(container)),
        _ => null,
    };

    private static Run CodeSpan(string text)
    {
        var code = new Run(text);
        code.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
        code.SetResourceReference(TextElement.BackgroundProperty, "CheckedBrush");
        return code;
    }

    private static Span Emphasis(EmphasisInline emphasis)
    {
        Span span;
        if (emphasis.DelimiterChar == '~')
        {
            span = new Span { TextDecorations = TextDecorations.Strikethrough };
        }
        else if (emphasis.DelimiterCount >= 2)
        {
            span = new Bold();
        }
        else
        {
            span = new Italic();
        }

        span.Inlines.AddRange(Inlines(emphasis));
        return span;
    }

    private static Span Link(string? url, bool bare, List<Inline> label)
    {
        if (bare || label.Count == 0)
        {
            label = [new Run(LinkText(url ?? ""))];
        }

        var span = new Span();
        if (WebLink(url) is { } uri)
        {
            span = new Hyperlink { NavigateUri = uri };
        }

        span.Inlines.AddRange(label);
        return span;
    }

    private static Span Group(List<Inline> inlines)
    {
        var span = new Span();
        span.Inlines.AddRange(inlines);
        return span;
    }

    // GitHub shows a bare link to a commit, pull request or comparison by its short name, and the
    // full URLs would fill most of a line each.
    internal static string LinkText(string url)
    {
        var match = GitHubLink().Match(url);
        if (!match.Success)
        {
            return url;
        }

        var target = match.Groups[2].Value;
        return match.Groups[1].Value switch
        {
            "commit" => target[..Math.Min(7, target.Length)],
            "compare" => target,
            _ => "#" + target,
        };
    }

    internal static Uri? WebLink(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return null;
        }

        return uri;
    }

    private static string? Source(string tag)
    {
        var source = SourceAttribute().Match(tag);
        if (!source.Success)
        {
            return null;
        }

        return WebUtility.HtmlDecode(source.Groups[1].Value);
    }

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImageTag();

    [GeneratedRegex(@"\bsrc\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex SourceAttribute();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTag();

    [GeneratedRegex(@"<!--.*?-->|</?[a-zA-Z][^>]*>", RegexOptions.Singleline)]
    private static partial Regex Tag();

    [GeneratedRegex(@"^https://github\.com/[\w.-]+/[\w.-]+/(commit|pull|issues|compare)/([^/?#]+)$")]
    private static partial Regex GitHubLink();
}
