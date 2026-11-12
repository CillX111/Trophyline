create or replace function tl_clean(v text, max_len int) returns text language sql immutable as $$
  -- removes control characters (keeps line breaks) and invisible / text-direction characters
  select left(btrim(translate(coalesce(v, ''),
    (select string_agg(chr(c), '') from generate_series(1, 31) c where c <> 10)
      || chr(127) || (select string_agg(chr(c), '') from generate_series(128, 159) c)
      || chr(8203) || chr(8204) || chr(8205) || chr(8206) || chr(8207)
      || chr(8234) || chr(8235) || chr(8236) || chr(8237) || chr(8238) || chr(65279),
    '')), max_len)
$$;
