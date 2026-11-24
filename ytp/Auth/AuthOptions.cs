namespace Ytp.Auth;

public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>Shortest accepted key or signing secret; shorter values are rejected at startup.</summary>
    public const int MinSecretLength = 32;

    /// <summary>Keys for the regular endpoints (/bestAudio, /prefetch, /audio).</summary>
    public string[] Keys { get; set; } = [];

    /// <summary>Keys that can also read and modify the cache (/cache). Admin keys work everywhere.</summary>
    public string[] AdminKeys { get; set; } = [];

    /// <summary>
    /// HMAC secret for signed /audio links. When unset a random one is generated at startup,
    /// so links handed out before a restart stop working.
    /// </summary>
    public string? SigningKey { get; set; }

    /// <summary>How long a signed /audio link from /bestAudio stays valid.</summary>
    public TimeSpan AudioLinkLifetime { get; set; } = TimeSpan.FromHours(6);
}
