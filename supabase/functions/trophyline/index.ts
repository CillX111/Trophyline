// Trophyline server function (Supabase Edge Function "trophyline").
//
// It does the two things the database can't do on its own:
//   link   - confirms a "Sign in with Steam" with Steam itself, then links that Steam account
//   verify - asks Steam (with the server owner's Steam Web API key) which games and achievements
//            the linked account really has, and stores that as the player's verified numbers
//   code   - links a Steam account by a code the player put in their Steam profile name
//            (for networks that block Steam's login page)
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

// ---------- link: Sign in with Steam ----------

async function link(body: any): Promise<any> {
  const q: Record<string, string> = {};
  for (const [k, v] of Object.entries(body?.openid ?? {})) if (k.startsWith("openid.")) q[k] = String(v);

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
  // the nonce starts with the time Steam made it; only fresh sign-ins count
  const nonce = q["openid.response_nonce"] ?? "";
  const made = Date.parse(nonce.slice(0, 20));
  if (!Number.isFinite(made) || Math.abs(Date.now() - made) > 15 * 60 * 1000) {
    return { error: "This sign-in is too old. Try again." };
  }

  // ask Steam whether the answer is genuine
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

  return await db("tl_srv_link", { p_id: body.id, p_secret: body.secret, p_steam_id: m[1], p_nonce: nonce + "|" + m[1] });
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

  let isPrivate = false;
  const games = (await pool(batch, PARALLEL, async (g) => {
    if (isPrivate) return null;
    const pa = await steam(`/ISteamUserStats/GetPlayerAchievements/v1/?key=${k}&steamid=${sid}&appid=${g.appid}&format=json`);
    const ps = pa.body?.playerstats;
    if (pa.status === 403 || /not public/i.test(ps?.error ?? "")) { isPrivate = true; return null; }
    if (!ps?.success || !Array.isArray(ps.achievements)) {
      // "Requested app has no stats" and similar: no achievements to count
      return pa.status === 400 || ps?.success === false ? { a: g.appid, total: 0, u: [] } : null;
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
      partial: todo.length > batch.length,
    },
  });
  if (result?.ok && Array.isArray(result.latest)) await latestUnlocks(start.id, result.latest, k);
  delete result?.latest;
  return { ...result, left: Math.max(0, todo.length - batch.length) };
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
