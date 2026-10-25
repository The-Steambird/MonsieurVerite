using System.IO;
using MonsieurVerite.ViewModels;

namespace MonsieurVerite.Tests;

public class QueueTests
{
    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "1 KB")]
    [InlineData(640L * 1024 * 1024, "640 MB")]
    [InlineData(1_503_238_553, "1.4 GB")]
    public void SizeIsFormattedForTheQueue(long bytes, string expected)
    {
        Assert.Equal(expected, QueueItem.FormatSize(bytes));
    }

    [Fact]
    public void ClearingTheQueueStopsListeningToTheOldRows()
    {
        var items = new QueueItems();
        var old = new QueueItem("a.usm");
        items.Add(old);
        items.Clear();
        var raised = false;
        items.RowChanged += (_, _) => raised = true;

        old.IsChecked = true;

        Assert.False(raised);
    }

    [Fact]
    public void RemovedRowsStopRaisingRowChanged()
    {
        var removed = new QueueItem("a.usm");
        var kept = new QueueItem("b.usm");
        var items = new QueueItems { removed, kept };
        items.Remove(removed);
        var senders = new List<object?>();
        items.RowChanged += (sender, _) => senders.Add(sender);

        removed.IsChecked = true;
        kept.IsChecked = true;

        Assert.Equal([kept], senders);
    }

    [Theory]
    [InlineData("5.3", "5.3")]
    [InlineData("not a version", "0.0")]
    [InlineData(null, null)]
    public void VersionSortsByNumberAndUnreadableVersionsFirst(string? version, string? expected)
    {
        var item = new QueueItem("a.usm") { Version = version };

        Assert.Equal(expected, item.VersionOrder?.ToString());
    }

    [Fact]
    public void ASavedQueueComesBackOnceWithItsProbeResults()
    {
        using var scratch = new ScratchFolder();
        var path = scratch.File("queue.json");
        var queue = new QueueItems
        {
            new QueueItem(scratch.File("a.usm"))
            {
                IsChecked = true, Key = KeyState.Recovered, Version = "6.1", Subtitles = ["EN", "JP"],
                StreamCipher = true, VideoKey = 6542161829131848,
            },
            new QueueItem(scratch.File("b.usm")) { Key = KeyState.Missing },
        };

        queue.Save(path);
        var rows = QueueItems.TakeSaved(path);

        Assert.NotNull(rows);
        var restored = rows.Select(QueueItem.Restore).ToList();
        Assert.Equal([scratch.File("a.usm"), scratch.File("b.usm")], restored.Select(item => item.FullPath));
        var first = restored[0];
        Assert.True(first.IsChecked);
        Assert.Equal(KeyState.Recovered, first.Key);
        Assert.Equal("6.1", first.Version);
        Assert.Equal(["EN", "JP"], first.Subtitles!);
        Assert.True(first.StreamCipher);
        Assert.Equal(6542161829131848UL, first.VideoKey);
        Assert.Equal(KeyState.Missing, restored[1].Key);
        Assert.Null(restored[1].Subtitles);
        Assert.False(File.Exists(path));
        Assert.Null(QueueItems.TakeSaved(path));
    }

    [Fact]
    public void AnUnreadableSavedQueueIsDroppedForAProbe()
    {
        using var scratch = new ScratchFolder();
        var path = scratch.File("queue.json");
        File.WriteAllText(path, "not json");

        Assert.Null(QueueItems.TakeSaved(path));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("5.3", KeyState.Present, "5.3")]
    [InlineData(null, KeyState.Missing, "?")]
    [InlineData(null, KeyState.Unknown, "")]
    public void AnUnknownVersionShowsOnlyOnceTheProbeHasRun(string? version, KeyState key, string expected)
    {
        var item = new QueueItem("a.usm") { Version = version, Key = key };

        Assert.Equal(expected, item.VersionText);
    }
}
