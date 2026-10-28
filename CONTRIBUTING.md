# Contributing to CHC

Thanks for helping! Bug reports, translations, new health checks and UI improvements are all welcome.

By contributing you agree that your contribution is released under the project's
[PolyForm Noncommercial License 1.0.0](LICENSE).

## Reporting a problem

Please attach the **JSON export** (Report → Save JSON data) and say whether CHC ran as administrator.
Serial numbers are masked in exports unless you turn them on.

## Translations

Language files live in `src/CHC.Core/Localization/Languages/`. The file name is the language code
(`de.json`, `pt-BR.json`, `zh-CN.json`...).

- Start from `en.json`; it always has every key.
- Translate only the values. Keep every key and every placeholder such as `{0}`, `{1:0}` or `{0:N0}` exactly as it is.
- `_language` is the language's own name as shown in the language picker (for example `Deutsch`).
- Missing keys fall back to English, so a partial translation still works.
- Use the percent style of your language in sentences (`{0:0}%`, `{0:0} %` or `%{0:0}`).

You can test a translation without building: put the file into a `Languages` folder next to `CHC.exe`
and restart CHC.

## Adding a health check

1. Collect the data in `src/CHC.Core/Collection/` and store it in the model (`src/CHC.Core/Models/Snapshot.cs`).
2. Add the rule to `src/CHC.Core/Health/HealthAnalyzer.cs` with a new key, for example `storage.something`.
3. Add `<key>.title`, and optionally `<key>.detail` and `<key>.fix`, to `en.json` (and other languages if you can).

## Code style

- Keep `CHC.Core` free of UI code so the reports and a future command line tool can reuse it.
- Match the surrounding code; the repository builds without warnings and should stay that way.
