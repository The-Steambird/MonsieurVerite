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
        // The placeholder quotes charlotte's built-in tuning, which only its source knows. This
        // skips like the live tests when the sibling checkout is absent.
        if (Settings.ResolveEngine() is not { } engine)
        {
            output.WriteLine("SKIPPED: no sibling charlotte checkout with main.py found.");
            return;
        }

        // The tuning is the one place ffmpeg_params lists quoted key=value strings, one per line.
        var source = File.ReadAllText(Path.Combine(engine.WorkingDirectory, "stages", "filter.py"));
        var listed = Regex.Matches(source, """^\s+"([^"=]+=[^"]+)",$""", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value);

        Assert.Equal(listed, RunOptions.DefaultX265Params.Split(':'));
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
        };

        Assert.Equal(
            [
                "--default-audio", "en", "--default-sub", "JP", "--audio-codec", "opus",
                "--no-cleanup", "--skip-existing", "--flat",
                "--vapoursynth", "--crf", "13.5", "--preset", "slower",
            ],
            options.ToArguments());
    }

    [Fact]
    public void EncoderSettingsRideOnlyWithVapourSynth()
    {
        var options = new RunOptions { Crf = 18, Preset = "medium", X265Params = "aq-mode=3", SkipExisting = false };

        // With VapourSynth off none of them appear, however they are set.
        Assert.DoesNotContain("--crf", options.ToArguments());

        options.UseVapourSynth = true;
        Assert.Equal(
            [
                "--default-audio", "ja", "--default-sub", "EN", "--audio-codec", "flac",
                "--vapoursynth", "--crf", "18", "--preset", "medium", "--x265-params", "aq-mode=3",
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
            Crf = 20,
            Preset = "fast",
            X265Params = "x=1",
        };

        var copy = original.Clone();

        Assert.Equal(original.ToArguments(), copy.ToArguments());
        Assert.NotSame(original, copy);
    }
}
