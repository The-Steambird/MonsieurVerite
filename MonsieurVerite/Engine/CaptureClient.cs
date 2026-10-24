using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Threading.Channels;

namespace MonsieurVerite.Engine;

/// <summary>
/// Runs a key capture, which needs administrator rights. The engine is started through UAC, and
/// an elevated process cannot be handed this one's stdio, which is why its events and the stop
/// command travel over a named pipe instead. Closing the pipe stops the capture, because an
/// elevated engine is out of reach of the Job Object and of a kill from this process.
/// </summary>
public sealed class CaptureClient : IAsyncDisposable
{
    private const int UacDeclined = 1223;
    private readonly string executablePath;
    private readonly string pipeName = $"charlotte-capture-{Guid.NewGuid():N}";
    private readonly NamedPipeServerStream pipe;

    private readonly Channel<EngineEvent> events = Channel.CreateUnbounded<EngineEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private StreamWriter? writer;
    private Process? process;
    private Task pumped = Task.CompletedTask;

    public CaptureClient(string executablePath)
    {
        this.executablePath = executablePath;
        pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    /// <summary>Completes once the engine has closed its end of the pipe.</summary>
    public ChannelReader<EngineEvent> Events => events.Reader;

    /// <summary>The language goes as a flag because UAC does not pass the environment on.</summary>
    internal static IReadOnlyList<string> Arguments(string pipeName, string language) =>
        ["--capture", "--json", "--pipe", pipeName, "--lang", language];

    /// <returns>Why the engine never connected, or null once it has.</returns>
    public async Task<string?> StartAsync()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? ".",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in Arguments(pipeName, Strings.Language))
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            // The call returns only once the UAC prompt is answered, which is why it runs off the
            // UI thread.
            process = await Task.Run(() => Process.Start(startInfo)).ConfigureAwait(true)
                      ?? throw new InvalidOperationException($"Could not start '{executablePath}'.");
        }
        catch (Win32Exception e) when (e.NativeErrorCode == UacDeclined)
        {
            return Strings.CAPTURE_NEEDS_ADMIN;
        }

        // An engine that fails before it reaches the pipe never connects, which is why its exit
        // also ends the wait.
        var connected = pipe.WaitForConnectionAsync();
        await Task.WhenAny(connected, process.WaitForExitAsync()).ConfigureAwait(true);
        if (!pipe.IsConnected)
        {
            return Strings.CAPTURE_ENGINE_STOPPED;
        }

        writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        pumped = PumpAsync();
        return null;
    }

    public void SendCancel()
    {
        try
        {
            writer?.WriteLine("""{"type":"cancel"}""");
        }
        catch (IOException)
        {
            // The engine already closed its end.
        }
    }

    public void ClosePipe() => pipe.Dispose();

    public async ValueTask DisposeAsync()
    {
        await pipe.DisposeAsync().ConfigureAwait(true);
        await pumped.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        process?.Dispose();
    }

    private async Task PumpAsync()
    {
        try
        {
            using var reader = new StreamReader(pipe, leaveOpen: true);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                events.Writer.TryWrite(EngineEvent.Parse(line));
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // Closing the pipe from this side ends the read this way.
        }
        finally
        {
            events.Writer.TryComplete();
        }
    }
}
