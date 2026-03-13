using System.Runtime.InteropServices;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Utils;

namespace BloombergProxy;

/// <summary>
/// Transcodes an HLS stream (H.264/AAC) to WebM (VP8/Vorbis) in-process
/// using Sdcb.FFmpeg native library bindings, writing output to a Stream.
/// </summary>
public static unsafe class Transcoder
{
    public static void TranscodeHlsToWebM(string inputUrl, Stream output, CancellationToken ct)
    {
        // ── Input ────────────────────────────────────────────────────────────
        using FormatContext ic = FormatContext.OpenInputUrl(inputUrl);
        ic.LoadStreamInfo();

        // Find best video/audio stream by iterating (no FindBestStreamIndex in 6.0.x)
        int vsi = -1, asi = -1;
        for (int i = 0; i < ic.Streams.Count; i++)
        {
            AVMediaType type = ic.Streams[i].Codecpar!.CodecType;
            if (type == AVMediaType.Video && vsi < 0) vsi = i;
            if (type == AVMediaType.Audio && asi < 0) asi = i;
        }
        if (vsi < 0) throw new InvalidOperationException("No video stream found");

        MediaStream vStream = ic.Streams[vsi];

        // ── Video decoder ────────────────────────────────────────────────────
        using CodecContext vDec = new(Codec.FindDecoderById(vStream.Codecpar!.CodecId));
        vDec.FillParameters(vStream.Codecpar);
        vDec.Open();

        // ── Audio decoder (optional) ─────────────────────────────────────────
        CodecContext? aDec = null;
        if (asi >= 0)
        {
            MediaStream aStream = ic.Streams[asi];
            aDec = new CodecContext(Codec.FindDecoderById(aStream.Codecpar!.CodecId));
            aDec.FillParameters(aStream.Codecpar);
            aDec.Open();
        }

        // ── VP8 video encoder ────────────────────────────────────────────────
        Codec vpx = Codec.FindEncoderByName("libvpx")
            ?? throw new InvalidOperationException("libvpx not found — bundled FFmpeg build may not include it");

        using CodecContext vEnc = new(vpx);
        vEnc.Width       = vDec.Width;
        vEnc.Height      = vDec.Height;
        vEnc.PixelFormat = AVPixelFormat.Yuv420p;
        vEnc.TimeBase    = vStream.TimeBase;
        vEnc.Framerate   = vStream.AvgFrameRate;
        vEnc.BitRate     = 1_500_000;
        // av_opt_set for VP8-specific options (SetOption not in managed wrapper)
        ffmpeg.av_opt_set((AVCodecContext*)vEnc, "deadline", "realtime", (int)AV_OPT_SEARCH.Children);
        ffmpeg.av_opt_set((AVCodecContext*)vEnc, "cpu-used", "8",        (int)AV_OPT_SEARCH.Children);
        vEnc.Open(vpx);

        // ── Vorbis audio encoder ─────────────────────────────────────────────
        CodecContext? aEnc = null;
        if (aDec is not null)
        {
            Codec vorbis = Codec.FindEncoderByName("libvorbis")
                ?? throw new InvalidOperationException("libvorbis not found — bundled FFmpeg build may not include it");

            aEnc = new CodecContext(vorbis);
            aEnc.SampleRate   = aDec.SampleRate;
            aEnc.ChLayout     = aDec.ChLayout;
            aEnc.SampleFormat = AVSampleFormat.Fltp;
            aEnc.BitRate      = 128_000;
            aEnc.TimeBase     = new AVRational { Num = 1, Den = aDec.SampleRate };
            aEnc.Open(vorbis);
        }

        // ── Custom AVIO: write FFmpeg output directly to the response stream ──
        GCHandle streamHandle   = GCHandle.Alloc(output);
        avio_alloc_context_write_packet writeCallback = WritePacket;
        GCHandle delegateHandle = GCHandle.Alloc(writeCallback); // prevent GC collection

        byte* ioBuf = (byte*)ffmpeg.av_malloc(4096);
        AVIOContext* avio = ffmpeg.avio_alloc_context(
            ioBuf, 4096, 1,
            (void*)(IntPtr)streamHandle,
            null, writeCallback, null);

        // ── Output format context (WebM) ─────────────────────────────────────
        OutputFormat webm = OutputFormat.All.First(f => f.Name == "webm");
        using FormatContext oc = FormatContext.AllocOutput(webm);
        // Wire up the custom AVIO context via raw pointer
        ((AVFormatContext*)oc)->pb = avio;

        MediaStream ovs = oc.NewStream(vEnc.Codec);
        ovs.Codecpar!.CopyFrom(vEnc);
        ovs.TimeBase = vEnc.TimeBase;

        MediaStream oasValue = default;
        bool hasAudio = aEnc is not null;
        if (aEnc is not null)
        {
            oasValue = oc.NewStream(aEnc.Codec);
            oasValue.Codecpar!.CopyFrom(aEnc);
            oasValue.TimeBase = aEnc.TimeBase;
        }

        oc.WriteHeader();

        // ── Transcode loop ───────────────────────────────────────────────────
        using Packet pkt   = new();
        using Frame  frame = new();

        while (!ct.IsCancellationRequested)
        {
            if (ic.ReadFrame(pkt) < 0) break;

            try
            {
                if (pkt.StreamIndex == vsi)
                    TranscodePacket(vDec, vEnc, pkt, frame, oc, ovs);
                else if (pkt.StreamIndex == asi && aDec is not null && aEnc is not null && hasAudio)
                    TranscodePacket(aDec, aEnc, pkt, frame, oc, oasValue);
            }
            finally { pkt.Unref(); }
        }

        // ── Flush encoders ───────────────────────────────────────────────────
        FlushEncoder(vEnc, frame, oc, ovs);
        if (aEnc is not null && hasAudio)
            FlushEncoder(aEnc, frame, oc, oasValue);

        oc.WriteTrailer();

        delegateHandle.Free();
        streamHandle.Free();
        ffmpeg.av_free(ioBuf);

        aDec?.Dispose();
        aEnc?.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void TranscodePacket(
        CodecContext dec, CodecContext enc,
        Packet inPkt, Frame frame,
        FormatContext oc, MediaStream outStream)
    {
        dec.SendPacket(inPkt);
        while (dec.ReceiveFrame(frame) == 0)
        {
            enc.SendFrame(frame);
            frame.Unref();
            using Packet outPkt = new();
            while (enc.ReceivePacket(outPkt) == 0)
            {
                outPkt.StreamIndex = outStream.Index;
                ffmpeg.av_packet_rescale_ts((AVPacket*)outPkt, enc.TimeBase, outStream.TimeBase);
                ffmpeg.av_interleaved_write_frame((AVFormatContext*)oc, (AVPacket*)outPkt);
            }
        }
    }

    private static void FlushEncoder(
        CodecContext enc, Frame frame,
        FormatContext oc, MediaStream outStream)
    {
        enc.SendFrame(null);
        using Packet outPkt = new();
        while (enc.ReceivePacket(outPkt) == 0)
        {
            outPkt.StreamIndex = outStream.Index;
            ffmpeg.av_packet_rescale_ts((AVPacket*)outPkt, enc.TimeBase, outStream.TimeBase);
            ffmpeg.av_interleaved_write_frame((AVFormatContext*)oc, (AVPacket*)outPkt);
        }
    }

    private static int WritePacket(void* opaque, byte* buf, int bufSize)
    {
        var handle = GCHandle.FromIntPtr((IntPtr)opaque);
        var stream = (Stream)handle.Target!;
        try
        {
            stream.Write(new ReadOnlySpan<byte>(buf, bufSize));
            stream.Flush();
            return bufSize;
        }
        catch { return ffmpeg.AVERROR_EOF; }
    }
}
