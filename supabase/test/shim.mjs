// Local stand-in for a Supabase project: PGlite (real Postgres in WebAssembly) running
// ../trophyline.sql, answering POST /rest/v1/rpc/<function> like Supabase's REST API does.
// Calls run as the "anon" role, so the permissions in the SQL are tested too.
import { PGlite } from "@electric-sql/pglite";
import { readFileSync } from "node:fs";
import { createServer } from "node:http";

const port = Number(process.env.PORT || 54321);
const db = await PGlite.create();
await db.exec(`
  create schema if not exists extensions;
  do $$ begin create role anon nologin; exception when duplicate_object then null; end $$;
  do $$ begin create role authenticated nologin; exception when duplicate_object then null; end $$;
  do $$ begin create role service_role nologin; exception when duplicate_object then null; end $$;
  grant usage on schema public to anon, authenticated, service_role;
`);
await db.exec(readFileSync(new URL("../trophyline.sql", import.meta.url), "utf8"));
console.log("trophyline.sql loaded");
// EXTRA_SQL=file1;file2 runs more scripts (e.g. a fix snippet, or test data)
for (const f of (process.env.EXTRA_SQL || "").split(";").filter(Boolean)) {
  await db.exec(readFileSync(f, "utf8"));
  console.log(`${f} loaded`);
}

// named arguments, like PostgREST:  select fn(a => $1, b => $2)
// SERVICE_KEY (default "test-service-key") stands in for Supabase's secret key: calls with it run as service_role
const serviceKey = process.env.SERVICE_KEY || "test-service-key";

async function rpc(fn, args, role) {
  if (!/^[a-z_][a-z0-9_]*$/.test(fn)) throw new Error("bad function");
  const names = Object.keys(args);
  const sql = `select ${fn}(${names.map((n, i) => `${n} => $${i + 1}`).join(", ")}) as r`;
  const values = names.map((n) => (args[n] !== null && typeof args[n] === "object" ? JSON.stringify(args[n]) : args[n]));
  return db.transaction(async (tx) => {
    await tx.exec(`set local role ${role}`);
    const res = await tx.query(sql, values);
    return res.rows[0].r;
  });
}

createServer(async (req, res) => {
  const send = (status, body) => {
    res.writeHead(status, { "content-type": "application/json" });
    res.end(JSON.stringify(body));
  };
  const m = /^\/rest\/v1\/rpc\/([a-z_0-9]+)$/.exec(req.url.split("?")[0]);
  if (req.method !== "POST" || !m) return send(404, { message: "not found" });
  if (!req.headers["apikey"]) return send(401, { message: "No API key found in request" });
  let body = "";
  for await (const chunk of req) body += chunk;
  try {
    send(200, await rpc(m[1], body ? JSON.parse(body) : {}, req.headers["apikey"] === serviceKey ? "service_role" : "anon"));
  } catch (e) {
    send(400, { code: e.code || "P0001", message: e.message });
  }
}).listen(port, "127.0.0.1", () => console.log(`ready on http://127.0.0.1:${port}`));
