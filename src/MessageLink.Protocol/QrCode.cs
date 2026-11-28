using System.Globalization;
using System.Text;

namespace MessageLink.Protocol;

/// <summary>
/// A QR code (ISO/IEC 18004) of some text in byte mode, as the server's page and Manage server draw a join code (<see cref="JoinLink"/>, SWITCHBOARD.md 10.4) and the
/// Tailscale sign-in link (10.8). Made here rather than by a library or a script: the page runs no script and loads nothing from elsewhere, so the code is drawn by the
/// server as SVG. The smallest version that holds the text at the asked error correction is used, then the mask with the lowest penalty.
/// </summary>
public sealed class QrCode
{
    /// <summary>How much of the code can be lost and still read: about 7, 15, 25 and 30 percent.</summary>
    public enum Correction { Low, Medium, Quartile, High }

    /// <summary>The modules on a side (21 for version 1, four more for each version after it).</summary>
    public int Size { get; }

    /// <summary>The version, 1 to 40.</summary>
    public int Version { get; }

    private readonly bool[,] dark;
    private readonly bool[,] function;

    /// <summary>Whether the module at column <paramref name="x"/>, row <paramref name="y"/> is dark. Outside the code is light.</summary>
    public bool this[int x, int y] => x >= 0 && y >= 0 && x < Size && y < Size && dark[y, x];

    private QrCode(int version)
    {
        Version = version;
        Size = version * 4 + 17;
        dark = new bool[Size, Size];
        function = new bool[Size, Size];
    }

    /// <summary>The code of <paramref name="text"/> as UTF-8 bytes. Throws <see cref="ArgumentException"/> when it is too long for any version.</summary>
    public static QrCode Encode(string text, Correction correction = Correction.Medium) => Encode(Encoding.UTF8.GetBytes(text), correction);

    /// <summary>The code of <paramref name="data"/> in byte mode.</summary>
    public static QrCode Encode(byte[] data, Correction correction = Correction.Medium)
    {
        var version = 1;
        for (; version <= 40; version++)
            if (4 + CountBits(version) + data.Length * 8 <= DataCodewords(version, correction) * 8) break;
        if (version > 40) throw new ArgumentException("The text is too long for a QR code.", nameof(data));

        var capacity = DataCodewords(version, correction) * 8;
        var bits = new List<bool>(capacity);
        void Put(int value, int count) { for (var i = count - 1; i >= 0; i--) bits.Add((value >> i & 1) != 0); }
        Put(0b0100, 4);
        Put(data.Length, CountBits(version));
        foreach (var b in data) Put(b, 8);
        Put(0, Math.Min(4, capacity - bits.Count));
        Put(0, (8 - bits.Count % 8) % 8);
        for (var pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Put(pad, 8);
        var codewords = new byte[bits.Count / 8];
        for (var i = 0; i < bits.Count; i++) if (bits[i]) codewords[i >> 3] |= (byte)(0x80 >> (i & 7));

        var code = new QrCode(version);
        code.DrawFunctionPatterns();
        code.DrawCodewords(code.Interleave(codewords, correction));
        var best = 0;
        var lowest = long.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            code.ApplyMask(mask);
            code.DrawFormat(correction, mask);
            var penalty = code.Penalty();
            if (penalty < lowest) { lowest = penalty; best = mask; }
            code.ApplyMask(mask);
        }
        code.ApplyMask(best);
        code.DrawFormat(correction, best);
        return code;
    }

