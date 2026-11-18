package com.adbora.mic;

import android.content.Context;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.util.Log;
import android.view.Surface;

import java.io.IOException;
import java.io.OutputStream;
import java.nio.ByteBuffer;

/**
 * Mode "cast" (PICO headsets): creates a private virtual display with the PRESENTATION flag.
 * The PICO system treats such displays as "capture displays" (like its own screen recorder)
 * and has the XR runtime render the undistorted single-eye view into them. The display's
 * surface is an H.264 encoder; the encoded stream goes to ADBora.
 *
 * Stream: "ADBC" + version (1 byte) + width (int32 LE) + height (int32 LE), then packets:
 * length (int32 LE) + flags (int32 LE, 1 = key frame, 2 = codec config) + pts in µs (int64 LE)
 * + H.264 Annex-B data.
 */
final class ScreenCast {
    private static final String TAG = "ADBoraMic";
    private static final int VIRTUAL_DISPLAY_FLAG_PRESENTATION = 2;

    static void stream(Context context, OutputStream out, int width, int height, int bitrate, int fps) throws IOException {
        MediaFormat format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height);
        format.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
        format.setInteger(MediaFormat.KEY_BIT_RATE, bitrate);
        format.setInteger(MediaFormat.KEY_FRAME_RATE, fps);
        format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 2);
        format.setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, 100_000); // keep frames coming on a still picture
        format.setInteger("prepend-sps-pps-to-idr-frames", 1);
        MediaCodec codec = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC);
        VirtualDisplay display = null;
        try {
            codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            Surface surface = codec.createInputSurface();
            codec.start();
            DisplayManager dm = context.getSystemService(DisplayManager.class);
            display = dm.createVirtualDisplay("ADBoraCast", width, height, 320, surface, VIRTUAL_DISPLAY_FLAG_PRESENTATION);
            Log.i(TAG, "cast display " + width + "x" + height + " id " + display.getDisplay().getDisplayId());

            byte[] head = new byte[13];
            head[0] = 'A'; head[1] = 'D'; head[2] = 'B'; head[3] = 'C'; head[4] = 1;
            putInt(head, 5, width); putInt(head, 9, height);
            out.write(head);
            out.flush();

            MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
            byte[] buf = new byte[1 << 20];
            byte[] ph = new byte[16];
            while (true) {
                int index = codec.dequeueOutputBuffer(info, 500_000);
                if (index < 0) continue;
                try {
                    if (info.size > 0) {
                        ByteBuffer data = codec.getOutputBuffer(index);
                        if (buf.length < info.size) buf = new byte[info.size];
                        data.position(info.offset);
                        data.get(buf, 0, info.size);
                        int flags = ((info.flags & MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0 ? 1 : 0)
                                  | ((info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0 ? 2 : 0);
                        putInt(ph, 0, info.size); putInt(ph, 4, flags);
                        putInt(ph, 8, (int) info.presentationTimeUs); putInt(ph, 12, (int) (info.presentationTimeUs >>> 32));
                        out.write(ph);
                        out.write(buf, 0, info.size); // IOException = ADBora disconnected
                    }
                } finally {
                    codec.releaseOutputBuffer(index, false);
                }
            }
        } finally {
            if (display != null) display.release();
            try { codec.stop(); } catch (RuntimeException ignored) { }
            codec.release();
        }
    }

    private static void putInt(byte[] b, int o, int v) {
        b[o] = (byte) v; b[o + 1] = (byte) (v >> 8); b[o + 2] = (byte) (v >> 16); b[o + 3] = (byte) (v >> 24);
    }
}
