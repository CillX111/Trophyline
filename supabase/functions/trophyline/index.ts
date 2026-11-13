// Trophyline server function (Supabase Edge Function "trophyline").
//
// It does what the database can't do on its own:
//   login        - confirms a "Sign in with Steam" with Steam itself, then logs in (or signs up)
//                  that Steam account: a Trophyline account is a Steam account
//   code_start / code_finish - the same for networks that block Steam's login page: the player
//                  puts a short code in their Steam profile name, and Steam's Web API shows it
//   verify       - asks Steam (with the server owner's Steam Web API key) which games and
//                  achievements the account really has, and stores that as its verified numbers
//   link / code  - link a Steam account to a logged-in profile (app 1.8.0)
// Players' computers never send their own numbers here, so editing local files changes nothing.
//
// Setup (Supabase dashboard): Edge Functions -> Deploy a new function -> Via Editor, name it
// "trophyline", paste this file, deploy. Turn OFF "Verify JWT" for it. Then Edge Functions ->
// Secrets -> add STEAM_API_KEY (from https://steamcommunity.com/dev/apikey).
// SUPABASE_URL and the service key are provided by Supabase automatically.

const env = (k: string): string | undefined =>
  (globalThis as any).Deno?.env.get(k) ?? (globalThis as any).process?.env?.[k];

const STEAM_API = () => env("STEAM_API_BASE") ?? "https://api.steampowered.com";
const STEAM_OPENID = () => env("STEAM_OPENID_URL") ?? "https://steamcommunity.com/openid/login";
const OP_ENDPOINT = "https://steamcommunity.com/openid/login";

const PER_CALL = 120;       // games whose achievements are read per call (big libraries take a few calls)
const PARALLEL = 8;         // Steam requests at the same time
const DAY = 86400;

// ---------- small helpers ----------

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

function serviceKey(): string {
  const k = env("SUPABASE_SERVICE_ROLE_KEY");
  if (k) return k;
  const all = env("SUPABASE_SECRET_KEYS"); // newer projects: {"default": "sb_secret_..."}
  if (all) {
    try {
      const o = JSON.parse(all);
      const v = o.default ?? Object.values(o)[0];
      if (typeof v === "string") return v;
    } catch { /* fall through */ }
  }
  throw new Error("the service key is missing");
}

/** Calls one tl_* database function as the service role. */
async function db(fn: string, args: Record<string, unknown>): Promise<any> {
  const key = serviceKey();
  const headers: Record<string, string> = { "content-type": "application/json", apikey: key };
  if (key.startsWith("eyJ")) headers.authorization = `Bearer ${key}`; // old-style JWT keys
  const res = await fetch(`${env("SUPABASE_URL")}/rest/v1/rpc/${fn}`, {
    method: "POST", headers, body: JSON.stringify(args),
  });
  const text = await res.text();
  let body: any = null;
  try { body = JSON.parse(text); } catch { /* not JSON */ }
  if (!res.ok) throw new Error(body?.message ?? `database said ${res.status}`);
  return body;
}

/** GET from the Steam Web API. Returns the status and the parsed body (or null). */
async function steam(path: string): Promise<{ status: number; body: any }> {
  for (let attempt = 0; ; attempt++) {
    try {
      const res = await fetch(STEAM_API() + path, { signal: AbortSignal.timeout(20000) });
      const text = await res.text();
      if ((res.status === 429 || res.status >= 500) && attempt < 2) {
        await new Promise((r) => setTimeout(r, 1500 * (attempt + 1)));
        continue;
      }
      let body: any = null;
      try { body = JSON.parse(text); } catch { /* not JSON */ }
      return { status: res.status, body };
    } catch (e) {
      if (attempt >= 2) throw e;
    }
  }
}

/** Runs `work` for every item, a few at a time. */
async function pool<T, R>(items: T[], limit: number, work: (item: T) => Promise<R>): Promise<R[]> {
  const out: R[] = new Array(items.length);
  let next = 0;
  async function worker() {
    while (next < items.length) {
      const i = next++;
      out[i] = await work(items[i]);
    }
  }
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, worker));
  return out;
}

