// End-to-end test of Steam verification without the real Steam or Supabase:
//   - shim.mjs (PGlite) plays the database, with admin.sql so the test can act as the server owner
//   - a fake Steam answers the Web API and the OpenID check
//   - the real server function (../functions/trophyline/index.ts) runs in this process
// Run:  node verify-test.mjs        (KEEP=1 keeps everything running for testing the app:
//       database on :8790, server function on :8792/functions/v1/trophyline, fake Steam on :8791)
import { spawn } from "node:child_process";
import { createServer } from "node:http";

const BASE = Number(process.env.TEST_PORT_BASE || 8790); // TEST_PORT_BASE moves all three ports (to run next to another test)
const DB = `http://127.0.0.1:${BASE}`, STEAM = `http://127.0.0.1:${BASE + 1}`, FN_PORT = BASE + 2;
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
  // KEEP=1: any Steam account the app asks about exists, with the same games as the test account
  for (const k of ["steamid", "steamids"]) {
    const sid = p.get(k);
    if (process.env.KEEP === "1" && sid && /^7656119\d{10}$/.test(sid) && !accounts[sid])
      accounts[sid] = { private: false, name: "Tester", games: structuredClone(accounts["76561198000000009"]?.games ?? {}) };
  }
  if (url.pathname === "/test/name") { // the test changes a Steam profile name
    account(p.get("steamid")).name = p.get("name");
    return send(200, { ok: true });
  }
  if (url.pathname.includes("GetPlayerSummaries")) {
    if (p.get("key") !== "fake-key") return send(403, "<html>Forbidden</html>");
    const sid = p.get("steamids");
    return send(200, { response: { players: accounts[sid] ? [{ steamid: sid, personaname: accounts[sid].name ?? accounts[sid].persona ?? "Player " + sid.slice(-2) }] : [] } });
  }
  if (url.pathname.includes("GetGlobalAchievementPercentagesForApp")) {
    const appid = p.get("gameid");
    const g = Object.values(accounts).map((a) => a.games[appid]).find(Boolean);
    return send(200, { achievementpercentages: { achievements: (g?.ach ?? []).filter((a) => a.pct != null).map((a) => ({ name: a.name, percent: String(a.pct) })) } });
  }
  if (p.get("key") !== "fake-key") return send(403, "<html>Forbidden</html>");
  if (url.pathname.includes("GetSchemaForGame")) {
    const appid = p.get("appid");
    const g = Object.values(accounts).map((a) => a.games[appid]).find(Boolean);
    return send(200, { game: { availableGameStats: { achievements: (g?.ach ?? []).map((a) => ({ name: a.name, displayName: "Nice " + a.name,
      icon: `https://cdn.example/steamcommunity/public/images/apps/${appid}/${a.name.toLowerCase()}.jpg` })) } } });
  }
  const acc = accounts[p.get("steamid")];
  if (url.pathname.includes("GetOwnedGames")) {
    if (!acc || acc.private) return send(200, { response: {} });
    const games = Object.entries(acc.games).map(([appid, g]) => ({ appid: Number(appid), name: "Game " + appid, playtime_forever: g.minutes, has_community_visible_stats: g.stats }));
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
await new Promise((r) => steam.listen(BASE + 1, "127.0.0.1", r));

// ---------- database ----------
const shim = spawn(process.execPath, ["shim.mjs"], {
  cwd: new URL(".", import.meta.url), env: { ...process.env, PORT: String(BASE), EXTRA_SQL: "admin.sql" }, stdio: ["ignore", "pipe", "inherit"],
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

  account(A).name = "Alice";
  const oldReg = await rpc("tl_register", { p_username: "alice_test", p_password: "alice-password-1" });
  check("sign-up with a password is switched off", /logs in with your Steam account/.test(oldReg.error ?? ""), oldReg);

  let r = await fn({ action: "login", openid: openid(A, "forged") });
  check("forged sign-in is refused", /couldn't confirm/.test(r.error ?? ""), r);
  r = await fn({ action: "login", openid: { ...openid(A), "openid.return_to": "https://evil.example/steam" } });
  check("sign-in made for another site is refused", /wasn't made for Trophyline/.test(r.error ?? ""), r);
  r = await fn({ action: "login", openid: openid(A, "good", "2020-01-01T00:00:00Zold") });
  check("old sign-in is refused", /too old/.test(r.error ?? ""), r);
  const good = openid(A);
  const reg = await fn({ action: "login", openid: good });
  check("log in with Steam creates the account", reg.ok && reg.new === true && reg.steam_id === A && reg.name === "Alice" && !!reg.secret, reg);
  const me = { id: reg.id, secret: reg.secret };
  r = await fn({ action: "login", openid: good });
  check("the same sign-in can't be used twice", /already used/.test(r.error ?? ""), r);
  r = await fn({ action: "login", openid: openid(A) });
  check("logging in again opens the same account", r.ok && r.id === me.id && !r.new && r.secret !== me.secret, r);

  const direct = await rpc("tl_srv_apply", { p_id: me.id, p: { owned: [], games: [], partial: false } });
  check("app key can't store Steam results itself", !!direct.httpError && /permission denied/.test(direct.httpError), direct);
  const direct2 = await rpc("tl_srv_steam_login", { p_steam_id: A, p_name: "x", p_nonce: "x" });
  check("app key can't log in without Steam's check", !!direct2.httpError, direct2);

  // a profile from before Steam login is kept when its computer logs in with Steam
  await sql(`insert into players (id, secret_hash, name, created, updated) values ('legacyprofile0000000001', tl_hash('legacy-secret-legacy-secret-1'), 'Oldie', 1, 1)`);
  const L1 = "76561198000000005";
  account(L1).name = "Oldie on Steam";
  r = await fn({ action: "login", id: "legacyprofile0000000001", secret: "legacy-secret-legacy-secret-1", openid: openid(L1) });
  check("an old profile becomes the Steam account's profile", r.ok && r.id === "legacyprofile0000000001" && r.name === "Oldie on Steam", r); // and takes the Steam name

  // name code, for networks that block Steam's login page
  const N = "76561198000000006";
  account(N).name = "Nina";
  r = await fn({ action: "code_start", steam_id: "123" });
  check("name code needs a real Steam id", !!r.error, r);
  const cs = await fn({ action: "code_start", steam_id: N });
  check("name code is handed out", cs.ok && /^TL[0-9A-F]{5}$/.test(cs.code) && cs.name === "Nina", cs);
  r = await fn({ action: "code_finish", challenge: cs.challenge });
  check("without the code in the name it waits", r.waiting === true && /Add TL/.test(r.error), r);
  account(N).name = "Nina " + cs.code.toLowerCase();
  r = await fn({ action: "code_finish", challenge: cs.challenge });
  check("with the code in the name it logs in", r.ok && r.steam_id === N && r.name === "Nina", r);
  r = await fn({ action: "code_finish", challenge: cs.challenge });
  check("a used code can't log in again", /expired/.test(r.error ?? ""), r);
  r = await fn({ action: "code_finish", challenge: "made-up" });
  check("a made-up code is refused", /expired/.test(r.error ?? ""), r);
  for (let i = 0; i < 5; i++) await fn({ action: "code_start", steam_id: N });
  r = await fn({ action: "code_start", steam_id: N });
  check("codes can't be spammed", /Too many codes/.test(r.error ?? ""), r);

  // client claims huge numbers before verification: shown on profile, never ranked
  await rpc("tl_upsert", { p: { id: me.id, secret: me.secret, name: "Alice", xp: 9999999, level: 300, perfect: 999, over75: 999, seasons: [{ id: "2026-4", xp: 999999 }] } });
  let lb = await rpc("tl_leaderboard", { p_board: "xp", p_me: me.id });
  check("unverified player is not on the leaderboard", lb.rows.length === 0 && lb.me === null, lb);
  await new Promise((res) => setTimeout(res, 11000)); // tl_upsert allows one upload per 10 s
  await rpc("tl_upsert", { p: { id: me.id, secret: me.secret, name: "Alice", xp: 9999999, level: 300,
    profile: { icon: "trophy", links: { discord: "alice" }, topGames: [{ appId: "1", name: "Fake", unlocked: 99, total: 99 }], recent: [{ name: "Fake" }] } } });
  const unv = await rpc("tl_player", { p_id: me.id });
  check("unverified profile shows no numbers at all", unv.xp === 0 && unv.level === 1 && unv.perfect === 0 && unv.verified === false, unv);
  check("...and no best games or unlocks sent by the app", !unv.profile.topGames && !unv.profile.recent, unv.profile);
  check("an icon a low level could not buy is not shown", unv.profile.icon === "" && unv.profile.links.discord === "alice", unv.profile);
  const raw = (await query(`select xp, level from players where id = '${me.id}'`))[0];
  check("the uploaded numbers are not even stored", raw.xp === 0 && raw.level === 1, raw);

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
  const up = await rpc("tl_upsert", { p: { id: me.id, secret: me.secret, name: "Fake Name", xp: 9999999, level: 300, perfect: 999, over75: 999, seasons: [{ id: "2026-4", xp: 999999 }] } });
  pl = (await query(`select name, xp, perfect from players where id = '${me.id}'`))[0];
  check("uploads from the app can't change verified numbers", up.ok && pl.xp === 2290 && pl.perfect === 2, { up, pl });
  check("the name stays the Steam name (the app can't set another)", pl.name === "Alice", pl);
  account(A).name = "Alice Renamed"; // renamed in Steam
  await sql(`update players set v_next = 0 where id = '${me.id}'`);
  r = await fn({ action: "verify", ...me });
  pl = (await query(`select name, xp from players where id = '${me.id}'`))[0];
  check("a new Steam name is picked up at the next check", r.ok && pl.name === "Alice Renamed" && pl.xp === 2290, { r, pl });
  const vs = await rpc("tl_verify_status", { p_id: me.id, p_secret: me.secret });
  check("the app reads the name from the server", vs.name === "Alice Renamed", vs);
  account(A).name = "Alice";
  await sql(`update players set v_next = 0 where id = '${me.id}'`);
  await fn({ action: "verify", ...me });
  lb = await rpc("tl_leaderboard", { p_board: "season", p_me: me.id });
  check("season board uses Steam's season XP", lb.rows.length === 1 && lb.rows[0].value === 1780, lb.rows);
  const prof = await rpc("tl_player", { p_id: me.id });
  check("profile shows verified", prof.verified === true && prof.places.xp === 1, prof);
  check("best games come from Steam", prof.profile.topGames?.[0]?.name === "Game 400" && prof.profile.topGames[1].name === "Game 200"
        && prof.profile.topGames.length === 3, prof.profile.topGames);
  check("latest unlocks come from Steam with names and icons", prof.profile.recent?.length === 10
        && prof.profile.recent.every((u) => /^Nice C\d+$/.test(u.name) && u.icon === u.name.slice(5).toLowerCase() + ".jpg" && u.game === "Game 400"),
        prof.profile.recent);
  const fakeRecent = await rpc("tl_srv_profile", { p_id: me.id, p_recent: [] });
  check("app key can't write the profile unlocks", !!fakeRecent.httpError, fakeRecent);

  // ----- linking with a name code -----
  const E = "76561198000000007";
  account(E).persona = "Erin";
  account(E).games = { 900: { minutes: 120, stats: true, ach: [{ name: "Z0", got: true, time: now - 7200, pct: 2 }, { name: "Z1", got: false, pct: 2 }] } };
  // (a profile from before Steam login, linking with the 1.8.0 name code)
  await sql(`insert into players (id, secret_hash, name, created, updated) values ('erinprofile000000000001', tl_hash('erin-secret-erin-secret-erin-1'), 'Erin', 1, 1)`);
  const er = { id: "erinprofile000000000001", secret: "erin-secret-erin-secret-erin-1" };
  let cb = await rpc("tl_code_begin", { p_id: er.id, p_secret: er.secret, p_steam_id: A });
  check("a code for a Steam account someone else linked is refused", /already linked/.test(cb.error ?? ""), cb);
  cb = await rpc("tl_code_begin", { p_id: er.id, p_secret: er.secret, p_steam_id: E });
  check("get a name code", cb.ok && /^TL-[0-9A-F]{6}$/.test(cb.code), cb);
  r = await fn({ action: "code", ...er });
  check("not linked while the code isn't in the Steam name", r.ok && r.linked === false && r.persona === "Erin", r);
  r = await fn({ action: "code", id: er.id, secret: "wrong-secret-wrong-secret-wrong" });
  check("someone else's login can't use the code", /Log in/.test(r.error ?? ""), r);
  account(E).persona = "Erin " + cb.code.toLowerCase();
  r = await fn({ action: "code", ...er });
  check("linked once the code is in the Steam name", r.ok && r.linked === true && r.steam_id === E, r);
  r = await fn({ action: "code", ...er });
  check("the code works only once", /ran out/.test(r.error ?? ""), r);
  r = await fn({ action: "verify", ...er });
  check("a code-linked account is checked with Steam like any other", r.ok && r.unlocked === 1 && r.xp === 100 + 50 + 20, r);
  const sr = await rpc("tl_srv_code", { p_id: er.id, p_secret: er.secret });
  check("app key can't read codes as the server", !!sr.httpError, sr);
  await rpc("tl_delete", { p_id: er.id, p_secret: er.secret });

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
  const reg2 = await fn({ action: "login", openid: openid(B) });
  check("second player logs in with another account", reg2.ok && reg2.id !== me.id, reg2);
  r = await fn({ action: "verify", id: reg2.id, secret: reg2.secret });
  check("big library: first part", r.ok && r.partial === true && r.left === 10 && r.state === "checking", r);
  pl = (await query(`select ranked from players where id = '${reg2.id}'`))[0];
  check("not ranked until the whole library is checked", pl.ranked === false, pl);
  r = await fn({ action: "verify", id: reg2.id, secret: reg2.secret });
  check("big library: next part needs a short pause", r.wait > 0 && r.wait <= 3, r);
  await new Promise((res) => setTimeout(res, 4000)); // the app waits 4 s between parts too
  r = await fn({ action: "verify", id: reg2.id, secret: reg2.secret });
  check("big library: rest after the pause", r.ok && r.partial === false && r.unlocked === 130 && r.games === 130, r);
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
  // POST /test/persona?sid=...&name=...  sets a fake Steam profile name (for testing name codes);
  // an account that doesn't exist yet gets the same games as D
  if (req.url.startsWith("/test/persona")) {
    const q = new URL(req.url, "http://x").searchParams;
    const acc = account(q.get("sid"));
    if (Object.keys(acc.games).length === 0) acc.games = structuredClone(account(D).games);
    acc.persona = q.get("name");
    res.writeHead(200); return res.end("ok");
  }
  if (!req.url.startsWith("/functions/v1/trophyline")) { res.writeHead(404); return res.end(); }
  let body = ""; for await (const c of req) body += c;
  const parsed = body ? JSON.parse(body) : {};
  // the app's fake sign-in carries sig "app-test": make it look like Steam's real answer for account D
  if (parsed.openid?.["openid.sig"] === "app-test") parsed.openid = { ...openid(D), "openid.return_to": parsed.openid["openid.return_to"] };
  const out = await handle(new Request("http://x/", { method: req.method, body: JSON.stringify(parsed) }));
  res.writeHead(out.status, { "content-type": "application/json" });
  res.end(await out.text());
}).listen(FN_PORT, "127.0.0.1", () => console.log(`function on http://127.0.0.1:${FN_PORT}/functions/v1/trophyline (fake Steam account ${D})`));
