using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MonsieurVerite;

public static class Updater
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/The-Steambird/charlotte/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    public static async Task<IReadOnlyList<string>> InstallLatestAsync(
        string appDirectory, IProgress<string> status, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        ArgumentNullException.ThrowIfNull(status);

        status.Report("Finding download…");
        var url = await FindZipUrlAsync(cancellationToken).ConfigureAwait(false)
                  ?? throw new InvalidDataException(
                      "The latest release has no .zip asset to install.");

        var zipPath = Path.Combine(Path.GetTempPath(), "charlotte-update.zip");
        try
        {
            await DownloadAsync(url, zipPath, status, cancellationToken).ConfigureAwait(false);

            status.Report("Installing…");
            return Install(zipPath, appDirectory);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    private static async Task<string?> FindZipUrlAsync(CancellationToken cancellationToken)
    {
        using var response =
            await Http.GetAsync(LatestReleaseApi, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        return PickZipUrl(json);
    }

    internal static string? PickZipUrl(string releaseJson)
    {
        ArgumentNullException.ThrowIfNull(releaseJson);
        using var document = JsonDocument.Parse(releaseJson);
        if (!document.RootElement.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameProperty)
                ? nameProperty.GetString() ?? ""
                : "";
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && asset.TryGetProperty("browser_download_url", out var url))
            {
                return url.GetString();
            }
        }

        return null;
    }

    private static async Task DownloadAsync(
        string url, string destination, IProgress<string> status,
        CancellationToken cancellationToken)
    {
        using var response = await Http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var file = File.Create(destination);
        var buffer = new byte[64 * 1024];
        long done = 0;
        var shown = "";
        int read;
        while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            done += read;
            var text = total is { } bytes
                ? $"Downloading · {done * 100 / bytes}%"
                : $"Downloading · {done >> 20} MB";
            if (text != shown)
            {
                shown = text;
                status.Report(text);
            }
        }
    }

    public static IReadOnlyList<string> Install(string zipPath, string appDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory));
        using var archive = ZipFile.OpenRead(zipPath);
        // Windows archivers are known to write backslashes where the zip spec says slash.
        var entries = archive.Entries
            .Select(entry => (Entry: entry, Name: entry.FullName.Replace('\\', '/')))
            .Where(file => !file.Name.EndsWith('/'))
            .ToList();
        if (entries.Count == 0)
        {
            throw new InvalidDataException("The update archive is empty.");
        }

        var prefix = CommonFolder(entries.Select(file => file.Name));
        var movedAside = new List<(string Original, string Stale)>();
        var written = new List<string>();
        try
        {
            foreach (var (entry, name) in entries)
            {
                var relative = name[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
                var target = Path.GetFullPath(Path.Combine(root, relative));
                if (!target.StartsWith(root + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"The update archive tries to write outside the app folder: {name}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    var stale = target + ".old";
                    File.Delete(stale);
                    File.Move(target, stale);
                    movedAside.Add((target, stale));
                }

                entry.ExtractToFile(target);
                written.Add(target);
            }
        }
        catch
        {
            foreach (var path in written)
            {
                File.Delete(path);
            }

            for (var i = movedAside.Count - 1; i >= 0; i--)
            {
                File.Move(movedAside[i].Stale, movedAside[i].Original, overwrite: true);
            }

            throw;
        }

        return written;
    }

    public static void DeleteStaleFiles(string appDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        // The default output folder sits under the app folder, and an unreadable subfolder in it
        // must not fail the sweep, which runs at every startup.
        var options = new EnumerationOptions { RecurseSubdirectories = true };
        foreach (var stale in Directory.EnumerateFiles(appDirectory, "*.old", options))
        {
            try
            {
                File.Delete(stale);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Could not delete {stale}: {e.Message}");
            }
        }
    }

    private static string CommonFolder(IEnumerable<string> names)
    {
        string? prefix = null;
        foreach (var name in names)
        {
            var slash = name.IndexOf('/', StringComparison.Ordinal);
            var folder = slash < 0 ? "" : name[..(slash + 1)];
            if (folder.Length == 0 || (prefix is not null && prefix != folder))
            {
                return "";
            }

            prefix = folder;
        }

        return prefix ?? "";
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("charlotte-gui", App.Version));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
