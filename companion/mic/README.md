# ADBora Mic (companion app)

Tiny Android app that ADBora installs on the headset (Meta Quest, Play For Dream, …) to
include the headset microphone in the "Headset casting" stream. Neither Meta's nor
Play For Dream's casting service sends the microphone.

* Captures the microphone (16-bit PCM, mono). ADBora starts it as an instrumentation
  (`am instrument`), so it runs without window or service and stops with `am force-stop`.
* Provides it on the abstract local socket `adbora_mic`; ADBora reads it with
  `adb forward tcp:N localabstract:adbora_mic` over the USB cable (or Wi-Fi ADB).
* No network permission, no activity, no service, no UI. The microphone is only open while ADBora
  is connected.

Build: `build\BUILD-COMPANION.ps1` (Android SDK build-tools 34 + platform 34 + JDK, no Gradle).
The resulting `ADBoraMic.apk` is embedded in ADBora (`src/AdbTool/Resources`).
