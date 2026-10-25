using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace MonsieurVerite.Engine;

public sealed class EngineClient : IAsyncDisposable
{
    private const int StandardErrorTailCapacity = 20;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string executablePath;
    private readonly JobObject job = new();
    private readonly Queue<string> standardErrorTail = new();

    private readonly Channel<EngineEvent> events = Channel.CreateUnbounded<EngineEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private Process? process;
    private Task pumped = Task.CompletedTask;
    private bool disposed;

    public EngineClient(string executablePath) => this.executablePath = executablePath;

    /// <summary>Completes once the engine has exited and both of its pipes are drained.</summary>
    public ChannelReader<EngineEvent> Events => events.Reader;

    /// <summary>
    /// The last lines the engine wrote outside the protocol. They are final only once
    /// <see cref="Start"/>'s task has completed.
    /// </summary>
    public IReadOnlyList<string> StandardErrorTail => [.. standardErrorTail];

    /// <returns>The exit code, completing after the last event has been written.</returns>
    public Task<int> Start(IReadOnlyList<string> arguments, string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executablePath) ?? ".",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            StandardInputEncoding = Utf8,
        };
        startInfo.Environment["CHARLOTTE_LANG"] = Strings.Language;

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var started = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{executablePath}'.");
        process = started;
        job.Assign(started);
        var exit = PumpAsync(started);
        pumped = exit;
        return exit;
    }

    public void SendAnswer(string id, bool value) => Send(AnswerCommand(id, value));

    internal static string AnswerCommand(string id, bool value) =>
        JsonSerializer.Serialize(new { type = "answer", id, value });

    public void SendCancel() => Send("""{"type":"cancel"}""");

    public void SendSkip(string file) => Send(SkipCommand(file));

    internal static string SkipCommand(string file) =>
        JsonSerializer.Serialize(new { type = "skip", file });

    /// <summary>Ends the engine and everything it started.</summary>
    public void Kill() => job.Terminate();

    /// <summary>Ends the engine if it is still running and completes once it has exited.</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        job.Terminate();
        await pumped.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        job.Dispose();
        process?.Dispose();
        process = null;
    }

    private void Send(string json)
    {
        try
        {
            process!.StandardInput.Write(json + "\n");
            process.StandardInput.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The engine already exited and closed the pipe.
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
