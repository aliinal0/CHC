<p align="center">
  <img src="src/CHC.App/Assets/chc.png" width="96" alt="CHC-Logo">
</p>

<h1 align="center">CHC – Computer Health Check</h1>

<p align="center">
  Eine kostenlose Windows-App, die den Zustand Ihres PCs prüft (Prozessor, Arbeitsspeicher, Laufwerke, Grafikkarte, Akku, Mainboard und Systemstabilität) und die Ergebnisse erklärt – von einer verständlichen Zusammenfassung bis zu den technischen Rohdaten.
</p>

<p align="center">
  <a href="README.md">English</a> ·
  <a href="README.tr.md">Türkçe</a> ·
  <b>Deutsch</b> ·
  <a href="README.es.md">Español</a> ·
  <a href="README.fr.md">Français</a> ·
  <a href="README.pt-BR.md">Português (Brasil)</a> ·
  <a href="README.ru.md">Русский</a> ·
  <a href="README.zh-CN.md">简体中文</a>
</p>

## Funktionen

- **Gesundheitswert (0–100)** für den ganzen Computer und jede Komponente, mit klaren Zuständen *OK / Hinweis / Warnung / Kritisch*.
- **Probleme und Empfehlungen.** Jedes Ergebnis erklärt, was nicht stimmt, warum es wichtig ist und was Sie tun können.
- **Von einfach bis technisch.** Jede Komponentenseite beginnt mit einer kurzen Zusammenfassung, dann folgen Live-Werte, technische Daten, Tabellen (Speichermodule, SMART-Attribute, Ereignisprotokoll) und alle Rohsensoren.
- **Prozessor:** Auslastung, Temperatur, Takt, Leistung, Kerne und Threads, Cache, Virtualisierung.
- **Arbeitsspeicher:** Auslastung, zugesicherter Speicher, Module pro Steckplatz (Typ, Geschwindigkeit, Teilenummer), Erkennung von Single-Channel und XMP/EXPO.
- **Speicher:** Windows-Integritätsstatus und vollständige SMART-Daten (dieselbe Engine wie CrystalDiskInfo, über DiskInfoToolkit): Restlebensdauer, Temperatur, Betriebsstunden, geschriebene Datenmenge, neu zugewiesene, ausstehende und nicht korrigierbare Sektoren, kritische NVMe-Warnungen und freier Platz pro Laufwerk.
- **Grafik:** Temperatur, Hotspot, Speichertemperatur, Auslastung, Leistung, VRAM, Treiberversion und -alter.
- **Akku** (Laptops): Verschleiß, Nenn- und volle Ladekapazität, Ladezyklen.
- **Mainboard & System:** BIOS/UEFI-Version und -Alter, Secure Boot, TPM, Windows-Version und Laufzeit.
- **Stabilität:** unerwartete Abschaltungen, Bluescreens, WHEA-Hardwarefehler und Laufwerksfehler der letzten 30 Tage sowie Probleme im Geräte-Manager.
- **Live-Sensoren** für alles, was LibreHardwareMonitor lesen kann, mit Filter.
- **PDF-Bericht** mit einem Klick in einem fertigen, übersichtlichen Layout. Zusätzlich JSON-Export und eine Textzusammenfassung zum Einfügen in Foren. Seriennummern bleiben ausgeblendet, sofern Sie sie nicht einschalten.
- **8 Sprachen:** English, Türkçe, Deutsch, Español, Français, Português (Brasil), Русский, 简体中文. Weitere Sprachen lassen sich ohne Neukompilieren hinzufügen.
- Helles und dunkles Design (folgt Windows), Windows-11-Fluent-Design.

## Herunterladen und starten

1. Laden Sie `CHC.exe` von der Seite [Releases](../../releases) herunter.
2. Starten Sie die Datei. CHC benötigt die [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (Windows bietet beim ersten Start die Installation an, falls sie fehlt).

CHC startet ohne Administratorrechte im **eingeschränkten Modus**. Klicken Sie in der Seitenleiste auf **Als Administrator starten**, um SMART-Daten und weitere Sensoren freizuschalten.

Für CPU-Temperatur und Mainboard-Sensoren (Lüfter, Spannungen) wird außerdem der kostenlose, signierte Treiber [PawnIO](https://pawnio.eu) benötigt, den moderne Hardware-Monitoring-Tools verwenden. CHC funktioniert auch ohne ihn und weist darauf hin, wenn er fehlt.

> CHC liest nur Informationen. Es ändert keine Einstellungen, übertaktet nichts und schreibt nicht auf Ihre Laufwerke.

## Aus dem Quellcode erstellen

Voraussetzungen: Windows 10/11 x64 und das [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
git clone https://github.com/aliinal0/CHC.git
cd CHC
dotnet run --project src/CHC.App
```

Eine einzelne `CHC.exe` erstellen:

```bash
dotnet publish src/CHC.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Sie möchten einen Grenzwert ändern oder eine Prüfung hinzufügen? Alle Regeln stehen in `src/CHC.Core/Health/HealthAnalyzer.cs`, die Texte unter denselben Schlüsseln in den Sprachdateien.

## Übersetzungen

Alle Texte befinden sich in `src/CHC.Core/Localization/Languages/<code>.json`. So fügen Sie eine Sprache hinzu:

1. Kopieren Sie `en.json` nach `<code>.json` (zum Beispiel `it.json`).
2. Übersetzen Sie die Werte; Schlüssel und Platzhalter wie `{0}` bleiben unverändert.
3. Öffnen Sie einen Pull Request oder legen Sie die Datei in den Ordner `Languages` neben `CHC.exe`. Nach einem Neustart erscheint sie unter **Einstellungen → Sprache**.

Details finden Sie in [CONTRIBUTING.md](CONTRIBUTING.md).

## Lizenz

CHC steht unter der **[PolyForm Noncommercial License 1.0.0](LICENSE)**.

- ✅ Sie dürfen CHC für private und andere nicht kommerzielle Zwecke kostenlos nutzen, untersuchen, verändern und weitergeben.
- ❌ Sie dürfen CHC nicht verkaufen, gegen Geld verbreiten oder kommerziell nutzen.

Komponenten von Drittanbietern behalten ihre eigenen Lizenzen: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Haftungsausschluss

Der Gesundheitswert beruht auf den Daten, die Ihre Hardware meldet, und auf gängigen Faustregeln. Er ist ein hilfreicher Hinweis, keine Garantie. Sichern Sie wichtige Daten immer.
