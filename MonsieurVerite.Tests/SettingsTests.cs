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
            var saved = new Settings { SourceDirectory = @"D:\usm", OutputDirectory = @"D:\out" };
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
            Assert.Equal(saved.Options.ToArguments(), loaded.Options.ToArguments());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
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
