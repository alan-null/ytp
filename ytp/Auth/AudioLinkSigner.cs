using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Ytp.Auth;

/// <summary>
/// Signs /audio links so media players, which usually cannot send headers, can fetch cached audio
/// without the API key appearing in the URL. A link is valid for one video ID until it expires.
/// </summary>
public sealed class AudioLinkSigner
{
    private readonly byte[] secret;
    private readonly TimeSpan lifetime;
    private readonly TimeProvider time;

    public AudioLinkSigner(IOptions<AuthOptions> options, TimeProvider time, ILogger<AudioLinkSigner> logger)
    {
        var o = options.Value;
        this.time = time;
        lifetime = o.AudioLinkLifetime;

        if (string.IsNullOrEmpty(o.SigningKey))
        {
            secret = RandomNumberGenerator.GetBytes(32);
            logger.LogInformation("{Setting} is not set; using a random signing key. Audio links stop working after a restart.",
                $"{AuthOptions.Section}:SigningKey");
        }
        else if (o.SigningKey.Length < AuthOptions.MinSecretLength)
        {
            throw new InvalidOperationException(
                $"{AuthOptions.Section}:SigningKey is shorter than {AuthOptions.MinSecretLength} characters.");
        }
        else
        {
            secret = Encoding.UTF8.GetBytes(o.SigningKey);
        }
    }

    /// <summary>Query string (without '?') that authorizes <paramref name="videoId"/>.</summary>
    public string Sign(string videoId)
    {
        var exp = time.GetUtcNow().Add(lifetime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"exp={exp}&sig={WebEncoders.Base64UrlEncode(Mac(videoId, exp))}";
    }

    public bool Verify(string videoId, string? exp, string? sig)
    {
        if (!long.TryParse(exp, NumberStyles.None, CultureInfo.InvariantCulture, out var expSeconds)
            || expSeconds < time.GetUtcNow().ToUnixTimeSeconds()
            || string.IsNullOrEmpty(sig))
        {
            return false;
        }
        byte[] presented;
        try
        {
            presented = WebEncoders.Base64UrlDecode(sig);
        }
        catch (FormatException)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(presented, Mac(videoId, exp!));
    }

    private byte[] Mac(string videoId, string exp) =>
        HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes($"audio\n{videoId}\n{exp}"));
}
