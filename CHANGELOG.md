# Changelog

## v1.5.3 – 2026-10-23

- DE: APK Backup: Sortierung umschaltbar zwischen „Zuletzt installiert“ (Standard bei jedem Start, neueste
  zuoberst) und „Alphabetisch“; Auswahl bleibt beim Umschalten erhalten. Installationsdatum im Tooltip.
- DE: Weitere Sprachen: „Sprache ändern“ in den Allgemeinen Einstellungen ersetzt die
  Auswahl Deutsch/Englisch – Sprache wählen sowie zusätzliche Sprachen aus dem GitHub-Repository (Ordner
  `lang`) installieren, aktualisieren oder entfernen, ohne neue ADBora-Version. Vorlage für Übersetzungen:
  `lang/template.json`. Erste zusätzliche Sprache: Französisch.
- EN: APK backup: sort order switchable between "Last installed" (default at every start, newest first)
  and "Alphabetical"; the selection is kept when switching. Install date in the tooltip.
- EN: More languages: "Change language" in the general settings replaces the
  German/English choice – choose the language and install, update or remove additional languages from the
  GitHub repository (folder `lang`) without a new ADBora version. Translation template: `lang/template.json`.
  First additional language: French.

## v1.5.2 – 2026-10-23

- DE: ADBora läuft nur noch einmal: ein zweiter Start holt das offene Fenster nach vorne (zwei Instanzen
  würden sich beim selben ADB-Gerät in die Quere kommen).
