namespace NativeBeeper.Presentation.Services;

// Which colour an outgoing bubble wears. Google Messages keeps its route colours (dark blue RCS, light blue SMS/MMS, a
// muted blue that claims neither); the other services each get their own, and the person can choose any of them.
// Incoming messages are always the neutral grey. Kept out of the view so every colour the app can draw is checked for
// readable text offline, rather than discovered in a screenshot. Neutral claims no service and cannot be chosen: it is
// a linked conversation with service colours off, and a service this table does not know.
public enum BubbleFamily { GoogleRcs, GoogleSms, GoogleUnknown, Messenger, Instagram, GoogleVoice, Hermes, Neutral }

// Why a family was chosen, so a check can tell a linked conversation's neutral bubble from an unknown service's.
public enum BubbleReason { GoogleRoute, Service, LinkedNeutral, UnknownService }

public readonly record struct BubbleResolution(BubbleFamily Family, BubbleReason Reason);

// A named colour with a variant for each theme. Both variants carry readable text; the checks hold every swatch to it.
public sealed record BubbleSwatch(string Id, string Name, Rgba Light, Rgba Dark)
{
    public Rgba For(bool dark) => dark ? Dark : Light;
}

// A CSS linear gradient a bubble is painted with in place of its solid colour (BubbleColours.Fade), which still decides
// its ink and colours the send button, dots and tags. Stops are fractions of the gradient line.
public sealed record BubbleGradient(Rgba From, double FromStop, Rgba To, double ToStop, double Degrees)
{
    // Where the line runs on a width by height box, as a browser draws it: through the centre at the CSS angle (0 points
    // up, clockwise), long enough that its ends' perpendiculars pass through the far corners.
    public (double X1, double Y1, double X2, double Y2) Line(double width, double height)
    {
        var radians = Degrees * Math.PI / 180;
        var (dx, dy) = (Math.Sin(radians), -Math.Cos(radians));
        var half = (Math.Abs(width * dx) + Math.Abs(height * dy)) / 2;
        return (width / 2 - dx * half, height / 2 - dy * half, width / 2 + dx * half, height / 2 + dy * half);
    }
}

public static class BubbleColours
{
    public const double MinimumContrast = 4.5;
    public static readonly Rgba White = new(255, 255, 255);
    private static readonly Rgba Black = new(0, 0, 0);
    // The light theme's primary text is black at 89% over whatever is behind it, so its contrast depends on the fill.
    private static readonly Rgba LightThemeText = new(0, 0, 0, 0xE4);
    // The send button's glyph on a light fill, as Google Messages draws it, wherever it stays readable.
    private static readonly Rgba Navy = new(4, 30, 73);

    // The families a person can colour. Neutral is left out, so recolouring Google's unknown route never recolours it.
    public static IReadOnlyList<BubbleFamily> Families { get; } = [.. Enum.GetValues<BubbleFamily>().Where(family => family != BubbleFamily.Neutral)];

    // The defaults are the owner's (2026-10-29). In dark the RCS navy stays, and SMS, Messenger, Instagram and Voice went
    // a step deeper so none glows on the dark thread, every one carrying white; the order from darkest to brightest (RCS,
    // Messenger, SMS) stayed, so each still says which service it is. In light the bubbles went between the old strong
    // colours and pale tints: RCS a medium blue under white, the rest tints under the dark ink, the blues set apart by
    // lightness and hue (RCS the darkest, Messenger sky, SMS the palest periwinkle), since the pale tints alone were too
    // close to tell apart. Teal to lavender are offered only.
    public static IReadOnlyList<BubbleSwatch> Swatches { get; } =
    [
        new("blue", "Blue", new(0x2C, 0x5B, 0xB0), new(0x07, 0x35, 0x80)),
        new("light-blue", "Light blue", new(0xC4, 0xD4, 0xF7), new(0x5A, 0x86, 0xCC)),
        new("muted-blue", "Muted blue", new(0xDD, 0xE2, 0xEA), new(0x25, 0x42, 0x6E)),
        new("bright-blue", "Sky blue", new(0x7C, 0xCB, 0xF7), new(0x0A, 0x72, 0xB8)),
        new("purple", "Purple", new(0xD3, 0xB3, 0xEE), new(0x7A, 0x36, 0xAD)),
        new("green", "Green", new(0xA8, 0xDD, 0xB5), new(0x10, 0x6A, 0x2F)),
        new("teal", "Teal", new(0x00, 0x79, 0x6B), new(0x00, 0x69, 0x5C)),
        new("red", "Red", new(0xC5, 0x22, 0x1F), new(0xB3, 0x26, 0x1E)),
        new("pink", "Pink", new(0xC2, 0x18, 0x5B), new(0xAD, 0x14, 0x57)),
        new("orange", "Orange", new(0xB3, 0x54, 0x1E), new(0xA0, 0x47, 0x1A)),
        new("graphite", "Graphite", new(0x5F, 0x63, 0x68), new(0x3C, 0x40, 0x43)),
        new("lavender", "Lavender", new(0xE8, 0xDE, 0xF8), new(0xD0, 0xBC, 0xFF)),
    ];

