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

-- the player whose secret this is, or null
create or replace function tl_auth(p_id text, p_secret text) returns text language sql stable
set search_path = public, extensions as $$
  select id from players where id = p_id and secret_hash = tl_hash(coalesce(p_secret, ''))
$$;

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
  select jsonb_build_object('ok', true, 'season', tl_season(), 'players', (select count(*) from players))
$$;

create or replace function tl_upsert(p jsonb) returns jsonb language plpgsql security definer
set search_path = public, extensions as $$
declare
  v_id text := p->>'id';
  v_secret text := p->>'secret';
  v_name text := tl_clean(p->>'name', 24);
  v_now bigint := tl_now();
  v_hash text;
  v_old_hash text;
  v_old_updated bigint;
  v_profile jsonb := coalesce(p->'profile', '{}'::jsonb);
  cur jsonb := tl_season();
  prev jsonb := tl_season_prev(tl_season());
  s jsonb;
  info jsonb;
begin
  if v_id is null or v_id !~ '^[a-z0-9]{16,40}$' then raise exception 'bad id'; end if;
  if v_secret is null or length(v_secret) < 24 or length(v_secret) > 128 then raise exception 'bad secret'; end if;
  if v_name = '' then raise exception 'name required'; end if;
  if length(v_profile::text) > 24000 then raise exception 'profile too big'; end if;
  v_hash := tl_hash(v_secret);

  select secret_hash, updated into v_old_hash, v_old_updated from players where id = v_id;
  if v_old_hash is not null and v_old_hash <> v_hash then raise exception 'not your profile'; end if;
  if v_old_hash is not null and v_now - v_old_updated < 10 then raise exception 'too often'; end if;

  insert into players (id, secret_hash, name, level, rank, xp, perfect, over75, unlocked, games, hours, profile, created, updated)
  values (v_id, v_hash, v_name,
          tl_int(p->>'level', 10000), coalesce(nullif(tl_clean(p->>'rank', 24), ''), 'Rookie'),
          tl_int(p->>'xp', 50000000), tl_int(p->>'perfect', 100000), tl_int(p->>'over75', 100000),
          tl_int(p->>'unlocked', 10000000), tl_int(p->>'games', 100000), tl_int(p->>'hours', 10000000),
          v_profile, v_now, v_now)
  on conflict (id) do update set
    name = excluded.name, level = excluded.level, rank = excluded.rank, xp = excluded.xp,
    perfect = excluded.perfect, over75 = excluded.over75, unlocked = excluded.unlocked,
    games = excluded.games, hours = excluded.hours, profile = excluded.profile, updated = excluded.updated;

  -- season XP: the current season, and the previous one during the first week of a new season
  for s in select value from jsonb_array_elements(case when jsonb_typeof(p->'seasons') = 'array' then p->'seasons' else '[]'::jsonb end) limit 2 loop
    info := tl_season_parse(s->>'id');
    continue when info is null;
    continue when not (info->>'id' = cur->>'id'
                       or (info->>'id' = prev->>'id' and v_now - (cur->>'start')::bigint < 7 * 86400));
    insert into season_scores (season, player, xp, updated)
    values (info->>'id', v_id, tl_int(s->>'xp', 10000000), v_now)
    on conflict (season, player) do update set xp = excluded.xp, updated = excluded.updated;
  end loop;

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

create or replace function tl_player(p_id text) returns jsonb language plpgsql stable security definer
set search_path = public, extensions as $$
declare
  pl players;
  cur jsonb := tl_season();
  sxp bigint;
begin
  select * into pl from players where id = p_id;
  if pl.id is null then raise exception 'no such player'; end if;
  select xp into sxp from season_scores where season = cur->>'id' and player = p_id;
  return jsonb_build_object(
    'id', pl.id, 'name', pl.name, 'level', pl.level, 'rank', pl.rank, 'xp', pl.xp, 'perfect', pl.perfect,
    'over75', pl.over75, 'unlocked', pl.unlocked, 'games', pl.games, 'hours', pl.hours, 'profile', pl.profile,
    'created', pl.created, 'updated', pl.updated, 'avatar', pl.avatar_ver,
    'season', cur || jsonb_build_object('xp', coalesce(sxp, 0)),
    'places', jsonb_build_object(
      'xp', (select count(*) + 1 from players where xp > pl.xp),
      'perfect', (select count(*) + 1 from players where perfect > pl.perfect),
      'over75', (select count(*) + 1 from players where over75 > pl.over75)));
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
      select row_number() over (order by ss.xp desc, pl.xp desc) as pos, pl.id, pl.name, pl.level, pl.rank,
             coalesce(pl.profile->>'icon', '') as icon, pl.avatar_ver as avatar, ss.xp as value
      from season_scores ss join players pl on pl.id = ss.player
      where ss.season = s->>'id' and ss.xp > 0
      order by ss.xp desc, pl.xp desc limit lim) r;
    select xp into mv from season_scores where season = s->>'id' and player = p_me;
    if mv is not null then
      v_me := jsonb_build_object('pos', (select count(*) + 1 from season_scores where season = s->>'id' and xp > mv), 'value', mv);
    end if;
  elsif p_board in ('xp', 'perfect', 'over75') then
    s := tl_season();
    execute format(
      'select coalesce(jsonb_agg(to_jsonb(r) order by r.pos), ''[]''::jsonb) from (
         select row_number() over (order by %1$I desc, xp desc) as pos, id, name, level, rank,
                coalesce(profile->>''icon'', '''') as icon, avatar_ver as avatar, %1$I::bigint as value
         from players where %1$I > 0 order by %1$I desc, xp desc limit $1) r', p_board)
      into v_rows using lim;
    if p_me is not null then
      execute format('select %I::bigint from players where id = $1', p_board) into mv using p_me;
      if mv is not null then
        execute format('select count(*) + 1 from players where %I > $1', p_board) into v_pos using mv;
        v_me := jsonb_build_object('pos', v_pos, 'value', mv);
      end if;
    end if;
  else
    raise exception 'unknown board';
  end if;
  return jsonb_build_object('board', p_board, 'season', s, 'rows', v_rows, 'me', v_me,
                            'players', (select count(*) from players));
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
      where ss.season = s->>'id' and ss.xp > 0 order by ss.xp desc limit 3) r;
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
    where m.room = p_room and m.id > coalesce(p_after, 0) and m.time > tl_now() - 3600
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

-- ---------- who may call what ----------
-- Only the tl_* entry points are callable with the public app key.

revoke execute on all functions in schema public from public, anon, authenticated;
grant execute on function
  tl_status(), tl_upsert(jsonb), tl_delete(text, text), tl_player(text),
  tl_leaderboard(text, text, text, int), tl_seasons(),
  tl_chat_send(jsonb), tl_chat_lobby(bigint, text, text), tl_chat_dm(text, text, text, bigint),
  tl_chat_inbox(text, text), tl_chat_block(text, text, text, boolean),
  tl_set_avatar(text, text, text), tl_avatar(text)
to anon, authenticated;
