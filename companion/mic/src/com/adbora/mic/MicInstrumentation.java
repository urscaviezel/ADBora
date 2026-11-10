package com.adbora.mic;

import android.app.Instrumentation;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.net.LocalServerSocket;
import android.net.LocalSocket;
import android.os.Bundle;
import android.util.Log;

import java.io.IOException;
import java.io.OutputStream;

/**
 * Streams the microphone as 16-bit PCM (mono, little-endian) to the first client of the
 * abstract socket "adbora_mic". Started by ADBora as an instrumentation, so the app runs
 * without any window or service (headset systems like Play For Dream close apps whose
 * window ends and block background service starts), as a foreground process that may
 * use the microphone.
 *
 * Start: am instrument -e rate 48000 com.adbora.mic/.MicInstrumentation
 * Stop:  am force-stop com.adbora.mic
 *
 * Stream: header "ADBM" + version (1 byte) + channels (1 byte) + sample rate (int32 LE),
 * followed by raw PCM. The microphone is only open while a client is connected.
 */
public final class MicInstrumentation extends Instrumentation {
    private static final String TAG = "ADBoraMic";
    private static final String SOCKET = "adbora_mic";
    private int rate = 48000;

    @Override
    public void onCreate(Bundle arguments) {
        super.onCreate(arguments);
        try {
            if (arguments != null && arguments.getString("rate") != null)
                rate = Integer.parseInt(arguments.getString("rate"));
        } catch (NumberFormatException ignored) { }
        start(); // → onStart() on a separate thread
    }

    @Override
    public void onStart() {
        try (LocalServerSocket server = new LocalServerSocket(SOCKET)) {
            Log.i(TAG, "listening, " + rate + " Hz");
            while (true) {
                try (LocalSocket client = server.accept()) {
                    // Only adb (shell, uid 2000) or root may read the microphone
                    int uid = client.getPeerCredentials().getUid();
                    if (uid != 2000 && uid != 0) {
                        Log.w(TAG, "rejected uid " + uid);
                        continue;
                    }
                    stream(client.getOutputStream());
                } catch (IOException e) {
                    Log.i(TAG, "client ended: " + e.getMessage());
                }
            }
        } catch (IOException e) {
            Log.e(TAG, "socket", e);
            finish(1, new Bundle());
        }
    }

    private void stream(OutputStream out) throws IOException {
        int sampleRate = rate;
        int min = AudioRecord.getMinBufferSize(sampleRate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
        AudioRecord rec = new AudioRecord(MediaRecorder.AudioSource.MIC, sampleRate,
                AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, Math.max(min, sampleRate / 5));
        if (rec.getState() != AudioRecord.STATE_INITIALIZED) {
            rec.release();
            throw new IOException("AudioRecord not initialized");
        }
        byte[] header = { 'A', 'D', 'B', 'M', 1, 1,
                (byte) sampleRate, (byte) (sampleRate >> 8), (byte) (sampleRate >> 16), (byte) (sampleRate >> 24) };
        byte[] buf = new byte[sampleRate / 50 * 2]; // 20 ms
        try {
            out.write(header);
            rec.startRecording();
            while (true) {
                int n = rec.read(buf, 0, buf.length);
                if (n < 0) throw new IOException("read " + n);
                if (n > 0) out.write(buf, 0, n);
            }
        } finally {
            try { rec.stop(); } catch (IllegalStateException ignored) { }
            rec.release();
        }
    }
}
