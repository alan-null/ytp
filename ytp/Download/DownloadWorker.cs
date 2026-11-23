using Microsoft.Extensions.Options;
using Ytp.Cache;
using Ytp.YtDlp;

namespace Ytp.Download;

/// <summary>Drains <see cref="DownloadQueue"/>: yt-dlp downloads to a temp dir, then the file is streamed into the cache.</summary>
public sealed class DownloadWorker(
    DownloadQueue queue,
    YtDlpClient ytDlp,
    IStreamCache cache,
    IOptions<CacheOptions> options,
    ILogger<DownloadWorker> logger) : BackgroundService
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "ytp-downloads");

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Math.Max(1, options.Value.MaxConcurrentDownloads);
        return Task.WhenAll(Enumerable.Range(0, workers).Select(_ => RunAsync(stoppingToken)));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await foreach (var videoId in queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await DownloadAsync(videoId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (YtDlpException e)
            {
                logger.LogWarning("Download of {VideoId} failed: {Error}", videoId, e.Message);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Download of {VideoId} failed", videoId);
            }
            finally
            {
                queue.MarkDone(videoId);
            }
        }
    }

    private async Task DownloadAsync(string videoId, CancellationToken ct)
    {
        if (await cache.GetInfoAsync(videoId, ct) is not null)
        {
            return;
        }

        var workDir = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            logger.LogInformation("Downloading {VideoId}", videoId);
            var path = await ytDlp.DownloadBestAudioAsync(videoId, workDir, ct);

            await using var file = File.OpenRead(path);
            var contentType = await SniffContentTypeAsync(file, ct) ?? ContentTypeFor(path);
            file.Position = 0;
            var entry = await cache.SaveAsync(videoId, file, contentType, ct);
            logger.LogInformation("Cached {VideoId} ({Bytes} bytes, {ContentType})", videoId, entry.Length, entry.ContentType);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
        }
    }

    // The extension can lie: HLS audio downloaded without ffmpeg is raw ADTS AAC in a ".mp4" file.
    private static async Task<string?> SniffContentTypeAsync(Stream file, CancellationToken ct)
    {
        var header = new byte[16];
        var read = await file.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        var type = SniffContainer(header.AsSpan(0, read), out var id3Size);
        if (type is not null || id3Size == 0)
        {
            return type;
        }

        // An ID3 tag can prefix both ADTS AAC and MP3; inspect the first frame after it.
        file.Position = id3Size;
        read = await file.ReadAtLeastAsync(header, 2, throwOnEndOfStream: false, ct);
        return SniffFrame(header.AsSpan(0, read));
    }

    private static string? SniffContainer(ReadOnlySpan<byte> h, out long id3Size)
    {
        id3Size = 0;
        if (h.Length >= 8 && h.Slice(4, 4).SequenceEqual("ftyp"u8)) return "audio/mp4";
        if (h.Length >= 4 && h[..4].SequenceEqual((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3])) return "audio/webm";
        if (h.Length >= 4 && h[..4].SequenceEqual("OggS"u8)) return "audio/ogg";
        if (h.Length >= 10 && h[..3].SequenceEqual("ID3"u8))
        {
            id3Size = 10 + ((h[6] & 0x7F) << 21 | (h[7] & 0x7F) << 14 | (h[8] & 0x7F) << 7 | (h[9] & 0x7F));
            return null;
        }
        return SniffFrame(h);
    }

    private static string? SniffFrame(ReadOnlySpan<byte> h)
    {
        if (h.Length < 2 || h[0] != 0xFF) return null;
        if ((h[1] & 0xF6) == 0xF0) return "audio/aac";  // ADTS sync word, layer 0
        if ((h[1] & 0xE0) == 0xE0) return "audio/mpeg"; // MPEG audio frame sync
        return null;
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".webm" => "audio/webm",
        ".m4a" or ".mp4" => "audio/mp4",
        ".opus" or ".ogg" => "audio/ogg",
        ".mp3" => "audio/mpeg",
        ".aac" => "audio/aac",
        _ => "application/octet-stream"
    };
}
