using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MonsieurVerite.ViewModels;

public sealed partial class RunOptions : ObservableObject
{
    public const string X265Tuning =
        "keyint=300:min-keyint=30:no-open-gop=1:aq-mode=3:aq-strength=0.75:qcomp=0.72:cbqpoffs=-2:crqpoffs=-2:no-cutree=1:psy-rd=2.0:psy-rdoq=1.7:no-strong-intra-smoothing=1:deblock=-2,-2:no-sao=1:no-sao-non-deblock=1";

    public const string X265Effort = "ref=6:bframes=8:lookahead-slices=0:rd=4:max-merge=5:tskip=1";

    public static IReadOnlyList<string> X265EffortPresets { get; } =
        ["slow", "slower", "veryslow", "placebo"];

    public static string DefaultX265ParamsFor(string preset) =>
        X265EffortPresets.Contains(preset) ? $"{X265Tuning}:{X265Effort}" : X265Tuning;

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
        Crf = 13.5;
        Preset = "slower";
    }

    [ObservableProperty] public partial string DefaultAudio { get; set; }

    [ObservableProperty] public partial string DefaultSubtitle { get; set; }

    [ObservableProperty] public partial string AudioCodec { get; set; }

    [ObservableProperty] public partial bool KeepIntermediates { get; set; }

    [ObservableProperty] public partial bool SkipExisting { get; set; }

    [ObservableProperty] public partial bool FlatOutput { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Reencodes))]
    public partial bool UseVapourSynth { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Reencodes))]
    public partial bool HardSub { get; set; }

    [JsonIgnore] public bool Reencodes => UseVapourSynth || HardSub;

    [ObservableProperty] public partial double Crf { get; set; }

    [ObservableProperty] public partial string Preset { get; set; }

    /// <summary>Null = built-in tuning for the preset. Empty = bare preset.</summary>
    [ObservableProperty] public partial string? X265Params { get; set; }

    [JsonIgnore]
    public string X265ParamLines
    {
        get => (X265Params ?? DefaultX265ParamsFor(Preset)).Replace(':', '\n');
        set
        {
            var joined = string.Join(':',
                value.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));
            X265Params = joined == DefaultX265ParamsFor(Preset) ? null : joined;
        }
    }

    partial void OnPresetChanged(string oldValue, string newValue)
    {
        if (X265Params is null && DefaultX265ParamsFor(oldValue) != DefaultX265ParamsFor(newValue))
        {
            OnPropertyChanged(nameof(X265ParamLines));
        }
    }

    public RunOptions Clone() =>
        JsonSerializer.Deserialize<RunOptions>(JsonSerializer.Serialize(this))!;

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
        }

        if (HardSub)
        {
            arguments.Add("--hard-sub");
        }

        if (Reencodes)
        {
            arguments.Add("--crf");
            arguments.Add(Crf.ToString(CultureInfo.InvariantCulture));
            arguments.Add("--preset");
            arguments.Add(Preset);
            if (X265Params is not null)
            {
                arguments.Add("--x265-params");
                arguments.Add(X265Params);
            }
        }

        return arguments;
    }
}

public sealed record Language(string Code, string Name)
{
    public string Display => $"{Code} · {Name}";
}
