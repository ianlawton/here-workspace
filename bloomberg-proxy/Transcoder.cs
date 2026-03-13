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
        ffmpeg.av_log_set_level((int)Sdcb.FFmpeg.Raw.LogLevel.Quiet);

        // ── Input ────────────────────────────────────────────────────────────
        // Bloomberg's CDN requires browser-like headers; without them the request
        // times out (AVERROR(ETIMEDOUT) = -138) on machines without a warm cache.
        using MediaDictionary inputOpts = new();
        inputOpts["user_agent"]    = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        inputOpts["headers"]       = "Referer: https://www.bloomberg.com/\r\nOrigin: https://www.bloomberg.com\r\n";
        inputOpts["reconnect"]     = "1";
        inputOpts["reconnect_streamed"] = "1";

        using FormatContext ic = FormatContext.OpenInputUrl(inputUrl, null, inputOpts);
        ic.LoadStreamInfo();

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
            ?? throw new InvalidOperationException("libvpx not found");

        using CodecContext vEnc = new(vpx);
        vEnc.Width       = vDec.Width;
        vEnc.Height      = vDec.Height;
        vEnc.PixelFormat = AVPixelFormat.Yuv420p;
        vEnc.TimeBase    = vStream.TimeBase;
        vEnc.Framerate   = vStream.AvgFrameRate;
        vEnc.BitRate     = 1_500_000;
        ffmpeg.av_opt_set((AVCodecContext*)vEnc, "deadline", "realtime", (int)AV_OPT_SEARCH.Children);
        ffmpeg.av_opt_set((AVCodecContext*)vEnc, "cpu-used", "8",        (int)AV_OPT_SEARCH.Children);
        vEnc.Open(vpx);

        // ── Vorbis audio encoder ─────────────────────────────────────────────
        CodecContext? aEnc = null;
        AVAudioFifo* audioFifo = null;
        int audioFrameSize = 0;

        if (aDec is not null)
        {
            Codec vorbis = Codec.FindEncoderByName("libvorbis")
                        ?? throw new InvalidOperationException("libvorbis not found");

            aEnc = new CodecContext(vorbis);
            aEnc.SampleRate   = aDec.SampleRate;
            aEnc.ChLayout     = aDec.ChLayout;
            aEnc.SampleFormat = AVSampleFormat.Fltp;
            aEnc.BitRate      = 128_000;
            aEnc.Open(vorbis);

            // Read frame_size from the raw context — encoder sets this after Open
            AVCodecContext* pAEnc = (AVCodecContext*)aEnc;
            audioFrameSize = pAEnc->frame_size;
            int nbChannels  = pAEnc->ch_layout.nb_channels;

            // If frame_size==0 the encoder accepts variable sizes; default to 1024
            int fifoChunk = audioFrameSize > 0 ? audioFrameSize : 1024;
            audioFifo = ffmpeg.av_audio_fifo_alloc(
                (AVSampleFormat)(int)aEnc.SampleFormat, nbChannels, fifoChunk * 8);
        }

        // ── Custom AVIO: write FFmpeg output directly to the response stream ──
        GCHandle streamHandle   = GCHandle.Alloc(output);
        avio_alloc_context_write_packet writeCallback = WritePacket;
        GCHandle delegateHandle = GCHandle.Alloc(writeCallback);

        byte* ioBuf = (byte*)ffmpeg.av_malloc(4096);
        AVIOContext* avio = ffmpeg.avio_alloc_context(
            ioBuf, 4096, 1,
            (void*)(IntPtr)streamHandle,
            null, writeCallback, null);

        // ── Output format context (WebM) ─────────────────────────────────────
        OutputFormat webm = OutputFormat.All.First(f => f.Name == "webm");
        using FormatContext oc = FormatContext.AllocOutput(webm);
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
            // Use the encoder's actual time_base after open
            AVCodecContext* pAEnc = (AVCodecContext*)aEnc;
            oasValue.TimeBase = pAEnc->time_base.Num == 0
                ? new AVRational { Num = 1, Den = pAEnc->sample_rate }
                : pAEnc->time_base;
        }

        oc.WriteHeader();

        // Capture input stream timebases for A/V sync calculation
        AVRational vInputTb  = vStream.TimeBase;
        AVRational aInputTb  = asi >= 0 ? ic.Streams[asi].TimeBase : default;
        int aEncSampleRate   = aEnc is not null ? ((AVCodecContext*)aEnc)->sample_rate : 48000;

        // ── Transcode loop ───────────────────────────────────────────────────
        using Packet pkt   = new();
        using Frame  frame = new();
        int frameCount = 0;
        long audioPts     = 0;              // sample counter; initialised from stream on first audio frame
        bool audioPtsSet  = false;          // becomes true once audioPts is derived from real timestamps
        long videoPtsBase = long.MinValue;  // first video frame PTS — subtracted to reset to 0

        while (!ct.IsCancellationRequested)
        {
            var readResult = ic.ReadFrame(pkt);
            if (readResult < 0) break;

            try
            {
                if (pkt.StreamIndex == vsi)
                    TranscodeVideoPacket(vDec, vEnc, pkt, frame, oc, ovs, ref videoPtsBase);
                else if (pkt.StreamIndex == asi && aDec is not null && aEnc is not null && hasAudio)
                    TranscodeAudioPacket(aDec, aEnc, pkt, frame, oc, oasValue, audioFifo, audioFrameSize,
                        ref audioPts, ref audioPtsSet, videoPtsBase, vInputTb, aInputTb, aEncSampleRate);
            }
            catch { break; }
            finally { pkt.Unref(); }

            frameCount++;
        }

        // ── Flush encoders ───────────────────────────────────────────────────
        try { FlushEncoder(vEnc, frame, oc, ovs); } catch { }
        if (aEnc is not null && hasAudio)
            try { FlushEncoder(aEnc, frame, oc, oasValue); } catch { }

        try { oc.WriteTrailer(); } catch { }

        if (audioFifo is not null) ffmpeg.av_audio_fifo_free(audioFifo);
        delegateHandle.Free();
        streamHandle.Free();
        ffmpeg.av_free(ioBuf);

        aDec?.Dispose();
        aEnc?.Dispose();
    }

    // ── Video transcode (unchanged from working version) ─────────────────────

    private static void TranscodeVideoPacket(
        CodecContext dec, CodecContext enc,
        Packet inPkt, Frame frame,
        FormatContext oc, MediaStream outStream,
        ref long ptsBase)
    {
        dec.SendPacket(inPkt);
        while (dec.ReceiveFrame(frame) == 0)
        {
            // Reset video PTS to start from 0 so it aligns with audio (which starts at 0)
            AVFrame* pf = (AVFrame*)frame;
            if (pf->pts != long.MinValue) // AV_NOPTS_VALUE == long.MinValue
            {
                if (ptsBase == long.MinValue) ptsBase = pf->pts;
                pf->pts -= ptsBase;
            }

            try { enc.SendFrame(frame); }
            finally { frame.Unref(); }

            using Packet outPkt = new();
            while (enc.ReceivePacket(outPkt) == 0)
            {
                outPkt.StreamIndex = outStream.Index;
                ffmpeg.av_packet_rescale_ts((AVPacket*)outPkt, enc.TimeBase, outStream.TimeBase);
                AVPacket* p = (AVPacket*)outPkt;
                if (p->dts == long.MinValue) p->dts = p->pts; // VP8 has no B-frames
                ffmpeg.av_write_frame((AVFormatContext*)oc, p);
            }
        }
    }

    // ── Audio transcode with FIFO to handle frame-size mismatches ────────────

    private static void TranscodeAudioPacket(
        CodecContext dec, CodecContext enc,
        Packet inPkt, Frame frame,
        FormatContext oc, MediaStream outStream,
        AVAudioFifo* fifo, int encFrameSize,
        ref long audioPts, ref bool audioPtsSet,
        long videoPtsBase, AVRational vInputTb, AVRational aInputTb, int sampleRate)
    {
        dec.SendPacket(inPkt);
        while (dec.ReceiveFrame(frame) == 0)
        {
            // On the first decoded audio frame, derive audioPts from actual stream timestamps
            // so audio lines up with the zeroed video PTS rather than starting at an arbitrary 0.
            if (!audioPtsSet && videoPtsBase != long.MinValue)
            {
                AVFrame* pf = (AVFrame*)frame;
                if (pf->pts != long.MinValue)
                {
                    // Convert video base and audio frame start to the same sample-rate timebase
                    long videoBaseInSamples = ffmpeg.av_rescale_q(
                        videoPtsBase, vInputTb, new AVRational { Num = 1, Den = sampleRate });
                    long audioStartInSamples = ffmpeg.av_rescale_q(
                        pf->pts, aInputTb, new AVRational { Num = 1, Den = sampleRate });
                    audioPts = Math.Max(0L, audioStartInSamples - videoBaseInSamples);
                }
                audioPtsSet = true;
            }

            try
            {
                AVFrame* pFrame = (AVFrame*)frame;
                ffmpeg.av_audio_fifo_write(fifo, (void**)pFrame->extended_data, pFrame->nb_samples);
            }
            finally { frame.Unref(); }

            // Drain FIFO in encoder-sized chunks (or all at once if variable-size)
            int sendSize = encFrameSize > 0 ? encFrameSize : ffmpeg.av_audio_fifo_size(fifo);
            while (ffmpeg.av_audio_fifo_size(fifo) >= sendSize && sendSize > 0)
            {
                AVFrame* encFrame = ffmpeg.av_frame_alloc();
                try
                {
                    AVCodecContext* pEnc = (AVCodecContext*)enc;
                    encFrame->nb_samples = sendSize;
                    encFrame->format     = (int)pEnc->sample_fmt;
                    encFrame->pts        = audioPts;   // monotonically increasing sample PTS
                    ffmpeg.av_channel_layout_copy(&encFrame->ch_layout, &pEnc->ch_layout);
                    ffmpeg.av_frame_get_buffer(encFrame, 0);

                    ffmpeg.av_audio_fifo_read(fifo, (void**)encFrame->extended_data, sendSize);
                    ffmpeg.avcodec_send_frame((AVCodecContext*)enc, encFrame);

                    audioPts += sendSize; // advance by samples consumed
                }
                finally
                {
                    ffmpeg.av_frame_free(&encFrame);
                }

                using Packet outPkt = new();
                while (enc.ReceivePacket(outPkt) == 0)
                {
                    outPkt.StreamIndex = outStream.Index;
                    ffmpeg.av_packet_rescale_ts((AVPacket*)outPkt, enc.TimeBase, outStream.TimeBase);
                    ffmpeg.av_write_frame((AVFormatContext*)oc, (AVPacket*)outPkt);
                }
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
            ffmpeg.av_write_frame((AVFormatContext*)oc, (AVPacket*)outPkt);
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
