// End-to-end test of Steam verification without the real Steam or Supabase:
//   - shim.mjs (PGlite) plays the database, with admin.sql so the test can act as the server owner
//   - a fake Steam answers the Web API and the OpenID check
//   - the real server function (../functions/trophyline/index.ts) runs in this process
// Run:  node verify-test.mjs        (KEEP=1 keeps everything running for testing the app:
//       database on :8790, server function on :8792/functions/v1/trophyline, fake Steam on :8791)
import { spawn } from "node:child_process";
import { createServer } from "node:http";

const DB = "http://127.0.0.1:8790", STEAM = "http://127.0.0.1:8791", FN_PORT = 8792;
process.env.SUPABASE_URL = DB;
process.env.SUPABASE_SERVICE_ROLE_KEY = "test-service-key";
process.env.STEAM_API_KEY = "fake-key";
process.env.STEAM_API_BASE = STEAM;
process.env.STEAM_OPENID_URL = STEAM + "/openid/login";

// ---------- fake Steam ----------
const now = Math.floor(Date.now() / 1000);
const accounts = {};
function account(sid) { return (accounts[sid] ??= { private: false, games: {} }); }
// game: { minutes, stats: bool, ach: [{ name, got, time, pct }] }

const steam = createServer(async (req, res) => {
  const url = new URL(req.url, STEAM);
  const send = (status, body) => { res.writeHead(status, { "content-type": "application/json" }); res.end(typeof body === "string" ? body : JSON.stringify(body)); };
  if (url.pathname === "/openid/login" && req.method === "POST") {
    let body = ""; for await (const c of req) body += c;
    const f = new URLSearchParams(body);
    const ok = f.get("openid.mode") === "check_authentication" && f.get("openid.sig") === "good";
    return send(200, `ns:http://specs.openid.net/auth/2.0\nis_valid:${ok}\n`);
  }
  const p = url.searchParams;
  if (url.pathname.includes("GetGlobalAchievementPercentagesForApp")) {
    const appid = p.get("gameid");
    const g = Object.values(accounts).map((a) => a.games[appid]).find(Boolean);
    return send(200, { achievementpercentages: { achievements: (g?.ach ?? []).filter((a) => a.pct != null).map((a) => ({ name: a.name, percent: String(a.pct) })) } });
  }
  if (p.get("key") !== "fake-key") return send(403, "<html>Forbidden</html>");
  const acc = accounts[p.get("steamid")];
  if (url.pathname.includes("GetOwnedGames")) {
    if (!acc || acc.private) return send(200, { response: {} });
    const games = Object.entries(acc.games).map(([appid, g]) => ({ appid: Number(appid), playtime_forever: g.minutes, has_community_visible_stats: g.stats }));
    return send(200, { response: { game_count: games.length, games } });
  }
  if (url.pathname.includes("GetPlayerAchievements")) {
    if (!acc || acc.private) return send(403, { playerstats: { error: "Profile is not public", success: false } });
    const g = acc.games[p.get("appid")];
    if (!g || !g.stats) return send(400, { playerstats: { error: "Requested app has no stats", success: false } });
    return send(200, { playerstats: { steamID: p.get("steamid"), gameName: "Game", success: true,
      achievements: g.ach.map((a) => ({ apiname: a.name, achieved: a.got ? 1 : 0, unlocktime: a.got ? a.time : 0 })) } });
  }
  send(404, { error: "not found" });
});
await new Promise((r) => steam.listen(8791, "127.0.0.1", r));

// ---------- database ----------
const shim = spawn(process.execPath, ["shim.mjs"], {
  cwd: new URL(".", import.meta.url), env: { ...process.env, PORT: "8790", EXTRA_SQL: "admin.sql" }, stdio: ["ignore", "pipe", "inherit"],
});
await new Promise((resolve, reject) => {
  shim.stdout.on("data", (d) => { if (String(d).includes("ready on")) resolve(); });
  shim.on("exit", (c) => reject(new Error("shim exited " + c)));
});
const cleanup = () => { try { shim.kill(); } catch {} };
process.on("exit", cleanup);

async function rpc(fn, args, key = "anon-key") {
  const res = await fetch(`${DB}/rest/v1/rpc/${fn}`, { method: "POST", headers: { apikey: key, "content-type": "application/json" }, body: JSON.stringify(args) });
  const body = await res.json();
  return res.ok ? body : { httpError: body.message };
}
const sql = (s) => rpc("zz_exec", { p_sql: s });
const query = (s) => rpc("zz_query", { p_sql: s });

// ---------- the server function ----------
const { handle, xpFor } = await import("../functions/trophyline/index.ts");
async function fn(body) {
  const res = await handle(new Request("http://x/", { method: "POST", body: JSON.stringify(body) }));
  return res.json();
}

