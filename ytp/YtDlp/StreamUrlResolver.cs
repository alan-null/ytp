using System.Collections.Concurrent;

namespace Ytp.YtDlp;

/// <summary>Thrown when all yt-dlp lookup slots stay busy for too long.</summary>
public sealed class LookupBusyException() : Exception("Too many lookups in progress; try again shortly.");

/// <summary>Resolves direct googlevideo URLs and keeps them in memory while they stay valid.</summary>
public sealed class StreamUrlResolver
{
    public const string MaxConcurrentLookupsSetting = "YtDlp:MaxConcurrentLookups";

    // googlevideo URLs expire after about 6 hours; older entries are never worth a HEAD check.
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(5);
    private const int MaxEntries = 1000;
    private static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, (string Url, DateTimeOffset ResolvedAt)> urls = new();
    private readonly YtDlpClient ytDlp;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim lookupSlots;

    public StreamUrlResolver(YtDlpClient ytDlp, IHttpClientFactory httpClientFactory, TimeProvider time, IConfiguration configuration)
    {
        this.ytDlp = ytDlp;
        this.httpClientFactory = httpClientFactory;
        this.time = time;
        var slots = Math.Max(1, configuration.GetValue(MaxConcurrentLookupsSetting, 4));
        lookupSlots = new SemaphoreSlim(slots, slots);
    }

    public async Task<string> ResolveAsync(string videoId, CancellationToken ct = default)
    {
        if (urls.TryGetValue(videoId, out var cached))
        {
            if (time.GetUtcNow() - cached.ResolvedAt < MaxAge && await IsAccessibleAsync(cached.Url, ct))
            {
                return cached.Url;
            }
            urls.TryRemove(videoId, out _);
        }

        // Each lookup is a yt-dlp process: cap how many run at once so a burst of requests
        // cannot exhaust the machine or get its IP rate-limited by YouTube.
        if (!await lookupSlots.WaitAsync(SlotWait, ct))
        {
            throw new LookupBusyException();
        }
        string url;
        try
        {
            url = await ytDlp.GetBestAudioUrlAsync(videoId, ct);
        }
        finally
        {
            lookupSlots.Release();
        }

        Remember(videoId, url);
        return url;
    }

    private void Remember(string videoId, string url)
    {
        var now = time.GetUtcNow();
        if (urls.Count >= MaxEntries)
        {
            foreach (var (key, entry) in urls)
            {
                if (now - entry.ResolvedAt >= MaxAge)
                {
                    urls.TryRemove(key, out _);
                }
            }
            // Still full of fresh entries: drop the oldest tenth rather than growing without bound.
            if (urls.Count >= MaxEntries)
            {
                foreach (var (key, _) in urls.OrderBy(e => e.Value.ResolvedAt).Take(MaxEntries / 10))
                {
                    urls.TryRemove(key, out _);
                }
            }
        }
        urls[videoId] = (url, now);
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
