using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MonsieurVerite.ViewModels;

public sealed partial class RunOptions : ObservableObject
{
    public static IReadOnlyList<Language> AudioLanguages { get; } =
    [
        new("ja", "日本語"),
        new("en", "English"),
        new("zh", "中文"),
        new("ko", "한국어"),
    ];

    public static IReadOnlyList<Language> SubtitleLanguages { get; } =
    [
        new("EN", "English"),
        new("JP", "日本語"),
        new("CHS", "简体中文"),
        new("CHT", "繁體中文"),
        new("KR", "한국어"),
        new("DE", "Deutsch"),
        new("ES", "Español"),
        new("FR", "Français"),
        new("ID", "Bahasa Indonesia"),
        new("IT", "Italiano"),
        new("PT", "Português"),
        new("RU", "Русский"),
        new("TH", "ภาษาไทย"),
        new("TR", "Türkçe"),
        new("VI", "Tiếng Việt"),
    ];

    public static IReadOnlyList<string> AudioCodecs { get; } = ["flac", "opus"];

    public static IReadOnlyList<string> Presets { get; } =
    [
        "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower",
        "veryslow", "placebo",
    ];

    public RunOptions()
    {
        DefaultAudio = "ja";
        DefaultSubtitle = "EN";
        AudioCodec = "flac";
        SkipExisting = true;
        // charlotte's own defaults (DEFAULT_CRF and DEFAULT_PRESET in stages/filter.py).
        Crf = 13.5;
        Preset = "slower";
        X265Params = "";
    }

    [ObservableProperty] public partial string DefaultAudio { get; set; }

    [ObservableProperty] public partial string DefaultSubtitle { get; set; }

    [ObservableProperty] public partial string AudioCodec { get; set; }

    [ObservableProperty] public partial bool KeepIntermediates { get; set; }

    [ObservableProperty] public partial bool SkipExisting { get; set; }

    [ObservableProperty] public partial bool FlatOutput { get; set; }

    [ObservableProperty] public partial bool UseVapourSynth { get; set; }

    [ObservableProperty] public partial double Crf { get; set; }

    [ObservableProperty] public partial string Preset { get; set; }

    [ObservableProperty] public partial string X265Params { get; set; }

    public void CopyFrom(RunOptions other)
    {
        ArgumentNullException.ThrowIfNull(other);
        DefaultAudio = other.DefaultAudio;
        DefaultSubtitle = other.DefaultSubtitle;
        AudioCodec = other.AudioCodec;
        KeepIntermediates = other.KeepIntermediates;
        SkipExisting = other.SkipExisting;
        FlatOutput = other.FlatOutput;
        UseVapourSynth = other.UseVapourSynth;
        Crf = other.Crf;
        Preset = other.Preset;
        X265Params = other.X265Params;
    }

    public RunOptions Clone()
    {
        var copy = new RunOptions();
        copy.CopyFrom(this);
        return copy;
    }

    public IReadOnlyList<string> ToArguments()
    {
        var arguments = new List<string>
        {
            "--default-audio", DefaultAudio,
            "--default-sub", DefaultSubtitle,
            "--audio-codec", AudioCodec,
        };

        if (KeepIntermediates)
        {
            arguments.Add("--no-cleanup");
        }

        if (SkipExisting)
        {
            arguments.Add("--skip-existing");
        }

        if (FlatOutput)
        {
            arguments.Add("--flat");
        }

        if (UseVapourSynth)
        {
            arguments.Add("--vapoursynth");
            arguments.Add("--crf");
            arguments.Add(Crf.ToString(CultureInfo.InvariantCulture));
            arguments.Add("--preset");
            arguments.Add(Preset);
            if (X265Params.Trim() is { Length: > 0 } parameters)
            {
                arguments.Add("--x265-params");
                arguments.Add(parameters);
            }
        }

        return arguments;
    }
}

public sealed record Language(string Code, string Name)
{
    public string Display => $"{Code} · {Name}";
}