/** XP for one achievement, the same as Xp.ForAchievement in the app: rarer = more. */
export function xpFor(rarity: number | null): number {
  if (rarity === null || !Number.isFinite(rarity)) return 25;
  if (rarity < 1) return 150;
  if (rarity < 5) return 100;
  if (rarity < 10) return 60;
  if (rarity < 20) return 40;
  if (rarity < 50) return 25;
  return 15;
}

// ---------- logging in with Steam ----------

/** The Steam profile name of an account (public even when the rest of the profile is private). */
async function personaName(steamId: string): Promise<{ name?: string; error?: string }> {
  const key = env("STEAM_API_KEY");
  if (!key) return { error: "The Trophyline server doesn't have a Steam API key yet. Ask the server owner to add it." };
  const r = await steam(`/ISteamUser/GetPlayerSummaries/v2/?key=${encodeURIComponent(key)}&steamids=${steamId}&format=json`);
  if (r.status === 401 || r.status === 403) return { error: "Steam refused the server's API key. Ask the server owner to check it." };
  if (r.status !== 200) return { error: `Steam didn't answer (${r.status}). Try again in a minute.` };
  const p = r.body?.response?.players?.[0];
  if (!p || p.steamid !== steamId) return { error: "Steam doesn't know this account." };
  return { name: String(p.personaname ?? "") };
}

/** Log in (or sign up) with Steam's own login page: the app sends Steam's signed answer. */
async function login(body: any): Promise<any> {
  const checked = await checkOpenId(body?.openid);
  if (checked.error) return checked;
  const who = await personaName(checked.steamId!);
  if (who.error) return who;
  return await db("tl_srv_steam_login", {
    p_steam_id: checked.steamId, p_name: who.name, p_nonce: checked.nonce + "|" + checked.steamId,
    p_id: body?.id ?? null, p_secret: body?.secret ?? null,
  });
}

const codeStarts = new Map<string, number[]>(); // caller IP -> times (ms) of recent code requests

/** At most 10 code requests per IP in 15 minutes (per running copy of the function). */
export function allowCodeStart(ip: string, now = Date.now()): boolean {
  const recent = (codeStarts.get(ip) ?? []).filter((t) => now - t < 15 * 60 * 1000);
  if (recent.length >= 10) { codeStarts.set(ip, recent); return false; }
  recent.push(now);
  codeStarts.set(ip, recent);
  if (codeStarts.size > 10000) codeStarts.clear(); // keep memory bounded
  return true;
}

/** For networks that block Steam's login page: a code to put in your Steam profile name. */
async function codeStart(body: any): Promise<any> {
  const sid = String(body?.steam_id ?? "");
  if (!/^7656119\d{10}$/.test(sid)) return { error: "That is not a Steam account." };
  const who = await personaName(sid);
  if (who.error) return who;
  const r = await db("tl_srv_code_start", { p_steam_id: sid });
  return r?.error ? r : { ...r, name: who.name };
}

/** Checks whether the code is in the Steam profile name now, and logs in if it is. */
async function codeFinish(body: any): Promise<any> {
  const c = await db("tl_srv_code_get", { p_challenge: String(body?.challenge ?? "") });
  if (c?.error) return c;
  const who = await personaName(c.steam_id);
  if (who.error) return who;
  if (!who.name!.toUpperCase().includes(c.code)) {
    return { error: `Steam still shows your profile name as "${who.name}". Add ${c.code} to it, wait a few seconds and try again.`, waiting: true };
  }
  // the name without the code, so a new profile isn't called "ftgt6565 TL4F9A2"
  const clean = who.name!.replace(new RegExp(c.code, "ig"), "").replace(/\s+/g, " ").trim();
  const r = await db("tl_srv_steam_login", {
    p_steam_id: c.steam_id, p_name: clean, p_nonce: "code|" + body.challenge,
    p_id: body?.id ?? null, p_secret: body?.secret ?? null,
  });
  if (!r?.error) await db("tl_srv_code_done", { p_challenge: String(body.challenge) });
  return r;
}

