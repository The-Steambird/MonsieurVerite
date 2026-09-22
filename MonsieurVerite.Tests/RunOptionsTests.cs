using System.IO;
using System.Text.RegularExpressions;

using MonsieurVerite.ViewModels;

using Xunit.Abstractions;

namespace MonsieurVerite.Tests;

public class RunOptionsTests(ITestOutputHelper output)
{
    [Fact]
    public void DefaultsSpellOnlyTheFlagsThatAreOn()
    {
        var options = new RunOptions();

        Assert.Equal(
            ["--default-audio", "ja", "--default-sub", "EN", "--audio-codec", "flac", "--skip-existing"],
            options.ToArguments());
    }

    [Fact]
    public void DefaultLanguagesAndCodecAreInTheDropdownLists()
    {
        var options = new RunOptions();

        Assert.Contains(RunOptions.AudioLanguages, language => language.Code == options.DefaultAudio);
        Assert.Contains(RunOptions.SubtitleLanguages, language => language.Code == options.DefaultSubtitle);
        Assert.Contains(options.AudioCodec, RunOptions.AudioCodecs);
        Assert.Contains(options.Preset, RunOptions.Presets);
    }

    [Fact]
    public void DefaultX265ParamsMatchCharlottes()
    {
        if (Charlotte.Checkout is not { } checkout)
        {
            output.WriteLine("SKIPPED: no sibling charlotte checkout found.");
            return;
        }

        var source = File.ReadAllText(Path.Combine(checkout, "stages", "filter.py"));

        // encode_args builds `tuning = [...]`, extends it with `tuning += [...]` for the presets
        // in `preset in (...)`, and each is a Python literal of quoted strings.
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
    public void UntouchedParamLinesFollowThePreset()
    {
        var options = new RunOptions();

        Assert.Equal(RunOptions.DefaultX265ParamsFor("slower").Replace(':', '\n'), options.X265ParamLines);

        options.Preset = "fast";
        Assert.Equal(RunOptions.X265Tuning.Replace(':', '\n'), options.X265ParamLines);
        Assert.Null(options.X265Params);
    }

    [Fact]
    public void RetypingTheTuningStillMeansTheDefault()
    {
        var options = new RunOptions();

        options.X265ParamLines = RunOptions.DefaultX265ParamsFor(options.Preset).Replace(":", " \r\n\r\n");

        Assert.Null(options.X265Params);
    }

    [Fact]
    public void EditedParamLinesAreStoredColonJoinedAndSurviveAPresetChange()
    {
        var options = new RunOptions();

        options.X265ParamLines = "rd=4\r\n  psy-rd=2.0 \r\n\r\naq-mode=3:no-sao=1\n";
        Assert.Equal("rd=4:psy-rd=2.0:aq-mode=3:no-sao=1", options.X265Params);
        Assert.Equal("rd=4\npsy-rd=2.0\naq-mode=3\nno-sao=1", options.X265ParamLines);

        options.Preset = "fast";
        Assert.Equal("rd=4:psy-rd=2.0:aq-mode=3:no-sao=1", options.X265Params);
    }

    [Fact]
    public void ClearedParamLinesMeanTheBarePresetAndStayCleared()
    {
        var options = new RunOptions();

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
        Assert.Equal(["--x265-params", ""], options.ToArguments().TakeLast(2));

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
    public void CloneCopiesEveryValue()
    {
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

        Assert.NotSame(original, copy);
        Assert.Equal(original.ToArguments(), copy.ToArguments());
    }
}
