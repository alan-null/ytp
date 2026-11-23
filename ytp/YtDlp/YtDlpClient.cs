using System.ComponentModel;
using System.Diagnostics;

namespace Ytp.YtDlp;

public sealed class YtDlpException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Thin wrapper around the yt-dlp executable.</summary>
public sealed class YtDlpClient(IConfiguration configuration, ILogger<YtDlpClient> logger)
{
    public const string PathSetting = "YtDlp:Path";

    // PublicationOnly: a failed lookup is retried on the next call instead of being cached.
    private readonly Lazy<string> executable = new(
        () => ResolveExecutable(configuration[PathSetting] is { Length: > 0 } configured ? configured : "yt-dlp"),
        LazyThreadSafetyMode.PublicationOnly);

    public string ExecutablePath => executable.Value;

    public async Task<string> GetVersionAsync(CancellationToken ct = default) =>
        LastLine(await RunAsync(["--version"], UrlTimeout, ct)) ?? "unknown";

    /// <summary>
    /// YtDlp:Path semantics, as in a shell: a value containing a directory separator is used as given;
    /// a bare name is looked up on PATH. Resolved here because Process.Start would also search the
    /// app directory and the working directory for a bare name.
    /// </summary>
    private static string ResolveExecutable(string configured)
    {
        if (configured.Contains(Path.DirectorySeparatorChar))
        {
            return File.Exists(configured)
                ? Path.GetFullPath(configured)
                : throw new YtDlpException($"{PathSetting} '{configured}' does not exist.");
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, configured);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        throw new YtDlpException($"'{configured}' not found on PATH. Install yt-dlp or set {PathSetting}.");
    }

    private static readonly TimeSpan UrlTimeout = TimeSpan.FromSeconds(60);

    // When YouTube forces SABR streaming, only HLS (m3u8) audio is left and plain "bestaudio" picks it.
    // URLs handed to clients must be direct media files: an m3u8 playlist looks like a valid URL but
    // plain-file players cannot play it. So URL lookups fail instead of falling back.
    private const string DirectAudioFormat = "bestaudio[protocol^=http]";

    public async Task<string> GetBestAudioUrlAsync(string videoId, CancellationToken ct = default)
    {
        var output = await RunAsync(["--get-url", "-f", DirectAudioFormat, "--no-playlist", "--", VideoId.ToWatchUrl(videoId)],
            UrlTimeout, ct);
        return LastLine(output) ?? throw new YtDlpException($"yt-dlp returned no URL for {videoId}.");
    }

    private async Task<string> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(ExecutablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // ArgumentList passes each value as one argv entry: no shell-style splitting or flag injection.
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new YtDlpException("yt-dlp could not be started.");
        }
        catch (Win32Exception e)
        {
            throw new YtDlpException("yt-dlp not found on PATH.", e);
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (ct.IsCancellationRequested) throw;
                throw new YtDlpException($"yt-dlp timed out after {timeout}.");
            }

            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0)
            {
                logger.LogWarning("yt-dlp exited with {ExitCode}: {Error}", process.ExitCode, error.Trim());
                throw new YtDlpException($"yt-dlp failed with exit code {process.ExitCode}.");
            }
            return output;
        }
    }

    private static string? LastLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
}
