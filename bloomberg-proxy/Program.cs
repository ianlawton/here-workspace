using FFMpegCore;
using FFMpegCore.Pipes;
using Microsoft.Extensions.FileProviders;

const string BloombergStream = "https://www.bloomberg.com/media-manifest/streams/phoenix-us.m3u8";

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Configure ffmpeg binary location from appsettings.json
var ffmpegPath = app.Configuration["FFmpeg:Path"]
    ?? throw new InvalidOperationException("FFmpeg:Path is not configured in appsettings.json");

GlobalFFOptions.Configure(opts =>
{
    opts.BinaryFolder = Path.GetDirectoryName(ffmpegPath)!;
    opts.TemporaryFilesFolder = Path.GetTempPath();
});

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

// Bloomberg live TV transcoding endpoint
// Fetches the Bloomberg HLS stream and transcodes H.264 -> VP8/Vorbis WebM
// so that OpenFin's Chromium (which lacks proprietary H.264 codec) can play it.
app.MapGet("/bloomberg-stream", async (HttpContext ctx, CancellationToken ct) =>
{
    ctx.Response.ContentType = "video/webm";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
    ctx.Response.Headers["Transfer-Encoding"] = "chunked";

    Console.WriteLine("[Bloomberg] Client connected — starting transcode");

    var sink = new StreamPipeSink(ctx.Response.Body);

    try
    {
        await FFMpegArguments
            .FromUrlInput(new Uri(BloombergStream), inputOptions => inputOptions
                .WithCustomArgument("-re"))
            .OutputToPipe(sink, outputOptions => outputOptions
                .WithVideoCodec("libvpx")
                .WithCustomArgument("-b:v 1500k")
                .WithCustomArgument("-crf 10")
                .WithAudioCodec("libvorbis")
                .WithCustomArgument("-b:a 128k")
                .ForceFormat("webm")
                .WithCustomArgument("-deadline realtime")
                .WithCustomArgument("-cpu-used 8"))
            .CancellableThrough(ct)
            .ProcessAsynchronously();
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("[Bloomberg] Client disconnected — ffmpeg process killed");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Bloomberg] Error: {ex.Message}");
    }
});

var port = app.Configuration["Server:Port"] ?? "8080";
Console.WriteLine($"[Server] Listening on http://localhost:{port}");
Console.WriteLine($"[Server] Serving static files from: {publicPath}");
Console.WriteLine($"[Server] FFmpeg: {ffmpegPath}");

app.Run($"http://localhost:{port}");
