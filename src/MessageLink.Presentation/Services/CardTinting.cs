namespace MessageLink.Presentation.Services;

// Colour arithmetic for a card tinted with a state's own colour, kept out of the view so the mistake it exists to
// prevent can be tested rather than discovered in a screenshot.
//
// Theme surface brushes are usually translucent: the dark theme's card fill is white at roughly 5% *over the window
// behind it*. Reading only its colour says "white", so tinting against it produces a near-white card in dark mode —
// with the theme's own near-white text on top, which is unreadable. Flatten first, always.
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public Rgba Opaque => this with { A = 255 };
}

public static class CardTinting
{
    // A translucent colour resolved against whatever is actually behind it.
    public static Rgba Flatten(Rgba over, Rgba under)
        => over.A == 255 ? over : Mix(over.Opaque, under, over.A / 255.0);

    // amount 0 returns onto, 1 returns colour.
    public static Rgba Mix(Rgba colour, Rgba onto, double amount)
    {
        var clamped = Math.Clamp(amount, 0, 1);
        static byte Step(byte from, byte to, double amount) => (byte)Math.Round(from + (to - from) * amount);
        return new(Step(onto.R, colour.R, clamped), Step(onto.G, colour.G, clamped), Step(onto.B, colour.B, clamped));
    }

    public static double Lightness(Rgba colour) => 0.2126 * colour.R + 0.7152 * colour.G + 0.0722 * colour.B;

    // Whether ink on this colour should be dark. The theme's on-accent brush assumes the system accent; a red critical
    // fill is not that, and black on red is the pairing that fails first.
    public static bool PrefersDarkInk(Rgba colour) => Lightness(colour) > 150;
}