    public static string DefaultSwatch(BubbleFamily family) => family switch
    {
        BubbleFamily.GoogleRcs => "blue",
        BubbleFamily.GoogleSms => "light-blue",
        BubbleFamily.Messenger => "bright-blue",
        BubbleFamily.Instagram => "purple",
        BubbleFamily.GoogleVoice => "green",
        // A colour no other service wears by default, carrying white in both themes.
        BubbleFamily.Hermes => "teal",
        // The route-unknown default, and the fixed neutral colour: the muted blue both had before colours could be chosen.
        _ => "muted-blue"
    };

    public static BubbleSwatch? Swatch(string? id) => Swatches.FirstOrDefault(swatch => swatch.Id == id);

    public static Rgba Incoming(bool dark) => dark ? new(48, 48, 48) : new(213, 213, 213);

    // A reaction's round badge on a message's corner, as Google Messages draws one (owner, 2026-10-30): the incoming
    // bubble taken a tenth of the way to its own ink, so the badge stands a step farther from the conversation than the
    // bubble does (lighter in dark, darker in light) and shows where it overlaps either side's bubble. Google's is tone
    // 22 on a tone-12 bubble; a tenth is that step on ours.
    public static Rgba ReactionBadge(Rgba incoming) => CardTinting.Mix(Ink(incoming.Opaque), incoming.Opaque, .1);

    // Google messages follow their own reported route; a pending one carries its expected route until the phone reports
    // the real one. The other services have one colour each. A linked conversation either keeps each message's service
    // colour or, when that is turned off, draws every outgoing message in the one neutral colour. A service this table
    // does not know claims nothing.
    public static BubbleResolution Resolve(string? network, string? transport, bool linked, bool serviceColoursWhenLinked)
    {
        if (linked && !serviceColoursWhenLinked) return new(BubbleFamily.Neutral, BubbleReason.LinkedNeutral);
        return network switch
        {
            "Google Messages" => new(ComposeTransport.BubbleFamily(transport) switch
            {
                "RCS" => BubbleFamily.GoogleRcs,
                "SMS" => BubbleFamily.GoogleSms,
                _ => BubbleFamily.GoogleUnknown
            }, BubbleReason.GoogleRoute),
            "Messenger" => new(BubbleFamily.Messenger, BubbleReason.Service),
            "Instagram" => new(BubbleFamily.Instagram, BubbleReason.Service),
            "Google Voice" => new(BubbleFamily.GoogleVoice, BubbleReason.Service),
            "Hermes" => new(BubbleFamily.Hermes, BubbleReason.Service),
            _ => new(BubbleFamily.Neutral, BubbleReason.UnknownService)
        };
    }

    // The send button wears the colour its message will have: the member conversation it goes through, or the
    // conversation itself before one is chosen. A linked conversation's own network is "Merged", which names no service,
    // so its button stays neutral until a route is chosen.
    public static BubbleResolution Sending(string? sendNetwork, string? conversationNetwork, string? routeFamily, bool linked, bool serviceColoursWhenLinked) =>
        Resolve(sendNetwork ?? conversationNetwork, routeFamily, linked, serviceColoursWhenLinked);

    // A saved choice is a swatch id or a custom #RRGGBB. Anything else, including nothing, is the family's default.
    // Neutral takes no choice at all.
    public static Rgba Fill(BubbleFamily family, string? choice, bool dark)
    {
        if (family == BubbleFamily.Neutral) choice = null;
        if (TryParseCustom(choice, out var custom)) return Readable(custom).Colour;
        return (Swatch(choice) ?? Swatch(DefaultSwatch(family))!).For(dark);
    }

    // The gradient a bubble of this family is painted with: its fill fading darker (Fade).
    public static BubbleGradient Gradient(BubbleFamily family, string? choice, bool dark) => Fade(Fill(family, choice, dark), dark);

