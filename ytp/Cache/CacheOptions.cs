namespace Ytp.Cache;

public sealed class CacheOptions
{
    public const string Section = "Cache";

    /// <summary>Where entries are stored: <see cref="CacheBackend.FileSystem"/> (default) or <see cref="CacheBackend.S3"/>.</summary>
    public CacheBackend Backend { get; set; } = CacheBackend.FileSystem;

    /// <summary>Settings for <see cref="CacheBackend.S3"/>.</summary>
    public S3CacheOptions S3 { get; set; } = new();

    /// <summary>
    /// Cache directory for <see cref="CacheBackend.FileSystem"/>. Use an absolute path for services (e.g. /var/cache/ytp); a relative path
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

public enum CacheBackend { FileSystem, S3 }

/// <summary>
/// An S3-compatible bucket, e.g. Backblaze B2 (ServiceUrl "https://s3.eu-central-003.backblazeb2.com"),
/// Cloudflare R2 or MinIO. The bucket should be private; clients get presigned URLs.
/// </summary>
public sealed class S3CacheOptions
{
    /// <summary>Endpoint of the S3 API, e.g. "https://s3.eu-central-003.backblazeb2.com".</summary>
    public string ServiceUrl { get; set; } = "";

    /// <summary>Signing region. For B2 it is the middle part of the endpoint, e.g. "eu-central-003".</summary>
    public string Region { get; set; } = "us-east-1";

    public string Bucket { get; set; } = "";

    /// <summary>Key ID. Prefer the environment (Cache__S3__AccessKeyId) over appsettings.json.</summary>
    public string AccessKeyId { get; set; } = "";

    /// <summary>Key secret. Prefer the environment (Cache__S3__SecretAccessKey) over appsettings.json.</summary>
    public string SecretAccessKey { get; set; } = "";

    /// <summary>Prefix for all objects, so the bucket can be shared, e.g. "ytp/".</summary>
    public string Prefix { get; set; } = "";

    /// <summary>
    /// Hand clients a presigned bucket URL (in /bestAudio, and as a redirect from /audio) so the
    /// bytes bypass ytp. When false, ytp streams the object itself.
    /// </summary>
    public bool PresignedUrls { get; set; } = true;

    /// <summary>
    /// Last access is recorded by rewriting a small marker object; this limits it to once per interval
    /// per entry, which keeps write transactions low. Eviction by idle time is only as precise as this.
    /// </summary>
    public TimeSpan AccessUpdateInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Use path-style URLs (bucket in the path). Needed by MinIO; B2 and R2 work either way.</summary>
    public bool ForcePathStyle { get; set; }
}
