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
- **Levels & XP**: everything earns XP: achievements (15–150 XP, rarer = more), your
  first achievement in a game (50), 100% completion (500), every hour played (10) and
  every tracked session of 15+ minutes (20). Your level and XP bar sit in the sidebar;
  the Level page shows ranks (Rookie → Explorer → Hunter → Veteran → Elite → Master →
  Legend, Mythic, Immortal, Titan, Celestial, Eternal; each rank multiplies the XP you earn while you hold it, from ×1.05 up to ×2), where your XP comes from and recent gains. Pop-ups show the XP you earned
  and a "Level up!" card when you reach a new level
- **Leaderboards** (online, log in with Steam, Steam-verified): Season XP, Highest XP, Most completed games and
  Most games over 75%, plus every season's champions. Click anyone to see their
  profile: level, rank, places, best games and latest unlocks
- **Seasons**: each lasts 3 months (Season 1 = Autumn 2026, October–December);
  season XP starts at 0 every season
- **Chat** (online): a public lobby and private messages (open a profile → Message),
  with blocking
- **Shop**: every level you reach gives coins (50 + 10 × level). Spend them on 22 profile
  icons in four tiers (Common, Rare, Epic, Legendary); the equipped icon shows in the
  sidebar, on your profile and on the leaderboards
- **Secret achievements stay secret**: no name, description or picture until you unlock them
- **Choose your Steam account** (Settings → Steam account): automatic (whoever is signed in
  to Steam right now), any account that has been used on this computer, or **Sign in with
  Steam** (Steam's own login page in your browser; Trophyline never sees a password). The
  numbers always come from that account's Steam files on this computer
- **Storage**: installed games by size, flags games not played in 6+ months
- **Music**: save your Spotify playlists, albums or artists (paste a share link) and play
  them with one click; a mini-player in the sidebar shows the current song with
  previous / play-pause / next. It controls the Spotify app on your computer, so the
  music keeps playing when Trophyline is closed. No Spotify login needed.
- **Lyrics**: the Music page shows the lyrics of the song that's playing (from
  [LRCLIB](https://lrclib.net)), lighting up and scrolling along with the song. Out of
  sync? Click the line that's being sung, or nudge them with Earlier / Later; the fix is
  remembered per song.
- **Game soundtracks**: give a game one of your playlists on its page. When you launch
  that game, the background tracker starts the playlist (and pauses it when you quit,
  if you like), even with the window closed
- **Settings**: pick an accent colour for the whole window

## How it works

- **You log in with your Steam account** (Settings → Steam account → Log
  in with Steam). No extra password: Steam's own login page opens in your browser and
  Trophyline never sees your Steam password. On networks that block that page (some school
  and work networks), use a **name code**: put the short code Trophyline shows (like
  `TL4F9A2`) into your Steam profile name for a moment, the server sees it through Steam's
  Web API, and you're in (then change your name back). Log in the same way on any
  computer; Log out keeps your account and places. Profiles from before keep everything
  when that computer logs in with Steam once.
  Online, the app only sends your chosen name, equipped icon, profile links and the chat
  messages you write. Never your Steam login.
  Delete account removes all of it from the server. The server is a Supabase project set
  up by [`supabase/trophyline.sql`](supabase/trophyline.sql): the tables can't be read
  directly, only through its `tl_*` functions, and every change needs your login.
- **Only numbers from Steam are shown online.** After you log in, the Trophyline server
  reads your games and achievements from Steam's Web API itself and re-checks every 6 hours.
  Levels, XP, season XP, game counts, best games and latest unlocks online all come only
  from there, so editing files on your computer changes nothing. Your Steam profile's
  **Game details** must be public. Online XP doesn't include tracked sessions (Steam doesn't
  know about them), and a shop icon only shows if your Steam-checked level could have bought it.
- **Cheat checks**: the server pauses a player (not ranked, until the owner looks) when
  almost a whole game was unlocked in one second, when achievements appear in 3+ games
  within one minute, or when a game with no playtime has achievements. These are typical of
  achievement-unlocker tools, which Steam itself can't tell apart from real unlocks.
- **Moderation**: players can **Report** a profile. In Supabase → Table Editor:
  `reports` lists the reports; in `players`, tick `approve` to clear a paused player, or
  `banned` to remove someone from leaderboards, profiles and chat.
- Profiles that aren't Steam-verified (or are paused) show no numbers at all. Numbers
  uploaded before 1.8.0 were cleared, because they were never checked.
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
Trophyline --self-update install the newest version now (Windows, Linux)
```

Data folder: `%LOCALAPPDATA%\Trophyline` (Windows), `~/Library/Application Support/Trophyline`
(macOS), `~/.local/share/Trophyline` (Linux):
- `journal.tsv`: every recorded unlock (tab-separated, easy to import elsewhere)
- `journal.html`: the same journal as a web page
- `icons/`, `rarity/`: cached pictures and rarity stats
- `sessions.tsv`: every play session (app id, start, end as unix time);
  `open-session.txt` is the game running right now
- `music/`: saved playlists, `soundtracks.json` (which game plays which playlist) and
  `lyrics/` (lyrics already looked up, plus your timing fixes in `offsets.json`)
- `config.json`: `language` (e.g. `"german"`), `accent`, `hideSpotify`,
  `pauseSoundtrackOnQuit`, `unlockPopups`, and optional `steamFolder` / `statsFolder`
  if Steam is somewhere unusual

## Linux with Hyprland (Omarchy) and other tiling desktops

Trophyline runs through XWayland. Two optional tweaks:

- **Pop-ups float in the corner instead of being tiled:** the pop-up window is titled
  `Trophyline pop-up`. Add a rule to `~/.config/hypr/hyprland.conf` (syntax for
  Hyprland 0.48+; older versions use `windowrulev2 = float, title:...`):
  ```
  windowrule = float, pin, noinitialfocus, move 100%-420 100%-160, title:^(Trophyline pop-up)$
  ```
  On Sway: `for_window [title="^Trophyline pop-up$"] floating enable, sticky enable, move position 100 ppt -420 px 100 ppt -160 px`
- **Tracker at login:** `Trophyline --install` adds an XDG autostart entry. If your session
  doesn't run those, add `exec-once = /path/to/Trophyline --background` to
  `~/.config/hypr/autostart.conf` (Omarchy) or `hyprland.conf`.

Session tracking and game soundtracks work with normal, Flatpak and Snap Steam.

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

## Server setup (Supabase)

1. **SQL Editor** → New query → paste [`supabase/trophyline.sql`](supabase/trophyline.sql) → Run
   (safe to run again after updates; it keeps all data).
2. **Edge Functions** → Deploy a new function → Via Editor → name it `trophyline` → paste
   [`supabase/functions/trophyline/index.ts`](supabase/functions/trophyline/index.ts) → Deploy.
   In the function's settings turn **Verify JWT** off (the app sends the publishable key,
   which isn't a JWT; the function checks the player's login itself).
3. **Edge Functions → Secrets** → add `STEAM_API_KEY` with a key from
   <https://steamcommunity.com/dev/apikey>. It stays on the server; the app never sees it.

Tests without Supabase or Steam: `cd supabase/test && npm install && node verify-test.mjs`.

## License

GPL-3.0. Not affiliated with Valve or Steam.
