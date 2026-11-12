# Trophyline

Your Steam achievements, playtime and library in one good-looking window, plus a
tiny background tracker that keeps a dated journal of every achievement you unlock.

## Download

Get **`Trophyline.exe`** from the [latest release](https://github.com/CillX1/Trophyline/releases/latest)
and double-click it. Windows 10/11 (64-bit); no install and no .NET needed.

To start the tracker with Windows and add Trophyline to the Start menu, run it once with:

```
Trophyline.exe --install
```

The file isn't code-signed, so Windows SmartScreen may warn the first time:
click **More info → Run anyway**.

## How it works

- Reads the achievement files Steam already keeps on your PC
  (`Steam\appcache\stats`). No Steam API key, no public profile needed, works offline.
  It only reads those files and never changes them.
- First run imports your whole history with the original unlock dates.
- Event-driven: it sleeps until Steam updates a game's stats file. On startup it
  catches up on anything unlocked while it wasn't running.
- No window, idle CPU priority, about 7 MB of private memory.

## Use

Open **Trophyline** from the Start menu (right-click it, then
"Pin to taskbar"). Pages:

- **Overview**: your profile over the game you played last, stat tiles, a
  12-month activity heatmap, recent and rarest unlocks, most played games
- **Achievements**: every unlock by month, with search (Ctrl+F), game and rarity filters
- **Games**: cover-art grid with progress bars, sortable; click a game for its page:
  hero art, playtime, completion, and every achievement, including the ones still
  to get (easiest first, by global rarity)
- **Storage**: installed games by size, flags games not played in 6+ months

Everything comes from Steam's files on this PC. Two things are fetched online and
cached: global achievement rarity (Steam's public stats, weekly) and names of
games Steam has no local name for.

Two parts, one exe:
- the **tracker** (`--background`): tiny, starts at login, does all the work
- the **window** (no arguments): only uses memory while it's open

```
Trophyline.exe               open the window
Trophyline.exe --install     start the tracker now + at every login, add Start menu entry
Trophyline.exe --uninstall   remove both and stop (journal is kept)
Trophyline.exe --stop        stop the tracker
Trophyline.exe --update      scan once and update the journal
```

Files: `%LOCALAPPDATA%\Trophyline\`
- `icons\`: local copies of the achievement icons
- `journal.html`: the same journal as a web page
- `journal.tsv`: the data (tab-separated, easy to import elsewhere)
- `config.json`: `language` (e.g. `"german"`) and an optional `statsFolder`

## Build

Needs the .NET 10 SDK.

```
dotnet publish -c Release -r win-x64 -o publish
```

Standalone exe like the release (no .NET needed to run it):

```
dotnet publish -c Release -r win-x64 -o dist -p:SelfContained=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

Releases are built by GitHub Actions when a `v*` tag is pushed.

## License

GPL-3.0. Not affiliated with Valve or Steam.
