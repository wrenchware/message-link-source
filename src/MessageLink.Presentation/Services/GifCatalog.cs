using System.Net;
using System.Text.Json;

namespace MessageLink.Presentation.Services;

public sealed record CatalogGif(string Title, Uri Preview, Uri Original);
public sealed record GifPage(IReadOnlyList<CatalogGif> Items, int NextOffset, bool HasMore);
public interface IGifCatalog
{
    Task<GifPage> SearchAsync(string key, string query, int offset, CancellationToken token);
    Task<byte[]> DownloadAsync(Uri uri, CancellationToken token);
}
public sealed class GiphyCatalog(HttpClient http) : IGifCatalog
{
    public static Uri MediaUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !(uri.Host.EndsWith(".giphy.com", StringComparison.OrdinalIgnoreCase) || uri.Host == "giphy.com") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort) throw new InvalidDataException("Invalid GIF media address.");
        return uri;
    }
    public async Task<GifPage> SearchAsync(string key, string query, int offset, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Add your GIPHY API key in Settings first.");
        if (query.Length > 50 || offset < 0 || offset > (query.Length == 0 ? 499 : 4999)) throw new ArgumentOutOfRangeException(nameof(query));
        var endpoint = query.Length == 0 ? "trending" : "search";
        var url = $"https://api.giphy.com/v1/gifs/{endpoint}?api_key={Uri.EscapeDataString(key)}&limit=24&offset={offset}&rating=pg-13";
        if (query.Length > 0) url += "&q=" + Uri.EscapeDataString(query);
        var bytes = await ReadAsync(new Uri(url), 2 * 1024 * 1024, token);
        using var json = JsonDocument.Parse(bytes);
        var items = json.RootElement.GetProperty("data").EnumerateArray().Select(g =>
        {
            var images = g.GetProperty("images");
            return new CatalogGif(g.GetProperty("title").GetString() ?? "GIF",
                MediaUri(images.GetProperty("fixed_height").GetProperty("url").GetString()!),
                MediaUri(images.GetProperty("original").GetProperty("url").GetString()!));
        }).ToArray();
        var next = offset + items.Length;
        return new(items, next, items.Length > 0 && next < json.RootElement.GetProperty("pagination").GetProperty("total_count").GetInt32()
            && next <= (query.Length == 0 ? 499 : 4999));
    }
    public async Task<byte[]> DownloadAsync(Uri uri, CancellationToken token)
    {
        var bytes = await ReadAsync(MediaUri(uri.AbsoluteUri), 20 * 1024 * 1024, token);
        if (bytes.Length < 10 || !(bytes.AsSpan(0, 6).SequenceEqual("GIF89a"u8) || bytes.AsSpan(0, 6).SequenceEqual("GIF87a"u8)))
            throw new InvalidDataException("The selected file is not a GIF.");
        var width = bytes[6] | bytes[7] << 8; var height = bytes[8] | bytes[9] << 8;
        if (width == 0 || height == 0 || (long)width * height > 40_000_000) throw new InvalidDataException("GIF dimensions are too large.");
        return bytes;
    }
    private async Task<byte[]> ReadAsync(Uri uri, int limit, CancellationToken token)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new InvalidOperationException("GIPHY's hourly request limit was reached. Try again later.");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new InvalidOperationException("GIPHY rejected the API key. Check it in Settings.");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("GIPHY is unavailable. Try again later.");
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("GIF response is too large.");
        using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(); var buffer = new byte[65536]; int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("GIF response is too large.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return output.ToArray();
    }
}