    // Every sent bubble fades from its own colour to a darker one toward the bottom right, as Messages for Web draws RCS
    // (linear-gradient at 130deg, 30% to 95%), but only ever darker: Web's RCS began lighter than its navy, and beside the
    // flat colour read as the lighter of the two (owner, 2026-10-29). About a twentieth darker in dark and a thirtieth in
    // light, so it can barely be told: the owner saw a fifth, then a tenth, as too much. A pale bubble darkened as far as a
    // dark one turns muddy. A fill under the dark ink fades only as far as that ink still reads.
    public static BubbleGradient Fade(Rgba fill, bool dark)
    {
        fill = fill.Opaque;
        var ink = Ink(fill);
        var amount = dark ? .05 : .03;
        var to = CardTinting.Mix(Black, fill, amount);
        while (!LightInk(fill) && amount > 0 && Contrast(to, ink) < RequiredContrast(fill))
            to = CardTinting.Mix(Black, fill, amount = Math.Max(0, amount - .01));
        return new(fill, .30, to, .95, 130);
    }

    // What is worth saving: nothing for the default, so a later change of default reaches people who never chose.
    public static string? Normalize(BubbleFamily family, string? choice)
    {
        if (family == BubbleFamily.Neutral) return null;
        if (TryParseCustom(choice, out var custom)) return Hex(Readable(custom).Colour);
        return Swatch(choice) is { } swatch && swatch.Id != DefaultSwatch(family) ? swatch.Id : null;
    }

    // Saved choices as loaded, reduced to what a choice could have saved. A hand-edited or damaged file can hold a null,
    // an unknown family or something that is not a colour; each is dropped, so it reads as the default instead of
    // failing the next change.
    public static Dictionary<string, string>? Clean(IReadOnlyDictionary<string, string>? saved)
    {
        if (saved is null) return null;
        var kept = new Dictionary<string, string>();
        foreach (var family in Families)
            if (saved.TryGetValue(family.ToString(), out var choice) && Normalize(family, choice) is { } normal)
                kept[family.ToString()] = normal;
        return kept.Count == 0 ? null : kept;
    }

