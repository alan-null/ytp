using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Ytp.Auth;

namespace Ytp;

public sealed class RateLimitOptions
{
    public const string Section = "RateLimit";

    /// <summary>All requests from one client IP, with or without a valid key (also slows key guessing).</summary>
    public int PerIpPerMinute { get; set; } = 300;

    /// <summary>/bestAudio calls per API key.</summary>
    public int LookupsPerMinute { get; set; } = 30;

    /// <summary>/prefetch calls per API key (each may carry up to 500 IDs).</summary>
    public int PrefetchesPerMinute { get; set; } = 5;
}

public static class RateLimits
{
    public const string Lookup = "lookup";
    public const string Prefetch = "prefetch";

    public static IServiceCollection AddYtpRateLimits(this IServiceCollection services, IConfiguration configuration)
    {
        var o = configuration.GetSection(RateLimitOptions.Section).Get<RateLimitOptions>() ?? new RateLimitOptions();

        return services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }
                return ValueTask.CompletedTask;
            };

            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                PerMinute($"ip:{ClientIp(context)}", o.PerIpPerMinute));

            limiter.AddPolicy(Lookup, context => PerMinute(CallerKey(context), o.LookupsPerMinute));
            limiter.AddPolicy(Prefetch, context => PerMinute(CallerKey(context), o.PrefetchesPerMinute));
        });
    }

    private static RateLimitPartition<string> PerMinute(string partition, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, permits),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });

    // Keyed per API key, so clients sharing one IP (NAT) do not share a budget. Requests without a
    // valid key are rejected by the key check later; they fall back to the IP here.
    private static string CallerKey(HttpContext context) =>
        context.RequestServices.GetRequiredService<ApiKeys>().Identify(context).Id is { } id
            ? $"key:{id}"
            : $"ip:{ClientIp(context)}";

    private static string ClientIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
