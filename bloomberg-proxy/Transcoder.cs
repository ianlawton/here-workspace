using System.Runtime.InteropServices;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Utils;

namespace BloombergProxy;

/// <summary>
/// Transcodes an HLS stream (H.264/AAC) to WebM (VP8/Vorbis) in-process
/// using the Sdcb.FFmpeg native library bindings, writing output to a Stream.
/// </summary>
public static unsafe class Transcoder
{
    public static void TranscodeHlsToWebM(string inputUrl, Stream output, CancellationToken ct)
    {
        // ── Input ────────────────────────────────────────────────────────────
        using FormatContext ic = FormatContext.OpenInputUrl(inputUrl);
        ic.LoadStreamInfo();

        int vsi = ic.FindBestStreamIndex(AVMediaType.Video);
        int asi = ic.FindBestStreamIndex(AVMediaType.Audio);
        if (vsi < 0) throw new InvalidOperationException("No video stream found");

        MediaStream vStream = ic.Streams[vsi];

        // ── Decoders ─────────────────────────────────────────────────────────
        using CodecContext vDec = new(Codec.FindDecoderById(vStream.Codecpar!.CodecId));
        vDec.FillParameters(vStream.Codecpar);
        vDec.Open();

        CodecContext? aDec = null;
        MediaStream? aStream = asi >= 0 ? ic.Streams[asi] : null;
        if (aStream != null)
        {
            aDec = new CodecContext(Codec.FindDecoderById(aStream.Codecpar!.CodecId));
            aDec.FillParameters(aStream.Codecpar);
            aDec.Open();
        }

        // ── VP8 encoder ──────────────────────────────────────────────────────
        Codec vpx = Codec.FindEncoderByName("libvpx")
            ?? throw new InvalidOperationException("libvpx not found — is it included in the bundled FFmpeg build?");

        using CodecContext vEnc = new(vpx);
        vEnc.Width       = vDec.Width;
        vEnc.Height      = vDec.Height;
        vEnc.PixelFormat = AVPixelFormat.Yuv420p;
        vEnc.TimeBase    = vStream.TimeBase;
        vEnc.Framerate   = ic.GuessFrameRate(vStream);
        vEnc.BitRate     = 1_500_000;
        vEnc.SetOption("deadline", "realtime");
        vEnc.SetOption("cpu-used", "8");
        vEnc.Open(vpx);

        // ── Vorbis encoder ───────────────────────────────────────────────────
        CodecContext? aEnc = null;
        if (aDec != null)
        {
            Codec vorbis = Codec.FindEncoderByName("libvorbis")
                ?? throw new InvalidOperationException("libvorbis not found — is it included in the bundled FFmpeg build?");

            aEnc = new CodecContext(vorbis);
            aEnc.SampleRate   = aDec.SampleRate;
            aEnc.ChLayout     = aDec.ChLayout;
            aEnc.SampleFormat = AVSampleFormat.Fltp;
            aEnc.BitRate      = 128_000;
            aEnc.TimeBase     = new AVRational { num = 1, den = aDec.SampleRate };
            aEnc.Open(vorbis);
        }

        // ── Custom AVIO context writing to the response stream ───────────────
        GCHandle streamHandle  = GCHandle.Alloc(output);
        // Keep delegate alive for the duration of the transcode
        avio_alloc_context_write_packet writeCallback = WritePacket;
        GCHandle delegateHandle = GCHandle.Alloc(writeCallback);

        byte* ioBuf = (byte*)ffmpeg.av_malloc(4096);
        AVIOContext* avio = ffmpeg.avio_alloc_context(
            ioBuf, 4096, 1,
            (void*)(IntPtr)streamHandle,
            null, writeCallback, null);

        // ── Output format context (WebM) ─────────────────────────────────────
        using FormatContext oc = FormatContext.AllocOutput("webm");
        oc.Pb = avio;

        MediaStream ovs = oc.NewStream(vEnc.Codec);
        ovs.Codecpar!.CopyFrom(vEnc);
        ovs.TimeBase = vEnc.TimeBase;

        MediaStream? oas = null;
        if (aEnc != null)
        {
            oas = oc.NewStream(aEnc.Codec);
            oas.Codecpar!.CopyFrom(aEnc);
            oas.TimeBase = aEnc.TimeBase;
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
                else if (pkt.StreamIndex == asi && aDec != null && aEnc != null && oas != null)
                    TranscodePacket(aDec, aEnc, pkt, frame, oc, oas);
            }
            finally
            {
                pkt.Unref();
            }
        }

        // ── Flush ────────────────────────────────────────────────────────────
        FlushEncoder(vEnc, frame, oc, ovs);
        if (aEnc != null && oas != null)
            FlushEncoder(aEnc, frame, oc, oas);

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
                outPkt.RescaleTs(enc.TimeBase, outStream.TimeBase);
                oc.InterleavedWriteFrame(outPkt);
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
            outPkt.RescaleTs(enc.TimeBase, outStream.TimeBase);
            oc.InterleavedWriteFrame(outPkt);
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
        catch
        {
            return ffmpeg.AVERROR_EOF;
        }
    }
}
