package com.adbora.mic;

import android.net.ConnectivityManager;
import android.net.Network;
import android.util.Log;

/** Logs the state of the requested USB network (mode "ncm"). */
final class UsbNetworkCallback extends ConnectivityManager.NetworkCallback {
    @Override
    public void onAvailable(Network network) {
        Log.i("ADBoraMic", "USB network available: " + network);
    }

    @Override
    public void onLost(Network network) {
        Log.i("ADBoraMic", "USB network lost");
    }

    @Override
    public void onUnavailable() {
        Log.w("ADBoraMic", "USB network unavailable");
    }
}
