# Translating ADBora · ADBora übersetzen

**[English](#english) · [Deutsch](#deutsch)**

---

## English

ADBora is built in German and English. Every other language is a single file in this folder,
`<code>.json`, which ADBora users can download from *General settings › Change language* – no new
ADBora version needed. Existing languages: see the `*.json` files here. Corrections are very welcome!

### Improve an existing language

Open e.g. `it.json`, fix the texts on the **right** side and send a pull request (or open an issue
with your corrections). Several languages were created by an AI (see `_meta.translator`) – native
speakers please check them.

### Create a new language

1. Copy `template.json` and name it after the language code: `it.json`, `es.json`, `pt.json`
   (Brazil: `pt-br.json`), `ru.json`, `ko.json`, `zh-cn.json` (Simplified), `zh-tw.json` (Traditional) …
2. Fill in `_meta`: `"code"`, `"name"` (the language's own name, e.g. `"Italiano"`), `"version"`
   (the ADBora version you translated), `"translator"`. Delete `_readme`.
3. Translate the **values** (right side) only.
4. Test: *General settings › Change language › Open language folder*, put the file there and
   reopen the dialog. The column **Translated** shows e.g. `711 / 711`; problems are shown in red
   (hover for details). A broken file is listed in red with the reason (e.g. the line of a JSON error).
5. Send the file as pull request into this `lang` folder.

### Rules – please read

| | |
|---|---|
| **Never change the left side (key).** | It is the English original; ADBora finds the translation by it. One changed character and the text stays English. |
| **Keep placeholders.** | `{0}`, `{1}`, `{2:0.0}`, `{0:HH:mm:ss}` are replaced by values. They may move within the sentence but must all stay, unchanged. |
| **Keep `\n` and `\"`.** | `\n` = line break (same number as in the original), `\"` = quotation mark inside the text. |
| **Keep spaces at the start/end** | of a text (` · charging`, `Error: `) – texts are joined together. |
| **Do not translate** | ADB, adb.exe, APK, OBB, run-as, scrcpy, PFD-Metrics, OVR Metrics Tool, GitHub, commands, paths, property names (`ro.product.model`), units (MB, Mbit/s, Hz). |
| **Keep it short** | for tabs and buttons (e.g. `General settings`, `Speed test`) – long words get cut off. |
| **Plain JSON, UTF-8.** | No explanations before or after, no ```json markers. Save as UTF-8 (Windows Notepad does this by default) – then Cyrillic, Chinese, Korean etc. work. |

### Using an AI (ChatGPT, Gemini, Claude …)

AIs work well, but: **(1)** they often stop in the middle of long files – translate `template.json` in
3–4 parts and join them; **(2)** they add chat text ("Here is your file:", "json", "Use code with
caution") – that breaks the file. Use this prompt and check the result in ADBora (step 4):

```text
Translate the VALUES (right side) of this ADBora language file into <LANGUAGE>.
Keep every KEY (left side) exactly unchanged. Keep placeholders like {0}, {1:0.0},
\n and \" exactly. Do not translate adb, OBB, APK, run-as, scrcpy, file names,
commands or units. Keep tab and button texts short. Fill "_meta" with code "<code>"
and name "<language name>". Output ONLY the raw JSON – no explanation, no code fences.
```

---

## Deutsch

ADBora ist auf Deutsch und Englisch eingebaut. Jede weitere Sprache ist eine einzelne Datei in diesem
Ordner, `<code>.json`, die ADBora-Nutzer unter *Allgemeine Einstellungen › Sprache ändern* herunterladen
können – ohne neue ADBora-Version. Vorhandene Sprachen: die `*.json`-Dateien hier. Korrekturen sind
sehr willkommen!

### Vorhandene Sprache verbessern

Z. B. `it.json` öffnen, die Texte auf der **rechten** Seite korrigieren und als Pull Request schicken
(oder ein Issue mit den Korrekturen eröffnen). Einige Sprachen wurden von einer KI erstellt (siehe
`_meta.translator`) – Muttersprachler bitte prüfen.

### Neue Sprache erstellen

1. `template.json` kopieren und nach dem Sprachcode benennen: `it.json`, `es.json`, `pt.json`
   (Brasilien: `pt-br.json`), `ru.json`, `ko.json`, `zh-cn.json` (vereinfacht), `zh-tw.json` (traditionell) …
2. `_meta` ausfüllen: `"code"`, `"name"` (Name der Sprache in der Sprache selbst, z. B. `"Italiano"`),
   `"version"` (übersetzte ADBora-Version), `"translator"`. `_readme` löschen.
3. Nur die **Werte** (rechte Seite) übersetzen.
4. Testen: *Allgemeine Einstellungen › Sprache ändern › Sprachordner öffnen*, Datei hineinlegen und das
   Fenster neu öffnen. Die Spalte **Übersetzt** zeigt z. B. `711 / 711`; Probleme erscheinen rot (Maus
   darüber = Details). Eine defekte Datei wird rot mit Grund angezeigt (z. B. Zeile eines JSON-Fehlers).
5. Datei als Pull Request in diesen `lang`-Ordner schicken.

### Regeln – bitte lesen

| | |
|---|---|
| **Linke Seite (Schlüssel) nie ändern.** | Sie ist das englische Original; ADBora findet die Übersetzung darüber. Ein geändertes Zeichen und der Text bleibt englisch. |
| **Platzhalter behalten.** | `{0}`, `{1}`, `{2:0.0}`, `{0:HH:mm:ss}` werden durch Werte ersetzt. Sie dürfen im Satz wandern, müssen aber alle unverändert bleiben. |
| **`\n` und `\"` behalten.** | `\n` = Zeilenumbruch (gleiche Anzahl wie im Original), `\"` = Anführungszeichen im Text. |
| **Leerzeichen am Anfang/Ende** | eines Textes behalten (` · charging`, `Error: `) – Texte werden zusammengesetzt. |
| **Nicht übersetzen:** | ADB, adb.exe, APK, OBB, run-as, scrcpy, PFD-Metrics, OVR Metrics Tool, GitHub, Befehle, Pfade, Property-Namen (`ro.product.model`), Einheiten (MB, Mbit/s, Hz). |
| **Kurz halten** | bei Reitern und Schaltflächen (z. B. `General settings`, `Speed test`) – lange Wörter werden abgeschnitten. |
| **Reines JSON, UTF-8.** | Kein Text davor oder danach, keine ```json-Markierung. Als UTF-8 speichern (Windows-Editor macht das standardmässig) – dann funktionieren Kyrillisch, Chinesisch, Koreanisch usw. |

### Mit einer KI übersetzen (ChatGPT, Gemini, Claude …)

KIs funktionieren gut, aber: **(1)** sie hören bei langen Dateien oft mittendrin auf – `template.json` in
3–4 Teilen übersetzen lassen und zusammensetzen; **(2)** sie schreiben Chat-Text dazu („Hier ist deine
Datei:“, „json“, „Code mit Vorsicht verwenden“) – das macht die Datei kaputt. Den englischen Auftrag oben
verwenden (funktioniert in jeder KI) und das Ergebnis in ADBora prüfen (Schritt 4).
