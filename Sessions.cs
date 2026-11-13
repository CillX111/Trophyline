using System.Text;

namespace Trophyline;

public sealed record Session(string AppId, long Start, long End)
{
    public int Minutes => (int)((End - Start) / 60);
}

/// Play sessions recorded by the tracker: sessions.tsv (appid, start, end as unix seconds)
/// plus open-session.txt for the game running right now (appid, start, last seen).
/// The tracker refreshes "last seen" every minute, so time the PC was off or asleep isn't counted:
/// a session whose last sign of life is older than a few minutes ends at that last sign of life.
static class SessionLog
{
    static string FilePath => Path.Combine(Program.Home, "sessions.tsv");
    static string OpenPath => Path.Combine(Program.Home, "open-session.txt");

    /// A gap longer than this between heartbeats means the PC slept or was off.
    const long MaxGap = 5 * 60;

    static readonly object gate = new();

    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// When the computer last started (only for sessions saved by older versions without "last seen").
    static long BootTime => Now - Environment.TickCount64 / 1000;

    /// Called when the tracker starts: tidies up a session left open and starts one for a game already running.
    public static void Resume(string runningAppId)
    {
        lock (gate)
        {
            if (ReadOpen() is (string app, long start, long seen))
            {
                if (app == runningAppId && Now - seen <= MaxGap) { Write(app, start, Now); return; } // tracker restarted mid-game
                Close(app, start, seen);
            }
            if (runningAppId != "0") Write(runningAppId, Now, Now);
        }
    }

    public static void Changed(string before, string now)
    {
        lock (gate)
        {
            if (ReadOpen() is (string app, long start, long seen)) Close(app, start, Now - seen > MaxGap ? seen : Now);
            if (now != "0") Write(now, Now, Now);
        }
    }

    /// Called every minute by the tracker while it runs.
    public static void Heartbeat(string runningAppId)
    {
        lock (gate)
        {
            if (ReadOpen() is not (string app, long start, long seen))
            {
                if (runningAppId != "0") Write(runningAppId, Now, Now);
                return;
            }
            if (Now - seen > MaxGap || app != runningAppId)
            {
                // The PC slept (or the game changed unnoticed): end the old session where it was last seen.
                Close(app, start, Now - seen > MaxGap ? seen : Now);
                if (runningAppId != "0") Write(runningAppId, Now, Now);
                return;
            }
            Write(app, start, Now);
        }
    }

    static void Close(string app, long start, long end)
    {
        try
        {
            if (end - start >= 30) // ignore launches that closed straight away
                File.AppendAllText(FilePath, $"{app}\t{start}\t{end}\n", Encoding.UTF8);
            File.Delete(OpenPath);
        }
        catch (IOException ex) { Program.LogError("sessions: " + ex.Message); }
    }

    /// The game being played right now (according to the tracker), or null.
    public static (string AppId, long Start)? Current() =>
        ReadOpen() is (string app, long start, _) ? (app, start) : null;

    static (string AppId, long Start, long Seen)? ReadOpen()
    {
        try
        {
            if (!File.Exists(OpenPath)) return null;
            var p = ReadShared(OpenPath).Trim().Split('\t');
            if (p.Length < 2 || !long.TryParse(p[1], out var s)) return null;
            if (p.Length >= 3 && long.TryParse(p[2], out var seen) && seen >= s) return (p[0], s, seen);
            // written by an older version: assume it ran until the PC was shut down, or until now
            return (p[0], s, s < BootTime ? BootTime : Now);
        }
        catch (IOException) { return null; }
    }

    public static List<Session> Load()
    {
        var list = new List<Session>();
        try
        {
            if (!File.Exists(FilePath)) return list;
            foreach (var line in ReadShared(FilePath).Split('\n'))
            {
                var p = line.TrimEnd('\r').Split('\t');
                if (p.Length == 3 && long.TryParse(p[1], out var s) && long.TryParse(p[2], out var e) && e > s)
                    list.Add(new Session(p[0], s, e));
            }
        }
        catch (IOException ex) { Program.LogError("sessions read: " + ex.Message); }
        return list;
    }

    static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs, Encoding.UTF8);
        return r.ReadToEnd();
    }

    static void Write(string app, long start, long seen)
    {
        try { File.WriteAllText(OpenPath, $"{app}\t{start}\t{seen}", Encoding.UTF8); }
        catch (IOException ex) { Program.LogError("sessions: " + ex.Message); }
    }
}