// ---------- link: Sign in with Steam ----------

/** Checks a "Sign in with Steam" answer with Steam itself. Returns the account or an error. */
async function checkOpenId(raw: any): Promise<{ steamId?: string; nonce?: string; error?: string }> {
  const q: Record<string, string> = {};
  for (const [k, v] of Object.entries(raw ?? {})) if (k.startsWith("openid.")) q[k] = String(v);

  if (q["openid.mode"] !== "id_res") return { error: "The sign-in was cancelled." };
  if (q["openid.op_endpoint"] !== OP_ENDPOINT) return { error: "The answer didn't come from Steam." };
  const m = /^https:\/\/steamcommunity\.com\/openid\/id\/(7656119\d{10})$/.exec(q["openid.claimed_id"] ?? "");
  if (!m || q["openid.identity"] !== q["openid.claimed_id"]) return { error: "Steam didn't say which account this is." };
  if (!/^http:\/\/127\.0\.0\.1:\d{2,5}\/steam$/.test(q["openid.return_to"] ?? "")) {
    return { error: "This sign-in wasn't made for Trophyline." };
  }
  const signed = (q["openid.signed"] ?? "").split(",");
  for (const f of ["op_endpoint", "claimed_id", "identity", "return_to", "response_nonce"]) {
    if (!signed.includes(f)) return { error: "Steam's answer is incomplete. Try again." };
  }
  const nonce = q["openid.response_nonce"] ?? "";
  const made = Date.parse(nonce.slice(0, 20));
  if (!Number.isFinite(made) || Math.abs(Date.now() - made) > 15 * 60 * 1000) {
    return { error: "This sign-in is too old. Try again." };
  }
  const form = new URLSearchParams(q);
  form.set("openid.mode", "check_authentication");
  let text = "";
  try {
    const res = await fetch(STEAM_OPENID(), { method: "POST", body: form, signal: AbortSignal.timeout(20000) });
    text = await res.text();
  } catch {
    return { error: "Couldn't reach Steam to confirm the sign-in. Try again in a minute." };
  }
  if (!/^is_valid\s*:\s*true\s*$/m.test(text)) return { error: "Steam couldn't confirm the sign-in. Try again." };
  return { steamId: m[1], nonce };
}

/** Links a Steam account to an account logged in on this computer (older app versions). */
async function link(body: any): Promise<any> {
  const checked = await checkOpenId(body?.openid);
  if (checked.error) return checked;
  return await db("tl_srv_link", { p_id: body.id, p_secret: body.secret, p_steam_id: checked.steamId, p_nonce: checked.nonce + "|" + checked.steamId });
}

// ---------- verify: read the account's games and achievements from Steam ----------

