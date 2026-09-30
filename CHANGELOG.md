# Changelog

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
