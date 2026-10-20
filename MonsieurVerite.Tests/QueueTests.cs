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
}
