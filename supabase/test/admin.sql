-- TEST ONLY (never run this on the real server): lets the test script act as the server owner.
create or replace function zz_exec(p_sql text) returns jsonb language plpgsql security definer as $$
begin execute p_sql; return jsonb_build_object('ok', true); end $$;
create or replace function zz_query(p_sql text) returns jsonb language plpgsql security definer as $$
declare r jsonb;
begin execute format('select coalesce(jsonb_agg(to_jsonb(x)), ''[]''::jsonb) from (%s) x', p_sql) into r; return r; end $$;
grant execute on function zz_exec(text), zz_query(text) to anon;
