using System.Diagnostics;
using FFMpegCore;
using Microsoft.Extensions.FileProviders;

const string BloombergStream = "https://www.bloomberg.com/media-manifest/streams/phoenix-us.m3u8";

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Configure ffmpeg binary location from appsettings.json
var ffmpegPath = app.Configuration["FFmpeg:Path"]
    ?? throw new InvalidOperationException("FFmpeg:Path is not configured in appsettings.json");

// Use FFMpegCore to resolve and validate the ffmpeg binary folder
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

// Bloomberg live TV transcoding endpoint.
// Spawns ffmpeg to fetch Bloomberg's HLS stream (H.264/AAC) and transcode it
// to VP8/Vorbis WebM — which OpenFin's Chromium can play without proprietary codecs.
app.MapGet("/bloomberg-stream", async (HttpContext ctx, CancellationToken ct) =>
{
    ctx.Response.ContentType = "video/webm";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";

    Console.WriteLine("[Bloomberg] Client connected — starting transcode");

    var args = string.Join(" ",
        $"-i \"{BloombergStream}\"",
        "-vcodec libvpx",
        "-b:v 1500k",
        "-crf 10",
        "-acodec libvorbis",
        "-b:a 128k",
        "-f webm",
        "-deadline realtime",
        "-cpu-used 8",
        "pipe:1"
    );

    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }
    };

    process.Start();

    // Log ffmpeg stderr so we can see codec/network errors in the console
    _ = Task.Run(async () =>
    {
        string? line;
        while ((line = await process.StandardError.ReadLineAsync()) != null)
            Console.WriteLine($"[ffmpeg] {line}");
    }, CancellationToken.None);

    try
    {
        await process.StandardOutput.BaseStream.CopyToAsync(ctx.Response.Body, ct);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("[Bloomberg] Client disconnected");
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill();
            Console.WriteLine("[Bloomberg] ffmpeg process killed");
        }
    }
});

var port = app.Configuration["Server:Port"] ?? "8080";
Console.WriteLine($"[Server] Listening on http://localhost:{port}");
Console.WriteLine($"[Server] Serving static files from: {publicPath}");
Console.WriteLine($"[Server] FFmpeg: {ffmpegPath}");

app.Run($"http://localhost:{port}");
