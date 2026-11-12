using System.Text.Json;

namespace Trophyline;

/// Profile icons you buy with coins. You earn coins for every level you reach.
public sealed record ShopIcon(string Id, string Glyph, string Name, string Tier, int Price, string Color, int MinLevel = 0);

static class Shop
{
    // ---------- coins ----------

    /// Coins for reaching `level` (level 2 gives 70, level 10 gives 150, level 30 gives 350 …).
    public static int CoinsForLevel(int level) => 50 + 10 * level;

    public static long Earned(int level)
    {
        long sum = 0;
        for (int l = 2; l <= level; l++) sum += CoinsForLevel(l);
        return sum;
    }

    // ---------- the catalogue ----------

    public static readonly (string Tier, string Color, int Price)[] Tiers =
    [
        ("Common", "#8B99AB", 300),
        ("Rare", "#66C0F4", 800),
        ("Epic", "#B98CFF", 1600),
        ("Legendary", "#F5C451", 3000),
    ];

    static ShopIcon I(string id, string glyph, string name, int tier, int minLevel = 0) =>
        new(id, glyph, name, Tiers[tier].Tier, Tiers[tier].Price, Tiers[tier].Color, minLevel);

    public static readonly ShopIcon[] Icons =
    [
        I("controller", "🎮", "Gamer", 0),
        I("star", "⭐", "Star", 0),
        I("cat", "🐱", "Cat", 0),
        I("pizza", "🍕", "Pizza", 0),
        I("cactus", "🌵", "Cactus", 0),
        I("dice", "🎲", "Lucky Dice", 0),
        I("ghost", "👻", "Ghost", 1),
        I("alien", "👾", "Space Invader", 1),
        I("fox", "🦊", "Fox", 1),
        I("rocket", "🚀", "Rocket", 1),
        I("ninja", "🥷", "Ninja", 1),
        I("target", "🎯", "Sharpshooter", 1),
        I("fire", "🔥", "On Fire", 2, 10),
        I("skull", "💀", "Skull", 2, 10),
        I("robot", "🤖", "Robot", 2, 10),
        I("wolf", "🐺", "Lone Wolf", 2, 15),
        I("swords", "⚔️", "Duelist", 2, 15),
        I("lightning", "⚡", "Lightning", 2, 15),
        I("dragon", "🐉", "Dragon", 3, 20),
        I("crown", "👑", "Crown", 3, 25),
        I("diamond", "💎", "Diamond", 3, 30),
        I("trophy", "🏆", "Champion", 3, 40),
    ];

    public static ShopIcon? Find(string? id) => Icons.FirstOrDefault(i => i.Id == id);

    // ---------- what you own (shop.json) ----------

    public sealed class State
    {
        public List<string> Owned { get; set; } = [];
        public string Equipped { get; set; } = "";
        public long Spent { get; set; }
    }

    static string FilePath => Path.Combine(Program.Home, "shop.json");

    public static State Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new State();
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var r = doc.RootElement;
            return new State
            {
                Owned = r.TryGetProperty("owned", out var o) ? o.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).Distinct().ToList() : [],
                Equipped = r.TryGetProperty("equipped", out var e2) ? e2.GetString() ?? "" : "",
                Spent = r.TryGetProperty("spent", out var s2) && s2.TryGetInt64(out var sp) ? sp : 0,
            };
        }
        catch (Exception ex) { Program.LogError("shop: " + ex.Message); return new State(); }
    }

    public static void Save(State s)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteStartArray("owned");
            foreach (var id in s.Owned) w.WriteStringValue(id);
            w.WriteEndArray();
            w.WriteString("equipped", s.Equipped);
            w.WriteNumber("spent", s.Spent);
            w.WriteEndObject();
        }
        File.WriteAllBytes(FilePath, ms.ToArray());
    }

    public static long Balance(State s, int level) => Earned(level) - s.Spent;

    public static string? Buy(ShopIcon icon, int level)
    {
        var s = Load();
        if (s.Owned.Contains(icon.Id)) return "You already own this.";
        if (level < icon.MinLevel) return $"Reach level {icon.MinLevel} to unlock this.";
        if (Balance(s, level) < icon.Price) return "Not enough coins yet.";
        s.Owned.Add(icon.Id);
        s.Spent += icon.Price;
        s.Equipped = icon.Id; // put it on straight away
        Save(s);
        return null;
    }

    public static void Equip(string id)
    {
        var s = Load();
        if (id.Length > 0 && !s.Owned.Contains(id)) return;
        s.Equipped = id;
        Save(s);
    }
}
