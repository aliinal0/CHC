# Contributing to CHC

Thanks for helping! Bug reports, hardware reports, new health checks and UI improvements are all welcome.

By contributing you agree that your contribution is released under the project's
[PolyForm Noncommercial License 1.0.0](LICENSE).

## Testing CHC on your PC

The easiest way to help, no programming needed. CHC has been tested on only a few computers, and every
processor, graphics card, motherboard and laptop reports its sensors a little differently. Run CHC, then
tell us in [Discussions → Hardware reports](https://github.com/aliinal0/CHC/discussions/categories/hardware-reports)
what it got right and what it missed. Laptops, Wi-Fi and Bluetooth devices are especially welcome.

## Reporting a problem

Open a [bug report](https://github.com/aliinal0/CHC/issues/new/choose). Please attach the **JSON export**
(Report → Save JSON data) and say whether CHC ran as administrator.
Serial numbers are masked in exports unless you turn them on.

Security problems go through [private reporting](SECURITY.md), not public issues.

## Translations

CHC is English only for now, while its features settle. New texts go into
`src/CHC.Core/Localization/Languages/en.json`, and `python tools/check-languages.py` checks that file.

If you want CHC in your language today, it can load a translation from outside:

- Start from `en.json`; it always has every key.
- Translate only the values. Keep every key and every placeholder such as `{0}`, `{1:0}` or `{0:N0}` exactly as it is.
- `_language` is the language's own name as shown in the language picker (for example `Deutsch`).
- Missing keys fall back to English, so a partial translation still works.
- Put the file into a `Languages` folder next to `CHC.exe` and restart CHC. It then appears in Settings.

## Screenshots and demo mode

Never put screenshots of your own computer in the repository. Start CHC in demo mode instead; it shows a
made-up PC and changes nothing on your machine:

```bash
dotnet run --project src/CHC.App -- --demo
```

| Option | What it does |
|---|---|
| `--demo` | The sample desktop PC |
| `--demo-laptop` | The sample laptop, with a battery |
| `--welcome` | Opens the first-run guide (in demo mode it saves nothing) |
| `--theme light` or `--theme dark` | Picks the theme for that run only |
| `--screenshot` | Hides the "sample data" badge |

The README is written in English only. Its images live in `docs/readme/`, and every one of them is taken in demo mode.

## Adding a health check

1. Collect the data in `src/CHC.Core/Collection/` and store it in the model (`src/CHC.Core/Models/Snapshot.cs`).
2. Add the rule to `src/CHC.Core/Health/HealthAnalyzer.cs` with a new key, for example `storage.something`.
3. Add `<key>.title`, and optionally `<key>.detail` and `<key>.fix`, to `en.json`.

## Code style

- Keep `CHC.Core` free of UI code so the reports and a future command line tool can reuse it.
- Match the surrounding code; the repository builds without warnings and should stay that way.
- Don't send anything about the person off the PC, and don't watch what people type or click. See [Is it safe?](README.md#is-it-safe) for what CHC promises and for the little it goes online for.
