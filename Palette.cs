using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Trophyline;

/// The window's accent colour. Everything that uses it reads {DynamicResource Accent...},
/// so picking a new colour recolours the open window straight away.
static class Palette
{
    public const string Default = "#66C0F4";

    public static readonly (string Name, string Hex)[] Accents =
    [
        ("Steam blue", "#66C0F4"),
        ("Violet", "#B98CFF"),
        ("Emerald", "#5BD68A"),
        ("Gold", "#F5C451"),
        ("Sunset", "#FF8A4C"),
        ("Rose", "#FF6FAE"),
        ("Crimson", "#FF5C6C"),
        ("Aqua", "#3DD6C6"),
        ("Ice", "#C9D6E3"),
    ];

    public static Color Current { get; private set; } = Color.Parse(Default);

    public static void Apply(string? hex)
    {
        if (!Color.TryParse(hex ?? "", out var c)) c = Color.Parse(Default);
        Current = c;
        var r = Application.Current!.Resources;
        r["Accent"] = new ImmutableSolidColorBrush(c);
        r["AccentSoft"] = new ImmutableSolidColorBrush(Mix(Color.Parse("#151C25"), c, 0.22));
        r["AccentGlow"] = new BoxShadows(new BoxShadow { Blur = 24, Color = Color.FromArgb(0x73, c.R, c.G, c.B) });
        r["AccentHover"] = new ImmutableSolidColorBrush(Mix(c, Colors.White, 0.25));
        r["AccentGradient"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(c, 0), new GradientStop(Mix(c, Color.Parse("#7B5CFF"), 0.7), 1) },
        };
        r["AccentAvatar"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(c, 0), new GradientStop(Mix(c, Color.Parse("#7B5CFF"), 0.7), 1) },
        };
        var card = Color.Parse("#151C25");
        // page header banners: accent in the top-left corner fading into the card colour
        r["AccentHead"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Mix(card, c, 0.34), 0),
                new GradientStop(Mix(card, Mix(c, Color.Parse("#7B5CFF"), 0.6), 0.14), 0.55),
                new GradientStop(card, 1),
            },
        };
        // the selected menu entry
        r["AccentNav"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(0x40, c.R, c.G, c.B), 0), new GradientStop(Color.FromArgb(0x08, c.R, c.G, c.B), 1) },
        };
        // a soft glow at the top of every page
        r["AccentWash"] = new RadialGradientBrush
        {
            Center = new RelativePoint(0.8, -0.05, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.8, -0.05, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.55, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(0x26, c.R, c.G, c.B), 0), new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1) },
        };
    }

    public static IBrush Brush => new ImmutableSolidColorBrush(Current);

    /// Heatmap shades from "nothing" (the card line colour) up to the accent.
    public static IBrush[] HeatLevels()
    {
        var line = Color.Parse("#243042");
        return [new ImmutableSolidColorBrush(line),
                new ImmutableSolidColorBrush(Mix(line, Current, 0.3)),
                new ImmutableSolidColorBrush(Mix(line, Current, 0.55)),
                new ImmutableSolidColorBrush(Mix(line, Current, 0.8)),
                new ImmutableSolidColorBrush(Mix(Current, Colors.White, 0.3))];
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
