<p align="center">
  <img src="src/CHC.App/Assets/chc.png" width="96" alt="CHC logo">
</p>

<h1 align="center">CHC – Computer Health Check</h1>

<p align="center">
  A free Windows app that checks the health of your PC (processor, memory, disks, graphics card, battery, motherboard and system stability) and explains the results, from a plain-language summary down to the raw technical data.
</p>

<p align="center">
  <b>English</b> ·
  <a href="README.tr.md">Türkçe</a> ·
  <a href="README.de.md">Deutsch</a> ·
  <a href="README.es.md">Español</a> ·
  <a href="README.fr.md">Français</a> ·
  <a href="README.pt-BR.md">Português (Brasil)</a> ·
  <a href="README.ru.md">Русский</a> ·
  <a href="README.zh-CN.md">简体中文</a>
</p>

## Features

- **Health score (0–100)** for the whole computer and for every component, with clear *OK / Tip / Warning / Critical* states.
- **Issues and recommendations.** Every finding says what is wrong, why it matters and what to do about it.
- **From simple to technical.** Each component page starts with a short summary, then live values, then the technical specifications, tables (memory modules, SMART attributes, event log) and every raw sensor.
- **Processor:** load, temperature, clocks, power, cores and threads, cache, virtualization.
- **Memory:** usage, commit charge, modules per slot (type, speed, part number), single-channel and XMP/EXPO detection.
- **Storage:** Windows health status plus full SMART (the same engine as CrystalDiskInfo, through DiskInfoToolkit): life remaining, temperature, power-on hours, total writes, reallocated, pending and uncorrectable sectors, NVMe critical warnings, and free space per drive.
- **Graphics:** temperature, hot spot, memory junction, load, power, VRAM, driver version and age.
- **Battery** (laptops): wear level, design and full-charge capacity, cycle count.
- **Motherboard & system:** BIOS/UEFI version and age, Secure Boot, TPM, Windows version and uptime.
- **Stability:** unexpected shutdowns, blue screens, WHEA hardware errors and disk errors from the last 30 days, plus Device Manager problems.
- **Live sensors** for everything LibreHardwareMonitor can read, with a filter.
- **PDF report** in one click, with a clean ready-made layout. JSON export and a copyable text summary for forums are also available. Serial numbers are hidden unless you turn them on.
- **8 languages:** English, Türkçe, Deutsch, Español, Français, Português (Brasil), Русский, 简体中文. More can be added without recompiling.
- Light and dark theme (follows Windows), Windows 11 Fluent design.

## Download and run

1. Download `CHC.exe` from the [Releases](../../releases) page.
2. Run it. CHC needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (Windows asks to install it on first start if it is missing).

CHC starts in **limited mode** without administrator rights. Click **Run as administrator** in the sidebar to unlock SMART disk data and more sensors.

CPU temperature and motherboard sensors (fans, voltages) also need the free, signed [PawnIO](https://pawnio.eu) driver, which modern hardware monitoring tools use. CHC works without it and tells you when it is missing.

> CHC only reads information. It never changes settings, overclocks or writes to your disks.

## Building from source

Requirements: Windows 10/11 x64 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
git clone https://github.com/aliinal0/CHC.git
cd CHC
dotnet run --project src/CHC.App
```

Create a single `CHC.exe`:

```bash
dotnet publish src/CHC.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

### Project structure

```
src/
  CHC.Core/                 Data collection, health rules and reports (no UI)
    Collection/             WMI, registry, event log and SMART collectors
    Sensors/                LibreHardwareMonitor wrapper (live sensors)
    Health/                 HealthAnalyzer: every rule and threshold in one place
    Presentation/           Builds the display model shared by the app and the reports
    Reporting/              HTML/PDF template, JSON and text summary
    Localization/Languages  One JSON file per language
  CHC.App/                  WPF user interface (WPF UI, MVVM)
```

Want to change a threshold or add a check? Everything lives in `src/CHC.Core/Health/HealthAnalyzer.cs`, and the texts are in the language files under the same keys.

## Translations

Every text is in `src/CHC.Core/Localization/Languages/<code>.json`. To add a language:

1. Copy `en.json` to `<code>.json` (for example `it.json` or `pt-PT.json`).
2. Translate the values and keep the keys and `{0}` placeholders unchanged.
3. Either open a pull request or drop the file into the `Languages` folder next to `CHC.exe`. It appears in **Settings → Language** after a restart.

See [CONTRIBUTING.md](CONTRIBUTING.md) for details.

## License

CHC is released under the **[PolyForm Noncommercial License 1.0.0](LICENSE)**.

- ✅ You may use, study, modify and share CHC for free for personal and other noncommercial purposes.
- ❌ You may not sell CHC, distribute it for money or use it for commercial purposes.

Third-party components keep their own licenses. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Disclaimer

The health score is based on the data your hardware reports and on common rules of thumb. It is a helpful indicator, not a guarantee. Always keep backups of important data.
