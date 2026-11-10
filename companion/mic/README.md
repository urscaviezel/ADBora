# ADBora Mic (companion app)

Tiny Android app that ADBora installs on the headset (Meta Quest, Play For Dream, …) when
needed. It has no activity, no service, no network permission and no UI; ADBora starts it
as an instrumentation (`am instrument`) and it ends itself as soon as ADBora disconnects
(or after 30 s without a connection), so it never keeps running in the background.

Two modes:

* **Microphone** (default): captures the headset microphone (16-bit PCM, mono) and provides
  it on the abstract local socket `adbora_mic`; ADBora reads it with
  `adb forward tcp:N localabstract:adbora_mic` and mixes it into the "Headset casting"
  audio. Neither Meta's nor Play For Dream's casting service sends the microphone. If the
  microphone drops out (e.g. the headset sleeps), it is reopened.
* **USB networking** (`-e mode ncm`, Meta Quest): requests a network with `TRANSPORT_USB`
  like Virtual Desktop does, so the Quest's "EthernetOverUsb" service switches on USB NCM;
  the request is held while ADBora is connected to the socket `adbora_ncm` (used by the
  speed test "TCP/IP direct").

Only adb (shell, uid 2000) or root may connect to the sockets.

Build: `build\BUILD-COMPANION.ps1` (Android SDK build-tools 34 + platform 34 + JDK, no Gradle).
The resulting `ADBoraMic.apk` is embedded in ADBora (`src/AdbTool/Resources`); its version
code must match `MicRelay.VersionCode`.