async function verify(body: any): Promise<any> {
  const start = await db("tl_srv_begin", { p_id: body?.id ?? null, p_secret: body?.secret ?? null });
  if (start?.error) return start;
  const key = env("STEAM_API_KEY");
  if (!key) return { error: "The Trophyline server doesn't have a Steam API key yet. Ask the server owner to add it." };
  const sid: string = start.steam_id;
  const k = encodeURIComponent(key);

  const owned = await steam(`/IPlayerService/GetOwnedGames/v1/?key=${k}&steamid=${sid}&include_appinfo=1&include_played_free_games=1&format=json`);
  if (owned.status === 401 || owned.status === 403) {
    return { error: "Steam refused the server's API key. Ask the server owner to check it." };
  }
  if (owned.status !== 200) return { error: `Steam didn't answer (${owned.status}). Try again later.` };
  const list: any[] | undefined = owned.body?.response?.games;
  if (!Array.isArray(list)) return await db("tl_srv_apply", { p_id: start.id, p: { private: true } });

  // which games need their achievements read: new, played since the last check, or not checked for a while
  const now = Math.floor(Date.now() / 1000);
  const cache: Record<string, [number, number]> = start.games ?? {};
  const todo = list
    .filter((g) => g.has_community_visible_stats)
    .filter((g) => {
      const c = cache[String(g.appid)];
      if (!c || c[1] === 0) return true;
      if (c[0] !== (g.playtime_forever ?? 0)) return true;
      return now - c[1] > ((g.playtime_forever ?? 0) > 0 ? 7 : 30) * DAY;
    })
    .sort((a, b) => {
      // games played since the last check first, then by playtime
      const ca = cache[String(a.appid)], cb = cache[String(b.appid)];
      const pa = ca && ca[0] !== a.playtime_forever ? 1 : 0, pb = cb && cb[0] !== b.playtime_forever ? 1 : 0;
      return pb - pa || (b.playtime_forever ?? 0) - (a.playtime_forever ?? 0);
    });
  const batch = todo.slice(0, PER_CALL);

  // Supabase stops a function after 150 s: start no new Steam reads after 90 s and save what's done
  // (the rest counts as "left" for the next part).
  const deadline = Date.now() + 90_000;
  let notReached = 0;
  let isPrivate = false;
  const games = (await pool(batch, PARALLEL, async (g) => {
    if (isPrivate) return null;
    if (Date.now() > deadline) { notReached++; return null; }
    const pa = await steam(`/ISteamUserStats/GetPlayerAchievements/v1/?key=${k}&steamid=${sid}&appid=${g.appid}&format=json`);
    const ps = pa.body?.playerstats;
    if (pa.status === 403 || /not public/i.test(ps?.error ?? "")) { isPrivate = true; return null; }
    if (!ps?.success || !Array.isArray(ps.achievements)) {
      // "Requested app has no stats" and similar: no achievements to count
      // anything else (Steam busy, an odd reply): try this game again later, without blocking the others
      return pa.status === 400 || ps?.success === false ? { a: g.appid, total: 0, u: [] } : { a: g.appid, skip: true };
    }
    const got = ps.achievements.filter((x: any) => x.achieved === 1 || x.achieved === true);
    let rarity: Record<string, number> = {};
    if (got.length > 0) {
      const gp = await steam(`/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/?gameid=${g.appid}&format=json`);
      for (const r of gp.body?.achievementpercentages?.achievements ?? []) {
        const pct = Number(r.percent);
        if (typeof r.name === "string" && Number.isFinite(pct)) rarity[r.name] = pct;
      }
    }
    return {
      a: g.appid,
      total: ps.achievements.length,
      u: got.map((x: any) => [String(x.apiname).slice(0, 200), Number(x.unlocktime) || 0, xpFor(rarity[x.apiname] ?? null)]),
    };
  })).filter((g) => g !== null);

  if (isPrivate) return await db("tl_srv_apply", { p_id: start.id, p: { private: true } });

  const result = await db("tl_srv_apply", {
    p_id: start.id,
    p: {
      owned: list.map((g) => ({ a: g.appid, m: g.playtime_forever ?? 0, n: typeof g.name === "string" ? g.name.slice(0, 120) : "" })),
      games,
      partial: todo.length > batch.length || notReached > 0,
    },
  });
  if (result?.ok && Array.isArray(result.latest)) {
    // the scores are saved already: a problem here must not report the whole check as failed
    try { await latestUnlocks(start.id, result.latest, k); } catch (e) { console.error("latest unlocks", e); }
  }
  delete result?.latest;
  return { ...result, left: Math.max(0, todo.length - batch.length + notReached) };
}

