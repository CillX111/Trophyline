-- Trophyline: chat messages only last one hour (run once in the SQL Editor)
create index if not exists messages_time on messages (time);

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

-- remove what's already older than an hour
delete from messages where time < extract(epoch from now())::bigint - 3600;
