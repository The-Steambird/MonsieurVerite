using System.IO;
using System.Text.RegularExpressions;

using MonsieurVerite.ViewModels;

using Xunit.Abstractions;

namespace MonsieurVerite.Tests;

/// <summary>Pins the settings-to-flag spelling. A renamed charlotte flag fails here first.</summary>
public class RunOptionsTests(ITestOutputHelper output)
{
    [Fact]
    public void DefaultsMatchCharlottes()
    {
        var options = new RunOptions();

        Assert.Equal(
            ["--default-audio", "ja", "--default-sub", "EN", "--audio-codec", "flac", "--skip-existing"],
            options.ToArguments());
    }

    [Fact]
    public void DefaultLanguagesAndCodecAreInTheDropdownLists()
    {
        // The dialog's dropdowns are populated from these lists, which is why the defaults must
        // be in them.
        var options = new RunOptions();

        Assert.Contains(RunOptions.AudioLanguages, language => language.Code == options.DefaultAudio);
        Assert.Contains(RunOptions.SubtitleLanguages, language => language.Code == options.DefaultSubtitle);
        Assert.Contains(options.AudioCodec, RunOptions.AudioCodecs);
        Assert.Contains(options.Preset, RunOptions.Presets);
    }

    [Fact]
    public void DefaultX265ParamsMatchCharlottes()
    {
        // The box shows charlotte's built-in tuning, which only its source knows. This skips
        // like the live tests when the sibling checkout is absent.
        if (Settings.ResolveEngine() is not { } engine)
        {
            output.WriteLine("SKIPPED: no sibling charlotte checkout with main.py found.");
            return;
        }

        var source = File.ReadAllText(Path.Combine(engine.WorkingDirectory, "stages", "filter.py"));

        // encode_args builds `tuning = [...]`, extends it with `tuning += [...]` for the presets
        // in `preset in (...)`; each is a Python literal of quoted strings.
        static IEnumerable<string> Quoted(string source, string opener) =>
            Regex.Matches(Regex.Match(source, opener + "(.*?)[\\])]", RegexOptions.Singleline).Groups[1].Value,
                    "\"([^\"]+)\"")
                .Select(match => match.Groups[1].Value);

        Assert.Equal(Quoted(source, @"tuning = \["), RunOptions.X265Tuning.Split(':'));
        Assert.Equal(Quoted(source, @"tuning \+= \["), RunOptions.X265Effort.Split(':'));
        Assert.Equal(Quoted(source, @"preset in \("), RunOptions.X265EffortPresets);
    }

    [Theory]
    [InlineData("ultrafast", false)]
    [InlineData("medium", false)]
    [InlineData("slow", true)]
    [InlineData("placebo", true)]
    public void EffortKnobsRideWithTheSlowPresets(string preset, bool effort)
    {
        var expected = effort ? $"{RunOptions.X265Tuning}:{RunOptions.X265Effort}" : RunOptions.X265Tuning;

        Assert.Equal(expected, RunOptions.DefaultX265ParamsFor(preset));
    }

    [Fact]
    public void ParamLinesShowTheTuningAndStoreOnlyEdits()
    {
        var options = new RunOptions();

        // Untouched, the box shows the tuning for the preset and follows the preset.
        Assert.Equal(RunOptions.DefaultX265ParamsFor("slower").Replace(':', '\n'), options.X265ParamLines);
        options.Preset = "fast";
        Assert.Equal(RunOptions.X265Tuning.Replace(':', '\n'), options.X265ParamLines);
        Assert.Null(options.X265Params);

        // Writing the tuning back, however spaced, still means "leave it to charlotte".
        options.X265ParamLines = RunOptions.X265Tuning.Replace(":", " \r\n\r\n");
        Assert.Null(options.X265Params);

        // Anything else is stored colon-joined and comes back one per line.
        options.X265ParamLines = "rd=4\r\n  psy-rd=2.0 \r\n\r\naq-mode=3:no-sao=1\n";
        Assert.Equal("rd=4:psy-rd=2.0:aq-mode=3:no-sao=1", options.X265Params);
        Assert.Equal("rd=4\npsy-rd=2.0\naq-mode=3\nno-sao=1", options.X265ParamLines);

        // An edited box stays put when the preset changes.
        options.Preset = "slower";
        Assert.Equal("rd=4:psy-rd=2.0:aq-mode=3:no-sao=1", options.X265Params);

        // Cleared means the bare preset, and stays cleared across presets.
        options.X265ParamLines = "";
        Assert.Equal("", options.X265Params);
        options.Preset = "fast";
        Assert.Equal("", options.X265ParamLines);
    }

    [Fact]
    public void ParamsFlagFollowsTheThreeStates()
    {
        var options = new RunOptions { HardSub = true };
        Assert.DoesNotContain("--x265-params", options.ToArguments());

        options.X265Params = "";
        Assert.Contains("--x265-params", options.ToArguments());
        Assert.Equal("", options.ToArguments()[^1]);

        options.X265Params = "rd=6";
        Assert.Equal(["--x265-params", "rd=6"], options.ToArguments().TakeLast(2));
    }

    [Fact]
    public void EveryToggleSpellsItsFlag()
    {
        var options = new RunOptions
        {
            DefaultAudio = "en",
            DefaultSubtitle = "JP",
            AudioCodec = "opus",
            KeepIntermediates = true,
            SkipExisting = true,
            FlatOutput = true,
            UseVapourSynth = true,
            HardSub = true,
        };

        Assert.Equal(
            [
                "--default-audio", "en", "--default-sub", "JP", "--audio-codec", "opus",
                "--no-cleanup", "--skip-existing", "--flat",
                "--vapoursynth", "--hard-sub", "--crf", "13.5", "--preset", "slower",
            ],
            options.ToArguments());
    }

    [Theory]
    [InlineData(true, false, "--vapoursynth")]
    [InlineData(false, true, "--hard-sub")]
    public void EncoderSettingsRideWithWhicheverOptionReencodes(bool vapourSynth, bool hardSub, string flag)
    {
        var options = new RunOptions { Crf = 18, Preset = "medium", X265Params = "aq-mode=3", SkipExisting = false };

        // With neither on, none of them appear, however they are set.
        Assert.DoesNotContain("--crf", options.ToArguments());
        Assert.False(options.Reencodes);

        options.UseVapourSynth = vapourSynth;
        options.HardSub = hardSub;
        Assert.True(options.Reencodes);
        Assert.Equal(
            [
                "--default-audio", "ja", "--default-sub", "EN", "--audio-codec", "flac",
                flag, "--crf", "18", "--preset", "medium", "--x265-params", "aq-mode=3",
            ],
            options.ToArguments());
    }

    [Fact]
    public void CrfIsSpelledWithADotWhateverTheCulture()
    {
        var options = new RunOptions { UseVapourSynth = true, Crf = 13.5 };
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            Assert.Contains("13.5", options.ToArguments());
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void CloneAndCopyFromCoverEveryValue()
    {
        // The dialog edits a clone and copies back on OK. A value missing from either would be
        // silently reset to its default on every OK.
        var original = new RunOptions
        {
            DefaultAudio = "ko",
            DefaultSubtitle = "TH",
            AudioCodec = "opus",
            KeepIntermediates = true,
            SkipExisting = false,
            FlatOutput = true,
            UseVapourSynth = true,
            HardSub = true,
            Crf = 20,
            Preset = "fast",
            X265Params = "x=1",
        };

        var copy = original.Clone();

        Assert.Equal(original.ToArguments(), copy.ToArguments());
        Assert.NotSame(original, copy);
    }
}
