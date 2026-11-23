using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Ytp.Cache;

/// <summary>
/// Stores each entry as <c>{key}.data</c> plus a <c>{key}.json</c> metadata sidecar.
/// An entry exists only once its sidecar exists; writes go to a temp file and are renamed into place.
/// Last access is the data file's access time, set explicitly on every read (independent of mount options).
/// </summary>
public sealed partial class FileSystemStreamCache : IStreamCache
{
    private sealed record Metadata(string ContentType, DateTimeOffset CreatedAt);

    private readonly string root;
    private readonly ILogger<FileSystemStreamCache> logger;

    public FileSystemStreamCache(IOptions<CacheOptions> options, ILogger<FileSystemStreamCache> logger)
    {
        this.logger = logger;
        root = Path.GetFullPath(options.Value.Directory);
        Directory.CreateDirectory(root);

        // Leftovers from writes interrupted by a crash or restart.
        foreach (var tmp in Directory.EnumerateFiles(root, "*.tmp"))
        {
            TryDelete(tmp);
        }
        logger.LogInformation("File system cache at {Root}", root);
    }

    public Task<CacheEntry?> GetInfoAsync(string key, CancellationToken ct = default)
    {
        EnsureValidKey(key);
        return ReadEntryAsync(key, ct);
    }

    public async Task<CachedStream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        EnsureValidKey(key);
        var entry = await ReadEntryAsync(key, ct);
        if (entry is null)
        {
            return null;
        }

        FileStream stream;
        try
        {
            // FileShare.Delete: eviction can unlink the file while a client is still streaming it.
            stream = new FileStream(DataPath(key), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, bufferSize: 64 * 1024, useAsync: true);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        try { File.SetLastAccessTimeUtc(DataPath(key), now.UtcDateTime); } catch (IOException) { }
        return new CachedStream(stream, entry with { LastAccessedAt = now });
    }

    public Task<Uri?> GetDirectUrlAsync(string key, TimeSpan lifetime, CancellationToken ct = default) =>
        Task.FromResult<Uri?>(null);

    public async Task<CacheEntry> SaveAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        EnsureValidKey(key);
        var dataTmp = TempPath();
        var metaTmp = TempPath();
        try
        {
            await using (var file = new FileStream(dataTmp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 64 * 1024, useAsync: true))
            {
                await content.CopyToAsync(file, ct);
            }

            var metadata = new Metadata(contentType, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(metaTmp, JsonSerializer.Serialize(metadata), ct);

            // Data first, then the sidecar that makes the entry visible.
            File.Move(dataTmp, DataPath(key), overwrite: true);
            File.Move(metaTmp, MetaPath(key), overwrite: true);

            return new CacheEntry(key, new FileInfo(DataPath(key)).Length, contentType,
                metadata.CreatedAt, metadata.CreatedAt);
        }
        finally
        {
            TryDelete(dataTmp);
            TryDelete(metaTmp);
        }
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        EnsureValidKey(key);
        // Sidecar first: the entry disappears before its data does.
        var existed = File.Exists(MetaPath(key));
        TryDelete(MetaPath(key));
        TryDelete(DataPath(key));
        return Task.FromResult(existed);
    }

    public async IAsyncEnumerable<CacheEntry> ListAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var meta in Directory.EnumerateFiles(root, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            var key = Path.GetFileNameWithoutExtension(meta);
            if (!KeyPattern().IsMatch(key))
            {
                continue;
            }
            if (await ReadEntryAsync(key, ct) is { } entry)
            {
                yield return entry;
            }
        }
    }

    private async Task<CacheEntry?> ReadEntryAsync(string key, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(MetaPath(key), ct);
            var metadata = JsonSerializer.Deserialize<Metadata>(json);
            var data = new FileInfo(DataPath(key));
            if (metadata is null || !data.Exists)
            {
                return null;
            }
            return new CacheEntry(key, data.Length, metadata.ContentType, metadata.CreatedAt,
                new DateTimeOffset(data.LastAccessTimeUtc, TimeSpan.Zero));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (JsonException e)
        {
            logger.LogWarning(e, "Corrupt cache metadata for {Key}", key);
            return null;
        }
    }

    private string DataPath(string key) => Path.Combine(root, key + ".data");
    private string MetaPath(string key) => Path.Combine(root, key + ".json");
    private string TempPath() => Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex KeyPattern();

    private static void EnsureValidKey(string key)
    {
        if (!KeyPattern().IsMatch(key))
        {
            throw new ArgumentException($"Invalid cache key '{key}'.", nameof(key));
        }
    }
}
