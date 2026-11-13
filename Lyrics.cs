using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Trophyline;

public sealed record LyricLine(int Ms, string Text);

/// A song's lyrics. Synced ones have a time on every line; plain ones don't (Ms is 0).
public sealed record LyricSet(List<LyricLine> Lines, bool Synced);

/// Song lyrics from LRCLIB (free, no account), kept in music/lyrics so a song is only looked up once.
static partial class Lyrics
{
    static string Folder => Path.Combine(Program.Home, "music", "lyrics");
    static string OffsetsPath => Path.Combine(Folder, "offsets.json");

    static string Key(string title, string artist)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes((artist + "\n" + title).ToLowerInvariant()));
        return Convert.ToHexString(hash)[..16];
    }

    /// The lyrics, or null when LRCLIB has none for this song.
    public static async Task<LyricSet?> For(string title, string artist, int durationSec = 0)
    {
        var file = Path.Combine(Folder, Key(title, artist) + ".lrc");
        try
        {
            if (File.Exists(file))
            {
                var cached = await File.ReadAllTextAsync(file);
                return cached.Length == 0 ? null : Parse(cached);
            }
        }
        catch (IOException) { }

        string? text = null;
        bool failed = false;
        try { text = await Fetch(title, artist, durationSec); }
        catch (Exception ex) { failed = true; Program.LogError("lyrics: " + ex.Message); }
        if (failed) return null; // offline: try again next time
        try
        {
            Directory.CreateDirectory(Folder);
            await File.WriteAllTextAsync(file, text ?? ""); // empty file = "none found", so it isn't asked again
        }
        catch (IOException) { }
        return text is { Length: > 0 } ? Parse(text) : null;
    }

    /// Forgets the saved lyrics of a song, so they're looked up again.
    public static void Forget(string title, string artist)
    {
        try { File.Delete(Path.Combine(Folder, Key(title, artist) + ".lrc")); } catch (IOException) { }
    }

    static async Task<string?> Fetch(string title, string artist, int durationSec)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Trophyline/{Updater.Current.ToString(3)}");
        var clean = Clean(title);
        var mainArtist = artist.Split(',')[0].Trim();
        string?[] urls =
        [
            $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(clean)}&artist_name={Uri.EscapeDataString(mainArtist)}",
            $"https://lrclib.net/api/search?q={Uri.EscapeDataString(clean + " " + mainArtist)}",
            clean != title ? $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(mainArtist)}" : null,
        ];
        foreach (var url in urls)
        {
            if (url == null) continue;
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
            string? bestSynced = null, plain = null;
            double bestGap = double.MaxValue;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.TryGetProperty("instrumental", out var ins) && ins.ValueKind == JsonValueKind.True) continue;
                var synced = Str(e, "syncedLyrics");
                double dur = e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
                double gap = durationSec > 0 && dur > 0 ? Math.Abs(dur - durationSec) : 0;
                if (synced is { Length: > 0 } && gap < bestGap) { bestSynced = synced; bestGap = gap; }
                plain ??= Str(e, "plainLyrics") is { Length: > 0 } p ? p : null;
            }
            // a synced version whose length is far off is probably another edit of the song: still better than nothing
            if (bestSynced != null) return bestSynced;
            if (plain != null) return plain;
        }
        return null;
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// "Song (feat. X) - 2011 Remaster" → "Song".
    static string Clean(string title)
    {
        var t = FeatRegex().Replace(title, "");
        t = DashRegex().Replace(t, "");
        return t.Trim() is { Length: > 0 } c ? c : title;
    }

    [GeneratedRegex(@"\s*[\(\[](feat\.?|ft\.?|with)\s[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex FeatRegex();
    [GeneratedRegex(@"\s+-\s+.*(remaster|version|edit|live|mix|mono|stereo|deluxe|acoustic).*$", RegexOptions.IgnoreCase)]
    private static partial Regex DashRegex();
    [GeneratedRegex(@"\[(\d+):(\d+(?:[.:]\d+)?)\]")]
    private static partial Regex StampRegex();

    /// Reads LRC ("[01:02.34] words"); text without times becomes plain lyrics.
    public static LyricSet Parse(string text)
    {
        var lines = new List<LyricLine>();
        bool synced = false;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var stamps = StampRegex().Matches(raw);
            if (stamps.Count == 0)
            {
                if (!raw.StartsWith('[')) lines.Add(new LyricLine(0, raw.Trim()));
                continue;
            }
            synced = true;
            var words = raw[(stamps[^1].Index + stamps[^1].Length)..].Trim();
            foreach (Match m in stamps)
            {
                double sec = double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                lines.Add(new LyricLine(int.Parse(m.Groups[1].Value) * 60000 + (int)Math.Round(sec * 1000), words));
            }
        }
        if (synced)
        {
            lines = lines.Where(l => l.Ms > 0 || l.Text.Length > 0).OrderBy(l => l.Ms).ToList();
            // keep one empty line for pauses, but not many in a row
            lines = lines.Where((l, i) => l.Text.Length > 0 || i == 0 || lines[i - 1].Text.Length > 0).ToList();
        }
        else
        {
            while (lines.Count > 0 && lines[0].Text.Length == 0) lines.RemoveAt(0);
            while (lines.Count > 0 && lines[^1].Text.Length == 0) lines.RemoveAt(lines.Count - 1);
        }
        return new LyricSet(lines, synced);
    }

    /// The index of the line being sung at `ms` (-1 before the first line).
    public static int LineAt(LyricSet set, long ms)
    {
        int lo = 0, hi = set.Lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (set.Lines[mid].Ms <= ms) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return found;
    }

    // ---------- your own timing fixes, per song ----------

    static Dictionary<string, int>? offsets;

    static Dictionary<string, int> Offsets()
    {
        if (offsets != null) return offsets;
        offsets = [];
        try
        {
            if (File.Exists(OffsetsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(OffsetsPath));
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Number) offsets[p.Name] = p.Value.GetInt32();
            }
        }
        catch (Exception ex) { Program.LogError("lyrics offsets: " + ex.Message); }
        return offsets;
    }

    /// How many milliseconds the lyrics are moved for this song (+ = lyrics come earlier).
    public static int OffsetFor(string title, string artist) => Offsets().GetValueOrDefault(Key(title, artist));

    public static void SetOffset(string title, string artist, int ms)
    {
        var o = Offsets();
        if (ms == 0) o.Remove(Key(title, artist)); else o[Key(title, artist)] = ms;
        try
        {
            Directory.CreateDirectory(Folder);
            using var ms2 = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms2))
            {
                w.WriteStartObject();
                foreach (var (k, v) in o) w.WriteNumber(k, v);
                w.WriteEndObject();
            }
            File.WriteAllBytes(OffsetsPath, ms2.ToArray());
        }
        catch (IOException) { }
    }
}