/** Names, icons and rarity for the newest unlocks on the profile (Steam's own names, never the app's). */
async function latestUnlocks(id: string, latest: [number, string, number, number][], k: string): Promise<void> {
  const apps = [...new Set(latest.map((l) => l[0]))].slice(0, 10);
  const info: Record<string, { name?: string; icon?: string; pct?: number }> = {};
  await pool(apps, PARALLEL, async (appid) => {
    try {
      const [schema, pct] = await Promise.all([
        steam(`/ISteamUserStats/GetSchemaForGame/v2/?key=${k}&appid=${appid}&l=english&format=json`),
        steam(`/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/?gameid=${appid}&format=json`),
      ]);
      for (const a of schema.body?.game?.availableGameStats?.achievements ?? [])
        if (typeof a.name === "string")
          info[`${appid}/${a.name}`] = { name: String(a.displayName ?? a.name), icon: String(a.icon ?? "").split("/").pop() };
      for (const r of pct.body?.achievementpercentages?.achievements ?? []) {
        const v = Number(r.percent);
        if (typeof r.name === "string" && Number.isFinite(v)) (info[`${appid}/${r.name}`] ??= {}).pct = v;
      }
    } catch { /* names are nice to have: the unlock still shows with its internal name */ }
  });
  const recent = latest.map(([appid, api]) => {
    const i = info[`${appid}/${api}`] ?? {};
    return { appId: String(appid), api, name: i.name ?? "", icon: i.icon ?? "", rarity: i.pct != null ? String(i.pct) : null };
  });
  await db("tl_srv_profile", { p_id: id, p_recent: recent });
}

// ---------- code: link with a code in the Steam profile name ----------
// For networks that block Steam's login page. The player puts a short code (e.g. TL-3F9A21) in their
// Steam profile name; the server sees it there through the Steam Web API, which only the real owner
// of that Steam account could have done.

async function code(body: any): Promise<any> {
  const c = await db("tl_srv_code", { p_id: body?.id ?? null, p_secret: body?.secret ?? null });
  if (c?.error) return c;
  const key = env("STEAM_API_KEY");
  if (!key) return { error: "The Trophyline server doesn't have a Steam API key yet. Ask the server owner to add it." };
  const s = await steam(`/ISteamUser/GetPlayerSummaries/v2/?key=${encodeURIComponent(key)}&steamids=${c.steam_id}&format=json`);
  if (s.status !== 200) return { error: `Steam didn't answer (${s.status}). Try again in a minute.` };
  const persona = String(s.body?.response?.players?.[0]?.personaname ?? "");
  if (!persona.toUpperCase().includes(String(c.code).toUpperCase())) return { ok: true, linked: false, persona };
  const r = await db("tl_srv_link", { p_id: body.id, p_secret: body.secret, p_steam_id: c.steam_id, p_nonce: "code|" + c.code + "|" + c.steam_id });
  return r?.error ? r : { ok: true, linked: true, steam_id: c.steam_id };
}

// ---------- entry point ----------

export async function handle(req: Request): Promise<Response> {
  if (req.method !== "POST") return json({ error: "Use POST" }, 405);
  let body: any;
  try { body = await req.json(); } catch { return json({ error: "Bad request" }, 400); }
  try {
    if (body?.action === "login") return json(await login(body));
    if (body?.action === "code_start") {
      // so nobody can use up a player's (or everyone's) login codes from one machine
      const ip = (req.headers.get("x-forwarded-for") ?? "").split(",")[0].trim() || "unknown";
      if (!allowCodeStart(ip)) return json({ error: "Too many codes from this network. Wait 15 minutes and try again." });
      return json(await codeStart(body));
    }
    if (body?.action === "code_finish") return json(await codeFinish(body));
    if (body?.action === "link") return json(await link(body));
    if (body?.action === "verify") return json(await verify(body));
    if (body?.action === "code") return json(await code(body));
    return json({ error: "Unknown action" }, 400);
  } catch (e) {
    console.error(e);
    return json({ error: "Server problem: " + ((e as Error)?.message ?? String(e)) }, 500);
  }
}

if ((globalThis as any).Deno?.serve) (globalThis as any).Deno.serve(handle);
