using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Ytp;
using Ytp.Auth;
using Ytp.Cache;
using Ytp.Download;
using Ytp.YtDlp;

// Resolve appsettings.json next to the binary, not the caller's working directory.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Request bodies are at most a /prefetch list of 500 IDs; refuse anything far larger.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 64 * 1024);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
builder.Services.AddSingleton<ApiKeys>();
builder.Services.AddSingleton<AudioLinkSigner>();
builder.Services.AddYtpRateLimits(builder.Configuration);

// Behind a reverse proxy, take the client IP and scheme from X-Forwarded-* headers, but only when the
// request comes from a trusted proxy (loopback by default, plus Proxy:KnownProxies). Otherwise a client
// could fake its IP and escape per-IP rate limits.
builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var proxy in builder.Configuration.GetSection("Proxy:KnownProxies").Get<string[]>() ?? [])
    {
        forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
    }
});

builder.Services.AddHttpClient();
builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection(CacheOptions.Section));
builder.Services.AddSingleton<YtDlpClient>();
builder.Services.AddSingleton<StreamUrlResolver>();
var cacheBackend = builder.Configuration.GetSection(CacheOptions.Section).Get<CacheOptions>()?.Backend ?? CacheBackend.FileSystem;
if (cacheBackend == CacheBackend.S3)
{
    builder.Services.AddSingleton<IStreamCache, S3StreamCache>();
}
else
{
    builder.Services.AddSingleton<IStreamCache, FileSystemStreamCache>();
}
builder.Services.AddSingleton<ICacheEvictionPolicy, IdleAndSizeEvictionPolicy>();
builder.Services.AddSingleton<DownloadQueue>();
builder.Services.AddHostedService<DownloadWorker>();
builder.Services.AddHostedService<CacheJanitor>();

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILogger<Program>>();

// Lifetime of direct (presigned) cache URLs; comparable to how long googlevideo URLs stay valid.
var directUrlLifetime = TimeSpan.FromHours(6);

// Fail at startup, not on the first request, when keys are missing or too weak.
app.Services.GetRequiredService<ApiKeys>();
app.Services.GetRequiredService<AudioLinkSigner>();

app.UseForwardedHeaders();
app.UseRouting();
app.UseRateLimiter(); // before the key check, so failed key attempts are rate limited too
app.UseApiKeys();

// Log which yt-dlp binary and version are used.
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
{
    var ytDlp = app.Services.GetRequiredService<YtDlpClient>();
    try
    {
        logger.LogInformation("Using yt-dlp {Path} ({Version})", ytDlp.ExecutablePath, await ytDlp.GetVersionAsync());
    }
    catch (YtDlpException e)
    {
        logger.LogError("yt-dlp is not usable: {Error}", e.Message);
    }
}));

app.Urls.Add("http://*:497");