let failed = 0;
function check(name, ok, detail) {
  console.log(`${ok ? "PASS" : "FAIL"}  ${name}${ok ? "" : "  ->  " + JSON.stringify(detail)}`);
  if (!ok) failed++;
}

let nonceN = 0;
function openid(sid, sig = "good", nonce) {
  const claimed = "https://steamcommunity.com/openid/id/" + sid;
  return {
    "openid.ns": "http://specs.openid.net/auth/2.0", "openid.mode": "id_res",
    "openid.op_endpoint": "https://steamcommunity.com/openid/login",
    "openid.claimed_id": claimed, "openid.identity": claimed,
    "openid.return_to": "http://127.0.0.1:51234/steam",
    "openid.response_nonce": nonce ?? new Date().toISOString().slice(0, 19) + "Z" + (++nonceN) + "abc",
    "openid.assoc_handle": "1234567890",
    "openid.signed": "signed,op_endpoint,claimed_id,identity,return_to,response_nonce,assoc_handle",
    "openid.sig": sig,
  };
}

if (process.env.KEEP !== "1") {
  // ----- data for player A -----
  const A = "76561198000000001";
  const old = 1735689600; // 2025-01-01
  account(A).games = {
    100: { minutes: 600, stats: true, ach: [
      { name: "A0", got: true, time: old, pct: 0.5 }, { name: "A1", got: true, time: old + 100, pct: 3 },
      { name: "A2", got: true, time: old + 200, pct: 8 }, { name: "A3", got: true, time: now - 2 * 86400, pct: 15 },
      { name: "A4", got: true, time: now - 2 * 86400 + 3600, pct: 30 }, { name: "A5", got: true, time: now - 2 * 86400 + 7200, pct: 70 },
      { name: "A6", got: false, pct: 1 }, { name: "A7", got: false, pct: 1 }, { name: "A8", got: false, pct: 1 }, { name: "A9", got: false, pct: 1 },
    ] },
    200: { minutes: 300, stats: true, ach: [0, 1, 2, 3].map((i) => ({ name: "B" + i, got: true, time: now - 86400 + i * 600 })) },
    300: { minutes: 50, stats: false, ach: [] },
    400: { minutes: 0, stats: true, ach: Array.from({ length: 20 }, (_, i) => ({ name: "C" + i, got: true, time: now - 3 * 3600 })) },
  };

  check("xp table matches the app", [xpFor(0.5), xpFor(3), xpFor(8), xpFor(15), xpFor(30), xpFor(70), xpFor(null)].join() === "150,100,60,40,25,15,25");

  const reg = await rpc("tl_register", { p_username: "alice_test", p_password: "alice-password-1" });
  check("register", reg.ok, reg);
  const me = { id: reg.id, secret: reg.secret };

  const direct = await rpc("tl_srv_apply", { p_id: me.id, p: { owned: [], games: [], partial: false } });
  check("app key can't store Steam results itself", !!direct.httpError && /permission denied/.test(direct.httpError), direct);
  const direct2 = await rpc("tl_srv_link", { p_id: me.id, p_secret: me.secret, p_steam_id: A, p_nonce: "x" });
  check("app key can't link without Steam's check", !!direct2.httpError, direct2);

  let r = await fn({ action: "verify", ...me });
  check("verify before linking asks to link", /Link your Steam/.test(r.error ?? ""), r);

  r = await fn({ action: "link", ...me, openid: openid(A, "forged") });
  check("forged sign-in is refused", /couldn't confirm/.test(r.error ?? ""), r);
  r = await fn({ action: "link", ...me, openid: { ...openid(A), "openid.return_to": "https://evil.example/steam" } });
  check("sign-in made for another site is refused", /wasn't made for Trophyline/.test(r.error ?? ""), r);
  r = await fn({ action: "link", ...me, openid: openid(A, "good", "2020-01-01T00:00:00Zold") });
  check("old sign-in is refused", /too old/.test(r.error ?? ""), r);
  r = await fn({ action: "link", id: me.id, secret: "wrong-secret-wrong-secret-wrong", openid: openid(A) });
  check("someone else's Trophyline login can't link", /Log in/.test(r.error ?? ""), r);
  const good = openid(A);
  r = await fn({ action: "link", ...me, openid: good });
  check("genuine sign-in links the account", r.ok && r.steam_id === A, r);
  r = await fn({ action: "link", ...me, openid: good });
  check("the same sign-in can't be used twice", /already used/.test(r.error ?? ""), r);

  // a second Trophyline account can't take the same Steam account
  const reg2 = await rpc("tl_register", { p_username: "mallory_test", p_password: "mallory-password-1" });
  r = await fn({ action: "link", id: reg2.id, secret: reg2.secret, openid: openid(A) });
  check("a Steam account can only be linked once", /already linked/.test(r.error ?? ""), r);

  // client claims huge numbers before verification: shown on profile, never ranked
  await rpc("tl_upsert", { p: { id: me.id, secret: me.secret, name: "Alice", xp: 9999999, level: 300, perfect: 999, over75: 999, seasons: [{ id: "2026-4", xp: 999999 }] } });
  let lb = await rpc("tl_leaderboard", { p_board: "xp", p_me: me.id });
  check("unverified player is not on the leaderboard", lb.rows.length === 0 && lb.me === null, lb);

  r = await fn({ action: "verify", ...me });
  check("verify reads Steam", r.ok && r.state === "ok" && !r.partial, r);
  check("XP worked out from Steam", r.xp === 2290 && r.level === tl(2290), { xp: r.xp, level: r.level });
  check("counts from Steam", r.perfect === 2 && r.over75 === 2 && r.unlocked === 30 && r.games === 3 && r.hours === 15, r);
  check("season XP from unlock dates", r.season_xp === 1780, r.season_xp);
  check("cheat check flags the 0-playtime instant 100%", r.flagged === true, r);
  let pl = (await query(`select flags, ranked from players where id = '${me.id}'`))[0];
  check("flags say why", /app 400/.test(pl.flags) && /no playtime/.test(pl.flags) && pl.ranked === false, pl);
  lb = await rpc("tl_leaderboard", { p_board: "xp", p_me: me.id });
  check("flagged player is not ranked", lb.rows.length === 0, lb);
  let st = await rpc("tl_verify_status", { p_id: me.id, p_secret: me.secret });
  check("status tells the player they're paused", st.linked && st.flagged && !st.ranked, st);

  r = await fn({ action: "verify", ...me });
  check("checks can't be spammed", /Try again in/.test(r.error ?? ""), r);

  // the owner approves
  await sql(`update players set approve = true where id = '${me.id}'`);
  lb = await rpc("tl_leaderboard", { p_board: "xp", p_me: me.id });
  check("approved player is ranked right away", lb.rows.length === 1 && lb.rows[0].value === 2290, lb.rows);
  await sql(`update players set v_next = 0 where id = '${me.id}'`);
  r = await fn({ action: "verify", ...me });
  check("after approval the old unlocks don't flag again", r.ok && r.flagged === false, r);
  pl = (await query(`select ranked, approve, xp, level from players where id = '${me.id}'`))[0];
  check("still ranked with Steam's numbers", pl.ranked && !pl.approve && pl.xp === 2290, pl);

  // client upload after verification can't change the numbers
  await rpc("tl_upsert", { p: { id: me.id, secret: me.secret, name: "Alice", xp: 9999999, level: 300, perfect: 999, over75: 999 } }).catch(() => {});
  await new Promise((res) => setTimeout(res, 11000)); // tl_upsert allows one upload per 10 s
  const up = await rpc("tl_upsert", { p: { id: me.id, secret: me.secret, name: "Alice", xp: 9999999, level: 300, perfect: 999, over75: 999, seasons: [{ id: "2026-4", xp: 999999 }] } });
  pl = (await query(`select name, xp, perfect from players where id = '${me.id}'`))[0];
  check("uploads from the app can't change verified numbers", up.ok && pl.xp === 2290 && pl.perfect === 2 && pl.name === "Alice", { up, pl });
  lb = await rpc("tl_leaderboard", { p_board: "season", p_me: me.id });
  check("season board uses Steam's season XP", lb.rows.length === 1 && lb.rows[0].value === 1780, lb.rows);
  const prof = await rpc("tl_player", { p_id: me.id });
  check("profile shows verified", prof.verified === true && prof.places.xp === 1, prof);

  // later: Steam Achievement Manager style cheating in three games within one minute
  const t0 = Math.floor(now / 60) * 60 - 600;
  for (const [i, appid] of [500, 600, 700].entries())
    account(A).games[appid] = { minutes: 10, stats: true, ach: [{ name: "S", got: true, time: t0 + i, pct: 50 }, { name: "T", got: false }] };
  await sql(`update players set v_next = 0 where id = '${me.id}'`);
  r = await fn({ action: "verify", ...me });
  pl = (await query(`select flags, ranked from players where id = '${me.id}'`))[0];
  check("unlocks in 3 games within a minute get flagged", r.flagged && /3 different games/.test(pl.flags) && !pl.ranked, pl);

  // the profile goes private
  account(A).private = true;
  await sql(`update players set v_next = 0 where id = '${me.id}'`);
  r = await fn({ action: "verify", ...me });
  check("private game details are reported", r.ok && r.state === "private", r);
  account(A).private = false;

  // big library: checked in parts
  const B = "76561198000000002";
  account(B).games = Object.fromEntries(Array.from({ length: 130 }, (_, i) => [1000 + i,
    { minutes: 60, stats: true, ach: [{ name: "X", got: true, time: old + i * 86400 * 2, pct: 70 }, { name: "Y", got: false }] }]));
  r = await fn({ action: "link", id: reg2.id, secret: reg2.secret, openid: openid(B) });
  check("second player links another account", r.ok, r);
  r = await fn({ action: "verify", id: reg2.id, secret: reg2.secret });
  check("big library: first part", r.ok && r.partial === true && r.left === 10 && r.state === "checking", r);
  pl = (await query(`select ranked from players where id = '${reg2.id}'`))[0];
  check("not ranked until the whole library is checked", pl.ranked === false, pl);
  r = await fn({ action: "verify", id: reg2.id, secret: reg2.secret });
  check("big library: rest right away", r.ok && r.partial === false && r.unlocked === 130 && r.games === 130, r);
  check("big library XP", r.xp === 130 * 15 + 130 * 50 + 10 * 130, r.xp);

  // reports
  r = await rpc("tl_report", { p_id: reg2.id, p_secret: reg2.secret, p_target: me.id, p_reason: "100% in one second" });
  check("report someone", r.ok, r);
  r = await rpc("tl_report", { p_id: me.id, p_secret: me.secret, p_target: me.id, p_reason: "me" });
  check("can't report yourself", !!r.error, r);
  const reps = await query(`select reason from reports`);
  check("owner sees the report", reps.length === 1 && reps[0].reason === "100% in one second", reps);

  // a different Steam account replaces the old verified data
  const C = "76561198000000003";
  account(C).games = { 100: { minutes: 5, stats: true, ach: [{ name: "A0", got: true, time: old, pct: 0.5 }] } };
  r = await fn({ action: "link", ...me, openid: openid(C) });
  pl = (await query(`select steam_id, verified_at, ranked, (select count(*) from steam_unlocks where player = '${me.id}') as n from players where id = '${me.id}'`))[0];
  check("linking another Steam account starts over", r.ok && pl.steam_id === C && pl.verified_at === 0 && !pl.ranked && pl.n === 0, pl);

  // banned players can't check
  await sql(`update players set banned = true where id = '${reg2.id}'`);
  await sql(`update players set v_next = 0 where id = '${reg2.id}'`);
  r = await fn({ action: "verify", id: reg2.id, secret: reg2.secret });
  check("banned player can't verify", !!r.error, r);

  // delete removes the Steam data too
  await sql(`update players set banned = false where id = '${reg2.id}'`);
  await rpc("tl_delete", { p_id: reg2.id, p_secret: reg2.secret });
  const left = await query(`select (select count(*) from steam_games where player = '${reg2.id}') as g, (select count(*) from reports where reporter = '${reg2.id}') as r`);
  check("deleting an account removes its Steam data and reports", left[0].g === 0 && left[0].r === 0, left);

  console.log(failed === 0 ? "\nALL PASSED" : `\n${failed} FAILED`);
  cleanup();
  process.exit(failed === 0 ? 0 : 1);
}

function tl(xp) { let l = 1; while (xp >= 200 + 75 * (l - 1)) { xp -= 200 + 75 * (l - 1); l++; } return l; }

// ---------- KEEP=1: serve the function for the app ----------
// Fake Steam account 76561198000000009 has three games; the app's sign-in is simulated by
// TROPHYLINE_FAKE_OPENID (see SteamAccounts.cs), which hands the function a "good" answer.
const D = "76561198000000009";
account(D).games = {
  730: { minutes: 1200, stats: true, ach: Array.from({ length: 10 }, (_, i) => ({ name: "D" + i, got: i < 8, time: now - (i + 1) * 3 * 86400, pct: 5 + i * 9 })) },
  570: { minutes: 90, stats: true, ach: [{ name: "E0", got: true, time: now - 5 * 86400, pct: 0.7 }, { name: "E1", got: true, time: now - 4 * 86400, pct: 40 }] },
};
createServer(async (req, res) => {
  if (!req.url.startsWith("/functions/v1/trophyline")) { res.writeHead(404); return res.end(); }
  let body = ""; for await (const c of req) body += c;
  const parsed = body ? JSON.parse(body) : {};
  // the app's fake sign-in carries sig "app-test": make it look like Steam's real answer for account D
  if (parsed.openid?.["openid.sig"] === "app-test") parsed.openid = { ...openid(D), "openid.return_to": parsed.openid["openid.return_to"] };
  const out = await handle(new Request("http://x/", { method: req.method, body: JSON.stringify(parsed) }));
  res.writeHead(out.status, { "content-type": "application/json" });
  res.end(await out.text());
}).listen(FN_PORT, "127.0.0.1", () => console.log(`function on http://127.0.0.1:${FN_PORT}/functions/v1/trophyline (fake Steam account ${D})`));
