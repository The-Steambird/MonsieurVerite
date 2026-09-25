using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace MonsieurVerite.Engine;

public sealed class EngineClient : IDisposable
{
    private const int StandardErrorTailCapacity = 20;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly EngineLaunchProfile profile;
    private readonly JobObject job = new();
    private readonly Lock stdinGate = new();
    private readonly Queue<string> standardErrorTail = new();

    private readonly Channel<EngineEvent> events = Channel.CreateUnbounded<EngineEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private Process? process;
    private bool disposed;

    public EngineClient(EngineLaunchProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        this.profile = profile;
    }

    /// <summary>Completes once the engine has exited and both of its pipes are drained.</summary>
    public ChannelReader<EngineEvent> Events => events.Reader;

    /// <summary>
    /// The last lines the engine wrote outside the protocol, which are final only once
    /// <see cref="Start"/>'s task has completed.
    /// </summary>
    public IReadOnlyList<string> StandardErrorTail => [.. standardErrorTail];

    /// <returns>The exit code, completing after the last event has been written.</returns>
    public Task<int> Start(IReadOnlyList<string> arguments)
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
        return PumpAsync(started);
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

    /// <summary>Ends the engine and everything it started.</summary>
    public void Kill() => job.Terminate();

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
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // The engine already exited and closed the pipe.
            }
        }
    }

    private async Task<int> PumpAsync(Process target)
    {
        try
        {
            var stderr = PumpStandardErrorAsync(target);
            while (await target.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                events.Writer.TryWrite(EngineEvent.Parse(line));
            }

            await stderr.ConfigureAwait(false);
            await target.WaitForExitAsync().ConfigureAwait(false);
            return target.ExitCode;
        }
        finally
        {
            events.Writer.TryComplete();
        }
    }

    private async Task PumpStandardErrorAsync(Process target)
    {
        while (await target.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            Debug.WriteLine(line);
            standardErrorTail.Enqueue(line);
            if (standardErrorTail.Count > StandardErrorTailCapacity)
            {
                standardErrorTail.Dequeue();
            }
        }
    }
}
