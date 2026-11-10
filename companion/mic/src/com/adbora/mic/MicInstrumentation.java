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
 *
 * The app ends itself when ADBora disconnects (stream stopped, ADBora closed, cable
 * unplugged) or when nobody connects within 30 s after the start – it never keeps running
 * in the background, even if ADBora could not send "am force-stop".
 */
public final class MicInstrumentation extends Instrumentation {
    private static final String TAG = "ADBoraMic";
    private static final String SOCKET = "adbora_mic";
    private static final long CONNECT_TIMEOUT_MS = 30_000;
    private int rate = 48000;
    private boolean ncm;
    private volatile boolean connected;

    @Override
    public void onCreate(Bundle arguments) {
        super.onCreate(arguments);
        try {
            if (arguments != null && arguments.getString("rate") != null)
                rate = Integer.parseInt(arguments.getString("rate"));
        } catch (NumberFormatException ignored) { }
        ncm = arguments != null && "ncm".equals(arguments.getString("mode"));
        start(); // → onStart() on a separate thread
        Thread watchdog = new Thread(() -> {
            try { Thread.sleep(CONNECT_TIMEOUT_MS); } catch (InterruptedException ignored) { }
            if (!connected) {
                Log.i(TAG, "no client within " + CONNECT_TIMEOUT_MS / 1000 + " s, exiting");
                exit();
            }
        }, "ADBoraMicWatchdog");
        watchdog.setDaemon(true);
        watchdog.start();
    }

    /**
     * ADBora never sends anything, so reading only returns when the connection ends (stream
     * stopped, ADBora closed, cable unplugged). Independent of the microphone, which may block
     * for a long time while the headset sleeps.
     */
    private void watchDisconnect(LocalSocket client) {
        Thread t = new Thread(() -> {
            try {
                java.io.InputStream in = client.getInputStream();
                while (in.read() >= 0) { /* ignore */ }
            } catch (IOException ignored) { }
            Log.i(TAG, "ADBora disconnected, exiting");
            exit();
        }, "ADBoraMicDisconnect");
        t.setDaemon(true);
        t.start();
    }

    /**
     * Mode "ncm": requests a network with TRANSPORT_USB (without INTERNET/TRUSTED) like
     * Virtual Desktop. The headset's "EthernetOverUsb" service then switches on USB networking
     * (NCM) and keeps it on as long as the request exists, i.e. until ADBora disconnects from
     * the socket "adbora_ncm". If USB networking was never allowed on the headset, it shows its
     * consent dialog.
     */
    private void holdUsbNetwork() {
        android.net.ConnectivityManager cm = getContext().getSystemService(android.net.ConnectivityManager.class);
        android.net.NetworkRequest request = new android.net.NetworkRequest.Builder()
                .removeCapability(12)   // NET_CAPABILITY_INTERNET
                .removeCapability(14)   // NET_CAPABILITY_TRUSTED
                .addTransportType(8)    // TRANSPORT_USB
                .build();
        android.net.ConnectivityManager.NetworkCallback callback = new UsbNetworkCallback();
        cm.requestNetwork(request, callback);
        Log.i(TAG, "USB network requested");
        try (LocalServerSocket server = new LocalServerSocket("adbora_ncm")) {
            while (true) {
                try (LocalSocket client = server.accept()) {
                    int uid = client.getPeerCredentials().getUid();
                    if (uid != 2000 && uid != 0) continue;
                    connected = true;
                    Log.i(TAG, "ADBora connected, holding USB network");
                    client.getOutputStream().write('K'); // handshake: ADBora knows the request is held
                    client.getOutputStream().flush();
                    // Hold the request until ADBora disconnects
                    java.io.InputStream in = client.getInputStream();
                    while (in.read() >= 0) { /* ignore */ }
                } catch (IOException ignored) { }
                if (connected) break;
            }
        } catch (IOException e) {
            Log.e(TAG, "socket", e);
        }
        try { cm.unregisterNetworkCallback(callback); } catch (RuntimeException ignored) { }
        Log.i(TAG, "USB network released");
        exit();
    }

    /** Ends the instrumentation and the process. */
    private void exit() {
        try { finish(0, new Bundle()); } catch (RuntimeException ignored) { }
        android.os.Process.killProcess(android.os.Process.myPid());
    }

    @Override
    public void onStart() {
        if (ncm) {
            holdUsbNetwork();
            return;
        }
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
                    connected = true;
                    watchDisconnect(client);
                    stream(client.getOutputStream());
                } catch (IOException e) {
                    Log.i(TAG, "client ended: " + e.getMessage());
                }
                if (connected) break; // ADBora disconnected: done
            }
        } catch (IOException e) {
            Log.e(TAG, "socket", e);
        }
        exit();
    }

    /**
     * Streams until the client disconnects (write fails → IOException). If the microphone
     * fails (e.g. the headset goes to sleep and Android takes it away), it is reopened after
     * a short pause instead of ending the session.
     */
    private void stream(OutputStream out) throws IOException {
        int sampleRate = rate;
        byte[] header = { 'A', 'D', 'B', 'M', 1, 1,
                (byte) sampleRate, (byte) (sampleRate >> 8), (byte) (sampleRate >> 16), (byte) (sampleRate >> 24) };
        out.write(header);
        byte[] buf = new byte[sampleRate / 50 * 2]; // 20 ms
        while (true) {
            AudioRecord rec = open(sampleRate);
            if (rec == null) {
                silence(out, sampleRate);
                continue;
            }
            try {
                rec.startRecording();
                while (true) {
                    int n = rec.read(buf, 0, buf.length);
                    if (n < 0) {
                        Log.w(TAG, "microphone read " + n + ", reopening");
                        break;
                    }
                    if (n > 0) out.write(buf, 0, n); // IOException = ADBora disconnected
                }
            } finally {
                try { rec.stop(); } catch (IllegalStateException ignored) { }
                rec.release();
            }
            silence(out, sampleRate);
        }
    }

    private static AudioRecord open(int sampleRate) {
        try {
            int min = AudioRecord.getMinBufferSize(sampleRate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
            AudioRecord rec = new AudioRecord(MediaRecorder.AudioSource.MIC, sampleRate,
                    AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, Math.max(min, sampleRate / 5));
            if (rec.getState() == AudioRecord.STATE_INITIALIZED) return rec;
            rec.release();
        } catch (RuntimeException e) {
            Log.w(TAG, "microphone: " + e.getMessage());
        }
        return null;
    }

    /**
     * 500 ms of silence in 20 ms steps while the microphone is unavailable: keeps the audio in
     * time on the PC and notices right away when ADBora disconnects (write fails).
     */
    private static void silence(OutputStream out, int sampleRate) throws IOException {
        byte[] zero = new byte[sampleRate / 50 * 2];
        for (int i = 0; i < 25; i++) {
            out.write(zero);
            try { Thread.sleep(20); } catch (InterruptedException ignored) { }
        }
    }
}
