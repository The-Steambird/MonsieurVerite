using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using MonsieurVerite.Engine;

namespace MonsieurVerite.ViewModels;

public enum KeyState
{
    Unknown,
    Present,
    Missing,
    Recovered,
}

public enum ItemStatus
{
    Pending,
    Queued,
    Running,
    Done,
    Skipped,
    Error,
    Cancelled,
}

public sealed class QueueItems : ObservableCollection<QueueItem>
{
    public event PropertyChangedEventHandler? RowChanged;

    public IEnumerable<QueueItem> Checked => this.Where(item => item.IsChecked);

    public bool AllChecked => Count > 0 && this.All(item => item.IsChecked);

    public QueueItem? SingleChecked => Checked.Take(2).ToList() is [var only] ? only : null;

    public QueueItem? Find(string fileName) =>
        this.FirstOrDefault(item => item.FileName == fileName);

    protected override void InsertItem(int index, QueueItem item)
    {
        item.PropertyChanged += OnRowChanged;
        base.InsertItem(index, item);
    }

    protected override void SetItem(int index, QueueItem item)
    {
        this[index].PropertyChanged -= OnRowChanged;
        item.PropertyChanged += OnRowChanged;
        base.SetItem(index, item);
    }

    protected override void RemoveItem(int index)
    {
        this[index].PropertyChanged -= OnRowChanged;
        base.RemoveItem(index);
    }

    // Clear raises Reset without naming the removed rows, which is why they are unsubscribed here.
    protected override void ClearItems()
    {
        foreach (var item in this)
        {
            item.PropertyChanged -= OnRowChanged;
        }

        base.ClearItems();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) =>
        RowChanged?.Invoke(sender, e);
}

public sealed partial class QueueItem : ObservableObject
{
    public QueueItem(string fullPath)
    {
        FullPath = fullPath;
        FileName = Path.GetFileName(fullPath);
        var file = new FileInfo(fullPath);
        Size = file.Exists ? file.Length : 0;
        HasVsScript = true;
        Detail = "";
    }

    public string FullPath { get; }

    public string FileName { get; }

    public long Size { get; }

    public string SizeText => FormatSize(Size);

    public static string FormatSize(long bytes) => bytes switch
    {
        0 => "",
        < 1L << 20 => $"{Math.Max(1, bytes >> 10)} KB",
        < 1L << 30 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / (double)(1L << 30):0.0} GB",
    };

    [ObservableProperty] public partial bool IsChecked { get; set; }

    [ObservableProperty] public partial KeyState Key { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionOrder))]
    public partial string? Version { get; set; }

    public System.Version? VersionOrder
    {
        get
        {
            if (Version is null)
            {
                return null;
            }

            if (System.Version.TryParse(Version, out var parsed))
            {
                return parsed;
            }

            return new System.Version(0, 0);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSubtitles), nameof(SubtitlesTip))]
    public partial IReadOnlyList<string>? Subtitles { get; set; }

    public bool? HasSubtitles => Subtitles is null ? null : Subtitles.Count > 0;

    public string SubtitlesTip => Strings.CACHED_SUBTITLES_TIP(string.Join(", ", Subtitles ?? []));

    [ObservableProperty] public partial bool HasVsScript { get; set; }

    [ObservableProperty] public partial bool StreamCipher { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress), nameof(StatusLabel))]
    public partial ItemStatus Status { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    public partial string Detail { get; set; }

    public bool HasProgress =>
        Status is ItemStatus.Running or ItemStatus.Done or ItemStatus.Error or ItemStatus.Cancelled;

    public string StatusLabel => Status switch
    {
        ItemStatus.Running when Detail.Length > 0 => Detail,
        ItemStatus.Pending => Strings.STATUS_PENDING,
        ItemStatus.Queued => Strings.STATUS_QUEUED,
        ItemStatus.Running => Strings.STATUS_RUNNING,
        ItemStatus.Done => Strings.STATUS_DONE,
        ItemStatus.Skipped => Strings.STATUS_SKIPPED,
        ItemStatus.Error => Strings.STATUS_ERROR,
        ItemStatus.Cancelled => Strings.STATUS_CANCELLED,
        _ => "",
    };

    [ObservableProperty] public partial double Progress { get; set; }

    [ObservableProperty] public partial string? OutputPath { get; set; }

    [ObservableProperty] public partial ulong? VideoKey { get; set; }

    public void MarkQueued() => SetState(ItemStatus.Queued, "", 0);

    public void MarkRunning() => SetState(ItemStatus.Running, "", 0);

    public void EnterStage(string stage)
    {
        Detail = stage;
        Progress = 0;
    }

    public void MarkPending() => SetState(ItemStatus.Pending, "", Progress);

    public void MarkDone(string output)
    {
        SetState(ItemStatus.Done, "", 100);
        OutputPath = output;
    }

    public void MarkFailed(string message) => SetState(ItemStatus.Error, message, Progress);

    public void MarkSkipped(string reason) => SetState(ItemStatus.Skipped, reason, Progress);

    public void MarkCancelled() => SetState(ItemStatus.Cancelled, "", Progress);

    public void ApplyProbe(ProbeEvent probe)
    {
        Key = probe.Key ? KeyState.Present : KeyState.Missing;
        Version = probe.Version;
        Subtitles = probe.Subtitles;
        HasVsScript = probe.VsScript is not null;
        StreamCipher = probe.StreamCipher;
    }

    // A key the probe found in keys.json stays on a decline, because a decline says nothing
    // about keys.json.
    public void ApplyCrack(ulong? videoKey)
    {
        if (videoKey is { } key)
        {
            Key = KeyState.Recovered;
            VideoKey = key;
        }
        else if (Key == KeyState.Unknown)
        {
            Key = KeyState.Missing;
        }
    }

    private void SetState(ItemStatus status, string detail, double progress)
    {
        Status = status;
        Detail = detail;
        Progress = progress;
    }
}
