using System.IO;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MonsieurVerite.Engine;

namespace MonsieurVerite.ViewModels;

/// <summary>
/// The capture dialog's state. Every key the engine reports is kept, because the "only new"
/// filter can still change after the capture has ended.
/// </summary>
public sealed partial class KeyCapture : ObservableObject
{
    private readonly string engine;
    private readonly Settings settings;
    private readonly List<CaptureKeyEvent> keys = [];
    private CaptureClient? client;

    public KeyCapture(string engine, Settings settings)
    {
        this.engine = engine;
        this.settings = settings;
        Status = new LogLine("");
        Text = "";
    }

    public Action<string>? CopyText { get; set; }

    /// <summary>The engine's lines go only to the main window's log.</summary>
    public Action<LogLine>? AppendLog { get; set; }

    public bool NewKeysOnly
    {
        get => settings.CaptureNewKeysOnly;
        set
        {
            if (settings.CaptureNewKeysOnly == value)
            {
                return;
            }

            settings.CaptureNewKeysOnly = value;
            OnPropertyChanged();
            Render();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonLabel))]
    public partial bool IsCapturing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonLabel))]
    [NotifyCanExecuteChangedFor(nameof(ToggleCommand))]
    public partial bool IsStopping { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleCommand))]
    public partial bool IsStarting { get; private set; }

    public string ButtonLabel
    {
        get
        {
            if (IsStopping)
            {
                return Strings.STOPPING;
            }

            return IsCapturing ? Strings.STOP : Strings.START;
        }
    }

    /// <summary>What the capture is doing, or the failure that ended it.</summary>
    [ObservableProperty] public partial LogLine Status { get; private set; }

    /// <summary>The shown keys as keys.json groups, joined so they paste into a videoGroups list.</summary>
    [ObservableProperty] public partial string Text { get; private set; }

    // The Start click's execution lasts the whole capture. Without concurrent executions the
    // toolkit would disable the command while it runs and grey out Stop.
    [RelayCommand(CanExecute = nameof(CanToggle), AllowConcurrentExecutions = true)]
    private async Task ToggleAsync()
    {
        if (IsCapturing)
        {
            IsStopping = true;
            Status = new LogLine(Strings.STOPPING);
            client?.SendCancel();
            return;
        }

        await CaptureAsync().ConfigureAwait(true);
    }

    // A stop sent before the engine has connected would reach nobody.
    private bool CanToggle() => !IsStopping && !IsStarting;

    [RelayCommand]
    private void Copy() => CopyText?.Invoke(Text);

    /// <summary>Stops a capture whose dialog is closing. The engine takes a closed pipe as a stop.</summary>
    public void Abandon() => client?.ClosePipe();

    private async Task CaptureAsync()
    {
        keys.Clear();
        Render();
        Status = new LogLine(Strings.CAPTURE_STARTING);
        IsCapturing = true;
        IsStarting = true;
        var started = new CaptureClient(engine);
        client = started;
        try
        {
            var failure = await started.StartAsync().ConfigureAwait(true);
            IsStarting = false;
            if (failure is not null)
            {
                Status = new LogLine(failure, IsWarning: true);
                AppendLog?.Invoke(Status);
                return;
            }

            Status = new LogLine(Strings.CAPTURE_RUNNING);
            await foreach (var evt in started.Events.ReadAllAsync().ConfigureAwait(true))
            {
                Apply(evt);
            }
        }
        finally
        {
            if (!Status.IsWarning)
            {
                Status = new LogLine(Strings.CAPTURE_STOPPED);
            }

            client = null;
            IsStarting = false;
            IsCapturing = false;
            IsStopping = false;
            await started.DisposeAsync().ConfigureAwait(true);
        }
    }

    internal void Apply(EngineEvent evt)
    {
        switch (evt)
        {
            case CaptureKeyEvent key:
                keys.Add(key);
                Render();
                break;
            case LogEvent log:
                AppendLog?.Invoke(LogLine.From(log));
                break;
            case ErrorEvent error:
                Status = new LogLine(error.Message, IsWarning: true);
                AppendLog?.Invoke(Status);
                break;
        }
    }

    private void Render()
    {
        var shown = keys.Where(key => !NewKeysOnly || !key.Known);
        Text = string.Join(",\n", shown.Select(Group));
    }

    /// <summary>One key in keys.json's own field order, because it is pasted there.</summary>
    internal static string Group(CaptureKeyEvent key)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            json.WriteStartObject();
            if (key.VideoKey is { } videoKey)
            {
                json.WriteNumber("videoKey", videoKey);
            }
            else
            {
                json.WriteNumber("audioKey", key.AudioKey ?? 0);
                json.WriteString("aesKey", key.AesKey);
            }

            json.WriteString("parentQuestId", key.ParentQuestId);
            json.WriteStartArray("videos");
            foreach (var video in key.Videos)
            {
                json.WriteStringValue(video);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
