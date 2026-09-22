using System.IO;

namespace MonsieurVerite.Tests;

public class SettingsTests : IDisposable
{
    private readonly ScratchFolder scratch = new();

    public void Dispose()
    {
        scratch.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void RoundTripsThroughDiskIncludingTheRunOptions()
    {
        var path = scratch.File("settings.json");
        var engine = scratch.File(Path.Combine("charlotte", "charlotte-cli.exe"));
        var saved = new Settings
        {
            SourceDirectory = scratch.File("usm"),
            OutputDirectory = scratch.File("out"),
            EnginePath = engine,
        };
        saved.Options.DefaultAudio = "en";
        saved.Options.DefaultSubtitle = "JP";
        saved.Options.AudioCodec = "opus";
        saved.Options.UseVapourSynth = true;
        saved.Options.Crf = 16;
        saved.Options.X265Params = "aq-mode=3";
        saved.Save(path);

        var loaded = Settings.Load(path);

        Assert.Equal(saved.SourceDirectory, loaded.SourceDirectory);
        Assert.Equal(saved.OutputDirectory, loaded.OutputDirectory);
        Assert.Equal(engine, loaded.EnginePath);
        Assert.Equal(scratch.File(Path.Combine("charlotte", "recovered_keys.json")), loaded.RecoveredKeysPath);
        Assert.Equal(saved.Options.ToArguments(), loaded.Options.ToArguments());
    }

    [Fact]
    public void EnginePathStoresOnlyADeparture()
    {
        // The default spelled out is still the default. Storing it would drag an old path along
        // when a later release moves the GUI.
        var settings = new Settings { EnginePath = Settings.DefaultEnginePath };
        Assert.Null(settings.EnginePath);
        Assert.Equal(Settings.DefaultEnginePath, settings.EffectiveEnginePath);

        settings.EnginePath = "  ";
        Assert.Null(settings.EnginePath);

        var elsewhere = scratch.File("charlotte-cli.exe");
        settings.EnginePath = elsewhere;
        Assert.Equal(elsewhere, settings.EffectiveEnginePath);
    }

    [Fact]
    public void MissingFileMeansDefaults()
    {
        var loaded = Settings.Load(scratch.File("settings.json"));

        Assert.Null(loaded.SourceDirectory);
        Assert.Equal("ja", loaded.Options.DefaultAudio);
        Assert.True(loaded.Options.SkipExisting);
    }

    [Fact]
    public void CorruptFileMeansDefaultsNotACrash()
    {
        var path = scratch.File("settings.json");
        File.WriteAllText(path, "{ not json");

        Assert.Equal("ja", Settings.Load(path).Options.DefaultAudio);
    }
}
