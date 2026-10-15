using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace MonsieurVerite;

public static class Updater
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/The-Steambird/charlotte/releases/latest";

    private const string BundleFolder = "charlotte/";

    public static async Task<IReadOnlyList<string>> InstallLatestAsync(
        string appDirectory, IProgress<string> status, CancellationToken cancellationToken)
    {
        status.Report(Strings.FINDING_DOWNLOAD);
        var asset = await FindZipAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException(Strings.UPDATE_NO_ZIP);
        if (asset.Sha256 is not { } expected)
        {
            throw new InvalidDataException(Strings.UPDATE_NO_DIGEST);
        }

        var zipPath = Path.Combine(Path.GetTempPath(), $"charlotte-update-{Guid.NewGuid():N}.zip");
        try
        {
            var actual = await DownloadAndHashAsync(asset.Url, zipPath, status, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(Strings.UPDATE_DIGEST_MISMATCH(expected, actual));
            }

            status.Report(Strings.INSTALLING);
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
            await GitHub.Http.GetAsync(LatestReleaseApi, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        return PickZip(json);
    }

    internal static ReleaseAsset? PickZip(string releaseJson)
    {
        using var document = JsonDocument.Parse(releaseJson);
        if (!document.RootElement.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (GitHub.Text(asset, "name").EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && GitHub.Text(asset, "browser_download_url") is { Length: > 0 } url)
            {
                const string prefix = "sha256:";
                var digest = GitHub.Text(asset, "digest");
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

    private static async Task<string> DownloadAndHashAsync(
        string url, string destination, IProgress<string> status,
        CancellationToken cancellationToken)
    {
        using var response = await GitHub.Http
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
                ? Strings.DOWNLOADING_PERCENT(done * 100 / bytes)
                : Strings.DOWNLOADING_MEGABYTES(done >> 20);
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
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory));
        using var archive = ZipFile.OpenRead(zipPath);
        // Windows archivers are known to write backslashes where the zip spec says slash.
        var entries = archive.Entries
            .Select(entry => (Entry: entry, Name: entry.FullName.Replace('\\', '/')))
            .Where(file => !file.Name.EndsWith('/'))
            .Select(file => (file.Entry, Name: WithoutBundleFolder(file.Name)))
            .ToList();
        var movedAside = new List<(string Original, string Stale)>();
        var written = new List<string>();
        try
        {
            foreach (var (entry, name) in entries)
            {
                var relative = name.Replace('/', Path.DirectorySeparatorChar);
                var target = Path.GetFullPath(Path.Combine(root, relative));
                if (!target.StartsWith(root + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(Strings.UPDATE_ARCHIVE_ESCAPES(name));
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

            var names = string.Join(", ", unrestored.Select(Path.GetFileName));
            throw new IOException(Strings.UPDATE_ROLLBACK_FAILED(failure.Message, names), failure);
        }

        Translations.DeleteAll(root);
        return written;
    }

    private static string WithoutBundleFolder(string name)
    {
        if (name.StartsWith(BundleFolder, StringComparison.OrdinalIgnoreCase))
        {
            return name[BundleFolder.Length..];
        }

        return name;
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

    public static void Relaunch()
    {
        if (Environment.ProcessPath is not { } exe)
        {
            return;
        }

        var processId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        var successor = new ProcessStartInfo(exe, ["--wait-for", processId])
        {
            WorkingDirectory = AppContext.BaseDirectory,
        };
        Process.Start(successor)?.Dispose();
    }

    /// <summary>
    /// After an update the old instance starts this one before exiting, and its exe is one of the
    /// *.old files. Waiting lets the sweep delete it now rather than on the next launch.
    /// </summary>
    public static void WaitForPredecessor(string[] args)
    {
        if (args is not ["--wait-for", var text]
            || !int.TryParse(text, CultureInfo.InvariantCulture, out var processId))
        {
            return;
        }

        try
        {
            using var predecessor = Process.GetProcessById(processId);
            predecessor.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }
}
