using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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
        var asset = await FindZipAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException(
                        "The latest release has no .zip asset to install.");
        if (asset.Sha256 is not { } expected)
        {
            throw new InvalidDataException(
                "The latest release publishes no SHA-256 digest for its .zip, so it cannot be verified.");
        }

        var zipPath = Path.Combine(Path.GetTempPath(), $"charlotte-update-{Guid.NewGuid():N}.zip");
        try
        {
            var actual = await DownloadAndHashAsync(asset.Url, zipPath, status, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The download does not match the release's SHA-256 digest (expected {expected}, got {actual}).");
            }

            status.Report("Installing…");
            return Install(zipPath, appDirectory);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    internal sealed record ReleaseAsset(string Url, string? Sha256);

    private static async Task<ReleaseAsset?> FindZipAsync(CancellationToken cancellationToken)
    {
        using var response =
            await Http.GetAsync(LatestReleaseApi, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        return PickZip(json);
    }

    internal static ReleaseAsset? PickZip(string releaseJson)
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
            if (Text(asset, "name").EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && Text(asset, "browser_download_url") is { Length: > 0 } url)
            {
                const string prefix = "sha256:";
                var digest = Text(asset, "digest");
                string? sha256 = null;
                if (digest.StartsWith(prefix, StringComparison.Ordinal))
                {
                    sha256 = digest[prefix.Length..];
                }

                return new ReleaseAsset(url, sha256);
            }
        }

        return null;
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : "";

    private static async Task<string> DownloadAndHashAsync(
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
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long done = 0;
        var shown = "";
        int read;
        while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
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

        return Convert.ToHexStringLower(hash.GetHashAndReset());
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
        catch (Exception failure)
        {
            var unrestored = RollBack(written, movedAside);
            if (unrestored.Count == 0)
            {
                throw;
            }

            throw new IOException(
                $"{failure.Message} Restoring the previous version also failed for "
                + $"{string.Join(", ", unrestored.Select(Path.GetFileName))}; "
                + "reinstall from the release zip.",
                failure);
        }

        return written;
    }

    private static List<string> RollBack(
        List<string> written, List<(string Original, string Stale)> movedAside)
    {
        var unrestored = new List<string>();
        foreach (var path in written)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                unrestored.Add(path);
            }
        }

        for (var i = movedAside.Count - 1; i >= 0; i--)
        {
            try
            {
                File.Move(movedAside[i].Stale, movedAside[i].Original, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                unrestored.Add(movedAside[i].Original);
            }
        }

        return unrestored;
    }

    public static void DeleteStaleFiles(string appDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        // Only the top level is swept, because the bundle is flat and the app folder also holds the
        // user's output and subtitle cache.
        foreach (var stale in Directory.EnumerateFiles(appDirectory, "*.old"))
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