- EN: ADBora runs only once: starting it again brings the open window to the front (two instances would
  get in each other's way on the same ADB device).

## v1.5.1 – 2026-10-21

- DE: APK Backup: Dateien und Backup-Ordner tragen das Gerät im Namen, z. B.
  `Witchblood-1.0_PFDMR.apk` bzw. `Backup_2026-10-21_10-05-28_Quest3` – INSTALL.cmd/INSTALL.txt
  verwenden die neuen Namen. INSTALL.cmd stellt jetzt alles wieder her: APK, danach OBB- und
  Data-Ordner (sofern im Backup).
- DE: Spielstände: Bei debuggbaren Apps (viele VR-Spiele) sichert „Daten“ jetzt zusätzlich die internen
  App-Daten (`/data/data/<Paket>` per run-as, ohne Caches) nach `Internal/data.tar`; Batch-Restore und
  INSTALL.cmd schreiben sie zurück. Spalte „Datenordner“ zeigt „+ intern“ bzw. „Nur intern“.
- DE: Wiederherstellung nach Neuinstallation: zurückkopierte Dateien in Android/data und Android/obb
  gehörten dem ADB-Benutzer, sodass das Spiel Spielstände und Einstellungen nicht lesen/überschreiben
  konnte – ADBora (Batch-Restore) und INSTALL.cmd geben dem Spiel jetzt Lese-/Schreibzugriff darauf.
- DE: Backup warnt, wenn eine App mit „Daten“ noch läuft – viele Spiele schreiben Einstellungen und
  Spielstände erst beim Beenden.
- DE: OBB-Wiederherstellung kopiert jetzt ebenfalls über einen Zwischenordner (Unterordner in
  Android/obb wurden von neueren Android-Versionen abgelehnt).
- EN: APK backup: files and backup folders carry the device in their name, e.g.
  `Witchblood-1.0_PFDMR.apk` or `Backup_2026-10-21_10-05-28_Quest3` – INSTALL.cmd/INSTALL.txt use
  the new names. INSTALL.cmd now restores everything: APK, then the OBB and Data folders (if in the
  backup).
- EN: Save games: for debuggable apps (many VR games), "Data" now also backs up the internal app data
  (`/data/data/<package>` via run-as, without caches) to `Internal/data.tar`; batch restore and INSTALL.cmd
  write it back. The "Data folder" column shows "+ internal" or "Internal only".
- EN: Restore after a fresh install: files copied back to Android/data and Android/obb belonged to the
  ADB user, so the game could not read/overwrite its save games and settings – ADBora (batch restore) and
  INSTALL.cmd now give the game read/write access to them.
- EN: Backup warns if an app with "Data" is still running – many games write settings and save games
  only when they are quit.
- EN: OBB restore now also copies through a staging folder (newer Android versions refused sub folders
  in Android/obb).
- DE: Performance › „Werte auswählen …“: beim Play For Dream zeigt „Aktuell“ jetzt, warum ein
  Wert fehlt („nur Quest“ bzw. „nur mit PFD-Metrics“); „Alle anzeigen“ wählt die reinen
  Quest-Werte dort nicht mehr aus; der GPU-Takt (System), den das Gerät sperrt, erscheint dort
  nicht mehr in der Liste.
- EN: Performance › "Select values …": on the Play For Dream, "Current" now shows why a value is
  missing ("Quest only" or "PFD-Metrics only"); "Show all" no longer selects the Quest-only values
  there; the system GPU clock, which the device blocks, is no longer listed there.

## v1.5.0 – 2026-10-17

- DE: Neuer Tab **Performance**: Leistungswerte der XR-Runtime (Play For Dream: PerfStats /
  CompositorMonitor, Meta Quest: VrApi) und des Systems (CPU-/GPU-Last, Takt, Temperaturen,
  Akku, RAM, Thermik) in Echtzeit per ADB – ohne App auf dem Headset; Kacheln, Verlauf
  (bis 6 Werte), „Werte auswählen …“ (anzeigen / Graph pro Wert, zeigt, welche Werte das Gerät
  liefert; kleine Vorauswahl), Akku-Strom und Leistungsaufnahme in W (Play For Dream, über
  PFD-Metrics; nur im Standalone-Betrieb korrekt, über USB verfälscht das Laden die Werte),
  CSV-Protokoll (Dokumente\ADBora\Metrics), HUD von PFD-Metrics bzw. OVR Metrics Tool im
  Headset ein-/ausschalten; fehlt die HUD-App, installiert ADBora PFD-Metrics direkt von
  GitHub auf das Play For Dream bzw. öffnet für die Quest das OVR Metrics Tool im Meta Store.
- EN: New **Performance** tab: XR runtime values (Play For Dream: PerfStats / CompositorMonitor,
  Meta Quest: VrApi) and system values (CPU/GPU load, clocks, temperatures, battery, RAM,
  thermal) in real time via ADB – no app needed on the headset; tiles, history (up to 6 values),
  "Select values …" (show / graph per value, shows which values the device delivers; small
  default selection), battery current and power draw in W (Play For Dream, via PFD-Metrics; only correct standalone,
  charging over USB distorts them), CSV log (Documents\ADBora\Metrics),
  switch the PFD-Metrics / OVR Metrics Tool HUD on the headset; if the HUD app is missing, ADBora
  installs PFD-Metrics straight from GitHub onto the Play For Dream or opens the OVR Metrics Tool
  in the Meta Store for the Quest.

## v1.4.1 – 2026-10-12

- DE: Behebt den Absturz beim Start von 1.4.0 („Invoke oder BeginInvoke kann … erst aufgerufen
  werden …“). Der Build prüft jetzt jeden Start-Tab automatisch, bevor ein Paket entsteht.
- EN: Fixes the start-up crash of 1.4.0 ("Invoke or BeginInvoke cannot be called … until the
  window handle has been created"). The build now checks every start tab automatically before
  a package is created.

## v1.4.0 – 2026-10-12

- DE: Neuer Tab **Stream (Beta, experimentell)**: Headset-Bild live auf dem PC (scrcpy, wird bei
  Bedarf von GitHub geladen), Display-Auswahl, Ausschnitt (ganzes Bild, ein Auge, Viertel),
  Drehung und Feinabstimmung (Grösse, Versatz, Neigung) mit Voreinstellungen für Meta Quest 3
  und Play For Dream, gemerkt pro Gerätemodell; Qualität/FPS, Ton bzw. Mikrofon des Headsets,
  Aufnahme als MP4 (Videos\ADBora), Screenshot (Bilder\Screenshots); Näherungssensor aus
  (Quest) bzw. Display-Timeout verlängern (andere Geräte) während des Streams.
- EN: New **Stream (beta, experimental)** tab: headset view live on the PC (scrcpy, downloaded
  from GitHub when needed), display selection, view (full, one eye, quarter), rotation and fine
  tuning (size, offset, tilt) with presets for Meta Quest 3 and Play For Dream, remembered per
  device model; quality/FPS, headset audio or microphone, MP4 recording (Videos\ADBora),
  screenshot (Pictures\Screenshots); proximity sensor off (Quest) or longer display timeout
  (other devices) while streaming.

## v1.3.0 – 2026-10-09

- DE: Tab „ADB Commands“: **Befehle für dieses Gerät** – ADBora erkennt das aktive Gerät
  (Meta Quest, Pico, Android) und bietet passende Befehle mit Beschreibung an; riskante
  Befehle werden markiert und vor dem Ausführen bestätigt. **Entdecken …** liest Apps,
  Systemwerte, Einstellungen und Systemdienste live vom Gerät und erzeugt daraus Befehle.
  Eigene Befehle lassen sich einem Gerätemodell zuordnen.
- EN: "ADB commands" tab: **Commands for this device** – ADBora detects the active device
  (Meta Quest, Pico, Android) and offers matching commands with descriptions; risky commands
  are marked and confirmed before running. **Explore …** reads apps, system properties,
  settings and services live from the device and builds commands from them. Own commands
  can be assigned to a device model.

## v1.2.1 – 2026-10-02

- DE: Daten-/OBB-Backup bricht nicht mehr ab, wenn einzelne Dateien für ADB nicht lesbar sind
  (z. B. Vulkan-Shader-Caches mancher Spiele). Die übrigen Dateien werden einzeln kopiert,
  gesperrte Dateien übersprungen und in `manifest.json` („skipped_unreadable“) aufgelistet.
- EN: Data/OBB backups no longer fail when single files cannot be read by ADB (e.g. Vulkan
  shader caches of some games). The remaining files are copied one by one, blocked files are
  skipped and listed in `manifest.json` ("skipped_unreadable").

## v1.2.0 – 2026-10-02

**Deutsch**

- **.apks-Unterstützung:** Split-Apps werden im Backup als eine `.apks`-Datei gespeichert
  (wie SAI / AnExplorer, abschaltbar); `.apks`, `.xapk` (inkl. OBB) und unverschlüsselte
  `.apkm` lassen sich per Drag & Drop, „APK auswählen“ und Batch-Restore installieren
- Jedes App-Backup enthält ein **INSTALL.cmd** zum Doppelklicken (optional mit Seriennummer
  als Parameter); INSTALL.txt erklärt den manuellen Weg verständlicher
- Installation: bei Übertragungsfehlern automatisch ein zweiter Versuch ohne Streaming

**English**

- **.apks support:** split apps are stored as one `.apks` file in backups (like SAI /
  AnExplorer, can be switched off); `.apks`, `.xapk` (incl. OBB) and unencrypted `.apkm`
  can be installed via drag & drop, "Choose APK" and batch restore
- Every app backup contains an **INSTALL.cmd** to double-click (optionally with the serial
  as parameter); INSTALL.txt explains the manual way more clearly
- Install: on transfer errors a second attempt without streaming is made automatically

## v1.1.0 – 2026-09-30

**Deutsch**

- **Batch-Restore** (Tab „APK Install“): ganzen Backup-Ordner laden oder hineinziehen,
  pro App APK / OBB / Daten auswählen und in einem Rutsch wiederherstellen
- Neuer Tab **Build Info**: Version, Build-Datum, Variante, .NET, ADB, Repo, Datenordner, Lizenz
- **Update-Prüfung** beim Start und auf Knopfdruck, **Auto-Update** für Installer- und
  portable Version (Download SHA-256-geprüft, Neustart danach)
- Warnung beim Schließen, solange ein Vorgang (z. B. Backup) läuft

**English**

- **Batch restore** ("APK install" tab): load or drop a whole backup folder, choose
  APK / OBB / data per app and restore them in one go
- New **Build info** tab: version, build date, variant, .NET, ADB, repo, data folder, license
- **Update check** at start and on demand, **auto-update** for the installed and the
  portable version (SHA-256 verified download, restart afterwards)
- Warning when closing while an operation (e.g. a backup) is running

## v1.0.0 – 2026-09-30

Erste Veröffentlichung von **ADBora – Android Device Toolbox** · First release.

**Deutsch**

- Native Windows-Anwendung (.NET 8, self-contained), dunkles Theme, Deutsch/Englisch
- Globale Geräteauswahl (mehrere Geräte, USB und WLAN)
- **Allgemeine Einstellungen:** ADB-Einrichtung, Versionen, Datenablage, Geräteliste,
  WLAN-ADB mit automatischer Umschaltung (`adb tcpip`), Geräteinformationen inkl. IPv4
- **APK Backup:** Apps einlesen (Name, Version, Icon aus der APK, Metadaten-Cache),
  APK/OBB/Data sichern inkl. Split-APKs und Prüfsummen, Apps deinstallieren
- **APK Install:** APKs per Drag & Drop installieren (Split-APKs, Backup-Ordner),
  OBB-Ordner kopieren, Gerät (MTP) im Explorer öffnen
- **Speedtest:** ADB-Durchsatz über USB oder WLAN, maximale stabile Rate oder feste Bandbreite
- **ADB Commands:** Konsole mit Live-Ausgabe, Verlauf und gespeicherten Befehlen
- Portable Version (Daten neben der EXE) und Installer (Deinstaller fragt nach dem Löschen der Nutzerdaten)
- Übernimmt beim ersten Start Einstellungen der früheren Tools „APK Backup Tool“ und „ADB USB Speed Test“

**English**

- Native Windows application (.NET 8, self-contained), dark theme, German/English
- Global device selection (several devices, USB and Wi-Fi)
- **General settings:** ADB setup, versions, data storage, device list,
  Wi-Fi ADB with automatic switching (`adb tcpip`), device information incl. IPv4
- **APK backup:** scan apps (name, version, icon from the APK, metadata cache),
  back up APK/OBB/Data incl. split APKs and checksums, uninstall apps
- **APK install:** install APKs via drag & drop (split APKs, backup folders),
  copy OBB folders, open the device (MTP) in Explorer
- **Speed test:** ADB throughput via USB or Wi-Fi, maximum stable rate or fixed bandwidth
- **ADB commands:** console with live output, history and saved commands
- Portable version (data next to the EXE) and installer (uninstaller asks whether to delete user data)
- Takes over settings of the former tools "APK Backup Tool" and "ADB USB Speed Test" on first start
