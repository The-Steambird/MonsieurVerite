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
        var saved = new Settings
        {
            SourceDirectory = @"D:\usm", OutputDirectory = @"D:\out", EnginePath = @"D:\charlotte\charlotte-cli.exe",
        };
        saved.Options.DefaultAudio = "en";
        saved.Options.DefaultSubtitle = "JP";
        saved.Options.AudioCodec = "opus";
        saved.Options.UseVapourSynth = true;
        saved.Options.Crf = 16;
        saved.Options.X265Params = "aq-mode=3";
        saved.Save(path);

        var loaded = Settings.Load(path);

        Assert.Equal(@"D:\usm", loaded.SourceDirectory);
        Assert.Equal(@"D:\out", loaded.OutputDirectory);
        Assert.Equal(@"D:\charlotte\charlotte-cli.exe", loaded.EnginePath);
        Assert.Equal(@"D:\charlotte\recovered_keys.json", loaded.RecoveredKeysPath);
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

        settings.EnginePath = @"D:\charlotte\charlotte-cli.exe";
        Assert.Equal(@"D:\charlotte\charlotte-cli.exe", settings.EffectiveEnginePath);
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
