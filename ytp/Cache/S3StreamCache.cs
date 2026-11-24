using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Ytp.Cache;

/// <summary>
/// Stores entries in an S3-compatible bucket (Backblaze B2, Cloudflare R2, MinIO).
/// <list type="bullet">
/// <item><c>{prefix}media/{key}</c> holds the bytes, with the entry's Content-Type. Its LastModified is the creation time.</item>
/// <item><c>{prefix}access/{key}</c> is an empty marker whose LastModified is the last access.</item>
/// </list>
/// Object stores have no access time and listings carry no Content-Type, so last access lives in the
/// marker (rewritten at most once per <see cref="S3CacheOptions.AccessUpdateInterval"/>) and content
/// types are remembered in memory after the first HEAD. A single PUT is atomic, so readers never see
/// partial entries.
/// </summary>
public sealed partial class S3StreamCache : IStreamCache, IDisposable
{
    private readonly S3CacheOptions options;
    private readonly AmazonS3Client client;
    private readonly ILogger<S3StreamCache> logger;
    private readonly ConcurrentDictionary<string, string> contentTypes = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastAccess = new();

    public S3StreamCache(IOptions<CacheOptions> cacheOptions, ILogger<S3StreamCache> logger)
    {
        options = cacheOptions.Value.S3;
        this.logger = logger;

        var missing = new[]
        {
            (nameof(options.ServiceUrl), options.ServiceUrl),
            (nameof(options.Bucket), options.Bucket),
            (nameof(options.AccessKeyId), options.AccessKeyId),
            (nameof(options.SecretAccessKey), options.SecretAccessKey)
        }.Where(s => string.IsNullOrWhiteSpace(s.Item2)).Select(s => $"Cache:S3:{s.Item1}").ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"S3 cache is not configured: set {string.Join(", ", missing)}.");
        }

        client = new AmazonS3Client(
            new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey),
            new AmazonS3Config
            {
                ServiceURL = options.ServiceUrl,
                AuthenticationRegion = options.Region,
                ForcePathStyle = options.ForcePathStyle,
                // B2 and other S3-compatible stores reject or ignore the newer default checksum headers.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
            });
        logger.LogInformation("S3 cache at {ServiceUrl}/{Bucket}/{Prefix}", options.ServiceUrl, options.Bucket, options.Prefix);
    }

    public async Task<CacheEntry?> GetInfoAsync(string key, CancellationToken ct = default)
    {
        EnsureValidKey(key);
        return await HeadAsync(key, ct);
    }

    public async Task<CachedStream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        EnsureValidKey(key);
        if (await HeadAsync(key, ct) is not { } entry)
        {
            return null;
        }
        var now = Touch(key);
        return new CachedStream(new S3RangeStream(client, options.Bucket, MediaKey(key), entry.Length),
            entry with { LastAccessedAt = now });
    }

    public Task<Uri?> GetDirectUrlAsync(string key, TimeSpan lifetime, CancellationToken ct = default)
    {
        EnsureValidKey(key);
        if (!options.PresignedUrls)
        {
            return Task.FromResult<Uri?>(null);
        }

        // Handing out a URL is the last point ytp sees; count it as an access.
        Touch(key);
        var url = client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = options.Bucket,
            Key = MediaKey(key),
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(lifetime),
            Protocol = options.ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTP : Protocol.HTTPS
        });
        return Task.FromResult<Uri?>(new Uri(url));
    }

    public async Task<CacheEntry> SaveAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        EnsureValidKey(key);

        // A single PUT needs the length up front; spool streams that cannot tell it.
        string? spool = null;
        var body = content;
        try
        {
            if (!content.CanSeek)
            {
                spool = Path.Combine(Path.GetTempPath(), "ytp-s3-" + Guid.NewGuid().ToString("N"));
                body = new FileStream(spool, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                await content.CopyToAsync(body, ct);
                body.Position = 0;
            }

            var length = body.Length - body.Position;
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = options.Bucket,
                Key = MediaKey(key),
                InputStream = body,
                AutoCloseStream = false,
                ContentType = contentType,
                UseChunkEncoding = false
            }, ct);

            contentTypes[key] = contentType;
            lastAccess.TryRemove(key, out _);
            var now = Touch(key);
            return new CacheEntry(key, length, contentType, now, now);
        }
        finally
        {
            if (spool is not null)
            {
                await body.DisposeAsync();
            }
        }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        EnsureValidKey(key);
        var existed = await HeadAsync(key, ct) is not null;
        // Media first: the entry disappears before its marker does.
        await client.DeleteObjectAsync(options.Bucket, MediaKey(key), ct);
        await client.DeleteObjectAsync(options.Bucket, AccessKey(key), ct);
        contentTypes.TryRemove(key, out _);
        lastAccess.TryRemove(key, out _);
        return existed;
    }

    public async IAsyncEnumerable<CacheEntry> ListAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var accessed = new Dictionary<string, DateTimeOffset>();
        await foreach (var marker in ListObjectsAsync(AccessPrefix, ct))
        {
            accessed[marker.Key[AccessPrefix.Length..]] = ToOffset(marker.LastModified);
        }

        await foreach (var media in ListObjectsAsync(MediaPrefix, ct))
        {
            var key = media.Key[MediaPrefix.Length..];
            if (!KeyPattern().IsMatch(key))
            {
                continue;
            }

            if (!contentTypes.TryGetValue(key, out var contentType))
            {
                if (await HeadAsync(key, ct) is not { } head)
                {
                    continue; // deleted while listing
                }
                contentType = head.ContentType;
            }

            var created = ToOffset(media.LastModified);
            var last = Max(created, accessed.GetValueOrDefault(key), lastAccess.GetValueOrDefault(key));
            yield return new CacheEntry(key, media.Size ?? 0, contentType, created, last);
        }
    }

    public void Dispose() => client.Dispose();

    private async Task<CacheEntry?> HeadAsync(string key, CancellationToken ct)
    {
        try
        {
            var head = await client.GetObjectMetadataAsync(options.Bucket, MediaKey(key), ct);
            var contentType = head.Headers.ContentType ?? "application/octet-stream";
            contentTypes[key] = contentType;
            var created = ToOffset(head.LastModified);
            return new CacheEntry(key, head.ContentLength, contentType, created,
                Max(created, lastAccess.GetValueOrDefault(key)));
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            contentTypes.TryRemove(key, out _);
            return null;
        }
    }

    /// <summary>Records an access; writes the marker in the background when the last write is older than the interval.</summary>
    private DateTimeOffset Touch(string key)
    {
        var now = DateTimeOffset.UtcNow;
        var previous = lastAccess.GetValueOrDefault(key);
        lastAccess[key] = now;
        if (now - previous < options.AccessUpdateInterval)
        {
            return now;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = options.Bucket,
                    Key = AccessKey(key),
                    ContentBody = "",
                    UseChunkEncoding = false
                });
            }
            catch (Exception e)
            {
                logger.LogWarning("Could not record access to {Key}: {Error}", key, e.Message);
            }
        });
        return now;
    }

    private async IAsyncEnumerable<S3Object> ListObjectsAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var request = new ListObjectsV2Request { BucketName = options.Bucket, Prefix = prefix };
        ListObjectsV2Response response;
        do
        {
            response = await client.ListObjectsV2Async(request, ct);
            foreach (var item in response.S3Objects ?? [])
            {
                yield return item;
            }
            request.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated == true);
    }

    private string MediaPrefix => options.Prefix + "media/";
    private string AccessPrefix => options.Prefix + "access/";
    private string MediaKey(string key) => MediaPrefix + key;
    private string AccessKey(string key) => AccessPrefix + key;

    private static DateTimeOffset ToOffset(DateTime? value) =>
        value is { } v ? new DateTimeOffset(v.ToUniversalTime(), TimeSpan.Zero) : DateTimeOffset.MinValue;

    private static DateTimeOffset Max(params DateTimeOffset[] values) => values.Max();

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
