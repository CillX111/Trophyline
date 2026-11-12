// Trophyline online leaderboards: a tiny Cloudflare Worker with a D1 (SQLite) database.
//
//   GET    /v1/status                         current season, number of players
//   POST   /v1/players                        create / update your entry (needs your app's secret)
//   DELETE /v1/players/:id                    remove your entry (needs your app's secret)
//   GET    /v1/players/:id                    someone's public profile
//   GET    /v1/leaderboard?board=&season=&me= top 100 for season | xp | perfect | over75
//   GET    /v1/seasons                        every season so far with its top 3

const BOARDS = {
  xp: "xp",
  perfect: "perfect",
  over75: "over75",
};
const SEASON_NAMES = ["Winter", "Spring", "Summer", "Autumn"];
const FIRST_SEASON = { year: 2026, quarter: 4 }; // Season 1 = October to December 2026

// ---------- seasons: every 3 months (calendar quarters, UTC) ----------

export function seasonOf(date = new Date()) {
  const year = date.getUTCFullYear();
  const quarter = Math.floor(date.getUTCMonth() / 3) + 1;
  return seasonInfo(year, quarter);
}

function seasonInfo(year, quarter) {
  const start = Date.UTC(year, (quarter - 1) * 3, 1);
  const end = Date.UTC(quarter === 4 ? year + 1 : year, quarter === 4 ? 0 : quarter * 3, 1);
  const number = (year - FIRST_SEASON.year) * 4 + (quarter - FIRST_SEASON.quarter) + 1;
  return {
    id: `${year}-${quarter}`,
    number,
    name: `Season ${number}`,
    label: `${SEASON_NAMES[quarter - 1]} ${year}`,
    start: Math.floor(start / 1000),
    end: Math.floor(end / 1000),
  };
}

function parseSeason(id) {
  const m = /^(\d{4})-([1-4])$/.exec(id || "");
  return m ? seasonInfo(Number(m[1]), Number(m[2])) : null;
}

function previousSeason(s) {
  const [y, q] = s.id.split("-").map(Number);
  return q === 1 ? seasonInfo(y - 1, 4) : seasonInfo(y, q - 1);
}

// ---------- helpers ----------

const json = (data, status = 200) =>
  new Response(JSON.stringify(data), {
    status,
    headers: {
      "content-type": "application/json; charset=utf-8",
      "access-control-allow-origin": "*",
      "cache-control": "no-store",
    },
  });

const fail = (status, error) => json({ error }, status);

