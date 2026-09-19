using System.IO;

using CommunityToolkit.Mvvm.ComponentModel;

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

public sealed partial class QueueItem : ObservableObject
{
    public QueueItem(string fullPath)
    {
        FullPath = fullPath;
        FileName = Path.GetFileName(fullPath);
        HasSubtitles = true;
        HasVsScript = true;
        Detail = "";
    }

    public string FullPath { get; }

    public string FileName { get; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    public partial KeyState Key { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionOrder))]
    public partial string? Version { get; set; }

    public System.Version? VersionOrder =>
        Version is null ? null : System.Version.TryParse(Version, out var parsed) ? parsed : new System.Version(0, 0);

    [ObservableProperty]
    public partial bool HasSubtitles { get; set; }

    [ObservableProperty]
    public partial bool HasVsScript { get; set; }

    [ObservableProperty]
    public partial ItemStatus Status { get; set; }

    [ObservableProperty]
    public partial string Detail { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string? OutputPath { get; set; }

    [ObservableProperty]
    public partial ulong? VideoKey { get; set; }
}