// Returns a playable URL: ytp's own /audio endpoint when cached, otherwise a direct googlevideo URL.
// A failed lookup is an error response, never a URL. Caching happens through /prefetch
// (or, only when DownloadOnMiss is enabled, in the background after a miss).
app.MapGet("/bestAudio", async (string? v, HttpRequest request, IStreamCache cache, StreamUrlResolver resolver,
    DownloadQueue queue, IOptions<CacheOptions> options, AudioLinkSigner signer, CancellationToken ct) =>
{
    if (!VideoId.IsValid(v))
    {
        return Results.BadRequest("Parameter 'v' must be an 11-character YouTube video ID.");
    }

    logger.LogInformation("Requested VideoId: {VideoId}", v);

    if (await cache.GetInfoAsync(v!, ct) is not null)
    {
        var direct = await cache.GetDirectUrlAsync(v!, directUrlLifetime, ct);
        return Results.Json(new { url = direct?.ToString() ?? AudioUrl(request, options.Value, signer, v!) });
    }

    string url;
    try
    {
        url = await resolver.ResolveAsync(v!, ct);
    }
    catch (YtDlpException e)
    {
        return Results.Problem(e.Message, statusCode: StatusCodes.Status502BadGateway);
    }
    catch (LookupBusyException e)
    {
        request.HttpContext.Response.Headers.RetryAfter = "5";
        return Results.Problem(e.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (options.Value.DownloadOnMiss)
    {
        queue.TryEnqueue(v!);
    }
    return Results.Json(new { url });
}).RequireRateLimiting(RateLimits.Lookup);

// Streams cached audio with HTTP range support (seeking), or redirects to the store's direct URL.
app.MapMethods("/audio/{v}", ["GET", "HEAD"], async (string v, IStreamCache cache, CancellationToken ct) =>
{
    if (!VideoId.IsValid(v))
    {
        return Results.BadRequest("Invalid video ID.");
    }
    if (await cache.GetInfoAsync(v, ct) is null)
    {
        return Results.NotFound();
    }
    if (await cache.GetDirectUrlAsync(v, directUrlLifetime, ct) is { } direct)
    {
        return Results.Redirect(direct.ToString());
    }
    var cached = await cache.OpenReadAsync(v, ct);
    return cached is null
        ? Results.NotFound()
        : Results.Stream(cached.Content, cached.Entry.ContentType,
            lastModified: cached.Entry.CreatedAt, enableRangeProcessing: true);
}).AllowSignedAudioLink();

// Queues video IDs for download. Body: ["id1", "id2", ...]
app.MapPost("/prefetch", async (string[] ids, IStreamCache cache, DownloadQueue queue, CancellationToken ct) =>
{
    const int maxIds = 500;
    if (ids.Length > maxIds)
    {
        return Results.BadRequest($"At most {maxIds} IDs per request.");
    }

    var result = new Dictionary<string, List<string>>
    {
        ["queued"] = [], ["alreadyQueued"] = [], ["cached"] = [], ["invalid"] = [], ["queueFull"] = []
    };
    foreach (var id in ids.Distinct())
    {
        var bucket = !VideoId.IsValid(id) ? "invalid"
            : await cache.GetInfoAsync(id, ct) is not null ? "cached"
            : queue.TryEnqueue(id) switch
            {
                EnqueueResult.Queued => "queued",
                EnqueueResult.AlreadyQueued => "alreadyQueued",
                _ => "queueFull"
            };
        result[bucket].Add(id);
    }
    return Results.Json(result, statusCode: StatusCodes.Status202Accepted);
}).RequireRateLimiting(RateLimits.Prefetch);

app.MapGet("/cache", async (IStreamCache cache, DownloadQueue queue, CancellationToken ct) =>
{
    var entries = new List<CacheEntry>();
    await foreach (var entry in cache.ListAsync(ct))
    {
        entries.Add(entry);
    }
    return Results.Json(new
    {
        count = entries.Count,
        totalBytes = entries.Sum(e => e.Length),
        pendingDownloads = queue.PendingCount,
        entries = entries.OrderByDescending(e => e.LastAccessedAt)
    });
}).RequireAdmin();

app.MapDelete("/cache/{v}", async (string v, IStreamCache cache, CancellationToken ct) =>
    !VideoId.IsValid(v) ? Results.BadRequest("Invalid video ID.")
    : await cache.DeleteAsync(v, ct) ? Results.NoContent()
    : Results.NotFound()).RequireAdmin();

app.Run();

// Signed, so a media player can fetch it without the API key.
static string AudioUrl(HttpRequest request, CacheOptions options, AudioLinkSigner signer, string videoId)
{
    var baseUrl = string.IsNullOrWhiteSpace(options.PublicBaseUrl)
        ? $"{request.Scheme}://{request.Host}"
        : options.PublicBaseUrl.TrimEnd('/');
    return $"{baseUrl}/audio/{videoId}?{signer.Sign(videoId)}";
}