async function sha256(text) {
  const buf = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return [...new Uint8Array(buf)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

function int(v, max) {
  const n = Math.floor(Number(v));
  return Number.isFinite(n) && n >= 0 ? Math.min(n, max) : 0;
}

function cleanName(v) {
  return String(v ?? "")
    .replace(/[\u0000-\u001f\u007f-\u009f​-‏‪-‮]/g, "")
    .trim()
    .slice(0, 24);
}

const ID = /^[a-z0-9]{16,40}$/;

// ---------- handlers ----------

async function upsert(req, env) {
  let b;
  try {
    b = await req.json();
  } catch {
    return fail(400, "bad json");
  }
  if (!ID.test(b.id || "")) return fail(400, "bad id");
  if (typeof b.secret !== "string" || b.secret.length < 24 || b.secret.length > 128) return fail(400, "bad secret");
  const name = cleanName(b.name);
  if (!name) return fail(400, "name required");

  const now = Math.floor(Date.now() / 1000);
  const hash = await sha256(b.secret);
  const existing = await env.DB.prepare("SELECT secret_hash, updated FROM players WHERE id = ?").bind(b.id).first();
  if (existing && existing.secret_hash !== hash) return fail(403, "not your profile");
  if (existing && now - existing.updated < 10) return fail(429, "too often");

  const profile = JSON.stringify(b.profile ?? {});
  if (profile.length > 24000) return fail(413, "profile too big");

  const row = {
    level: int(b.level, 10000),
    rank: cleanName(b.rank) || "Rookie",
    xp: int(b.xp, 50_000_000),
    perfect: int(b.perfect, 100_000),
    over75: int(b.over75, 100_000),
    unlocked: int(b.unlocked, 10_000_000),
    games: int(b.games, 100_000),
    hours: int(b.hours, 10_000_000),
  };

  await env.DB.prepare(
    `INSERT INTO players (id, secret_hash, name, level, rank, xp, perfect, over75, unlocked, games, hours, profile, created, updated)
     VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?13)
     ON CONFLICT(id) DO UPDATE SET name=?3, level=?4, rank=?5, xp=?6, perfect=?7, over75=?8, unlocked=?9, games=?10,
       hours=?11, profile=?12, updated=?13`
  )
    .bind(b.id, hash, name, row.level, row.rank, row.xp, row.perfect, row.over75, row.unlocked, row.games, row.hours, profile, now)
    .run();

  // Season XP: the current season, and the previous one during the first week (late uploads).
  const current = seasonOf();
  const seasons = Array.isArray(b.seasons) ? b.seasons : [];
  for (const s of seasons.slice(0, 2)) {
    const info = parseSeason(s?.id);
    if (!info) continue;
    const allowed = info.id === current.id || (info.id === previousSeason(current).id && now - current.start < 7 * 86400);
    if (!allowed) continue;
    await env.DB.prepare(
      `INSERT INTO season_scores (season, player, xp, updated) VALUES (?1, ?2, ?3, ?4)
       ON CONFLICT(season, player) DO UPDATE SET xp = ?3, updated = ?4`
    )
      .bind(info.id, b.id, int(s.xp, 10_000_000), now)
      .run();
  }
  return json({ ok: true, season: current });
}

async function remove(req, env, id) {
  const secret = req.headers.get("x-trophyline-secret") || "";
  const row = await env.DB.prepare("SELECT secret_hash FROM players WHERE id = ?").bind(id).first();
  if (!row) return json({ ok: true });
  if (row.secret_hash !== (await sha256(secret))) return fail(403, "not your profile");
  await env.DB.batch([
    env.DB.prepare("DELETE FROM players WHERE id = ?").bind(id),
    env.DB.prepare("DELETE FROM season_scores WHERE player = ?").bind(id),
    env.DB.prepare("DELETE FROM messages WHERE sender = ?").bind(id),
    env.DB.prepare("DELETE FROM blocks WHERE player = ? OR blocked = ?").bind(id, id),
  ]);
  return json({ ok: true });
}

async function profile(env, id) {
  const p = await env.DB.prepare(
    "SELECT id, name, level, rank, xp, perfect, over75, unlocked, games, hours, profile, created, updated FROM players WHERE id = ?"
  )
    .bind(id)
    .first();
  if (!p) return fail(404, "no such player");
  const season = seasonOf();
  const s = await env.DB.prepare("SELECT xp FROM season_scores WHERE season = ? AND player = ?").bind(season.id, id).first();
  const pos = async (col) =>
    (await env.DB.prepare(`SELECT COUNT(*) + 1 AS pos FROM players WHERE ${col} > ?`).bind(p[col]).first()).pos;
  return json({
    ...p,
    profile: JSON.parse(p.profile || "{}"),
    season: { ...season, xp: s?.xp ?? 0 },
    places: { xp: await pos("xp"), perfect: await pos("perfect"), over75: await pos("over75") },
  });
}

async function leaderboard(url, env) {
  const board = url.searchParams.get("board") || "season";
  const limit = Math.min(int(url.searchParams.get("limit") || 100, 200), 200) || 100;
  const me = url.searchParams.get("me") || "";
  let rows, mine = null, season = null;

  if (board === "season") {
    season = parseSeason(url.searchParams.get("season")) || seasonOf();
    rows = (
      await env.DB.prepare(
        `SELECT p.id, p.name, p.level, p.rank, json_extract(p.profile, '$.icon') AS icon, s.xp AS value FROM season_scores s JOIN players p ON p.id = s.player
         WHERE s.season = ? AND s.xp > 0 ORDER BY s.xp DESC, p.xp DESC LIMIT ?`
      )
        .bind(season.id, limit)
        .all()
    ).results;
    if (ID.test(me)) {
      const m = await env.DB.prepare("SELECT xp FROM season_scores WHERE season = ? AND player = ?").bind(season.id, me).first();
      if (m) {
        const pos = await env.DB.prepare("SELECT COUNT(*) + 1 AS pos FROM season_scores WHERE season = ? AND xp > ?")
          .bind(season.id, m.xp)
          .first();
        mine = { pos: pos.pos, value: m.xp };
      }
    }
  } else {
    const col = BOARDS[board];
    if (!col) return fail(400, "unknown board");
    rows = (
      await env.DB.prepare(
        `SELECT id, name, level, rank, json_extract(profile, '$.icon') AS icon, ${col} AS value FROM players WHERE ${col} > 0 ORDER BY ${col} DESC, xp DESC LIMIT ?`
      )
        .bind(limit)
        .all()
    ).results;
    if (ID.test(me)) {
      const m = await env.DB.prepare(`SELECT ${col} AS value FROM players WHERE id = ?`).bind(me).first();
      if (m) {
        const pos = await env.DB.prepare(`SELECT COUNT(*) + 1 AS pos FROM players WHERE ${col} > ?`).bind(m.value).first();
        mine = { pos: pos.pos, value: m.value };
      }
    }
  }
  rows.forEach((r, i) => (r.pos = i + 1));
  const total = (await env.DB.prepare("SELECT COUNT(*) AS n FROM players").first()).n;
  return json({ board, season: season ?? seasonOf(), rows, me: mine, players: total });
}

async function seasons(env) {
  const now = seasonOf();
  const list = [];
  for (let s = now; s.number >= 1 && list.length < 12; s = previousSeason(s)) {
    const top = (
      await env.DB.prepare(
        `SELECT p.id, p.name, p.level, p.rank, json_extract(p.profile, '$.icon') AS icon, s.xp AS value FROM season_scores s JOIN players p ON p.id = s.player
         WHERE s.season = ? AND s.xp > 0 ORDER BY s.xp DESC LIMIT 3`
      )
        .bind(s.id)
        .all()
    ).results;
    list.push({ ...s, current: s.id === now.id, top });
  }
  return json({ current: now, seasons: list });
}

// ---------- chat ----------
// Only players on the leaderboards can write (that's what their secret proves).
// The lobby is public; private messages are only readable by the two people in them.

const dmRoom = (a, b) => (a < b ? `dm:${a}:${b}` : `dm:${b}:${a}`);

async function authed(env, id, secret) {
  if (!ID.test(id || "") || typeof secret !== "string") return null;
  const p = await env.DB.prepare("SELECT id, name, secret_hash FROM players WHERE id = ?").bind(id).first();
  return p && p.secret_hash === (await sha256(secret)) ? p : null;
}

const fromHeaders = (req, env) =>
  authed(env, req.headers.get("x-trophyline-id"), req.headers.get("x-trophyline-secret") || "");

function cleanText(v) {
  return String(v ?? "")
    .replace(/[\u0000-\u0009\u000b-\u001f\u007f-\u009f​-‏‪-‮]/g, "")
    .replace(/\n{3,}/g, "\n\n")
    .trim()
    .slice(0, 500);
}

async function chatSend(req, env) {
  let b;
  try {
    b = await req.json();
  } catch {
    return fail(400, "bad json");
  }
  const me = await authed(env, b.id, b.secret);
  if (!me) return fail(403, "join the leaderboards to chat");
  const text = cleanText(b.text);
  if (!text) return fail(400, "empty message");

  let room = "lobby";
  if (b.to) {
    if (!ID.test(b.to) || b.to === me.id) return fail(400, "bad recipient");
    const other = await env.DB.prepare("SELECT id FROM players WHERE id = ?").bind(b.to).first();
    if (!other) return fail(404, "no such player");
    const blocked = await env.DB.prepare("SELECT 1 FROM blocks WHERE player = ? AND blocked = ?").bind(b.to, me.id).first();
    if (blocked) return fail(403, "this player doesn't accept your messages");
    room = dmRoom(me.id, b.to);
  }

  const now = Math.floor(Date.now() / 1000);
  const last = await env.DB.prepare("SELECT time FROM messages WHERE sender = ? ORDER BY id DESC LIMIT 1").bind(me.id).first();
  if (last && now - last.time < 2) return fail(429, "slow down a little");

  const res = await env.DB.prepare("INSERT INTO messages (room, sender, text, time) VALUES (?, ?, ?, ?)")
    .bind(room, me.id, text, now)
    .run();
  // keep the lobby to the last 30 days
  if (Math.random() < 0.05)
    await env.DB.prepare("DELETE FROM messages WHERE room = 'lobby' AND time < ?").bind(now - 30 * 86400).run();
  return json({ ok: true, id: res.meta.last_row_id });
}

async function chatRead(env, room, url, viewer = null) {
  const after = int(url.searchParams.get("after") || 0, Number.MAX_SAFE_INTEGER);
  const rows = (
    await env.DB.prepare(
      `SELECT m.id, m.sender, m.text, m.time, p.name, p.level, json_extract(p.profile, '$.icon') AS icon
       FROM messages m LEFT JOIN players p ON p.id = m.sender
       WHERE m.room = ? AND m.id > ?
       ${viewer ? "AND m.sender NOT IN (SELECT blocked FROM blocks WHERE player = ?)" : ""}
       ORDER BY m.id DESC LIMIT 100`
    )
      .bind(...(viewer ? [room, after, viewer] : [room, after]))
      .all()
  ).results.reverse();
  return json({ room, messages: rows.map((r) => ({ ...r, name: r.name ?? "(left)" })) });
}

async function chatDm(req, env, url) {
  const me = await fromHeaders(req, env);
  if (!me) return fail(403, "not signed in");
  const other = url.searchParams.get("with") || "";
  if (!ID.test(other)) return fail(400, "bad player");
  return chatRead(env, dmRoom(me.id, other), url, me.id);
}

async function chatInbox(req, env) {
  const me = await fromHeaders(req, env);
  if (!me) return fail(403, "not signed in");
  const rows = (
    await env.DB.prepare(
      `SELECT m.room, MAX(m.id) AS last FROM messages m
       WHERE m.room LIKE ? OR m.room LIKE ? GROUP BY m.room ORDER BY last DESC LIMIT 50`
    )
      .bind(`dm:${me.id}:%`, `dm:%:${me.id}`)
      .all()
  ).results;
  const out = [];
  for (const r of rows) {
    const [, a, b] = r.room.split(":");
    const otherId = a === me.id ? b : a;
    const blocked = await env.DB.prepare("SELECT 1 FROM blocks WHERE player = ? AND blocked = ?").bind(me.id, otherId).first();
    const other = await env.DB.prepare("SELECT name, level, json_extract(profile, '$.icon') AS icon FROM players WHERE id = ?")
      .bind(otherId)
      .first();
    const msg = await env.DB.prepare("SELECT id, sender, text, time FROM messages WHERE id = ?").bind(r.last).first();
    out.push({ id: otherId, name: other?.name ?? "(left)", level: other?.level ?? 1, icon: other?.icon ?? "", blocked: !!blocked, last: msg });
  }
  return json({ conversations: out });
}

async function chatBlock(req, env) {
  let b;
  try {
    b = await req.json();
  } catch {
    return fail(400, "bad json");
  }
  const me = await authed(env, b.id, b.secret);
  if (!me) return fail(403, "not signed in");
  if (!ID.test(b.who || "") || b.who === me.id) return fail(400, "bad player");
  if (b.block) await env.DB.prepare("INSERT OR IGNORE INTO blocks (player, blocked) VALUES (?, ?)").bind(me.id, b.who).run();
  else await env.DB.prepare("DELETE FROM blocks WHERE player = ? AND blocked = ?").bind(me.id, b.who).run();
  return json({ ok: true });
}

export default {
  async fetch(req, env) {
    const url = new URL(req.url);
    const path = url.pathname.replace(/\/+$/, "");
    try {
      if (req.method === "OPTIONS") return json({});
      if (req.method === "GET" && path === "/v1/status") {
        const n = (await env.DB.prepare("SELECT COUNT(*) AS n FROM players").first()).n;
        return json({ ok: true, season: seasonOf(), players: n });
      }
      if (req.method === "POST" && path === "/v1/players") return await upsert(req, env);
      const m = /^\/v1\/players\/([a-z0-9]{16,40})$/.exec(path);
      if (m && req.method === "GET") return await profile(env, m[1]);
      if (m && req.method === "DELETE") return await remove(req, env, m[1]);
      if (req.method === "GET" && path === "/v1/leaderboard") return await leaderboard(url, env);
      if (req.method === "GET" && path === "/v1/seasons") return await seasons(env);
      if (req.method === "POST" && path === "/v1/chat") return await chatSend(req, env);
      if (req.method === "GET" && path === "/v1/chat/lobby") return await chatRead(env, "lobby", url);
      if (req.method === "GET" && path === "/v1/chat/dm") return await chatDm(req, env, url);
      if (req.method === "GET" && path === "/v1/chat/inbox") return await chatInbox(req, env);
      if (req.method === "POST" && path === "/v1/chat/block") return await chatBlock(req, env);
      if (path === "" || path === "/") return json({ name: "Trophyline leaderboards", season: seasonOf() });
      return fail(404, "not found");
    } catch (e) {
      return fail(500, "server error");
    }
  },
};
