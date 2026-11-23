using Microsoft.Extensions.Options;

namespace Ytp.Cache;

public interface ICacheEvictionPolicy
{
    IEnumerable<CacheEntry> SelectForEviction(IReadOnlyCollection<CacheEntry> entries, DateTimeOffset now);
}

/// <summary>Drops entries idle longer than MaxIdle, then the least recently used until under MaxBytes.</summary>
public sealed class IdleAndSizeEvictionPolicy(IOptions<CacheOptions> options) : ICacheEvictionPolicy
{
    public IEnumerable<CacheEntry> SelectForEviction(IReadOnlyCollection<CacheEntry> entries, DateTimeOffset now)
    {
        var o = options.Value;
        var total = entries.Sum(e => e.Length);

        foreach (var entry in entries.OrderBy(e => e.LastAccessedAt))
        {
            if (now - entry.LastAccessedAt > o.MaxIdle || total > o.MaxBytes)
            {
                total -= entry.Length;
                yield return entry;
            }
        }
    }
}

/// <summary>Periodically applies the eviction policy to the cache.</summary>
public sealed class CacheJanitor(
    IStreamCache cache,
    ICacheEvictionPolicy policy,
    IOptions<CacheOptions> options,
    ILogger<CacheJanitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.CleanupInterval);
        do
        {
            try
            {
                await CleanAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Cache cleanup failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task CleanAsync(CancellationToken ct)
    {
        var entries = new List<CacheEntry>();
        await foreach (var entry in cache.ListAsync(ct))
        {
            entries.Add(entry);
        }

        var evicted = 0;
        long freed = 0;
        foreach (var entry in policy.SelectForEviction(entries, DateTimeOffset.UtcNow).ToList())
        {
            if (await cache.DeleteAsync(entry.Key, ct))
            {
                evicted++;
                freed += entry.Length;
            }
        }

        if (evicted > 0)
        {
            logger.LogInformation("Cache cleanup evicted {Count} entries ({Bytes} bytes)", evicted, freed);
        }
    }
}
