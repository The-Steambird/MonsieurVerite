using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
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

    protected override void RemoveItem(int index)
    {
        this[index].PropertyChanged -= OnRowChanged;
        base.RemoveItem(index);
    }

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

    public void Save(string path)
    {
        var rows = this.Select(item => item.ToSaved()).ToList();
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(rows));
    }

    /// <summary>
    /// Reads a saved queue and deletes the file. A missing or unreadable one gives null, and the
    /// folder is probed as on any start.
    /// </summary>
    public static List<SavedRow>? TakeSaved(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<SavedRow>>(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed partial class QueueItem : ObservableObject
{
    public QueueItem(string fullPath)
    {
        FullPath = fullPath;
        FileName = Path.GetFileName(fullPath);
        var file = new FileInfo(fullPath);
        Size = file.Exists ? file.Length : 0;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionText))]
    public partial KeyState Key { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionOrder), nameof(VersionText))]
    public partial string? Version { get; set; }

    /// <summary>Blank until the probe has run, then "?" for a file keys.json has no version for.</summary>
    public string VersionText
    {
        get
        {
            if (Version is not null)
            {
                return Version;
            }

            return Key == KeyState.Unknown ? "" : "?";
        }
    }

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

    [ObservableProperty] public partial bool StreamCipher { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress), nameof(StatusLabel))]
    public partial ItemStatus Status { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    public partial string Detail { get; set; }

    public bool HasProgress =>
        Status is ItemStatus.Running or ItemStatus.Done or ItemStatus.Error;

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

    public void MarkCancelled() => SetState(ItemStatus.Cancelled, "", 0);

    public void ApplyProbe(ProbeEvent probe)
    {
        Key = probe.Key ? KeyState.Present : KeyState.Missing;
        Version = probe.Version;
        Subtitles = probe.Subtitles;
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

    public SavedRow ToSaved() => new(FullPath, IsChecked, Key, Version, Subtitles, StreamCipher, VideoKey);

    public static QueueItem Restore(SavedRow row) => new(row.FullPath)
    {
        IsChecked = row.IsChecked,
        Key = row.Key,
        Version = row.Version,
        Subtitles = row.Subtitles,
        StreamCipher = row.StreamCipher,
        VideoKey = row.VideoKey,
    };

    private void SetState(ItemStatus status, string detail, double progress)
    {
        Status = status;
        Detail = detail;
        Progress = progress;
    }
}

/// <summary>A row as a language restart hands it to the next instance, without its run status.</summary>
public sealed record SavedRow(
    string FullPath, bool IsChecked, KeyState Key, string? Version,
    IReadOnlyList<string>? Subtitles, bool StreamCipher, ulong? VideoKey);
