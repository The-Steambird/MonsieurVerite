using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MonsieurVerite;

public static class GitHub
{
    internal static readonly HttpClient Http = CreateClient();

    internal static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : "";

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
