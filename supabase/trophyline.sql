-- Trophyline online: leaderboards, 3-month seasons, profiles and chat.
--
-- Setup: in your Supabase project open "SQL Editor", paste this whole file, click "Run".
-- Running it again is safe (it only adds what's missing and replaces the functions).
--
-- How it's protected: the tables can't be read or written directly with the public app key
-- (row level security on, no policies). The app only calls the tl_* functions below, and
-- every change needs the secret that only the player's own Trophyline knows.

-- ---------- tables ----------

create table if not exists players (
  id          text primary key,            -- random id made by the app
  secret_hash text not null,               -- sha-256 of the app's secret
  name        text not null,
  level       int not null default 1,
  rank        text not null default 'Rookie',
  xp          bigint not null default 0,
  perfect     int not null default 0,      -- games at 100%
  over75      int not null default 0,      -- games at 75% or more
  unlocked    int not null default 0,      -- achievements
  games       int not null default 0,      -- games with achievements started
  hours       int not null default 0,
  profile     jsonb not null default '{}', -- icon, top games, latest unlocks
  created     bigint not null,
  updated     bigint not null
);
create index if not exists players_xp on players (xp desc);
create index if not exists players_perfect on players (perfect desc, xp desc);
create index if not exists players_over75 on players (over75 desc, xp desc);

create table if not exists season_scores (
  season  text not null,                   -- '2026-4' = October to December 2026
  player  text not null references players(id) on delete cascade,
  xp      bigint not null,
  updated bigint not null,
  primary key (season, player)
);
create index if not exists season_xp on season_scores (season, xp desc);

create table if not exists messages (
  id     bigserial primary key,
  room   text not null,                    -- 'lobby' or 'dm:<id>:<id>' (ids sorted)
  sender text not null references players(id) on delete cascade,
  text   text not null,
  time   bigint not null
);
create index if not exists messages_room on messages (room, id);
create index if not exists messages_sender on messages (sender, id desc);
create index if not exists messages_time on messages (time);

create table if not exists blocks (
  player  text not null references players(id) on delete cascade,
  blocked text not null references players(id) on delete cascade,
  primary key (player, blocked)
);

-- profile pictures: a small square JPEG or PNG (the app makes it 128 x 128)
alter table players add column if not exists avatar_ver bigint not null default 0; -- 0 = no picture
create table if not exists avatars (
  player  text primary key references players(id) on delete cascade,
  data    bytea not null,
  updated bigint not null
);

-- accounts: a username and password, so you can log in on any computer
alter table players add column if not exists username   text;                        -- null = old profile without a login
alter table players add column if not exists pw_salt    text;
alter table players add column if not exists pw_hash    text;
alter table players add column if not exists fail_count int not null default 0;      -- wrong passwords in a row
alter table players add column if not exists fail_time  bigint not null default 0;
alter table players add column if not exists banned     boolean not null default false; -- set to true in the Table Editor to ban a cheater
create unique index if not exists players_username on players (lower(username)) where username is not null;

-- one row per computer that is logged in
create table if not exists sessions (
  token_hash text primary key,
  player     text not null references players(id) on delete cascade,
  created    bigint not null,
  used       bigint not null
);
create index if not exists sessions_player on sessions (player);
alter table sessions enable row level security;
revoke all on sessions from anon, authenticated;

-- Steam verification: the server asks Steam itself for each player's achievements (supabase/functions/trophyline).
-- Only verified players are ranked. Their numbers come from Steam, not from their computer.
alter table players add column if not exists steam_id    text;                          -- linked with Sign in with Steam
alter table players add column if not exists verified_at bigint not null default 0;     -- last full check with Steam
alter table players add column if not exists v_state     text not null default '';      -- '', 'ok', 'checking', 'private'
alter table players add column if not exists v_partial   boolean not null default false; -- a big library is checked in parts
alter table players add column if not exists v_next      bigint not null default 0;     -- no new check before this time
alter table players add column if not exists flags       text not null default '';      -- why the cheat check paused this player
alter table players add column if not exists approve     boolean not null default false; -- owner: tick to clear the flags
alter table players add column if not exists cleared_at  bigint not null default 0;     -- unlocks up to here were approved
alter table players add column if not exists ranked boolean
  generated always as (steam_id is not null and verified_at > 0 and not banned and (flags = '' or approve)) stored;
create unique index if not exists players_steam on players (steam_id) where steam_id is not null;

-- what Steam said, per game and per achievement
create table if not exists steam_games (
  player       text not null references players(id) on delete cascade,
  appid        int not null,
  minutes      int not null default 0,     -- playtime now
  seen_minutes int not null default -1,    -- playtime when the achievements were last read
  achieved     int not null default 0,
  total        int not null default 0,
  checked      bigint not null default 0,
  primary key (player, appid)
);
create table if not exists steam_unlocks (
  player text not null references players(id) on delete cascade,
  appid  int not null,
  name   text not null,
  time   bigint not null,
  xp     int not null,
  primary key (player, appid, name)
);
create index if not exists steam_unlocks_time on steam_unlocks (player, time);
create table if not exists season_hours (     -- hours played when a season's counting started
  season text not null,
  player text not null references players(id) on delete cascade,
  hours  int not null,
  primary key (season, player)
);
create table if not exists openid_nonces (    -- each Steam sign-in can be used once
  nonce text primary key,
  used  bigint not null
);
-- reports from players: look at them in the Table Editor
create table if not exists reports (
  id       bigserial primary key,
  reporter text not null references players(id) on delete cascade,
  target   text not null references players(id) on delete cascade,
  reason   text not null,
  time     bigint not null,
  unique (reporter, target)
);
alter table steam_games enable row level security;
alter table steam_unlocks enable row level security;
alter table season_hours enable row level security;
alter table openid_nonces enable row level security;
alter table reports enable row level security;
revoke all on steam_games, steam_unlocks, season_hours, openid_nonces, reports from anon, authenticated;

-- game names, for the "best games" on profiles (from Steam's list of owned games)
alter table steam_games add column if not exists name text not null default '';

-- linking with a name code (for networks that block Steam's login page): the player puts the code in
-- their Steam profile name for a moment, and the server function sees it there through the Steam Web API
create table if not exists steam_codes (
  player   text primary key references players(id) on delete cascade,
  steam_id text not null,
  code     text not null,
  created  bigint not null
);
alter table steam_codes enable row level security;
revoke all on steam_codes from anon, authenticated;

-- Numbers that Steam never confirmed could have been typed in by hand, so they're cleared.
-- Only the server (tl_srv_apply) sets levels, XP, game counts, best games and latest unlocks.
update players set xp = 0, level = 1, rank = 'Rookie', perfect = 0, over75 = 0, unlocked = 0, games = 0, hours = 0
where verified_at = 0 and (xp <> 0 or level <> 1 or perfect <> 0 or over75 <> 0 or unlocked <> 0 or games <> 0 or hours <> 0);
update players set profile = profile - 'topGames' - 'recent'
where (profile ? 'topGames' or profile ? 'recent') and not coalesce(profile->>'fromSteam', '') = '1';
delete from season_scores ss using players p where p.id = ss.player and p.verified_at = 0;

alter table players enable row level security;
alter table season_scores enable row level security;
alter table messages enable row level security;
alter table blocks enable row level security;
alter table avatars enable row level security;
revoke all on players, season_scores, messages, blocks, avatars from anon, authenticated;

-- ---------- helpers ----------

create or replace function tl_now() returns bigint language sql stable as $$
  select extract(epoch from now())::bigint
$$;

create or replace function tl_int(v text, max_value bigint) returns bigint language sql immutable as $$
  select case when v ~ '^\d+(\.\d+)?$' then least(floor(v::numeric)::bigint, max_value) else 0 end
$$;

create or replace function tl_clean(v text, max_len int) returns text language sql immutable as $$
  -- removes control characters (keeps line breaks) and invisible / text-direction characters
  select left(btrim(translate(coalesce(v, ''),
    (select string_agg(chr(c), '') from generate_series(1, 31) c where c <> 10)
      || chr(127) || (select string_agg(chr(c), '') from generate_series(128, 159) c)
      || chr(8203) || chr(8204) || chr(8205) || chr(8206) || chr(8207)
      || chr(8234) || chr(8235) || chr(8236) || chr(8237) || chr(8238) || chr(65279),
    '')), max_len)
$$;

create or replace function tl_hash(secret text) returns text language sql immutable
set search_path = public, extensions as $$
  select encode(sha256(convert_to(secret, 'UTF8')), 'hex')
$$;

-- the player this secret belongs to (a login session, or an old profile's own secret), or null.
-- Banned players can't do anything.
create or replace function tl_auth(p_id text, p_secret text) returns text language sql stable
set search_path = public, extensions as $$
  select id from players
  where id = p_id and not banned
    and (secret_hash = tl_hash(coalesce(p_secret, ''))
         or exists (select 1 from sessions s where s.player = p_id and s.token_hash = tl_hash(coalesce(p_secret, ''))))
$$;

-- password fingerprint: salted SHA-256, repeated 20000 times (slow on purpose, to make guessing expensive)
create or replace function tl_pwhash(p_password text, p_salt text) returns text language plpgsql immutable as $$
declare
  h bytea := sha256(convert_to(p_salt || p_password, 'UTF8'));
  pw bytea := convert_to(p_password, 'UTF8');
begin
  for i in 1..20000 loop
    h := sha256(h || pw);
  end loop;
  return encode(h, 'hex');
end $$;

create or replace function tl_token() returns text language sql volatile as $$
  select replace(gen_random_uuid()::text, '-', '') || replace(gen_random_uuid()::text, '-', '')
$$;

-- starts a login session for a player and returns what the app needs to remember
create or replace function tl_session(p_player text) returns jsonb language plpgsql
set search_path = public, extensions as $$
declare
  token text := tl_token();
  v_now bigint := tl_now();
  pl players;
begin
  delete from sessions where used < v_now - 180 * 86400;
  insert into sessions (token_hash, player, created, used) values (tl_hash(token), p_player, v_now, v_now);
  select * into pl from players where id = p_player;
  return jsonb_build_object('ok', true, 'id', pl.id, 'secret', token, 'username', pl.username, 'name', pl.name);
end $$;

-- ---------- seasons: every 3 months (calendar quarters, UTC); Season 1 = Oct-Dec 2026 ----------

create or replace function tl_season_make(y int, q int) returns jsonb language sql immutable as $$
  select jsonb_build_object(
    'id', y || '-' || q,
    'number', (y - 2026) * 4 + (q - 4) + 1,
    'name', 'Season ' || ((y - 2026) * 4 + (q - 4) + 1),
    'label', (array['Winter', 'Spring', 'Summer', 'Autumn'])[q] || ' ' || y,
    'start', extract(epoch from make_timestamptz(y, (q - 1) * 3 + 1, 1, 0, 0, 0, 'UTC'))::bigint,
    'end', extract(epoch from make_timestamptz(y, (q - 1) * 3 + 1, 1, 0, 0, 0, 'UTC') + interval '3 months')::bigint)
$$;

create or replace function tl_season() returns jsonb language sql stable as $$
  select tl_season_make(extract(year from now() at time zone 'UTC')::int,
                        (extract(month from now() at time zone 'UTC')::int - 1) / 3 + 1)
$$;

create or replace function tl_season_parse(p text) returns jsonb language sql immutable as $$
  select case when p ~ '^\d{4}-[1-4]$' then tl_season_make(split_part(p, '-', 1)::int, split_part(p, '-', 2)::int) end
$$;

create or replace function tl_season_prev(s jsonb) returns jsonb language sql immutable as $$
  select case when split_part(s->>'id', '-', 2)::int = 1
              then tl_season_make(split_part(s->>'id', '-', 1)::int - 1, 4)
              else tl_season_make(split_part(s->>'id', '-', 1)::int, split_part(s->>'id', '-', 2)::int - 1) end
$$;

-- ---------- players ----------

create or replace function tl_status() returns jsonb language sql stable security definer
set search_path = public, extensions as $$
  select jsonb_build_object('ok', true, 'season', tl_season(), 'players', (select count(*) from players where not banned))
$$;

create or replace function tl_upsert(p jsonb) returns jsonb language plpgsql security definer
set search_path = public, extensions as $$
declare
  v_id text := p->>'id';
  v_secret text := p->>'secret';
  v_name text := tl_clean(p->>'name', 24);
  v_now bigint := tl_now();
  v_old_updated bigint;
  v_banned boolean;
  v_icon text := tl_clean(p->'profile'->>'icon', 24);
  v_links jsonb := '{}'::jsonb;
  v_level int;
  k text;
  v text;
  cur jsonb := tl_season();
begin
  if v_id is null or v_id !~ '^[a-z0-9]{16,40}$' then raise exception 'bad id'; end if;
  if v_secret is null or length(v_secret) < 24 or length(v_secret) > 128 then raise exception 'bad secret'; end if;
  if v_name = '' then raise exception 'name required'; end if;

  -- only an existing account can upload (create one with tl_register)
  select updated, banned into v_old_updated, v_banned from players where id = v_id;
  if v_old_updated is null then raise exception 'create an account first (update Trophyline if you can''t find it)'; end if;
  if v_banned then raise exception 'this account was banned'; end if;
  if tl_auth(v_id, v_secret) is null then raise exception 'not your profile'; end if;
  if v_now - v_old_updated < 10 then raise exception 'too often'; end if;

  -- Only what the player chooses: display name, equipped icon and profile links. Levels, XP, game counts,
  -- best games and latest unlocks are never taken from the app; the Steam check sets them (tl_srv_apply).
  select level into v_level from players where id = v_id;
  if v_icon <> '' and not tl_icon_ok(v_icon, v_level) then v_icon := ''; end if;
  if jsonb_typeof(p->'profile'->'links') = 'object' then
    for k, v in select key, value from jsonb_each_text(p->'profile'->'links') limit 12 loop
      if k ~ '^[a-z]{2,20}$' and length(v) between 1 and 64 then v_links := v_links || jsonb_build_object(k, tl_clean(v, 64)); end if;
    end loop;
  end if;
  update players set name = v_name, updated = v_now,
                     profile = (profile - 'icon' - 'links') || jsonb_build_object('icon', v_icon, 'links', v_links)
  where id = v_id;
  update sessions set used = v_now where token_hash = tl_hash(v_secret);

  return jsonb_build_object('ok', true, 'season', cur);
end $$;

create or replace function tl_delete(p_id text, p_secret text) returns jsonb language plpgsql security definer
set search_path = public, extensions as $$
begin
  if not exists (select 1 from players where id = p_id) then return jsonb_build_object('ok', true); end if;
  if tl_auth(p_id, p_secret) is null then raise exception 'not your profile'; end if;
  delete from players where id = p_id; -- seasons, messages and blocks go with it
  return jsonb_build_object('ok', true);
end $$;

-- ---------- accounts: register, log in, log out ----------
-- These return {"error": "..."} instead of failing, so a wrong password can be counted.

create or replace function tl_check_login(p_username text, p_password text) returns text language sql immutable as $$
  select case
    when coalesce(p_username, '') !~ '^[A-Za-z0-9_]{3,20}$' then 'Usernames are 3 to 20 letters, numbers or _'
    when length(coalesce(p_password, '')) < 8 then 'The password needs at least 8 characters'
    when length(p_password) > 200 then 'That password is too long'
    when lower(p_password) = lower(p_username) then 'The password can''t be your username'
  end
$$;

create or replace function tl_register(p_username text, p_password text) returns jsonb language plpgsql security definer
set search_path = public, extensions as $$
declare
  problem text := tl_check_login(p_username, p_password);
  v_id text := replace(gen_random_uuid()::text, '-', '');
  v_salt text := tl_token();
  v_now bigint := tl_now();
begin
  -- accounts now log in with Steam only (supabase/functions/trophyline, action login)
  return jsonb_build_object('error', 'Trophyline now logs in with your Steam account. Update Trophyline (Settings, Updates) and press Log in with Steam.');
  if problem is not null then return jsonb_build_object('error', problem); end if;
  if exists (select 1 from players where lower(username) = lower(p_username)) then
    return jsonb_build_object('error', 'That username is taken');
  end if;
  insert into players (id, secret_hash, name, username, pw_salt, pw_hash, created, updated)
  values (v_id, '', p_username, p_username, v_salt, tl_pwhash(p_password, v_salt), v_now, v_now - 60);
  return tl_session(v_id);
end $$;

create or replace function tl_login(p_username text, p_password text) returns jsonb language plpgsql security definer
set search_path = public, extensions as $$
declare
  pl players;
  v_now bigint := tl_now();
begin
  -- accounts now log in with Steam only (supabase/functions/trophyline, action login)
  return jsonb_build_object('error', 'Trophyline now logs in with your Steam account. Update Trophyline (Settings, Updates) and press Log in with Steam.');
  select * into pl from players where lower(username) = lower(coalesce(p_username, ''));
  if pl.id is null then return jsonb_build_object('error', 'Wrong username or password'); end if;
  if pl.banned then return jsonb_build_object('error', 'This account was banned'); end if;
  if pl.fail_count >= 5 and v_now - pl.fail_time < 300 then
    return jsonb_build_object('error', 'Too many wrong passwords. Wait 5 minutes and try again');
  end if;
  if pl.pw_hash is distinct from tl_pwhash(coalesce(p_password, ''), pl.pw_salt) then
    update players set fail_count = case when v_now - fail_time < 300 then fail_count + 1 else 1 end, fail_time = v_now where id = pl.id;
    return jsonb_build_object('error', 'Wrong username or password');
  end if;
  update players set fail_count = 0 where id = pl.id;
  return tl_session(pl.id);
end $$;

-- gives a profile from before accounts existed a username and password (it keeps everything)
create or replace function tl_claim(p_id text, p_secret text, p_username text, p_password text) returns jsonb
language plpgsql security definer
set search_path = public, extensions as $$
declare
  problem text := tl_check_login(p_username, p_password);
  v_salt text := tl_token();
begin
  -- accounts now log in with Steam only (supabase/functions/trophyline, action login)
  return jsonb_build_object('error', 'Trophyline now logs in with your Steam account. Update Trophyline (Settings, Updates) and press Log in with Steam.');
  if tl_auth(p_id, p_secret) is null then return jsonb_build_object('error', 'Not your profile'); end if;
  if exists (select 1 from players where id = p_id and username is not null) then
    return jsonb_build_object('error', 'This profile already has a login');
  end if;
  if problem is not null then return jsonb_build_object('error', problem); end if;
  if exists (select 1 from players where lower(username) = lower(p_username)) then
    return jsonb_build_object('error', 'That username is taken');
  end if;
  update players set username = p_username, pw_salt = v_salt, pw_hash = tl_pwhash(p_password, v_salt), secret_hash = '' where id = p_id;
  return tl_session(p_id);
end $$;

create or replace function tl_logout(p_id text, p_secret text) returns jsonb language plpgsql security definer
set search_path = public, extensions as $$
begin
  delete from sessions where player = p_id and token_hash = tl_hash(coalesce(p_secret, ''));
  return jsonb_build_object('ok', true);
end $$;

-- changes the password and logs out every other computer
create or replace function tl_change_password(p_id text, p_secret text, p_old text, p_new text) returns jsonb
language plpgsql security definer
set search_path = public, extensions as $$
declare
  pl players;
  v_salt text := tl_token();
  problem text;
begin
  if tl_auth(p_id, p_secret) is null then return jsonb_build_object('error', 'Log in again first'); end if;
  select * into pl from players where id = p_id;
  if pl.username is null then return jsonb_build_object('error', 'Create a login first'); end if;
  if pl.pw_hash is distinct from tl_pwhash(coalesce(p_old, ''), pl.pw_salt) then
    return jsonb_build_object('error', 'The current password is wrong');
  end if;
  problem := tl_check_login(pl.username, p_new);
  if problem is not null then return jsonb_build_object('error', problem); end if;
  update players set pw_salt = v_salt, pw_hash = tl_pwhash(p_new, v_salt) where id = p_id;
  delete from sessions where player = p_id and token_hash <> tl_hash(coalesce(p_secret, ''));
  return jsonb_build_object('ok', true);
end $$;

create or replace function tl_player(p_id text) returns jsonb language plpgsql stable security definer
set search_path = public, extensions as $$
declare
  pl players;
  cur jsonb := tl_season();
  sxp bigint;
begin
  select * into pl from players where id = p_id and not banned;
  if pl.id is null then raise exception 'no such player'; end if;
  select xp into sxp from season_scores where season = cur->>'id' and player = p_id;
  -- numbers only for players Steam has confirmed (and the cheat check hasn't paused): nothing unchecked is shown
  if not pl.ranked then
    return jsonb_build_object(
      'id', pl.id, 'name', pl.name, 'level', 1, 'rank', 'Rookie', 'xp', 0, 'perfect', 0, 'over75', 0, 'unlocked', 0,
      'games', 0, 'hours', 0, 'profile', pl.profile - 'topGames' - 'recent',
      'created', pl.created, 'updated', pl.verified_at, 'avatar', pl.avatar_ver,
      'verified', false, 'verified_at', pl.verified_at, 'linked', pl.steam_id is not null,
      'season', cur || jsonb_build_object('xp', 0), 'places', null);
  end if;
  return jsonb_build_object(
    'id', pl.id, 'name', pl.name, 'level', pl.level, 'rank', pl.rank, 'xp', pl.xp, 'perfect', pl.perfect,
    'over75', pl.over75, 'unlocked', pl.unlocked, 'games', pl.games, 'hours', pl.hours, 'profile', pl.profile,
    'created', pl.created, 'updated', pl.verified_at, 'avatar', pl.avatar_ver, 'linked', true,
    'verified', pl.ranked, 'verified_at', pl.verified_at,
    'season', cur || jsonb_build_object('xp', case when pl.ranked then coalesce(sxp, 0) else 0 end),
    'places', case when pl.ranked then jsonb_build_object(
      'xp', (select count(*) + 1 from players where ranked and xp > pl.xp),
      'perfect', (select count(*) + 1 from players where ranked and perfect > pl.perfect),
      'over75', (select count(*) + 1 from players where ranked and over75 > pl.over75)) end);
end $$;

create or replace function tl_leaderboard(p_board text default 'season', p_season text default null,
                                          p_me text default null, p_limit int default 100)
returns jsonb language plpgsql stable security definer
set search_path = public, extensions as $$
declare
  s jsonb;
  v_rows jsonb;
  v_me jsonb;
  mv bigint;
  v_pos bigint;
  lim int := least(greatest(coalesce(p_limit, 100), 1), 200);
begin
  if p_board = 'season' then
    s := coalesce(tl_season_parse(p_season), tl_season());
    select coalesce(jsonb_agg(to_jsonb(r) order by r.pos), '[]'::jsonb) into v_rows from (
      -- rank(): players with the same score share a place, the same way "your place" is counted below
      select rank() over (order by ss.xp desc) as pos, pl.id, pl.name, pl.level, pl.rank,
             coalesce(pl.profile->>'icon', '') as icon, pl.avatar_ver as avatar, ss.xp as value
      from season_scores ss join players pl on pl.id = ss.player
      where ss.season = s->>'id' and ss.xp > 0 and pl.ranked
      order by ss.xp desc, pl.xp desc limit lim) r;
    select ss.xp into mv from season_scores ss join players pl on pl.id = ss.player
    where ss.season = s->>'id' and ss.player = p_me and pl.ranked;
    if mv > 0 then -- with 0 you aren't in the list, so you have no place either
      v_me := jsonb_build_object('pos', (select count(*) + 1 from season_scores ss join players pl on pl.id = ss.player
                                         where ss.season = s->>'id' and ss.xp > mv and pl.ranked), 'value', mv);
    end if;
  elsif p_board in ('xp', 'perfect', 'over75') then
    s := tl_season();
    execute format(
      'select coalesce(jsonb_agg(to_jsonb(r) order by r.pos), ''[]''::jsonb) from (
         select rank() over (order by %1$I desc) as pos, id, name, level, rank,
                coalesce(profile->>''icon'', '''') as icon, avatar_ver as avatar, %1$I::bigint as value
         from players where %1$I > 0 and ranked order by %1$I desc, xp desc limit $1) r', p_board)
      into v_rows using lim;
    if p_me is not null then
      execute format('select %I::bigint from players where id = $1 and ranked', p_board) into mv using p_me;
      if mv > 0 then
        execute format('select count(*) + 1 from players where ranked and %I > $1', p_board) into v_pos using mv;
        v_me := jsonb_build_object('pos', v_pos, 'value', mv);
      end if;
    end if;
  else
    raise exception 'unknown board';
  end if;
  return jsonb_build_object('board', p_board, 'season', s, 'rows', v_rows, 'me', v_me,
                            'players', (select count(*) from players where not banned),
                            'ranked', (select count(*) from players where ranked));
end $$;

create or replace function tl_seasons() returns jsonb language plpgsql stable security definer
set search_path = public, extensions as $$
declare
  cur jsonb := tl_season();
  s jsonb := tl_season();
  out jsonb := '[]'::jsonb;
  top jsonb;
begin
  while (s->>'number')::int >= 1 and jsonb_array_length(out) < 12 loop
    select coalesce(jsonb_agg(to_jsonb(r) order by r.value desc), '[]'::jsonb) into top from (
      select pl.id, pl.name, pl.level, pl.rank, coalesce(pl.profile->>'icon', '') as icon, pl.avatar_ver as avatar, ss.xp as value
      from season_scores ss join players pl on pl.id = ss.player
      where ss.season = s->>'id' and ss.xp > 0 and pl.ranked order by ss.xp desc limit 3) r;
    out := out || jsonb_build_array(s || jsonb_build_object('current', s->>'id' = cur->>'id', 'top', top));
    s := tl_season_prev(s);
  end loop;
  return jsonb_build_object('current', cur, 'seasons', out);
end $$;

-- ---------- chat ----------

create or replace function tl_dm_room(a text, b text) returns text language sql immutable as $$
  select case when a < b then 'dm:' || a || ':' || b else 'dm:' || b || ':' || a end
$$;

create or replace function tl_chat_send(p jsonb) returns jsonb language plpgsql security definer
set search_path = public, extensions as $$
declare
  me text := tl_auth(p->>'id', p->>'secret');
  v_text text := regexp_replace(tl_clean(p->>'text', 500), E'\n{3,}', E'\n\n', 'g');
  v_to text := nullif(p->>'to', '');
  v_room text := 'lobby';
  v_now bigint := tl_now();
  last_time bigint;
  new_id bigint;
begin
  if me is null then raise exception 'join the leaderboards to chat'; end if;
  if v_text = '' then raise exception 'empty message'; end if;
  if v_to is not null then
    if v_to !~ '^[a-z0-9]{16,40}$' or v_to = me then raise exception 'bad recipient'; end if;
    if not exists (select 1 from players where id = v_to) then raise exception 'no such player'; end if;
    if exists (select 1 from blocks where player = v_to and blocked = me) then
      raise exception 'this player doesn''t accept your messages';
    end if;
    v_room := tl_dm_room(me, v_to);
  end if;
  select time into last_time from messages where sender = me order by id desc limit 1;
  if last_time is not null and v_now - last_time < 2 then raise exception 'slow down a little'; end if;
  insert into messages (room, sender, text, time) values (v_room, me, v_text, v_now) returning id into new_id;
  -- chats only last an hour: older messages (lobby and private) are deleted
  delete from messages where time < v_now - 3600;
  return jsonb_build_object('ok', true, 'id', new_id);
end $$;

create or replace function tl_chat_room(p_room text, p_after bigint, p_viewer text) returns jsonb language sql stable
set search_path = public, extensions as $$
  select jsonb_build_object('room', p_room, 'messages', coalesce(jsonb_agg(to_jsonb(r) order by r.id), '[]'::jsonb)) from (
    select m.id, m.sender, m.text, m.time, pl.name, pl.level, coalesce(pl.profile->>'icon', '') as icon, pl.avatar_ver as avatar
    from messages m join players pl on pl.id = m.sender
    where m.room = p_room and m.id > coalesce(p_after, 0) and m.time > tl_now() - 3600 and not pl.banned
      and (p_viewer is null or m.sender not in (select blocked from blocks where player = p_viewer))
    order by m.id desc limit 100) r
$$;

create or replace function tl_chat_lobby(p_after bigint default 0, p_id text default null, p_secret text default null)
returns jsonb language sql stable security definer
set search_path = public, extensions as $$
  select tl_chat_room('lobby', p_after, tl_auth(p_id, p_secret))
$$;

create or replace function tl_chat_dm(p_id text, p_secret text, p_with text, p_after bigint default 0)
returns jsonb language plpgsql stable security definer
set search_path = public, extensions as $$
declare me text := tl_auth(p_id, p_secret);
begin
  if me is null then raise exception 'not signed in'; end if;
  return tl_chat_room(tl_dm_room(me, p_with), p_after, me);
end $$;

create or replace function tl_chat_inbox(p_id text, p_secret text) returns jsonb language plpgsql stable security definer
set search_path = public, extensions as $$
declare me text := tl_auth(p_id, p_secret);
begin
  if me is null then raise exception 'not signed in'; end if;
  return jsonb_build_object('conversations', coalesce((
    select jsonb_agg(to_jsonb(c) order by c.last_id desc) from (
      select other.id, other.name, other.level, coalesce(other.profile->>'icon', '') as icon, other.avatar_ver as avatar,
             exists (select 1 from blocks b where b.player = me and b.blocked = other.id) as blocked,
             lm.id as last_id,
             jsonb_build_object('id', lm.id, 'sender', lm.sender, 'text', lm.text, 'time', lm.time) as last
      from (select room, max(id) as last_id from messages
            where (room like 'dm:' || me || ':%' or room like 'dm:%:' || me) and time > tl_now() - 3600 group by room
            order by last_id desc limit 50) r
      join messages lm on lm.id = r.last_id
      join players other on other.id = case when split_part(r.room, ':', 2) = me
                                            then split_part(r.room, ':', 3) else split_part(r.room, ':', 2) end
    ) c), '[]'::jsonb));
end $$;

create or replace function tl_chat_block(p_id text, p_secret text, p_who text, p_block boolean) returns jsonb
language plpgsql security definer
set search_path = public, extensions as $$
declare me text := tl_auth(p_id, p_secret);
begin
  if me is null then raise exception 'not signed in'; end if;
  if p_who is null or p_who = me or not exists (select 1 from players where id = p_who) then raise exception 'bad player'; end if;
  if p_block then insert into blocks (player, blocked) values (me, p_who) on conflict do nothing;
  else delete from blocks where player = me and blocked = p_who; end if;
  return jsonb_build_object('ok', true);
end $$;

-- ---------- profile pictures ----------

-- p_data: base64 of a JPEG or PNG, at most 64 KB; empty removes the picture
create or replace function tl_set_avatar(p_id text, p_secret text, p_data text) returns jsonb
language plpgsql security definer
set search_path = public, extensions as $$
declare
  me text := tl_auth(p_id, p_secret);
  img bytea;
  v_now bigint := tl_now();
begin
  if me is null then raise exception 'join the leaderboards first'; end if;
  if coalesce(p_data, '') = '' then
    delete from avatars where player = me;
    update players set avatar_ver = 0 where id = me;
    return jsonb_build_object('ok', true, 'avatar', 0);
  end if;
  if length(p_data) > 90000 then raise exception 'picture too big'; end if;
  begin
    img := decode(p_data, 'base64');
  exception when others then
    raise exception 'not a picture';
  end;
  if length(img) > 65536 then raise exception 'picture too big'; end if;
  -- only JPEG (FF D8 FF) or PNG (89 50 4E 47)
  if not (substring(img from 1 for 3) = '\xffd8ff'::bytea or substring(img from 1 for 4) = '\x89504e47'::bytea) then
    raise exception 'only JPEG or PNG pictures';
  end if;
  insert into avatars (player, data, updated) values (me, img, v_now)
  on conflict (player) do update set data = excluded.data, updated = excluded.updated;
  update players set avatar_ver = v_now where id = me;
  return jsonb_build_object('ok', true, 'avatar', v_now);
end $$;

create or replace function tl_avatar(p_id text) returns jsonb language sql stable security definer
set search_path = public, extensions as $$
  select coalesce((select jsonb_build_object('ver', updated, 'data', encode(data, 'base64'))
                   from avatars where player = p_id), jsonb_build_object('ver', 0, 'data', ''))
$$;

-- ---------- Steam verification ----------
-- The steps that talk to Steam run in the server function supabase/functions/trophyline, which keeps
-- the Steam Web API key. The tl_srv_* functions can only be called by that function (service role).
--
-- For the server owner (Table Editor -> players):
--   banned  = true   removes a cheater from everything
--   flags   = text   the automatic cheat check paused this player (not ranked); look at their Steam profile
--   approve = true   clears those flags (the player is ranked again until something new looks wrong)
-- Table Editor -> reports lists what players reported.

create or replace function tl_level(p_xp bigint) returns int language plpgsql immutable as $$
declare
  lvl int := 1;
  left_xp bigint := coalesce(p_xp, 0);
begin
  while left_xp >= 200 + 75 * (lvl - 1) and lvl < 100000 loop
    left_xp := left_xp - (200 + 75 * (lvl - 1));
    lvl := lvl + 1;
  end loop;
  return lvl;
end $$;

create or replace function tl_rank(p_level int) returns text language sql immutable as $$
  select case when p_level >= 60 then 'Legend' when p_level >= 45 then 'Master' when p_level >= 30 then 'Elite'
              when p_level >= 20 then 'Veteran' when p_level >= 10 then 'Hunter' when p_level >= 5 then 'Explorer'
              else 'Rookie' end
$$;

-- XP from achievements, first achievements in a game and 100%s dated inside a season
create or replace function tl_season_unlock_xp(p_player text, s jsonb) returns bigint language sql stable as $$
  select
    coalesce((select sum(xp) from steam_unlocks
              where player = p_player and time >= (s->>'start')::bigint and time < (s->>'end')::bigint), 0)
    + 50 * (select count(*) from (select min(time) as t from steam_unlocks where player = p_player and time > 0 group by appid) f
            where f.t >= (s->>'start')::bigint and f.t < (s->>'end')::bigint)
    + 500 * (select count(*) from (
               select g.appid, (select max(u.time) from steam_unlocks u where u.player = p_player and u.appid = g.appid) as t
               from steam_games g where g.player = p_player and g.total > 0 and g.achieved >= g.total) d
             where d.t >= (s->>'start')::bigint and d.t < (s->>'end')::bigint)
$$;

-- step 1 of a check: who is asking, which Steam account, and what the server already knows
create or replace function tl_srv_begin(p_id text, p_secret text) returns jsonb language plpgsql security definer
set search_path = public, extensions as $$
declare
  me text := tl_auth(p_id, p_secret);
  pl players;
  v_now bigint := tl_now();
begin
  if me is null then return jsonb_build_object('error', 'Log in to your Trophyline account again'); end if;
  select * into pl from players where id = me;
  if pl.steam_id is null then return jsonb_build_object('error', 'Link your Steam account first'); end if;
  -- also between the parts of a big check (v_next is then a few seconds away), so nobody can hammer Steam
  if v_now < pl.v_next then
    return jsonb_build_object('error', 'Checked a moment ago. Try again in ' || ceil((pl.v_next - v_now) / 60.0) || ' min',
                              'wait', pl.v_next - v_now);
  end if;
  update players set v_next = v_now + 120 where id = me; -- one check at a time
  return jsonb_build_object('ok', true, 'id', me, 'steam_id', pl.steam_id,
    'games', coalesce((select jsonb_object_agg(appid::text, jsonb_build_array(seen_minutes, checked))
                       from steam_games where player = me), '{}'::jsonb));
end $$;

-- links a Steam account after the server function confirmed the sign-in with Steam
create or replace function tl_srv_link(p_id text, p_secret text, p_steam_id text, p_nonce text) returns jsonb
language plpgsql security definer
set search_path = public, extensions as $$
declare
  me text := tl_auth(p_id, p_secret);
  v_now bigint := tl_now();
  old text;
begin
  if me is null then return jsonb_build_object('error', 'Log in to your Trophyline account again'); end if;
  if coalesce(p_steam_id, '') !~ '^7656119[0-9]{10}$' then return jsonb_build_object('error', 'That is not a Steam account'); end if;
  delete from openid_nonces where used < v_now - 86400;
  insert into openid_nonces (nonce, used) values (left(coalesce(p_nonce, ''), 300), v_now) on conflict do nothing;
  if not found then return jsonb_build_object('error', 'This sign-in was already used. Try again.'); end if;
  if exists (select 1 from players where steam_id = p_steam_id and id <> me) then
    return jsonb_build_object('error', 'This Steam account is already linked to another Trophyline account');
  end if;
  select steam_id into old from players where id = me;
  if old is distinct from p_steam_id then
    -- a different Steam account: everything verified so far belonged to the old one
    delete from steam_games where player = me;
    delete from steam_unlocks where player = me;
    delete from season_scores where player = me;
    delete from season_hours where player = me;
    update players set steam_id = p_steam_id, verified_at = 0, v_state = '', v_partial = false, v_next = 0,
                       flags = '', approve = false, cleared_at = 0
    where id = me;
  end if;
  delete from steam_codes where player = me;
  return jsonb_build_object('ok', true, 'steam_id', p_steam_id);
end $$;

-- step 2 of a check: stores what Steam said, adds it up and runs the cheat checks
-- p = {"private": true} or {"owned": [{"a": appid, "m": minutes}], "games": [{"a": appid, "total": n,
--      "u": [[name, unlock time, xp], ...]}], "partial": true when more games are still to come}
create or replace function tl_srv_apply(p_id text, p jsonb) returns jsonb language plpgsql security definer
set search_path = public, extensions as $$
declare
  pl players;
  me text;
  v_now bigint := tl_now();
  is_partial boolean := coalesce((p->>'partial')::boolean, false);
  cur jsonb := tl_season();
  prev jsonb := tl_season_prev(tl_season());
  g jsonb;
  a int;
  v_cleared bigint;
  v_flags text := '';
  n bigint;
  t text;
  v_xp bigint; v_unlocked bigint; v_ach bigint; v_games int; v_perfect int; v_over75 int; v_hours int;
  base_cur int; base_prev int; sxp bigint;
  lvl int;
begin
  select * into pl from players where id = p_id;
  if pl.id is null or pl.steam_id is null then return jsonb_build_object('error', 'Link your Steam account first'); end if;
  me := pl.id;

  if coalesce((p->>'private')::boolean, false) then
    update players set v_state = 'private', v_partial = false, v_next = v_now + 300 where id = me;
    return jsonb_build_object('ok', true, 'state', 'private');
  end if;

  -- the owner approved what was flagged: everything unlocked so far is fine
  v_cleared := pl.cleared_at;
  if pl.approve then
    v_cleared := greatest(v_cleared, (select coalesce(max(time), 0) from steam_unlocks where player = me));
  end if;

  -- playtime of every game the account owns
  insert into steam_games (player, appid, minutes, name)
  select distinct on ((e->>'a')::int) me, (e->>'a')::int, tl_int(e->>'m', 100000000)::int, left(tl_clean(e->>'n', 120), 120)
  from jsonb_array_elements(coalesce(p->'owned', '[]'::jsonb)) e
  where (e->>'a') ~ '^[0-9]{1,9}$'
  on conflict (player, appid) do update set minutes = excluded.minutes,
    name = case when excluded.name <> '' then excluded.name else steam_games.name end;

  -- achievements of the games read this time
  for g in select value from jsonb_array_elements(coalesce(p->'games', '[]'::jsonb)) loop
    continue when coalesce(g->>'a', '') !~ '^[0-9]{1,9}$';
    a := (g->>'a')::int;
    if coalesce((g->>'skip')::boolean, false) then
      -- Steam didn't answer for this game: keep what we know and try it again in about a day,
      -- so it doesn't block the rest of a big library forever
      update steam_games set seen_minutes = minutes, checked = greatest(checked, v_now - 6 * 86400)
      where player = me and appid = a;
      continue;
    end if;
    delete from steam_unlocks where player = me and appid = a;
    insert into steam_unlocks (player, appid, name, time, xp)
    select me, a, left(e->>0, 200), tl_int(e->>1, 4000000000), least(tl_int(e->>2, 150), 150)::int
    from jsonb_array_elements(coalesce(g->'u', '[]'::jsonb)) e
    where coalesce(e->>0, '') <> ''
    on conflict do nothing;
    update steam_games set achieved = (select count(*) from steam_unlocks where player = me and appid = a),
                           total = tl_int(g->>'total', 100000)::int, seen_minutes = minutes, checked = v_now
    where player = me and appid = a;
  end loop;

  -- games that aren't owned any more (refunded, or a different account)
  if jsonb_typeof(p->'owned') = 'array' then
    delete from steam_games sg where sg.player = me and not exists
      (select 1 from jsonb_array_elements(p->'owned') e where (e->>'a') ~ '^[0-9]{1,9}$' and (e->>'a')::int = sg.appid);
    delete from steam_unlocks su where su.player = me and not exists
      (select 1 from steam_games sg where sg.player = me and sg.appid = su.appid);
  end if;

  -- the totals, worked out the same way as in the app (tracked sessions don't count here)
  select coalesce(sum(xp), 0), count(*) into v_ach, v_unlocked from steam_unlocks where player = me;
  select count(*) filter (where achieved > 0),
         count(*) filter (where total > 0 and achieved >= total),
         count(*) filter (where total > 0 and achieved * 4 >= total * 3),
         (coalesce(sum(minutes), 0) / 60)::int
  into v_games, v_perfect, v_over75, v_hours
  from steam_games where player = me;
  v_xp := v_ach + 50 * v_games + 500 * v_perfect + 10 * v_hours;
  lvl := tl_level(v_xp);

  -- cheat checks (only unlocks after the last approval count)
  select string_agg(appid::text, ', ') into t from (
    select distinct u.appid from steam_unlocks u join steam_games sg on sg.player = u.player and sg.appid = u.appid
    where u.player = me and u.time > v_cleared and u.time > 0 and sg.total >= 10
    group by u.appid, u.time, sg.total having count(*) * 5 >= sg.total * 4 limit 10) x;
  if t is not null then v_flags := v_flags || 'Almost a whole game unlocked in one second (app ' || t || '). '; end if;
  select coalesce(max(c), 0) into n from (
    select count(*) as c from steam_unlocks where player = me and time > v_cleared and time > 0 group by time) x;
  if n >= 30 then v_flags := v_flags || n || ' achievements unlocked in the same second. '; end if;
  select coalesce(max(c), 0) into n from (
    select count(distinct appid) as c from steam_unlocks where player = me and time > v_cleared and time > 0 group by time / 60) x;
  if n >= 3 then v_flags := v_flags || 'Achievements in ' || n || ' different games within one minute. '; end if;
  -- (skipped when Steam shows no playtime at all: "keep my total playtime private" hides every game's hours)
  select string_agg(appid::text, ', ') into t from (
    select sg.appid from steam_games sg where sg.player = me and sg.minutes = 0
      and exists (select 1 from steam_games h where h.player = me and h.minutes > 0)
      and (select count(*) from steam_unlocks u where u.player = me and u.appid = sg.appid
           and u.time > greatest(v_cleared, 1325376000)) >= 5
    limit 10) x;
  if t is not null then v_flags := v_flags || 'Achievements in games with no playtime (app ' || t || '). '; end if;

  -- season XP: hours count from the first check of each season
  insert into season_hours (season, player, hours)
  values (cur->>'id', me, case when pl.verified_at > 0 then pl.hours else v_hours end)
  on conflict do nothing;
  select hours into base_cur from season_hours where season = cur->>'id' and player = me;
  sxp := tl_season_unlock_xp(me, cur) + 10 * greatest(v_hours - base_cur, 0);
  insert into season_scores (season, player, xp, updated) values (cur->>'id', me, sxp, v_now)
  on conflict (season, player) do update set xp = excluded.xp, updated = excluded.updated;
  -- the season that just ended can still change during the first week of the new one
  if (prev->>'number')::int >= 1 and v_now - (cur->>'start')::bigint < 7 * 86400 then
    select hours into base_prev from season_hours where season = prev->>'id' and player = me;
    insert into season_scores (season, player, xp, updated)
    values (prev->>'id', me, tl_season_unlock_xp(me, prev) + 10 * greatest(base_cur - coalesce(base_prev, base_cur), 0), v_now)
    on conflict (season, player) do update set xp = excluded.xp, updated = excluded.updated;
  end if;

  update players set
    xp = v_xp, level = lvl, rank = tl_rank(lvl), perfect = v_perfect, over75 = v_over75,
    unlocked = v_unlocked, games = v_games, hours = v_hours,
    verified_at = case when is_partial then verified_at else v_now end,
    v_state = case when is_partial then 'checking' else 'ok' end,
    v_partial = is_partial, v_next = case when is_partial then v_now + 3 else v_now + 600 end,
    flags = v_flags, approve = false, cleared_at = v_cleared
  where id = me;

  -- best games on the profile, from Steam's numbers (latest unlocks are added by tl_srv_profile)
  update players set profile = profile || jsonb_build_object('fromSteam', '1', 'topGames', coalesce((
    select jsonb_agg(jsonb_build_object('appId', appid::text, 'name', case when name <> '' then name else 'App ' || appid end,
                                        'unlocked', achieved, 'total', total)
                     order by (achieved >= total) desc, (achieved::real / total) desc, achieved desc)
    from (select * from steam_games where player = me and total > 0 and achieved > 0
          order by (achieved >= total) desc, (achieved::real / total) desc, achieved desc limit 12) t), '[]'::jsonb))
  where id = me;

  return jsonb_build_object('ok', true, 'state', case when is_partial then 'checking' else 'ok' end, 'partial', is_partial,
    'xp', v_xp, 'level', lvl, 'perfect', v_perfect, 'over75', v_over75, 'unlocked', v_unlocked,
    'games', v_games, 'hours', v_hours, 'season_xp', sxp, 'flagged', v_flags <> '',
    -- the newest unlocks: the server function looks up their names and icons, then calls tl_srv_profile
    'latest', coalesce((select jsonb_agg(jsonb_build_array(appid, name, time, xp) order by time desc)
                        from (select appid, name, time, xp from steam_unlocks where player = me and time > 0
                              order by time desc limit 10) l), '[]'::jsonb));
end $$;

-- the latest unlocks on the profile, with names and icons the server function looked up on Steam.
-- Only unlocks that Steam reported for this player are accepted.
create or replace function tl_srv_profile(p_id text, p_recent jsonb) returns jsonb language plpgsql security definer
set search_path = public, extensions as $$
declare
  out jsonb;
begin
  select coalesce(jsonb_agg(jsonb_build_object(
           'appId', u.appid::text, 'game', coalesce(nullif(g.name, ''), 'App ' || u.appid),
           'name', coalesce(nullif(tl_clean(e->>'name', 120), ''), u.name), 'time', u.time,
           'rarity', case when e->>'rarity' ~ '^\d+(\.\d+)?$' then round((e->>'rarity')::numeric, 1) end,
           'icon', left(regexp_replace(coalesce(e->>'icon', ''), '[^A-Za-z0-9._-]', '', 'g'), 120))
         order by u.time desc), '[]'::jsonb)
  into out
  from jsonb_array_elements(case when jsonb_typeof(p_recent) = 'array' then p_recent else '[]'::jsonb end) e
  join steam_unlocks u on u.player = p_id and u.appid::text = e->>'appId' and u.name = e->>'api'
  left join steam_games g on g.player = u.player and g.appid = u.appid;
  update players set profile = profile || jsonb_build_object('fromSteam', '1', 'recent', coalesce(out, '[]'::jsonb)) where id = p_id;
  return jsonb_build_object('ok', true, 'recent', jsonb_array_length(coalesce(out, '[]'::jsonb)));
end $$;

-- ---------- linking with a name code ----------

-- the app asks for a code for the Steam account it shows; the player puts it in their Steam profile name
create or replace function tl_code_begin(p_id text, p_secret text, p_steam_id text) returns jsonb
language plpgsql security definer
set search_path = public, extensions as $$
declare
  me text := tl_auth(p_id, p_secret);
  v_code text := 'TL-' || upper(substr(tl_token(), 1, 6));
begin
  if me is null then return jsonb_build_object('error', 'Log in to your Trophyline account again'); end if;
  if coalesce(p_steam_id, '') !~ '^7656119[0-9]{10}$' then return jsonb_build_object('error', 'Pick a Steam account first'); end if;
  if exists (select 1 from players where steam_id = p_steam_id and id <> me) then
    return jsonb_build_object('error', 'This Steam account is already linked to another Trophyline account');
  end if;
  insert into steam_codes (player, steam_id, code, created) values (me, p_steam_id, v_code, tl_now())
  on conflict (player) do update set steam_id = excluded.steam_id, code = excluded.code, created = excluded.created;
  return jsonb_build_object('ok', true, 'code', v_code, 'steam_id', p_steam_id, 'expires', tl_now() + 1200);
end $$;

-- for the server function: the code it should look for (codes work for 20 minutes)
create or replace function tl_srv_code(p_id text, p_secret text) returns jsonb language plpgsql security definer
set search_path = public, extensions as $$
declare
  me text := tl_auth(p_id, p_secret);
  c steam_codes;
begin
  if me is null then return jsonb_build_object('error', 'Log in to your Trophyline account again'); end if;
  delete from steam_codes where created < tl_now() - 1200;
  select * into c from steam_codes where player = me;
  if c.player is null then return jsonb_build_object('error', 'The code ran out. Get a new one.'); end if;
  return jsonb_build_object('ok', true, 'steam_id', c.steam_id, 'code', c.code);
end $$;

-- coins earned up to a level (50 + 10 x level for every level from 2 up), as in Shop.cs
create or replace function tl_coins(p_level int) returns bigint language sql immutable as $$
  select case when p_level < 2 then 0
              else 50 * (p_level - 1)::bigint + 10 * ((p_level::bigint * (p_level + 1)) / 2 - 1) end
$$;

-- a shop icon only shows if the Steam-checked level could have bought it
create or replace function tl_icon_ok(p_icon text, p_level int) returns boolean language sql immutable as $$
  select exists (select 1 from (values
      ('controller', 300, 0), ('star', 300, 0), ('cat', 300, 0), ('pizza', 300, 0), ('cactus', 300, 0), ('dice', 300, 0),
      ('ghost', 800, 0), ('alien', 800, 0), ('fox', 800, 0), ('rocket', 800, 0), ('ninja', 800, 0), ('target', 800, 0),
      ('fire', 1600, 10), ('skull', 1600, 10), ('robot', 1600, 10), ('wolf', 1600, 15), ('swords', 1600, 15), ('lightning', 1600, 15),
      ('dragon', 3000, 20), ('crown', 3000, 25), ('diamond', 3000, 30), ('trophy', 3000, 40)) i(id, price, min_level)
    where i.id = p_icon and coalesce(p_level, 1) >= i.min_level and tl_coins(coalesce(p_level, 1)) >= i.price)
$$;

-- what the app shows in Settings
create or replace function tl_verify_status(p_id text, p_secret text) returns jsonb language plpgsql stable security definer
set search_path = public, extensions as $$
declare
  me text := tl_auth(p_id, p_secret);
  pl players;
begin
  if me is null then return jsonb_build_object('error', 'Log in to your Trophyline account again'); end if;
  select * into pl from players where id = me;
  return jsonb_build_object('linked', pl.steam_id is not null, 'steam_id', pl.steam_id, 'state', pl.v_state,
    'verified_at', pl.verified_at, 'ranked', pl.ranked, 'flagged', pl.flags <> '' and not pl.approve,
    'partial', pl.v_partial, 'xp', pl.xp, 'level', pl.level, 'perfect', pl.perfect, 'over75', pl.over75,
    'unlocked', pl.unlocked, 'games', pl.games, 'hours', pl.hours,
    'season_xp', coalesce((select xp from season_scores where season = tl_season()->>'id' and player = me), 0));
end $$;

-- ---------- logging in with Steam ----------
-- A Trophyline account is a Steam account. The server function proves it's yours, either with
-- Steam's own login page or (on networks that block that page) a short code you put in your
-- Steam profile name for a minute. Then tl_srv_steam_login opens a session.

create table if not exists login_codes (
  id       text primary key,                -- random, given to the app
  steam_id text not null,
  code     text not null,                   -- what goes in the Steam profile name, e.g. TL4F9A2
  created  bigint not null
);
alter table login_codes enable row level security;
revoke all on login_codes from anon, authenticated;

create or replace function tl_srv_steam_login(p_steam_id text, p_name text, p_nonce text,
                                              p_id text default null, p_secret text default null) returns jsonb
language plpgsql security definer
set search_path = public, extensions as $$
declare
  v_now bigint := tl_now();
  pl players;
  here text := tl_auth(p_id, p_secret);
  v_id text;
begin
  if coalesce(p_steam_id, '') !~ '^7656119[0-9]{10}$' then return jsonb_build_object('error', 'That is not a Steam account'); end if;
  delete from openid_nonces where used < v_now - 86400;
  insert into openid_nonces (nonce, used) values (left(coalesce(p_nonce, ''), 300), v_now) on conflict do nothing;
  if not found then return jsonb_build_object('error', 'This sign-in was already used. Try again.'); end if;

  -- this Steam account already has a Trophyline profile: log in to it
  select * into pl from players where steam_id = p_steam_id;
  if pl.id is not null then
    if pl.banned then return jsonb_build_object('error', 'This account was banned'); end if;
    update players set fail_count = 0 where id = pl.id;
    return tl_session(pl.id) || jsonb_build_object('steam_id', p_steam_id);
  end if;

  -- the profile on this computer, from before Steam login, becomes this Steam account's profile
  if here is not null and (select steam_id from players where id = here) is null then
    delete from steam_games where player = here;
    delete from steam_unlocks where player = here;
    delete from season_scores where player = here;
    delete from season_hours where player = here;
    update players set steam_id = p_steam_id, verified_at = 0, v_state = '', v_partial = false, v_next = 0,
                       flags = '', approve = false, cleared_at = 0
    where id = here;
    return tl_session(here) || jsonb_build_object('steam_id', p_steam_id);
  end if;

  -- a new profile
  v_id := replace(gen_random_uuid()::text, '-', '');
  insert into players (id, secret_hash, name, steam_id, created, updated)
  values (v_id, '', coalesce(nullif(tl_clean(p_name, 24), ''), 'Player'), p_steam_id, v_now, v_now - 60);
  return tl_session(v_id) || jsonb_build_object('steam_id', p_steam_id, 'new', true);
end $$;

-- a name code for a Steam account (valid 15 minutes)
create or replace function tl_srv_code_start(p_steam_id text) returns jsonb language plpgsql security definer
set search_path = public, extensions as $$
declare
  v_now bigint := tl_now();
  v_id text := tl_token();
  v_code text := 'TL' || upper(substr(replace(gen_random_uuid()::text, '-', ''), 1, 5));
begin
  if coalesce(p_steam_id, '') !~ '^7656119[0-9]{10}$' then return jsonb_build_object('error', 'That is not a Steam account'); end if;
  delete from login_codes where created < v_now - 900;
  if (select count(*) from login_codes where steam_id = p_steam_id) >= 5 then
    return jsonb_build_object('error', 'Too many codes for this account. Wait 15 minutes and try again.');
  end if;
  if (select count(*) from login_codes) >= 5000 then
    return jsonb_build_object('error', 'Too many people are logging in right now. Try again in a few minutes.');
  end if;
  insert into login_codes (id, steam_id, code, created) values (v_id, p_steam_id, v_code, v_now);
  return jsonb_build_object('ok', true, 'challenge', v_id, 'code', v_code, 'expires', v_now + 900);
end $$;

create or replace function tl_srv_code_get(p_challenge text) returns jsonb language plpgsql security definer
set search_path = public, extensions as $$
declare
  c login_codes;
begin
  select * into c from login_codes where id = p_challenge and created >= tl_now() - 900;
  if c.id is null then return jsonb_build_object('error', 'This code expired. Start again.'); end if;
  return jsonb_build_object('ok', true, 'steam_id', c.steam_id, 'code', c.code);
end $$;

create or replace function tl_srv_code_done(p_challenge text) returns jsonb language sql security definer
set search_path = public, extensions as $$
  delete from login_codes where id = p_challenge returning jsonb_build_object('ok', true)
$$;

-- ---------- reports ----------

create or replace function tl_report(p_id text, p_secret text, p_target text, p_reason text) returns jsonb
language plpgsql security definer
set search_path = public, extensions as $$
declare
  me text := tl_auth(p_id, p_secret);
  v_reason text := tl_clean(p_reason, 300);
  v_now bigint := tl_now();
begin
  if me is null then return jsonb_build_object('error', 'Log in to report someone'); end if;
  if p_target is null or p_target = me or not exists (select 1 from players where id = p_target) then
    return jsonb_build_object('error', 'You can''t report this player');
  end if;
  if v_reason = '' then return jsonb_build_object('error', 'Say in a few words what looks wrong'); end if;
  if (select count(*) from reports where reporter = me and time > v_now - 86400) >= 10 then
    return jsonb_build_object('error', 'You''ve sent a lot of reports today. Try again tomorrow');
  end if;
  insert into reports (reporter, target, reason, time) values (me, p_target, v_reason, v_now)
  on conflict (reporter, target) do update set reason = excluded.reason, time = excluded.time;
  return jsonb_build_object('ok', true);
end $$;

-- ---------- who may call what ----------
-- Only the tl_* entry points are callable with the public app key.

revoke execute on all functions in schema public from public, anon, authenticated;
grant execute on function
  tl_status(), tl_upsert(jsonb), tl_delete(text, text), tl_player(text),
  tl_leaderboard(text, text, text, int), tl_seasons(),
  tl_chat_send(jsonb), tl_chat_lobby(bigint, text, text), tl_chat_dm(text, text, text, bigint),
  tl_chat_inbox(text, text), tl_chat_block(text, text, text, boolean),
  tl_set_avatar(text, text, text), tl_avatar(text),
  tl_register(text, text), tl_login(text, text), tl_claim(text, text, text, text),
  tl_logout(text, text), tl_change_password(text, text, text, text),
  tl_verify_status(text, text), tl_report(text, text, text, text), tl_code_begin(text, text, text)
to anon, authenticated;
-- only the server function (service role) may store Steam results
grant execute on function tl_srv_begin(text, text), tl_srv_link(text, text, text, text), tl_srv_apply(text, jsonb),
  tl_srv_profile(text, jsonb), tl_srv_code(text, text),
  tl_srv_steam_login(text, text, text, text, text), tl_srv_code_start(text), tl_srv_code_get(text), tl_srv_code_done(text)
to service_role;

-- let the API see new or changed functions right away (otherwise: "Could not find the function ... in the schema cache")
notify pgrst, 'reload schema';