    public static bool TryParseCustom(string? value, out Rgba colour)
    {
        colour = default;
        if (value is not { Length: 7 } || value[0] != '#' || !uint.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.AllowHexSpecifier, null, out var rgb))
            return false;
        colour = new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    public static string Hex(Rgba colour) => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    // WCAG relative luminance and contrast ratio.
    public static double Luminance(Rgba colour)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(colour.R) + 0.7152 * Channel(colour.G) + 0.0722 * Channel(colour.B);
    }

    public static double Contrast(Rgba a, Rgba b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    // Ink is chosen by the fill, never fixed per family: a person can put a pale colour where a dark one was. White wherever
    // it holds 3:1, as Messenger writes on its own blue (owner, 2026-10-26: the near-black that reads a little better there
    // looked wrong; the phone does the same); otherwise whichever of the two themes' text reads better.
    public const double WhiteInkContrast = 3;
    public static bool LightInk(Rgba fill) => Contrast(fill, White) >= WhiteInkContrast || Contrast(fill, White) >= Contrast(fill, DarkInk(fill));
    public static Rgba DarkInk(Rgba fill) => CardTinting.Flatten(LightThemeText, fill.Opaque);
    public static Rgba Ink(Rgba fill) => LightInk(fill) ? White : DarkInk(fill);
    public static double InkContrast(Rgba fill) => Contrast(fill, Ink(fill));
    // What a fill's text must reach: 4.5:1 in the dark ink, and white's own 3:1 where white is chosen.
    public static double RequiredContrast(Rgba fill) => LightInk(fill) ? WhiteInkContrast : MinimumContrast;
    public static bool Reads(Rgba fill) => InkContrast(fill) >= RequiredContrast(fill);

    public static Rgba Glyph(Rgba fill) =>
        LightInk(fill) ? White : Contrast(fill, Navy) >= MinimumContrast ? Navy : DarkInk(fill);

    // The default highlight is the accent colour, which all but vanishes on a blue bubble. A darker shade of the bubble's
    // own colour shows against the bubble and carries white text, which is how Windows draws selected text. On a pale
    // bubble it stops at a middle tone, where the dark ink also stays legible in case the text keeps its own colour. On a bubble already dark (the
    // owner's navy RCS) the first shade dark enough for white text was too near the bubble to see, so it goes on darkening until it shows. A
    // bright one carrying the dark ink (the owner's Messenger blue, before it took white) was darkened past where that ink reads, so shades lighter
    // than 40% are tried after.
    public static Rgba SelectionHighlight(Rgba fill)
    {
        var dark = !LightInk(fill);
        foreach (var amount in Enumerable.Range(8, 12).Concat(Enumerable.Range(1, 7).Reverse()).Select(step => step * 0.05))
        {
            var shade = CardTinting.Mix(Black, fill.Opaque, amount);
            if (Contrast(shade, White) >= MinimumContrast && Contrast(shade, fill.Opaque) >= HighlightShows && (!dark || Contrast(shade, DarkInk(shade)) >= 3))
                return shade;
        }
        return Black;
    }

    // How far the highlight stands from its bubble, as a contrast ratio: enough to see where the selection is.
    public const double HighlightShows = 1.4;

    // The console's background in each theme: what ShellSurfaceBrush resolves to in App.xaml.
    public static readonly Rgba ConsoleLight = new(0xF3, 0xF3, 0xF3);
    public static readonly Rgba ConsoleDark = new(0x29, 0x29, 0x29);
    public static Rgba ConsoleBackground(bool dark) => dark ? ConsoleDark : ConsoleLight;

    // Which family a service tag in the console wears: the one its outgoing bubble would, a Google line by its own
    // route. The console never draws the linked neutral colour; each line names its own service.
    public static BubbleFamily TagFamily(string? network, string? transport) =>
        Resolve(network, transport, linked: false, serviceColoursWhenLinked: true).Family;

    // A service's bubble colour used as text on the console background. A bubble carries text on its colour; a tag is
    // text in its colour, so a pale fill that reads well behind dark text vanishes as ink on a light window. The fill
    // is kept where it already reads at 4.5:1; otherwise only its lightness moves, toward the readable side in small
    // steps, so the tag keeps the hue that says which service it is.
    public static Rgba TagInk(BubbleFamily family, string? choice, bool dark, Rgba background)
    {
        var fill = Fill(family, choice, dark).Opaque;
        background = background.Opaque;
        if (Contrast(fill, background) >= MinimumContrast) return fill;
        var darker = Contrast(Black, background) >= Contrast(White, background);
        var (hue, saturation, lightness) = ToHsl(fill);
        for (var step = 1; step <= 100; step++)
        {
            var next = FromHsl(hue, saturation, Math.Clamp(lightness + (darker ? -step : step) * 0.01, 0, 1));
            if (Contrast(next, background) >= MinimumContrast) return next;
        }
        return darker ? Black : White;
    }

    public static (double Hue, double Saturation, double Lightness) ToHsl(Rgba colour)
    {
        double r = colour.R / 255.0, g = colour.G / 255.0, b = colour.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2;
        var chroma = max - min;
        if (chroma == 0) return (0, 0, lightness);
        var saturation = chroma / (1 - Math.Abs(2 * lightness - 1));
        var hue = max == r ? (g - b) / chroma % 6 : max == g ? (b - r) / chroma + 2 : (r - g) / chroma + 4;
        hue *= 60;
        if (hue < 0) hue += 360;
        return (hue, Math.Min(1, saturation), lightness);
    }

    public static Rgba FromHsl(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = lightness - chroma / 2;
        var (r, g, b) = (hue % 360) switch
        {
            < 60 => (chroma, x, 0.0),
            < 120 => (x, chroma, 0.0),
            < 180 => (0.0, chroma, x),
            < 240 => (0.0, x, chroma),
            < 300 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x)
        };
        static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
        return new(Channel(r + m), Channel(g + m), Channel(b + m));
    }

    // A custom colour whose text cannot reach what its ink needs (RequiredContrast) is moved, darker or lighter, by the
    // smallest step that gets there. Only a narrow band of middle tones needs it, and the move is small enough to keep the
    // colour's character.
    public static (Rgba Colour, bool Adjusted) Readable(Rgba colour)
    {
        colour = colour.Opaque;
        if (Reads(colour)) return (colour, false);
        for (var step = 1; step <= 50; step++)
        {
            var darker = CardTinting.Mix(Black, colour, step * 0.02);
            if (Reads(darker)) return (darker, true);
            var lighter = CardTinting.Mix(White, colour, step * 0.02);
            if (Reads(lighter)) return (lighter, true);
        }
        return (Black, true);
    }
}
