using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MonsieurVerite.ViewModels;

public sealed partial class RunOptions : ObservableObject
{
    public const string X265Tuning =
        "keyint=300:min-keyint=30:no-open-gop=1:aq-mode=3:aq-strength=1.0:qcomp=0.6:cbqpoffs=-2:crqpoffs=-2:no-cutree=1:psy-rd=2.0:psy-rdoq=1.7:no-strong-intra-smoothing=1:deblock=-2,-2:no-sao=1";

    public const string X265Effort = "ref=6:bframes=8:lookahead-slices=0:rd=4:max-merge=5:tskip=1";

    public const double DefaultCrf = 13;

    public const string DefaultPreset = "slower";

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

    /// <summary>
    /// Null means the default. The default spelled out is stored as null too, because a stored
    /// value would keep an install on an old default after a release changes it.
    /// </summary>
    public double? Crf
    {
        get;
        set
        {
            var stored = value == DefaultCrf ? null : value;
            if (SetProperty(ref field, stored))
            {
                OnPropertyChanged(nameof(EffectiveCrf));
            }
        }
    }

    [JsonIgnore]
    public double EffectiveCrf
    {
        get => Crf ?? DefaultCrf;
        set => Crf = Math.Clamp(value, 0, 51);
    }

    /// <summary>Null means the default, stored the same way as <see cref="Crf" />.</summary>
    public string? Preset
    {
        get;
        set
        {
            var stored = string.IsNullOrEmpty(value) || value == DefaultPreset ? null : value;
            if (!SetProperty(ref field, stored))
            {
                return;
            }

            OnPropertyChanged(nameof(EffectivePreset));
            if (X265Params is null)
            {
                OnPropertyChanged(nameof(X265ParamLines));
            }
        }
    }

    [JsonIgnore]
    public string EffectivePreset
    {
        get => Preset ?? DefaultPreset;
        set => Preset = value;
    }

    /// <summary>Null means the built-in tuning for the preset, and empty means the bare preset.</summary>
    [ObservableProperty] public partial string? X265Params { get; set; }

    [JsonIgnore]
    public string X265ParamLines
    {
        get => (X265Params ?? DefaultX265ParamsFor(EffectivePreset)).Replace(':', '\n');
        set
        {
            var lines = value.Split('\n',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var joined = string.Join(':', lines);
            X265Params = joined == DefaultX265ParamsFor(EffectivePreset) ? null : joined;
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
            arguments.Add(EffectiveCrf.ToString(CultureInfo.InvariantCulture));
            arguments.Add("--preset");
            arguments.Add(EffectivePreset);
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
