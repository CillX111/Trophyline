namespace Trophyline;

/// Seasons last 3 months (calendar quarters, UTC). Season 1 = October to December 2026.
/// Must match the server (server/src/index.js).
public sealed record Season(string Id, int Number, string Label, long Start, long End)
{
    static readonly string[] Names = ["Winter", "Spring", "Summer", "Autumn"];

    public string Name => $"Season {Number}";

    public static Season Of(DateTime utc)
    {
        int q = (utc.Month - 1) / 3 + 1;
        return Make(utc.Year, q);
    }

    public static Season Current => Of(DateTime.UtcNow);

    public static Season Make(int year, int quarter)
    {
        var start = new DateTimeOffset(year, (quarter - 1) * 3 + 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddMonths(3);
        int number = (year - 2026) * 4 + (quarter - 4) + 1;
        return new Season($"{year}-{quarter}", number, $"{Names[quarter - 1]} {year}", start.ToUnixTimeSeconds(), end.ToUnixTimeSeconds());
    }

    public Season Previous => int.Parse(Id[5..]) == 1 ? Make(int.Parse(Id[..4]) - 1, 4) : Make(int.Parse(Id[..4]), int.Parse(Id[5..]) - 1);

    public int DaysLeft => (int)Math.Ceiling((End - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) / 86400.0);
}
