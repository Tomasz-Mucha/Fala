# Fala

A podcast player for Windows 10/11 (WinUI 3, .NET 8). The interface is available in English, Polish and Swedish.

Free and open source under the [MIT](LICENSE) license.

## Features

- **Subscriptions**: search the Apple Podcasts catalog, paste an RSS/Atom feed or a page with a feed,
  import and export **OPML** (folders in the file become groups).
- **Library**: podcast groups, episode filters (all / unplayed / downloaded), sort order,
  "mark all as played".
- **New episodes**: background checks every 1–12 hours, badges on tiles and in the menu, Windows notifications.
- **Playback**: speed 0.75–2×, sleep timer, media keys and the system media panel (SMTC),
  resume from the saved position, even after restarting the app.
- **Queue** saved between sessions, searchable **history**, **bookmarks** with comments
  (export to Markdown).
- **Show notes**: timestamps (e.g. `12:34`) are clickable and jump to that point in the episode.
- **Downloads** for offline listening.

### Keyboard shortcuts

| Key | Action |
|---|---|
| Space | play / pause |
| ← / → | back 15 s / forward 30 s |
| + / − | playback speed |
| M | mute |
| Ctrl+F | search |

## Installation

From **Releases**, download `Fala-<version>-win-x64.zip` (or `win-arm64` for ARM processors),
unzip it and run `Fala.exe`. The package is self-contained (.NET and Windows App SDK included).

Requirements: Windows 10 version 2004 (19041) or later.

## App data

Everything is stored locally in `%LOCALAPPDATA%\Fala`:

| File / folder | Contents |
|---|---|
| `fala.db` | library, progress, history, bookmarks, queue (SQLite) |
| `settings.json` | settings |
| `Downloads\` | downloaded episodes |
| `logs\` | logs from the last 7 days, useful when reporting bugs |

The `FALA_DATA_DIR` environment variable redirects all data to another location
(e.g. for testing on a copy of your data, or for a portable setup).

## License

Fala is released under the **MIT** license. See [`LICENSE`](LICENSE).
Copyright (c) 2026 Tomasz Mucha.

### Third-party libraries

Fala uses third-party libraries under the MIT, Apache-2.0 and BSD licenses and Microsoft licenses
(Windows App SDK). Their texts are in `Fala/THIRD-PARTY-NOTICES.txt` (shipped with every package
and available in Settings). Release packages contain Microsoft components (.NET, Windows App SDK,
WebView2) covered by their own licenses, not by MIT. After changing dependencies:

```powershell
./tools/Generate-ThirdPartyNotices.ps1          # generates the notices file
./tools/Generate-ThirdPartyNotices.ps1 -Check   # checks every dependency has an entry (runs in CI)
```

## Building

Requires the **.NET 10 SDK** (the project targets .NET 8 but uses C# 14).

```powershell
dotnet build Fala/Fala.csproj -p:Platform=x64
dotnet test Fala.Tests/Fala.Tests.csproj
```

The `Fala.Tests` project compiles the UI-independent logic (RSS, OPML, search, formatting)
directly from the app's source files.

### Secret leak protection

The repository has `pre-commit` and `pre-push` hooks that block keys, tokens, passwords, certificates,
databases and build output. Enable them once after cloning:

```powershell
git config core.hooksPath .githooks
```

### Releasing a new version

```powershell
git tag v0.5.0
git push origin v0.5.0
```

GitHub Actions runs the tests, builds x64 and ARM64 packages and publishes them in **Releases**.
Every push and pull request is tested and built (the `Build` workflow).
