using System.Text.RegularExpressions;

namespace Ytp;

public static partial class VideoId
{
    // YouTube video IDs are exactly 11 URL-safe base64 characters. Anything else is rejected
    // before it reaches yt-dlp or becomes a cache key / file name.
    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);

    public static string ToWatchUrl(string id) => $"https://www.youtube.com/watch?v={id}";
}
