using System.IO;

namespace MonsieurVerite.Tests;

public class SettingsTests
{
    [Fact]
    public void RoundTripsThroughDiskIncludingTheRunOptions()
    {
        var folder = Path.Combine(Path.GetTempPath(), "MonsieurVerite.Tests", Path.GetRandomFileName());
        var path = Path.Combine(folder, "settings.json");
        try
        {
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
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void EnginePathStoresOnlyADeparture()
    {
        // The dialog hands back whatever is in the box, and the default spelled out is still
        // the default, so a later release that moves the GUI does not drag an old path along.
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
        var loaded = Settings.Load(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json"));

        Assert.Null(loaded.SourceDirectory);
        Assert.Equal("ja", loaded.Options.DefaultAudio);
        Assert.True(loaded.Options.SkipExisting);
    }

    [Fact]
    public void CorruptFileMeansDefaultsNotACrash()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(path, "{ not json");
        try
        {
            Assert.Equal("ja", Settings.Load(path).Options.DefaultAudio);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
