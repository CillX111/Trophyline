-- Trophyline online leaderboards (Cloudflare D1 / SQLite)

CREATE TABLE IF NOT EXISTS players (
  id          TEXT PRIMARY KEY,          -- random id made by the app
  secret_hash TEXT NOT NULL,             -- sha-256 of the app's secret; only that app may update the row
  name        TEXT NOT NULL,
  level       INTEGER NOT NULL DEFAULT 1,
  rank        TEXT NOT NULL DEFAULT 'Rookie',
  xp          INTEGER NOT NULL DEFAULT 0,
  perfect     INTEGER NOT NULL DEFAULT 0, -- games at 100%
  over75      INTEGER NOT NULL DEFAULT 0, -- games at 75% or more
  unlocked    INTEGER NOT NULL DEFAULT 0, -- achievements unlocked
  games       INTEGER NOT NULL DEFAULT 0, -- games with achievements started
  hours       INTEGER NOT NULL DEFAULT 0,
  profile     TEXT NOT NULL DEFAULT '{}', -- showcase: top games, recent unlocks (JSON)
  created     INTEGER NOT NULL,
  updated     INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS players_xp ON players (xp DESC);
CREATE INDEX IF NOT EXISTS players_perfect ON players (perfect DESC, xp DESC);
CREATE INDEX IF NOT EXISTS players_over75 ON players (over75 DESC, xp DESC);

-- XP earned inside each 3-month season ("2026-4" = October to December 2026)
CREATE TABLE IF NOT EXISTS season_scores (
  season  TEXT NOT NULL,
  player  TEXT NOT NULL,
  xp      INTEGER NOT NULL,
  updated INTEGER NOT NULL,
  PRIMARY KEY (season, player)
);
CREATE INDEX IF NOT EXISTS season_xp ON season_scores (season, xp DESC);

-- Chat: the public lobby and private messages between two players
CREATE TABLE IF NOT EXISTS messages (
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  room      TEXT NOT NULL,              -- 'lobby' or 'dm:<id>:<id>' (ids sorted)
  sender    TEXT NOT NULL,
  text      TEXT NOT NULL,
  time      INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS messages_room ON messages (room, id);

CREATE TABLE IF NOT EXISTS blocks (
  player  TEXT NOT NULL,                -- who blocked
  blocked TEXT NOT NULL,                -- whom
  PRIMARY KEY (player, blocked)
);
