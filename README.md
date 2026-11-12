# Trophyline

Your Steam achievements, playtime and library in one good-looking window, plus a
tiny background tracker that keeps a dated journal of every achievement you unlock.
For **Windows, macOS and Linux**.

## Download

From the [latest release](https://github.com/CillX111/Trophyline/releases/latest):

| System | File |
|---|---|
| Windows 10/11 | `Trophyline.exe`: double-click it |
| macOS, Apple Silicon (M1 and newer) | `Trophyline-macos-apple-silicon.zip`: unzip, move `Trophyline.app` to Applications |
| macOS, Intel | `Trophyline-macos-intel.zip` |
| Linux (x64) | `Trophyline-linux-x64.tar.gz`: unpack, run `./Trophyline` |

No install and no .NET needed. Steam must be installed and signed in on the same computer.

To start the tracker with your computer and add Trophyline to the Start menu / app menu, run it once with `--install`:

```
Trophyline.exe --install                               (Windows)
./Trophyline --install                                 (Linux)
Trophyline.app/Contents/MacOS/Trophyline --install     (macOS)
```

The files aren't signed by Microsoft or Apple, so the first launch shows a warning.
**Windows:** click *More info → Run anyway*. **macOS:** right-click `Trophyline.app` → *Open* → *Open*
(or run `xattr -dr com.apple.quarantine Trophyline.app`).

## What you get

- **Overview**: your profile over the game you played last, stat tiles, a
  12-month activity heatmap, recent and rarest unlocks, most played games
- **Achievements**: every unlock by month, with search (Ctrl+F / Cmd+F), game and rarity filters
- **Games**: cover-art grid with progress bars, sortable; click a game for its page:
  hero art, playtime, completion, and every achievement, including the ones still
  to get (easiest first, by global rarity). A **Play** button starts the game in Steam
  (or **Install** if it isn't installed)
- **Playtime**: every play session, recorded by the tracker: today, this week, last
  30 days, longest session, a 14-day chart, recent sessions, and what you're playing
  right now. Game pages show your session count and longest session too
- **Achievement pop-ups**: a card slides in at the bottom-right corner the moment you
  unlock something, with the icon and how rare it is (gold for under 10%). It never
  takes focus from your game; click it to open Trophyline. Turn it off in Settings
- **Storage**: installed games by size, flags games not played in 6+ months
- **Music**: save your Spotify playlists, albums or artists (paste a share link) and play
  them with one click; a mini-player in the sidebar shows the current song with
  previous / play-pause / next. It controls the Spotify app on your computer, so the
  music keeps playing when Trophyline is closed. No Spotify login needed.
- **Game soundtracks**: give a game one of your playlists on its page. When you launch
  that game, the background tracker starts the playlist (and pauses it when you quit,
  if you like), even with the window closed
- **Settings**: pick an accent colour for the whole window

## How it works

- Reads the files Steam already keeps on your computer: achievements
  (`appcache/stats`), playtime (`userdata/…/localconfig.vdf`), installed games and the
  artwork Steam downloaded. No Steam API key or public profile needed.
  It only reads those files and never changes them.
- Two things are fetched online and cached: global achievement rarity (Steam's public
  stats, refreshed weekly) and names of games Steam has no local name for.
- The **tracker** (`--background`) is event-driven: it sleeps until Steam updates a
  game's stats file, then records the unlock with its date. It also notices when a
  Steam game starts (Windows: waits on Steam's registry key; Linux/macOS: watches
  `registry.vdf`) to start that game's soundtrack. About 7 MB of memory,
  lowest CPU priority. The **window** only uses memory while it's open.

Steam is found automatically (Windows registry; `~/Library/Application Support/Steam`
on macOS; `~/.steam/steam`, `~/.local/share/Steam`, Flatpak and Snap on Linux).

```
Trophyline               open the window
Trophyline --install     start the tracker now + at every login, add to Start menu / app menu
Trophyline --uninstall   undo --install and stop the tracker (journal is kept)
Trophyline --stop        stop the tracker
Trophyline --update      scan once and update the journal
```

Data folder: `%LOCALAPPDATA%\Trophyline` (Windows), `~/Library/Application Support/Trophyline`
(macOS), `~/.local/share/Trophyline` (Linux):
- `journal.tsv`: every recorded unlock (tab-separated, easy to import elsewhere)
- `journal.html`: the same journal as a web page
- `icons/`, `rarity/`: cached pictures and rarity stats
- `sessions.tsv`: every play session (app id, start, end as unix time);
  `open-session.txt` is the game running right now
- `music/`: saved playlists and `soundtracks.json` (which game plays which playlist)
- `config.json`: `language` (e.g. `"german"`), `accent`, `hideSpotify`,
  `pauseSoundtrackOnQuit`, `unlockPopups`, and optional `steamFolder` / `statsFolder`
  if Steam is somewhere unusual

## Build

Needs the .NET 10 SDK. The window uses [Avalonia](https://avaloniaui.net/).

```
dotnet publish -c Release -r win-x64 -o publish        # or linux-x64, osx-arm64, osx-x64
```

Standalone build like the releases (no .NET needed to run it):

```
dotnet publish -c Release -r win-x64 -o dist -p:SelfContained=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

Releases for all systems are built by GitHub Actions when a `v*` tag is pushed.

## License

GPL-3.0. Not affiliated with Valve or Steam.
