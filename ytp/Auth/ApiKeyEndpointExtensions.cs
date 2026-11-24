namespace Ytp.Auth;

/// <summary>Endpoint marker: only admin keys may call it.</summary>
public sealed class AdminOnlyAttribute;

/// <summary>Endpoint marker: a valid signed link (exp, sig query) for the route's video ID is accepted instead of a key.</summary>
public sealed class SignedAudioLinkAttribute;

public static class ApiKeyEndpointExtensions
{
    public static TBuilder RequireAdmin<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AdminOnlyAttribute());

    public static TBuilder AllowSignedAudioLink<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new SignedAudioLinkAttribute());

    /// <summary>
    /// Every request needs a key, including unknown paths, so unauthenticated callers learn nothing
    /// about which routes exist. Must run after routing so endpoint markers are visible.
    /// </summary>
    public static IApplicationBuilder UseApiKeys(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var endpoint = context.GetEndpoint();
        var caller = context.RequestServices.GetRequiredService<ApiKeys>().Identify(context);

        var allowed = endpoint?.Metadata.GetMetadata<AdminOnlyAttribute>() is not null
            ? caller.Access == Access.Admin
            : caller.Access != Access.None || IsValidSignedLink(context, endpoint);

        if (allowed)
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = caller.Access == Access.None
            ? StatusCodes.Status401Unauthorized
            : StatusCodes.Status403Forbidden;
    });

    private static bool IsValidSignedLink(HttpContext context, Endpoint? endpoint) =>
        endpoint?.Metadata.GetMetadata<SignedAudioLinkAttribute>() is not null
        && context.Request.RouteValues["v"] is string videoId
        && context.RequestServices.GetRequiredService<AudioLinkSigner>()
            .Verify(videoId, context.Request.Query["exp"], context.Request.Query["sig"]);
}
