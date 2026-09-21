using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MonsieurVerite;

public static class RecoveredKeys
{
    private static readonly JsonSerializerOptions
        SerializerOptions = new() { WriteIndented = true };

    public static void Add(string path, string stem, ulong videoKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(stem);

        var root = Load(path);
        var list = root["list"] as JsonArray ?? [];
        root["list"] = list;

        JsonObject? target = null;
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
                target = entry;
            }
        }

        if (target is null)
        {
            target = new JsonObject { ["videoKey"] = videoKey, ["videos"] = new JsonArray() };
            list.Add(target);
        }

        ((JsonArray)target["videos"]!).Add(stem);

        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i] is JsonObject group && group["videos"] is JsonArray { Count: 0 })
            {
                list.RemoveAt(i);
            }
        }

        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, root.ToJsonString(SerializerOptions));
    }

    private static JsonObject Load(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