    /// <summary>
    /// The code as an SVG image: a light square with <paramref name="border"/> modules of quiet zone around it and the dark modules as one path, one unit to a module,
    /// so it scales to any size crisply. Dark on light whatever the page's colours: a reader needs that contrast.
    /// </summary>
    public string ToSvg(int border = 4, string? label = null)
    {
        var whole = Size + border * 2;
        var svg = new StringBuilder();
        svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ").Append(whole).Append(' ').Append(whole).Append("\" shape-rendering=\"crispEdges\"");
        svg.Append(label is null ? " aria-hidden=\"true\"" : " role=\"img\" aria-label=\"" + System.Net.WebUtility.HtmlEncode(label) + "\"").Append('>');
        svg.Append("<rect width=\"100%\" height=\"100%\" fill=\"#fff\"/><path fill=\"#000\" d=\"");
        // Each row's runs of dark modules as one rectangle each.
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                if (!dark[y, x]) continue;
                var run = 1;
                while (x + run < Size && dark[y, x + run]) run++;
                svg.Append('M').Append((x + border).ToString(CultureInfo.InvariantCulture)).Append(' ').Append((y + border).ToString(CultureInfo.InvariantCulture))
                    .Append('h').Append(run.ToString(CultureInfo.InvariantCulture)).Append("v1h-").Append(run.ToString(CultureInfo.InvariantCulture)).Append('z');
                x += run;
            }
        return svg.Append("\"/></svg>").ToString();
    }

    // ---- Function patterns ----

    private void Set(int x, int y, bool value)
    {
        dark[y, x] = value;
        function[y, x] = true;
    }

    private void DrawFunctionPatterns()
    {
        for (var i = 0; i < Size; i++)
        {
            Set(6, i, i % 2 == 0);
            Set(i, 6, i % 2 == 0);
        }
        Finder(3, 3);
        Finder(Size - 4, 3);
        Finder(3, Size - 4);
        var positions = AlignmentPositions();
        for (var i = 0; i < positions.Length; i++)
            for (var j = 0; j < positions.Length; j++)
            {
                // Not over the three finders.
                var last = positions.Length - 1;
                if (i == 0 && j == 0 || i == 0 && j == last || i == last && j == 0) continue;
                for (var dy = -2; dy <= 2; dy++)
                    for (var dx = -2; dx <= 2; dx++)
                        Set(positions[i] + dx, positions[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        // Reserved now, written once the mask is chosen.
        DrawFormat(Correction.Low, 0);
        if (Version >= 7)
        {
            var remainder = Version;
            for (var i = 0; i < 12; i++) remainder = remainder << 1 ^ (remainder >> 11) * 0x1F25;
            var bits = Version << 12 | remainder;
            for (var i = 0; i < 18; i++)
            {
                var bit = (bits >> i & 1) != 0;
                int a = Size - 11 + i % 3, b = i / 3;
                Set(a, b, bit);
                Set(b, a, bit);
            }
        }
    }

    private void Finder(int cx, int cy)
    {
        for (var dy = -4; dy <= 4; dy++)
            for (var dx = -4; dx <= 4; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || y < 0 || x >= Size || y >= Size) continue;
                var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                Set(x, y, distance != 2 && distance != 4);
            }
    }

    private int[] AlignmentPositions()
    {
        if (Version == 1) return [];
        var count = Version / 7 + 2;
        var step = (Version * 8 + count * 3 + 5) / (count * 4 - 4) * 2;
        var positions = new int[count];
        positions[0] = 6;
        for (int i = count - 1, at = Size - 7; i >= 1; i--, at -= step) positions[i] = at;
        return positions;
    }

    private static readonly int[] FormatWord = [1, 0, 3, 2];

    private void DrawFormat(Correction correction, int mask)
    {
        var data = FormatWord[(int)correction] << 3 | mask;
        var remainder = data;
        for (var i = 0; i < 10; i++) remainder = remainder << 1 ^ (remainder >> 9) * 0x537;
        var bits = (data << 10 | remainder) ^ 0x5412;
        bool Bit(int i) => (bits >> i & 1) != 0;
        for (var i = 0; i <= 5; i++) Set(8, i, Bit(i));
        Set(8, 7, Bit(6));
        Set(8, 8, Bit(7));
        Set(7, 8, Bit(8));
        for (var i = 9; i < 15; i++) Set(14 - i, 8, Bit(i));
        for (var i = 0; i < 8; i++) Set(Size - 1 - i, 8, Bit(i));
        for (var i = 8; i < 15; i++) Set(8, Size - 15 + i, Bit(i));
        Set(8, Size - 8, true);
    }

    // ---- Data ----

    private void DrawCodewords(byte[] data)
    {
        var i = 0;
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (var vertical = 0; vertical < Size; vertical++)
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = (right + 1 & 2) == 0;
                    var y = upward ? Size - 1 - vertical : vertical;
                    if (function[y, x] || i >= data.Length * 8) continue;
                    dark[y, x] = (data[i >> 3] >> 7 - (i & 7) & 1) != 0;
                    i++;
                }
        }
    }

    private void ApplyMask(int mask)
    {
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                if (function[y, x]) continue;
                var flip = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (flip) dark[y, x] = !dark[y, x];
            }
    }

    private static readonly bool[] FinderLike = [true, false, true, true, true, false, true, false, false, false, false];

    // The standard's four penalties: runs of five or more, 2x2 blocks, finder-like patterns, and dark modules away from half.
    private long Penalty()
    {
        long penalty = 0;
        for (var pass = 0; pass < 2; pass++)
            for (var a = 0; a < Size; a++)
            {
                var run = 1;
                for (var b = 1; b <= Size; b++)
                {
                    if (b < Size && At(pass, a, b) == At(pass, a, b - 1)) { run++; continue; }
                    if (run >= 5) penalty += 3 + run - 5;
                    run = 1;
                }
                for (var b = 0; b + FinderLike.Length <= Size; b++)
                {
                    bool forward = true, backward = true;
                    for (var k = 0; k < FinderLike.Length; k++)
                    {
                        var module = At(pass, a, b + k);
                        forward &= module == FinderLike[k];
                        backward &= module == FinderLike[FinderLike.Length - 1 - k];
                    }
                    if (forward) penalty += 40;
                    if (backward) penalty += 40;
                }
            }
        for (var y = 0; y + 1 < Size; y++)
            for (var x = 0; x + 1 < Size; x++)
            {
                var c = dark[y, x];
                if (c == dark[y, x + 1] && c == dark[y + 1, x] && c == dark[y + 1, x + 1]) penalty += 3;
            }
        var darkCount = 0;
        foreach (var module in dark) if (module) darkCount++;
        var total = Size * Size;
        penalty += ((Math.Abs(darkCount * 20 - total * 10) + total - 1) / total - 1) * 10;
        return penalty;
    }

    // A row (pass 0) or a column (pass 1).
    private bool At(int pass, int line, int along) => pass == 0 ? dark[line, along] : dark[along, line];

    // ---- Error correction ----

    private byte[] Interleave(byte[] data, Correction correction)
    {
        var blocks = Blocks[(int)correction][Version];
        var eccLength = EccPerBlock[(int)correction][Version];
        var raw = RawModules(Version) / 8;
        var shortBlocks = blocks - raw % blocks;
        var shortLength = raw / blocks;
        var divisor = Divisor(eccLength);
        var parts = new byte[blocks][];
        for (int i = 0, k = 0; i < blocks; i++)
        {
            var length = shortLength - eccLength + (i < shortBlocks ? 0 : 1);
            var block = new byte[shortLength + 1];
            Array.Copy(data, k, block, 0, length);
            k += length;
            var ecc = Remainder(data.AsSpan(k - length, length), divisor);
            // A short block keeps a gap where the long ones have their last data codeword, skipped below.
            Array.Copy(ecc, 0, block, shortLength + 1 - eccLength, eccLength);
            parts[i] = block;
        }
        var result = new List<byte>(raw);
        for (var i = 0; i < shortLength + 1; i++)
            for (var j = 0; j < blocks; j++)
                if (i != shortLength - eccLength || j >= shortBlocks) result.Add(parts[j][i]);
        return [.. result];
    }

    private static byte[] Divisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = Multiply(root, 0x02);
        }
        return result;
    }

    private static byte[] Remainder(ReadOnlySpan<byte> data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++) result[i] ^= Multiply(divisor[i], factor);
        }
        return result;
    }

    // In GF(2^8) modulo x^8 + x^4 + x^3 + x^2 + 1.
    private static byte Multiply(int x, int y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = z << 1 ^ (z >> 7) * 0x11D;
            z ^= (y >> i & 1) * x;
        }
        return (byte)z;
    }

    // ---- Capacity ----

    private static int CountBits(int version) => version < 10 ? 8 : 16;

    private static int RawModules(int version)
    {
        var result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var count = version / 7 + 2;
            result -= (25 * count - 10) * count - 55;
            if (version >= 7) result -= 36;
        }
        return result;
    }

    private static int DataCodewords(int version, Correction correction) =>
        RawModules(version) / 8 - EccPerBlock[(int)correction][version] * Blocks[(int)correction][version];

    // Per correction level, indexed by version (0 unused): the error correction codewords in each block, and the number of blocks (ISO/IEC 18004 table 9).
    private static readonly int[][] EccPerBlock =
    [
        [-1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28],
        [-1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [-1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
    ];

    private static readonly int[][] Blocks =
    [
        [-1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25],
        [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49],
        [-1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68],
        [-1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81],
    ];
}
