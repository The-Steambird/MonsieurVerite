using System.Windows.Controls;
using System.Windows.Documents;

namespace MonsieurVerite.Views;

public partial class AboutDialog : DialogWindow
{
    public AboutDialog(bool hasEngine, string? engineVersion)
    {
        InitializeComponent();

        TitleText.Text = $"charlotte {App.Version}";
        BuildText.Text = App.Build ?? Strings.UNKNOWN_BUILD;
        if (!hasEngine)
        {
            EngineText.Text = Strings.NO_ENGINE_FOUND;
        }
        else if (engineVersion is { } version)
        {
            EngineText.Text = $"charlotte-cli {version}";
        }
        else
        {
            EngineText.Text = "charlotte-cli";
        }

        AppendFormat(CreditsText, nameof(Strings.POWERED_BY),
            ("ffmpeg", Link("ffmpeg", "https://ffmpeg.org")),
            ("vapoursynth", Link("VapourSynth", "https://www.vapoursynth.com")),
            ("gi_cutscenes", Link("GI‑cutscenes", "https://github.com/ToaHartor/GI-cutscenes")),
            ("usm_diviner", Link("UsmDiviner", "https://github.com/Senkin219/UsmDiviner")));
        SponsorText.Inlines.Add(" ");
        AppendFormat(SponsorText, nameof(Strings.SPONSOR_PROMPT),
            ("link", Link(Strings.SPONSOR_LINK, "https://github.com/sponsors/lunarmint")));
    }

    private static Hyperlink Link(string text, string uri) =>
        new(new Run(text)) { NavigateUri = new Uri(uri) };

    // The text is split around its placeholders instead of written as runs in the xaml, because a
    // translation may move the links. Split puts each placeholder's name between the text around it.
    private static void AppendFormat(
        TextBlock block, string key, params (string Name, Inline Link)[] links)
    {
        var parts = Strings.Placeholder().Split(Strings.Template(key));
        for (var i = 0; i < parts.Length; i++)
        {
            if (i % 2 == 0)
            {
                block.Inlines.Add(new Run(parts[i]));
            }
            else
            {
                block.Inlines.Add(links.First(link => link.Name == parts[i]).Link);
            }
        }
    }
}
