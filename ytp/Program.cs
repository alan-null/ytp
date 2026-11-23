using Ytp;
using Ytp.YtDlp;

// Resolve appsettings.json next to the binary, not the caller's working directory.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services.AddHttpClient();
builder.Services.AddSingleton<YtDlpClient>();
builder.Services.AddSingleton<StreamUrlResolver>();

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILogger<Program>>();

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

// Returns a direct googlevideo URL. A failed lookup is an error response, never a URL.
app.MapGet("/bestAudio", async (string? v, StreamUrlResolver resolver, CancellationToken ct) =>
{
    if (!VideoId.IsValid(v))
    {
        return Results.BadRequest("Parameter 'v' must be an 11-character YouTube video ID.");
    }

    logger.LogInformation("Requested VideoId: {VideoId}", v);

    try
    {
        return Results.Json(new { url = await resolver.ResolveAsync(v!, ct) });
    }
    catch (YtDlpException e)
    {
        return Results.Problem(e.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.Run();
