using System.Collections.Concurrent;

namespace Ytp.YtDlp;

/// <summary>Resolves direct googlevideo URLs and keeps them in memory while they stay valid.</summary>
public sealed class StreamUrlResolver(YtDlpClient ytDlp, IHttpClientFactory httpClientFactory)
{
    private readonly ConcurrentDictionary<string, string> urls = new();

    public async Task<string> ResolveAsync(string videoId, CancellationToken ct = default)
    {
        if (urls.TryGetValue(videoId, out var cached))
        {
            if (await IsAccessibleAsync(cached, ct))
            {
                return cached;
            }
            urls.TryRemove(videoId, out _);
        }

        var url = await ytDlp.GetBestAudioUrlAsync(videoId, ct);
        urls[videoId] = url;
        return url;
    }

    private async Task<bool> IsAccessibleAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await httpClientFactory.CreateClient()
                .SendAsync(new HttpRequestMessage(HttpMethod.Head, url), ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
