import { PGlite } from "@electric-sql/pglite";
import { readFileSync } from "node:fs";
const db = await PGlite.create();
await db.exec(`create schema if not exists extensions;
  create role anon nologin; create role authenticated nologin; create role service_role nologin;
  grant usage on schema public to anon, authenticated, service_role;`);
await db.exec(readFileSync(process.argv[2], "utf8"));
const r = (await db.query(`select tl_register('olduser', 'old-password-1') as r`)).rows[0].r;
await db.query(`select tl_upsert($1::jsonb)`, [JSON.stringify({ id: r.id, secret: r.secret, name: "Old", xp: 5000, level: 10, seasons: [{ id: "2026-4", xp: 700 }] })]);
await db.exec(readFileSync(process.argv[3], "utf8"));
await db.exec(readFileSync(process.argv[3], "utf8")); // twice: re-running is safe
const p = (await db.query(`select name, xp, ranked, steam_id, verified_at from players`)).rows;
const lb = (await db.query(`select tl_leaderboard('xp', null, null, 100) as r`)).rows[0].r;
const login = (await db.query(`select tl_login('olduser', 'old-password-1') as r`)).rows[0].r;
console.log(JSON.stringify({ players: p, lbRows: lb.rows.length, players_count: lb.players, ranked: lb.ranked, loginOk: login.ok }));