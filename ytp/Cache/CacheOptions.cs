namespace Ytp.Cache;

public sealed class CacheOptions
{
    public const string Section = "Cache";

    /// <summary>
    /// Cache directory. Use an absolute path for services (e.g. /var/cache/ytp); a relative path
    /// resolves against the working directory, never the binary's directory.
    /// </summary>
    public string Directory { get; set; } = "cache";

    /// <summary>Total size the janitor trims the cache down to (least recently used first).</summary>
    public long MaxBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    /// <summary>Entries not read for this long are removed.</summary>
    public TimeSpan MaxIdle { get; set; } = TimeSpan.FromDays(30);

    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Queue a background download when /bestAudio misses the cache. Off by default: the cache is
    /// filled only on demand through /prefetch, and a miss behaves exactly like a plain proxy.
    /// </summary>
    public bool DownloadOnMiss { get; set; }

    public int MaxConcurrentDownloads { get; set; } = 2;

    public int QueueCapacity { get; set; } = 1000;

    /// <summary>
    /// Base URL used in /bestAudio responses for cached audio, e.g. "https://ytp.example.com".
    /// When unset it is derived from the incoming request.
    /// </summary>
    public string? PublicBaseUrl { get; set; }
}
