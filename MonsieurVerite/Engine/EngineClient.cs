using System.IO;
using System.Text;
using System.Text.Json;

namespace MonsieurVerite.Engine;

public sealed class EngineClient : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly EngineLaunchProfile profile;
    private readonly JobObject job = new();
    private readonly Lock stdinGate = new();

    private Process? process;
    private bool disposed;

    public EngineClient(EngineLaunchProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        this.profile = profile;
    }

    public event Action<EngineEvent>? EventReceived;
    public event Action<string>? StandardErrorReceived;

    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (process is not null)
        {
            throw new InvalidOperationException("This client has already run an engine; create a new one per run.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = profile.FileName,
            WorkingDirectory = profile.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            StandardInputEncoding = Utf8,
        };

        foreach (var argument in profile.BaseArguments.Concat(arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var started = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{profile.FileName}'.");
        process = started;
        job.Assign(started);

        await using var registration = cancellationToken.Register(SendCancel).ConfigureAwait(false);

        var stdout = PumpStandardOutputAsync(started);
        var stderr = PumpStandardErrorAsync(started);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        await started.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        return started.ExitCode;
    }

    public void SendAnswer(string id, bool value)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        Send(AnswerCommand(id, value));
    }

    internal static string AnswerCommand(string id, bool value) =>
        JsonSerializer.Serialize(new { type = "answer", id, value });

    public void SendCancel() => Send("""{"type":"cancel"}""");

    public void SendSkip(string file)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        Send(SkipCommand(file));
    }

    internal static string SkipCommand(string file) =>
        JsonSerializer.Serialize(new { type = "skip", file });

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        job.Dispose();
        process?.Dispose();
        process = null;
    }

    private void Send(string json)
    {
        var target = process;
        if (target is null || disposed)
        {
            return;
        }

        lock (stdinGate)
        {
            try
            {
                target.StandardInput.Write(json + "\n");
                target.StandardInput.Flush();
            }
            catch (IOException)
            {
                // Engine already exited and closed the pipe.
            }
            catch (ObjectDisposedException)
            {
                // Same here.
            }
        }
    }

    private async Task PumpStandardOutputAsync(Process target)
    {
        while (await target.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            EventReceived?.Invoke(EngineEvent.Parse(line));
        }
    }

    private async Task PumpStandardErrorAsync(Process target)
    {
        while (await target.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            StandardErrorReceived?.Invoke(line);
        }
    }
}