/// Where Spotify is in the current song. Windows: the system media controls (read by a small helper
/// that runs only while the lyrics are on screen). macOS: AppleScript. Linux: MPRIS.
/// Between readings the position just keeps counting with the clock.
sealed class PlaybackClock : IDisposable
{
    public sealed record Reading(long PosMs, long DurMs, bool Playing, string Title);

    Reading? last;
    long lastAt;                     // Stopwatch time of `last`
    readonly Stopwatch watch = Stopwatch.StartNew();
    Process? helper;
    CancellationTokenSource? poll;

    /// The position right now (null when Spotify doesn't tell).
    public Reading? Now
    {
        get
        {
            var r = last;
            if (r == null || watch.ElapsedMilliseconds - lastAt > 15000) return null; // stale: the helper stopped
            if (!r.Playing) return r;
            var pos = r.PosMs + (watch.ElapsedMilliseconds - lastAt);
            return r with { PosMs = r.DurMs > 0 ? Math.Min(pos, r.DurMs) : pos };
        }
    }

    public bool Running => helper != null || poll != null;

    public void Start()
    {
        if (Running) return;
        try
        {
            if (Platform.Windows) StartWindows();
            else
            {
                poll = new CancellationTokenSource();
                var token = poll.Token;
                _ = Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        try { if (ReadOnce() is { } r) Set(r); } catch (Exception) { }
                        try { await Task.Delay(1000, token); } catch (TaskCanceledException) { }
                    }
                }, token);
            }
        }
        catch (Exception ex) { Program.LogError("playback clock: " + ex.Message); }
    }

    public void Stop()
    {
        poll?.Cancel();
        poll = null;
        var h = helper;
        helper = null;
        if (h != null) { try { if (!h.HasExited) h.Kill(); } catch { } h.Dispose(); }
    }

    public void Dispose() => Stop();

    void Set(Reading r)
    {
        last = r;
        lastAt = watch.ElapsedMilliseconds;
    }

    // ----- Windows: a PowerShell loop that prints the media controls' timeline twice a second -----

    void StartWindows()
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [Text.Encoding]::UTF8
            Add-Type -AssemblyName System.Runtime.WindowsRuntime
            $as = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
            $M = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager,Windows.Media.Control,ContentType=WindowsRuntime]
            $P = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties,Windows.Media.Control,ContentType=WindowsRuntime]
            $mgr = $as.MakeGenericMethod($M).Invoke($null, @($M::RequestAsync())).Result
            $asP = $as.MakeGenericMethod($P)
            $parent = {{Environment.ProcessId}}
            $title = ''; $tick = 0
            while ($true) {
              if (-not (Get-Process -Id $parent -ErrorAction SilentlyContinue)) { exit 0 }
              try {
                $s = $null
                foreach ($x in $mgr.GetSessions()) { if ($x.SourceAppUserModelId -like '*Spotify*') { $s = $x; break } }
                if ($s) {
                  if ($tick % 4 -eq 0) { $title = $asP.Invoke($null, @($s.TryGetMediaPropertiesAsync())).Result.Title }
                  $tl = $s.GetTimelineProperties()
                  $play = $s.GetPlaybackInfo().PlaybackStatus -eq 'Playing'
                  $pos = $tl.Position.TotalMilliseconds
                  if ($play) { $pos += ([DateTimeOffset]::UtcNow - $tl.LastUpdatedTime).TotalMilliseconds }
                  [Console]::Out.WriteLine((ConvertTo-Json -Compress @{ p = [long]$pos; d = [long]$tl.EndTime.TotalMilliseconds; s = $play; t = $title }))
                } else { [Console]::Out.WriteLine('{}') }
              } catch { [Console]::Out.WriteLine('{}') }
              [Console]::Out.Flush()
              $tick++
              Start-Sleep -Milliseconds 500
            }
            """;
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                                  Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            psi.ArgumentList.Add(a);
        var p = Process.Start(psi);
        if (p == null) return;
        helper = p;
        p.ErrorDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not { Length: > 2 } line) return;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                if (!r.TryGetProperty("p", out var pos)) return;
                Set(new Reading(pos.GetInt64(), r.GetProperty("d").GetInt64(), r.GetProperty("s").GetBoolean(),
                    r.TryGetProperty("t", out var t) ? t.GetString() ?? "" : ""));
            }
            catch (Exception) { }
        };
        p.BeginOutputReadLine();
    }

    // ----- macOS and Linux: asked once a second -----

    static Reading? ReadOnce()
    {
        if (Platform.Mac)
        {
            var r = Run("osascript", "-e", "tell application \"Spotify\" to (player position as string) & \"\n\" & (duration of current track as string) & \"\n\" & (player state as string) & \"\n\" & (name of current track)");
            var l = r.Split('\n');
            if (l.Length < 4 || !double.TryParse(l[0].Trim().Replace(',', '.'), CultureInfo.InvariantCulture, out var pos)) return null;
            long.TryParse(l[1].Trim(), out var dur);
            return new Reading((long)(pos * 1000), dur, l[2].Trim() == "playing", l[3].Trim());
        }
        if (Platform.Linux)
        {
            var pos = Run("dbus-send", "--print-reply", "--dest=org.mpris.MediaPlayer2.spotify", "/org/mpris/MediaPlayer2",
                "org.freedesktop.DBus.Properties.Get", "string:org.mpris.MediaPlayer2.Player", "string:Position");
            var m = Regex.Match(pos, @"int64\s+(\d+)");
            if (!m.Success) return null;
            var state = Run("dbus-send", "--print-reply", "--dest=org.mpris.MediaPlayer2.spotify", "/org/mpris/MediaPlayer2",
                "org.freedesktop.DBus.Properties.Get", "string:org.mpris.MediaPlayer2.Player", "string:PlaybackStatus");
            var meta = Run("dbus-send", "--print-reply", "--dest=org.mpris.MediaPlayer2.spotify", "/org/mpris/MediaPlayer2",
                "org.freedesktop.DBus.Properties.Get", "string:org.mpris.MediaPlayer2.Player", "string:Metadata");
            var len = Regex.Match(meta, "\"mpris:length\"\\s*variant\\s*u?int64\\s+(\\d+)");
            var title = Regex.Match(meta, "\"xesam:title\"\\s*variant\\s*string \"([^\"]*)\"");
            return new Reading(long.Parse(m.Groups[1].Value) / 1000, len.Success ? long.Parse(len.Groups[1].Value) / 1000 : 0,
                state.Contains("\"Playing\""), title.Success ? title.Groups[1].Value : "");
        }
        return null;
    }

    static string Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return "";
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(3000);
        return output;
    }
}
