# ADBora – Übergabe (Stand 19.11.2026)

Diese Übergabe beschreibt den Linux-Port. ADBora wurde von WinForms/.NET 8 auf eine **gemeinsame Basis für Windows und Linux** umgebaut: .NET 10, Avalonia 12 und SkiaSharp. Unter Linux (CachyOS, KDE Wayland) ist der Port umfassend getestet. Unter **Windows lief er noch nie**. Er kompiliert dort (Cross-Build `win-x64` ohne Warnungen), aber gestartet wurde er nicht. Der nächste Schritt ist der Test unter Windows 11.

## Repository

| Punkt | Stand |
|---|---|
| Branch | **`avalonia`** (14 Commits auf `main`); `main` = alte WinForms-Version |
| Version | 1.6.0 (`src/ADBora.App/ADBora.App.csproj`) |
| Remote | **nichts gepusht**, alles nur lokal committet; Push, PR oder Release nur nach Rückfrage |
| Zeilenenden | alle Dateien LF, keine `.gitattributes` |

### Struktur

```
ADBora.slnx                 Solution (beide Projekte)
src/ADBora.Core/            Logik ohne UI (Namespace AdbTool.*): ADB, Backup, Cast, Speedtest-Helfer,
                            Performance-Parser, Updater, Plattform-Weichen (Core/Platform.cs), Diagnosemodi
src/ADBora.App/             Avalonia-Oberfläche (AssemblyName "ADBora"): UI/ = Fenster + Helfer, Tabs/ = Seiten
build/BUILD.ps1 / BUILD.cmd Windows: Portable-ZIP + Setup (Inno Setup 6, build/installer.iss)
build/build-linux.sh        Linux: tar.gz, Smoke-Test aller Tabs
build/adb/                  mitgeliefertes ADB für Windows
tools/LangExtract           erzeugt lang/template.json (DE/EN-Texte aus Loc.T/Ui-Aufrufen)
CHANGELOG.md                Abschnitt "Unveröffentlicht / Unreleased" beschreibt alle Änderungen
```

Die UI-Helfer in `UI/Ui.cs` (`Ui.Label`, `Ui.Button`, `Ui.Grid`, `Ui.AddRow`, `Card` …) tragen bewusst die Namen aus der WinForms-Version. So liessen sich die Seiten mechanisch portieren. Die Texte stehen auf Deutsch und Englisch als `Ui.X("de", "en")` oder `Loc.T("de", "en")` im Code.

## Auf dem Windows-Rechner

1. **Voraussetzungen:**
   - .NET 10 SDK, nötig für den Build. Die Anwendung selbst ist self-contained und braucht auf dem Ziel-PC kein .NET.
   - Optional Inno Setup 6, für den Setup-Installer.
