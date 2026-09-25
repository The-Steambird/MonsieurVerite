using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MonsieurVerite;

public static class RecoveredKeys
{
    private static readonly JsonSerializerOptions
        SerializerOptions = new() { WriteIndented = true };

    /// <returns>Where an unreadable file was moved aside, or null.</returns>
    public static string? Add(string path, string stem, ulong videoKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(stem);

        var (root, setAside) = Load(path);
        if (root["list"] is not JsonArray list)
        {
            list = [];
            root["list"] = list;
        }

        JsonArray? target = null;
        foreach (var entry in list.OfType<JsonObject>())
        {
            if (entry["videos"] is not JsonArray videos)
            {
                continue;
            }

            for (var i = videos.Count - 1; i >= 0; i--)
            {
                if (videos[i] is JsonValue video && video.TryGetValue<string>(out var name) &&
                    name == stem)
                {
                    videos.RemoveAt(i);
                }
            }

            if (entry["videoKey"] is JsonValue value && value.TryGetValue<ulong>(out var key) &&
                key == videoKey)
            {
                target = videos;
            }
        }

        if (target is null)
        {
            target = [];
            list.Add(new JsonObject { ["videoKey"] = videoKey, ["videos"] = target });
        }

        target.Add(stem);

        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i] is JsonObject group && group["videos"] is JsonArray { Count: 0 })
            {
                list.RemoveAt(i);
            }
        }

        AtomicFile.WriteAllText(path, root.ToJsonString(SerializerOptions));
        return setAside;
    }

    private static (JsonObject Root, string? SetAside) Load(string path)
    {
        if (!File.Exists(path))
        {
            return ([], null);
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            parsed = null;
        }

        if (parsed is JsonObject root)
        {
            return (root, null);
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var setAside = Path.Combine(Path.GetDirectoryName(path) ?? "",
            $"{Path.GetFileNameWithoutExtension(path)}.unreadable-{stamp}{Path.GetExtension(path)}");
        File.Move(path, setAside);
        return ([], setAside);
    }
}
