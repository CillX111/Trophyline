using System.Text;

namespace Trophyline;

public sealed record Session(string AppId, long Start, long End)
{
    public int Minutes => (int)((End - Start) / 60);
}

/// Play sessions recorded by the tracker: sessions.tsv (appid, start, end as unix seconds)
/// plus open-session.txt for the game running right now.
static class SessionLog
{
    static string FilePath => Path.Combine(Program.Home, "sessions.tsv");
    static string OpenPath => Path.Combine(Program.Home, "open-session.txt");

    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// When the computer last started (sessions left open across a shutdown end there).
    static long BootTime => Now - Environment.TickCount64 / 1000;

    /// Called when the tracker starts: tidies up a session left open and starts one for a game already running.
    public static void Resume(string runningAppId)
    {
        if (Current() is (string app, long start))
        {
            if (app == runningAppId && start >= BootTime) return; // tracker restarted mid-game: keep going
            Close(start < BootTime ? BootTime : Now);
        }
        if (runningAppId != "0") Begin(runningAppId);
    }

    public static void Changed(string before, string now)
    {
        if (before != "0") Close(Now);
        if (now != "0") Begin(now);
    }

    static void Begin(string appId) =>
        Write(OpenPath, $"{appId}\t{Now}");

    static void Close(long end)
    {
        if (Current() is not (string app, long start)) return;
        try
        {
            if (end - start >= 30) // ignore launches that closed straight away
                File.AppendAllText(FilePath, $"{app}\t{start}\t{end}\n", Encoding.UTF8);
            File.Delete(OpenPath);
        }
        catch (IOException ex) { Program.LogError("sessions: " + ex.Message); }
    }

    /// The game being played right now (according to the tracker), or null.
    public static (string AppId, long Start)? Current()
    {
        try
        {
            if (!File.Exists(OpenPath)) return null;
            var p = ReadShared(OpenPath).Trim().Split('\t');
            return p.Length == 2 && long.TryParse(p[1], out var s) ? (p[0], s) : null;
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

    static void Write(string path, string text)
    {
        try { File.WriteAllText(path, text, Encoding.UTF8); }
        catch (IOException ex) { Program.LogError("sessions: " + ex.Message); }
    }
}