2. **Linux-Build-Reste löschen:** Der Ordner wird kopiert, nicht geklont. Damit kommen auch die Build-Ordner mit, und die `obj/project.assets.json` darin enthalten Linux-Pfade. Lösche deshalb vor dem ersten Build
   `src\ADBora.Core\bin`, `src\ADBora.Core\obj`, `src\ADBora.App\bin`, `src\ADBora.App\obj`, `tools\LangExtract\bin`, `tools\LangExtract\obj` und `dist\`. Diese Ordner sind nicht versioniert.
   Wenn keine lokalen Dateien ausserhalb von Git nötig sind, geht das auch mit `git clean -xdf`.
3. **Zeilenenden:** Damit Git nach dem Kopieren nicht alle Dateien als geändert meldet, im Repository einmal `git config core.autocrlf false` setzen. Visual Studio, Rider und dotnet kommen mit LF zurecht.
4. **Bauen und starten:**
   - Normal bauen: `dotnet build ADBora.slnx`
   - Starten: `dotnet run --project src\ADBora.App`
   - Fertige Pakete: `build\BUILD.cmd`. Das Skript macht einen Single-File-Publish, startet einen Smoke-Test mit jedem Tab als Starttab und erzeugt Portable-ZIP und Setup.
   - Bei Start-Abstürzen schreibt ADBora `error.log` in den Datenordner. Im Smoke-Modus kommt zusätzlich Exit-Code 3.
5. **Datenordner:**
   - Windows: `%LOCALAPPDATA%\ADBora`, im Portable-Modus `Data\` neben der EXE.
   - Linux: `~/.local/share/ADBora`.

## Was unter Windows zu prüfen ist

Unter Windows lief noch nichts davon; Linux-seitig ist alles getestet, soweit nicht anders vermerkt. Die Windows-eigenen Codepfade sind nach `Platform.IsWindows` getrennt:

| Bereich | Windows-spezifisch | Datei |
|---|---|---|
| Start, Single-Instance | Mutex + NamedPipe (zweiter Start holt das Fenster nach vorne) | `App/SingleInstance.cs` |
| ADB | mitgeliefertes `adb.exe`, Suche in den Windows-Pfaden | `Core/Core/Adb.cs` |
| Casting (Quest/PFDM/Pico) | FFmpeg-DLLs `avcodec-*.dll` aus dem scrcpy-Ordner (`Platform.FindLibrary`), scrcpy-Download (`ScrcpyTool`, Windows braucht scrcpy fürs Casting) | `Core/Cast/H264Decoder.cs`, `Core/Core/ScrcpyTool.cs` |
| Ton beim Casting | `WaveOutPlayer` (winmm) | `Core/Cast/AudioOut.cs` |
| scrcpy-Modus: Aufnahme beenden | scrcpy-Fenster schliessen, damit die Aufnahme sauber abgeschlossen wird (unter Linux SIGINT) | `App/Tabs/StreamPage.cs` |
| „Gerät im Explorer öffnen“ | MTP über `Shell.Application` | `Core/Core/MtpLocator.cs` |
| Updater | Windows-Paket und Installer, Release-Erkennung | `Core/Core/Updater.cs`, `App/UI/UpdateUi.cs` |
| Datenschutz | Benutzerordner maskieren (Windows-Pfad) | `Core/Core/Privacy.cs` |
| Speedtest | USB-Netzwerkadapter-Erkennung (unter Linux sysfs); nach dem ADB-Test weiterhin `adb kill-server` wie bisher | `App/Tabs/SpeedTestPage.cs` |
| Backup | `INSTALL.cmd` (zusätzlich gibt es jetzt `INSTALL.sh`) | `Core/Backup/BackupService.cs` |
| Build | `BUILD.ps1`, `installer.iss` (Pfade auf die neuen Projekte angepasst, nie ausgeführt) | `build/` |

Grob alle Tabs einmal durchklicken: Allgemeine Einstellungen, APK Backup (inkl. Restore-Dialog), APK Install (Drag & Drop!), Speedtest, Stream (Beta), Performance, ADB Commands, Build Info. Fenster klein ziehen (Seiten müssen scrollen statt abschneiden), Sprache umschalten, HiDPI-Skalierung.

## Neu seit der WinForms-Version (für beide Systeme)

- **Gemeinsame Basis:** Avalonia-Oberfläche, Grafik über SkiaSharp (früher GDI+).
- **Kleine Fenster:** Seiten scrollen, und die Tab-Leiste passt sich an (Icons fallen weg, Text wird gekürzt). Minimale Fenstergrösse 560×400.
- **Performance:**
  - Quest 3: Temperaturen kommen aus `dumpsys thermalservice`, weil die Thermal-Zonen gesperrt sind.
  - Die Messung übersteht ein kurzes USB-Neuanmelden des Headsets: Die Prozesse starten mit `wait-for-device` neu, und erst nach 20 s ohne Daten endet die Messung.
- **Geräteauswahl:** Verschwindet das gewählte Gerät kurz, bleibt es 10 s lang als „Offline“ gewählt. Ein automatischer Wechsel überschreibt nicht mehr das gespeicherte Gerät.
- **Speedtest:**
  - USB-NCM (IPv6 link-local über das Kabel) und USB-Ethernet-Adapter am Router sind getrennte Ziele.
  - Beendet sich der Empfänger auf dem Headset, kommt eine Fehlermeldung statt eines Fantasiewerts.
  - Unter Linux kein `kill-server` nach dem Test, weil die Quest 3 danach „offline“ hing.
  - Beim Beenden kein `kill-server`, wenn ein USB-Speedtest weniger als 30 s zurückliegt.
- **APK Backup:**
  - Icons aus den Dichte-Splits von App-Bundles (z. B. Kodi).
  - **App-Bilder aus dem Meta-Store**, Einstellung `StoreArtwork`, ab Werk an. Auf der Quest zeigen alle Apps ihr Store-Cover. Auf PFDM und Pico füllt ADBora nur Lücken: Apps ohne Icon oder mit Unity-, Unreal- oder Android-Standardlogo, erkannt per 16×16-Fingerabdruck in `Backup/PlaceholderIcons.cs`.
  - Die Suche läuft über den Namen. Ein Treffer gilt bei exaktem Namen oder wenn der APK-Name im Store-Namen steckt und die installierte Version im Store existiert. Das nutzt die inoffizielle GraphQL-Schnittstelle von `graph.oculus.com`, siehe `Backup/StoreArt.cs`. Ergebnisse liegen im Metadaten-Cache (`store_icon`, `store_checked`, `store_rev`).
- **Sonstiges:** PFD-Metrics wird direkt per ADB installiert, ohne PowerShell. Backups enthalten zusätzlich `INSTALL.sh`.

## Diagnosemodi (ohne Oberfläche bzw. automatisiert)

| Aufruf | Zweck |
|---|---|
| `--smoke-test --start-tab N` | Fenster mit Tab N öffnen und schliessen (Build-Check) |
| `--check-apk <apk>` | Name/Version/Icon einer APK → `<apk>.check.txt` |
| `--scan-test <adb> <serial> <ordner> [all\|gaps\|off]` | Backup-Scan wie im Tab, pro App Icon-Quelle und Platzhalter-Erkennung |
| `--icon-fp <ordner>` | Fingerabdrücke + Platzhalter-Erkennung für PNGs |
| `--cast-test <adb> <serial> <quest\|pfd\|pico> <s> <out.png>` | Headset-Casting, letztes Bild + Log + MKV |
| `--audio-test` | Testton auf dem PC |
| `--ncm-test <adb> <serial> <s> <log>` | Quest-USB-Netzwerk halten |
| `--backup-test <adb> <serial> <paket> <ziel>` | Backup einer App |
| `--ui-test speed <s> <adb\|direct> <out.png>` | Speedtest über den echten Tab |
| `--ui-test perf <s> - <out.png>` | Performance-Messung über den echten Tab |
| `--viewer-test` | Cast-Fenster mit Testbild |

`--ui-test` startet mit dem zuletzt gewählten Gerät und Tab aus der `settings.json`. Vor Tests diese Datei unmittelbar vorher sichern und danach zurückschreiben. Die Testmodi speichern selbst nichts.

## Testgeräte und Messwerte unter Linux (Referenz)

| Gerät | Ergebnis |
|---|---|
| Quest 3 | Casting 2064×1440 mit Ton. ADB-Speedtest 4700–4980 Mbit/s. USB-NCM ↓ 2500–2700 / ↑ 2000–2650 Mbit/s. Performance mit XR-Werten (Beat Saber 90 FPS). Store-Bilder für 87 Apps |
| PFDM MR | Casting 1280×720 (~23 fps), AAC, Mikrofon, MKV. ADB-Speedtest ~2030 Mbit/s. Backup + Restore per `INSTALL.sh` |
| Pico 4 Ultra (A9210) | Backup-Scan / Store-Lückenmodus |

## Bekannte Eigenheiten

- **Quest und NCM:** Beim Einschalten von USB-NCM meldet sich die Quest 3 per USB neu an (Produkt-ID 5012/5013 → 5018). Etwa 50 s nach dem Freigeben schaltet sie NCM selbst wieder aus, was eine zweite Neuanmeldung auslöst. Steht beim Start der Speedtest-Tab mit „TCP/IP direkt“ im Vordergrund, schaltet ADBora NCM sofort ein; das ist Absicht.
- **Kabel:** Am Anschluss der PFDM gab es USB-Fehler `-71`. Bei sporadischen Abbrüchen zuerst Kabel oder Hub prüfen.
- **Store-Bilder:** Bei Apps mit internem APK-Namen, etwa „Manta“ für Iron Man VR oder „VR4“ für Resident Evil 4, findet die Store-Suche nichts. Dann bleibt das Engine-Logo.

## Offen

- [ ] Windows 11: bauen, Smoke-Test, alle Tabs durchklicken, Casting mit Ton, Speedtest, Performance, Backup/Restore, Updater, Setup
- [ ] Speedtest mit USB-Ethernet-Adapter am Router (unter Linux mangels Adapter nicht getestet)
- [ ] Danach: Merge `avalonia` → `main`, Release (nur nach Rückfrage)
