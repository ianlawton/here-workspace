using BloombergProxy;
using Microsoft.Extensions.FileProviders;

const string BloombergStream = "https://www.bloomberg.com/media-manifest/streams/phoenix-us.m3u8";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.AllowSynchronousIO = true);

var app = builder.Build();

// Serve static files from the ../public directory
var publicPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "public"));
if (Directory.Exists(publicPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(publicPath),
        RequestPath = string.Empty,
        ServeUnknownFileTypes = true,
        DefaultContentType = "application/octet-stream"
    });
}

// Bloomberg live TV transcoding endpoint.
// Uses Sdcb.FFmpeg (bundled native libraries) to transcode Bloomberg's
// H.264/AAC HLS stream to VP8/Vorbis WebM in-process — no external ffmpeg install required.
app.MapGet("/bloomberg-stream", async (HttpContext ctx, CancellationToken ct) =>
{
    ctx.Response.ContentType = "video/webm";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";

    Console.WriteLine("[Bloomberg] Client connected — starting transcode");

    try
    {
        await Task.Run(() => Transcoder.TranscodeHlsToWebM(BloombergStream, ctx.Response.Body, ct), ct);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("[Bloomberg] Client disconnected");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Bloomberg] Error: {ex.Message}");
    }
});

// RSS proxy — fetches the feed server-side to avoid CORS restrictions
app.MapGet("/rss-proxy", async (HttpContext ctx, [Microsoft.AspNetCore.Mvc.FromQuery] string url) =>
{
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    var xml = await http.GetStringAsync(url);
    ctx.Response.ContentType = "application/xml; charset=utf-8";
    ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
    ctx.Response.Headers["Cache-Control"] = "max-age=120";
    await ctx.Response.WriteAsync(xml);
});

var port = app.Configuration["Server:Port"] ?? "8080";
Console.WriteLine($"[Server] Listening on http://localhost:{port}");
Console.WriteLine($"[Server] Serving static files from: {publicPath}");

app.Run($"http://localhost:{port}");
