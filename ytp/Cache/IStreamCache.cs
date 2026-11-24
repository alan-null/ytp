namespace Ytp.Cache;

public sealed record CacheEntry(
    string Key,
    long Length,
    string ContentType,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAccessedAt);

/// <summary>An open cache entry. The caller owns <see cref="Content"/> and must dispose it.</summary>
public sealed record CachedStream(Stream Content, CacheEntry Entry);

/// <summary>
/// Byte store for cached media. Implementations: local file system and S3-compatible buckets (B2, R2, MinIO).
/// Keys are opaque, URL-safe strings (<c>[A-Za-z0-9_-]</c>, 1-128 chars).
/// </summary>
public interface IStreamCache
{
    Task<CacheEntry?> GetInfoAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Opens an entry for reading and marks it as accessed. Returns null when missing.
    /// Local implementations return a seekable stream, so HTTP range requests work.
    /// </summary>
    Task<CachedStream?> OpenReadAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// A URL the client can fetch directly (e.g. a presigned object-store URL), bypassing ytp.
    /// Null when the store has no such URL; the caller then serves the bytes itself.
    /// </summary>
    Task<Uri?> GetDirectUrlAsync(string key, TimeSpan lifetime, CancellationToken ct = default);

    /// <summary>
    /// Stores <paramref name="content"/> under <paramref name="key"/>, replacing any existing entry.
    /// Atomic: readers see either the old entry, the new one, or nothing; never a partial write.
    /// </summary>
    Task<CacheEntry> SaveAsync(string key, Stream content, string contentType, CancellationToken ct = default);

    /// <summary>Returns true when an entry existed and was removed.</summary>
    Task<bool> DeleteAsync(string key, CancellationToken ct = default);

    IAsyncEnumerable<CacheEntry> ListAsync(CancellationToken ct = default);
}
