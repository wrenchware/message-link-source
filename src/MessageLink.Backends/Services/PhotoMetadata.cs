using System.Buffers.Binary;

namespace MessageLink.App.Services;

// A photo's metadata taken out without touching its picture: the JPEG, PNG, GIF or BMP comes back decoding to the same
// pixels (a JPEG's scans copied byte for byte, a PNG's rows compressed again, a GIF's and BMP's image data cut to what a
// decoder reads), and with nothing that
// says where, when or with what it was taken (EXIF with its GPS block, XMP, IPTC, comments, text chunks, maker blocks,
// embedded thumbnails, and whatever a camera appends after the end of the image, such as a second picture, an HDR gain
// map or a Samsung trailer).
//
// Bytes a decoder never reads count as metadata too, since they travel unseen: a segment, chunk or block longer than its
// contents, zlib data past its stream or past the rows, LZW data past its end code, a colour profile's gaps, a BMP's
// padding and palette reserved bytes. Reviews showed private bytes surviving in such places in files that still decoded.
// What is left unread on purpose: a JPEG scan's entropy-coded data is copied whole, because finding where its last block
// ends means decoding every block; PNG and GIF palette entries no pixel uses (a BMP's are zeroed); and the unused low
// bits at the end of a PNG row under 8 bits a pixel.
// The decision of 2026-10-09 (SWITCHBOARD.md "Decisions") is that a Google Voice photo never carries its
// location; this is how a photo small enough to send as it is keeps its quality and still goes without it.
//
// What is kept is what drawing the picture needs, listed rather than everything except the known metadata, so that a
// block nobody here has heard of is dropped, or the file refused, rather than sent:
// - JPEG: the tables, frames and scans; the JFIF header (with its thumbnail cut off) and Adobe's APP14 (its standard
//   12 bytes), which decide how a decoder reads the colours; and a stock ICC colour profile (PhotoMetadata.Icc.cs).
// - PNG: the critical chunks and the ones that change how it is drawn (transparency, gamma, colour space, a stock
//   profile under a fixed name, background, pixel size, animation). Text, eXIf, time stamps and private chunks go.
// - GIF: the frames, their timing and the loop count. Comments and every other application block (XMP and ICC among
//   them) go.
// - BMP: the file and bitmap headers, masks, palette and pixels, rebuilt without anything between or after them, with the image size
//   and important-colours count rewritten, the masks a BI_RGB V4/V5 header ignores zeroed, and a file whose 16 or 32-bit pixels carry
//   bits no mask covers (other than one empty or full value throughout) re-encoded.
//
// A photo stored turned (EXIF orientation 2 to 8) would show sideways once its EXIF is gone, so it gets a new EXIF block
// holding the orientation and nothing else, always big-endian so even the byte order says nothing about the camera. That
// is one number from 1 to 8 and keeps the picture lossless; turning it through the re-encoder instead would recompress it.
//
// Null means the file is not one this reads to the end with confidence (a broken segment, a marker this does not know,
// an EXIF block whose orientation cannot be found, a colour profile that is not a stock one, a file that is not what its
// type says, TIFF): the caller then re-encodes it, which writes no metadata, rather than sending it as it is.
internal static partial class PhotoMetadata
{
    /// <summary>The photo without its metadata. <paramref name="Turned"/> says a minimal orientation block was written.</summary>
    internal sealed record Stripped(byte[] Bytes, string Kind, bool Turned);

    public static Stripped? Strip(byte[] image, string mimeType)
    {
        try
        {
            return mimeType.ToLowerInvariant() switch
            {
                "image/jpeg" => Jpeg(image),
                "image/png" => Png(image),
                "image/gif" => Gif(image),
                "image/bmp" => Bmp(image),
                // TIFF keeps EXIF, GPS and XMP as directories pointing anywhere in the file; rewriting one safely is a
                // TIFF writer's job, so it is re-encoded.
                _ => null,
            };
        }
        // A length that runs off the end, and anything else malformed, is a file this cannot vouch for.
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException) { return null; }
    }

    // ---- JPEG ----------------------------------------------------------------------------------------------------------

    private static Stripped? Jpeg(byte[] source)
    {
        var s = source.AsSpan();
        if (s.Length < 4 || s[0] != 0xFF || s[1] != 0xD8 || s[2] != 0xFF) return null;
        using var output = new MemoryStream(source.Length);
        output.Write([0xFF, 0xD8]);
        var at = 2;
        var slot = 2;
        var scanned = false;
        var inScan = false;
        ushort? orientation = null;
        var profile = new List<(byte Sequence, byte Count, byte[] Data)>();
        while (true)
        {
            // A file that ends after its image data without an end marker is kept as it is, as decoders keep one (some
            // cameras write them); one that ends before any image data is not a photo.
            if (at >= s.Length) { if (!scanned) return null; break; }
            // After a restart marker the scan's data goes on.
            if (inScan && s[at] != 0xFF) { at = Entropy(s, at, output); continue; }
            if (s[at] != 0xFF) return null;
            // Fill bytes before a marker are padding with no meaning, and are not copied.
            var m = at + 1;
            while (m < s.Length && s[m] == 0xFF) m++;
            if (m >= s.Length) return null;
            var marker = s[m];
            at = m + 1;
            // Anything after the end marker (a second picture, a gain map, a maker's trailer) is never reached.
            if (marker == 0xD9) { if (!scanned) return null; output.Write([0xFF, 0xD9]); break; }
            if (marker is >= 0xD0 and <= 0xD7)
            {
                if (!inScan) return null;
                output.Write([0xFF, marker]);
                continue;
            }
            inScan = false;
            if (marker == 0x01) { output.Write([0xFF, marker]); continue; }
            if (at + 2 > s.Length) return null;
            int length = BinaryPrimitives.ReadUInt16BigEndian(s[at..]);
            if (length < 2 || at + length > s.Length) return null;
            var body = s.Slice(at + 2, length - 2);
            var end = at + length;
            if (marker == 0xDA)
            {
                // Its components, then the spectral selection and approximation: a longer header carries bytes nobody reads.
                if (body.Length < 1 || body.Length != 4 + 2 * body[0]) return null;
                output.Write([0xFF, marker]);
                output.Write(s[at..end]);
                at = Entropy(s, end, output);
                scanned = inScan = true;
                continue;
            }
            switch (marker)
            {
                // JFIF, as its first 14 bytes: what follows them is an uncompressed thumbnail, which can be a picture of the
                // photo before it was cropped. Shorter is not a JFIF header a decoder reads, so it goes.
                case 0xE0 when body.Length >= 14 && body.StartsWith("JFIF\0"u8):
                    var first = output.Position == 2;
                    output.Write([0xFF, 0xE0, 0x00, 16]);
                    output.Write(body[..12]);
                    output.Write([0, 0]);
                    if (first) slot = (int)output.Position;
                    break;
                // Kept as they are once every part has been seen to be a stock profile, below.
                case 0xE2 when body.StartsWith("ICC_PROFILE\0"u8):
                    if (body.Length < 14) return null;
                    profile.Add((body[12], body[13], body[14..].ToArray()));
                    output.Write([0xFF, marker]);
                    output.Write(s.Slice(at, length));
                    break;
                // Adobe's 12 bytes: version, two flag words and the colour transform. Anything past them is not read.
                case 0xEE when body.Length >= 12 && body.StartsWith("Adobe"u8):
                    output.Write([0xFF, 0xEE, 0x00, 14]);
                    output.Write(body[..12]);
                    break;
                // The first EXIF block is the one viewers read the orientation from, as libjpeg's readers do.
                case 0xE1 when orientation is null && body.StartsWith("Exif\0\0"u8):
                    orientation = Orientation(body[6..]);
                    if (orientation is null) return null;
                    break;
                // Every other APPn (EXIF, XMP, MPF, IPTC, maker blocks) and COM.
                case >= 0xE0 and <= 0xEF or 0xFE:
                    break;
                // Frames (SOF0-SOF15 without the reserved C8), DHT and DAC among them; DQT, DNL, DRI, DHP and EXP.
                case (>= 0xC0 and <= 0xCF and not 0xC8) or (>= 0xDB and <= 0xDF):
                    if (!Fits(marker, body)) return null;
                    output.Write([0xFF, marker]);
                    output.Write(s.Slice(at, length));
                    break;
                // Reserved and extension markers: a decoder that meets one of these is not reading an ordinary photo.
                default:
                    return null;
            }
            at = end;
        }
        if (profile.Count > 0 && !StockProfile(profile)) return null;
        var bytes = output.ToArray();
        if (orientation is not (>= 2 and <= 8)) return new Stripped(bytes, "jpeg", false);
        // After SOI, or after JFIF when that is the first segment kept, since JFIF must follow SOI directly.
        var exif = MinimalExif(orientation.Value);
        return new Stripped([.. bytes.AsSpan(0, slot), 0xFF, 0xE1, 0x00, (byte)(exif.Length + 8), .. "Exif\0\0"u8, .. exif, .. bytes.AsSpan(slot)], "jpeg", true);
    }

    // Whether a kept segment is exactly as long as what it declares: a decoder reads a table or frame header by its own
    // counts and steps over the rest of the segment, so bytes past them would travel unseen.
    private static bool Fits(byte marker, ReadOnlySpan<byte> body)
    {
        switch (marker)
        {
            // DHT: per table, its class and id, 16 code counts and that many symbols.
            case 0xC4:
                for (var at = 0; at < body.Length;)
                {
                    if (at + 17 > body.Length) return false;
                    var symbols = 0;
                    for (var length = 1; length <= 16; length++) symbols += body[at + length];
                    at += 17 + symbols;
                    if (at > body.Length) return false;
                }
                return body.Length > 0;
            // DAC: pairs of a table and its value.
            case 0xCC:
                return body.Length > 0 && body.Length % 2 == 0;
            // DQT: per table, its precision and id, then 64 values of one or two bytes.
            case 0xDB:
                for (var at = 0; at < body.Length;)
                {
                    at += 1 + 64 * ((body[at] >> 4) == 0 ? 1 : 2);
                    if (at > body.Length) return false;
                }
                return body.Length > 0;
            // DNL and DRI: one number.
            case 0xDC or 0xDD:
                return body.Length == 2;
            // EXP: one byte.
            case 0xDF:
                return body.Length == 1;
            // A frame header (SOFn, and DHP, which has its shape): six bytes and three per component.
            default:
                return body.Length >= 6 && body.Length == 6 + 3 * body[5];
        }
    }

    // Copies a scan's entropy-coded data, which runs to the next 0xFF that is neither a stuffed 0xFF00 nor a restart
    // marker, and returns where that marker starts.
    private static int Entropy(ReadOnlySpan<byte> s, int at, MemoryStream output)
    {
        var end = at;
        while (end < s.Length)
        {
            if (s[end] == 0xFF && end + 1 < s.Length && s[end + 1] != 0x00 && s[end + 1] is not (>= 0xD0 and <= 0xD7)) break;
            end++;
        }
        output.Write(s[at..end]);
        return end;
    }

    // An ICC profile split across APP2 segments: every part numbered 1 to n once, and the whole a stock profile.
    private static bool StockProfile(List<(byte Sequence, byte Count, byte[] Data)> parts)
    {
        var count = parts[0].Count;
        if (count == 0 || parts.Count != count || parts.Any(part => part.Count != count)) return false;
        var ordered = parts.OrderBy(part => part.Sequence).ToList();
        for (var index = 0; index < count; index++) if (ordered[index].Sequence != index + 1) return false;
        return IsStockProfile(ordered.SelectMany(part => part.Data).ToArray());
    }

    // ---- PNG -----------------------------------------------------------------------------------------------------------

    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10];

    // What drawing needs. mDCV/cLLI are the third edition's names for the HDR chunks; mDCv/cLLi the drafts' that shipped.
    // iCCP is handled on its own, below.
    private static readonly HashSet<string> PngKept =
        ["IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "cHRM", "sRGB", "cICP", "mDCV", "mDCv", "cLLI", "cLLi", "sBIT", "bKGD", "pHYs", "acTL", "fcTL", "fdAT"];

    // The image data is written again rather than copied. A zlib stream can be followed by bytes inflating never reaches,
    // and can inflate to more than the picture's rows; decoders ignore both, so private bytes could ride along in a file
    // that still decodes (a review's PNG did). Inflating exactly the rows the header declares and compressing them again
    // keeps every pixel and nothing else. Every other kept chunk must be exactly its declared size, for the same reason.
    private static Stripped? Png(byte[] source)
    {
        var s = source.AsSpan();
        if (s.Length < 8 + 12 || !s.StartsWith(PngSignature)) return null;
        var chunks = new List<(string Name, byte[] Content)>();
        ushort? orientation = null;
        var at = 8;
        while (true)
        {
            if (at + 12 > s.Length) return null;
            var length = BinaryPrimitives.ReadUInt32BigEndian(s[at..]);
            if (length > int.MaxValue || at + 12 + (long)length > s.Length) return null;
            var type = s.Slice(at + 4, 4);
            foreach (var letter in type) if (letter is not (>= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z')) return null;
            var name = System.Text.Encoding.ASCII.GetString(type);
            var content = s.Slice(at + 8, (int)length);
            if ((chunks.Count == 0) != (name == "IHDR")) return null;
            if (PngKept.Contains(name)) chunks.Add((name, content.ToArray()));
            else if (name == "iCCP")
            {
                // The profile's name is free text (an editor writes the display's, which can be a person's): a stock profile
                // goes under a fixed one. Any other profile means re-encoding.
                var end = content.IndexOf((byte)0);
                if (end is < 1 or > 79 || end + 2 > content.Length || content[end + 1] != 0) return null;
                if (Inflate(content[(end + 2)..], 1 << 16) is not { } profile || !IsStockProfile(profile)) return null;
                chunks.Add((name, profile));
            }
            else if (name == "eXIf")
            {
                orientation ??= Orientation(content);
                if (orientation is null) return null;
            }
            // A critical chunk (capital first letter) this does not know: a decoder must refuse the file, so this does too.
            else if (char.IsUpper(name[0])) return null;
            at += 12 + (int)length;
            if (name == "IEND") break;
        }

        var header = chunks[0].Content;
        if (header.Length != 13) return null;
        var width = BinaryPrimitives.ReadInt32BigEndian(header);
        var height = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
        var (depth, colour, interlace) = (header[8], header[9], header[12]);
        var channels = colour switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        var depthFits = colour switch { 0 => depth is 1 or 2 or 4 or 8 or 16, 3 => depth is 1 or 2 or 4 or 8, _ => depth is 8 or 16 };
        if (width <= 0 || height <= 0 || channels == 0 || !depthFits || header[10] != 0 || header[11] != 0 || interlace > 1) return null;
        var bits = channels * depth;
        if (!chunks.Any(chunk => chunk.Name == "IDAT")) return null;

        using var output = new MemoryStream(source.Length);
        output.Write(PngSignature);
        // fcTL and fdAT share one sequence, which starts again at 0 because fdATs of a frame are written as one.
        var sequence = 0u;
        var palette = 0;
        var (frameWidth, frameHeight) = (0, 0);
        for (var index = 0; index < chunks.Count;)
        {
            var (name, content) = chunks[index];
            switch (name)
            {
                case "IHDR":
                    WritePngChunk(output, name, content);
                    // Straight after IHDR, so it precedes IDAT as readers expect.
                    if (orientation is >= 2 and <= 8) WritePngChunk(output, "eXIf", MinimalExif(orientation.Value));
                    break;
                case "IDAT" or "fdAT":
                {
                    // A frame's chunks are consecutive; each fdAT starts with its sequence number.
                    var run = new MemoryStream();
                    var next = index;
                    for (; next < chunks.Count && chunks[next].Name == name; next++)
                    {
                        if (name == "fdAT" && chunks[next].Content.Length < 4) return null;
                        run.Write(chunks[next].Content.AsSpan(name == "fdAT" ? 4 : 0));
                    }
                    var rows = name == "IDAT" ? Rows(run.ToArray(), width, height, bits, interlace) : frameWidth == 0 ? null : Rows(run.ToArray(), frameWidth, frameHeight, bits, interlace);
                    if (rows is null) return null;
                    var compressed = Deflate(rows);
                    if (name == "IDAT") WritePngChunk(output, name, compressed);
                    else
                    {
                        var numbered = new byte[4 + compressed.Length];
                        BinaryPrimitives.WriteUInt32BigEndian(numbered, sequence++);
                        compressed.CopyTo(numbered, 4);
                        WritePngChunk(output, name, numbered);
                    }
                    index = next;
                    continue;
                }
                case "fcTL":
                {
                    if (content.Length != 26) return null;
                    frameWidth = BinaryPrimitives.ReadInt32BigEndian(content.AsSpan(4));
                    frameHeight = BinaryPrimitives.ReadInt32BigEndian(content.AsSpan(8));
                    if (frameWidth <= 0 || frameHeight <= 0 || frameWidth > width || frameHeight > height) return null;
                    var renumbered = content.ToArray();
                    BinaryPrimitives.WriteUInt32BigEndian(renumbered, sequence++);
                    WritePngChunk(output, name, renumbered);
                    break;
                }
                case "iCCP":
                    WritePngChunk(output, name, [.. "ICC Profile\0\0"u8, .. Deflate(content)]);
                    break;
                default:
                    if (name == "PLTE") palette = content.Length / 3;
                    if (!PngFits(name, content.Length, colour, depth, palette)) return null;
                    WritePngChunk(output, name, content);
                    break;
            }
            index++;
        }
        return new Stripped(output.ToArray(), "png", orientation is >= 2 and <= 8);
    }

    // Whether an ancillary or palette chunk is exactly the size its kind has for this colour type.
    private static bool PngFits(string name, int length, byte colour, byte depth, int palette) => name switch
    {
        "PLTE" => colour is 2 or 3 or 6 && length > 0 && length % 3 == 0 && length / 3 <= (colour == 3 ? 1 << depth : 256),
        "tRNS" => colour switch { 0 => length == 2, 2 => length == 6, 3 => length >= 1 && length <= palette, _ => false },
        "sBIT" => length == (colour switch { 0 => 1, 4 => 2, 6 => 4, _ => 3 }),
        "bKGD" => length == (colour switch { 0 or 4 => 2, 3 => 1, _ => 6 }),
        "gAMA" or "cICP" => length == 4,
        "cHRM" => length == 32,
        "sRGB" => length == 1,
        "mDCV" or "mDCv" => length == 24,
        "cLLI" or "cLLi" or "acTL" => length == 8,
        "pHYs" => length == 9,
        "IEND" => length == 0,
        _ => false,
    };

    // The filtered rows a width x height image of this many bits per pixel holds (Adam7's seven passes when interlaced),
    // inflated from the image's zlib stream and nothing past them; null when the stream is broken or holds fewer, or a row
    // names a filter that does not exist.
    private static byte[]? Rows(byte[] compressed, int width, int height, int bits, byte interlace)
    {
        (int X, int Y, int DX, int DY)[] passes = interlace == 0 ? [(0, 0, 1, 1)] : [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];
        var shapes = new List<(long Rows, long Bytes)>();
        long size = 0;
        foreach (var (x, y, dx, dy) in passes)
        {
            long across = (width - x + dx - 1) / dx, down = (height - y + dy - 1) / dy;
            if (across <= 0 || down <= 0) continue;
            var row = 1 + (across * bits + 7) / 8;
            shapes.Add((down, row));
            size += down * row;
        }
        // Past what the re-encoder would decode at all (64 million pixels), so not worth inflating.
        if (size > 256L << 20) return null;
        var rows = Inflate(compressed, (int)size);
        if (rows is null || rows.Length != size) return null;
        var at = 0L;
        foreach (var (down, row) in shapes)
            for (var line = 0; line < down; line++, at += row)
                if (rows[at] > 4) return null;
        return rows;
    }

    private static byte[] Deflate(ReadOnlySpan<byte> bytes)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(bytes);
        return compressed.ToArray();
    }

    private static void WritePngChunk(MemoryStream output, string type, ReadOnlySpan<byte> content)
    {
        byte[] typed = [.. System.Text.Encoding.ASCII.GetBytes(type), .. content];
        var number = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, content.Length);
        output.Write(number);
        output.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typed));
        output.Write(number);
    }

    // Up to the first `most` bytes a zlib stream inflates to (one more when there are more, so a caller can tell), or null
    // when it is broken.
    private static byte[]? Inflate(ReadOnlySpan<byte> compressed, int most)
    {
        try
        {
            using var zlib = new System.IO.Compression.ZLibStream(new MemoryStream(compressed.ToArray()), System.IO.Compression.CompressionMode.Decompress);
            var inflated = new byte[most];
            var filled = 0;
            int read;
            while (filled < most && (read = zlib.Read(inflated, filled, most - filled)) > 0) filled += read;
            return filled == most ? inflated : inflated[..filled];
        }
        catch (InvalidDataException) { return null; }
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    // ---- GIF -----------------------------------------------------------------------------------------------------------

    // Rebuilt block by block: the screen descriptor and colour tables, each frame's control block (exactly its 4 bytes) and
    // image, and the loop count in its standard form. Each image's LZW data is cut after its end code, with the unused bits
    // of that last byte cleared, so nothing a decoder never reads goes along; an image that decodes to more pixels than it
    // has room for (the excess is ignored, so it could hold anything) is not read. Comments, XMP, the ICC profile (which
    // hardly any decoder applies to a GIF, so dropping it costs nothing and keeps the animation) and plain text blocks
    // (which almost no decoder draws, so their text is hidden in practice) go, the last with the control block before it.
    private static Stripped? Gif(byte[] source)
    {
        var s = source.AsSpan();
        if (s.Length < 13 || !(s.StartsWith("GIF87a"u8) || s.StartsWith("GIF89a"u8))) return null;
        var at = 13 + ((s[10] & 0x80) != 0 ? 3 << ((s[10] & 7) + 1) : 0);
        if (at > s.Length) return null;
        using var output = new MemoryStream(source.Length);
        output.Write(s[..at]);
        var frames = 0;
        byte[]? control = null;
        var blocks = new List<Range>();
        while (true)
        {
            // Ending at a block boundary without the trailer is how some encoders leave a GIF; decoders show it, so it is kept.
            if (at >= s.Length) break;
            var start = at;
            switch (s[at])
            {
                case 0x3B:
                    output.WriteByte(0x3B);
                    return frames > 0 ? new Stripped(output.ToArray(), "gif", false) : null;
                // A stray terminator between blocks, which some encoders leave and decoders step over.
                case 0x00:
                    at++;
                    break;
                case 0x2C:
                {
                    if (at + 11 > s.Length) return null;
                    var descriptor = at + 10 + ((s[at + 9] & 0x80) != 0 ? 3 << ((s[at + 9] & 7) + 1) : 0);
                    if (descriptor + 1 > s.Length) return null;
                    var minimum = s[descriptor];
                    at = SubBlocks(s, descriptor + 1, blocks);
                    if (at < 0 || minimum is < 2 or > 11) return null;
                    var data = new MemoryStream();
                    foreach (var block in blocks) data.Write(s[block]);
                    var lzw = data.ToArray();
                    var pixels = (long)BinaryPrimitives.ReadUInt16LittleEndian(s[(start + 5)..]) * BinaryPrimitives.ReadUInt16LittleEndian(s[(start + 7)..]);
                    var kept = LzwLength(lzw, minimum, pixels);
                    if (kept < 0) return null;
                    if (control is not null) output.Write(control);
                    control = null;
                    output.Write(s[start..(descriptor + 1)]);
                    foreach (var piece in lzw.AsSpan(0, kept).ToArray().Chunk(255)) { output.WriteByte((byte)piece.Length); output.Write(piece); }
                    output.WriteByte(0);
                    frames++;
                    break;
                }
                case 0x21:
                {
                    if (at + 2 > s.Length) return null;
                    var label = s[at + 1];
                    at = SubBlocks(s, at + 2, blocks);
                    if (at < 0) return null;
                    if (label == 0xF9)
                    {
                        if (blocks.Count != 1 || s[blocks[0]].Length != 4) return null;
                        control = [0x21, 0xF9, 4, .. s[blocks[0]], 0];
                    }
                    else if (label == 0x01) control = null;
                    else if (label == 0xFF && blocks.Count == 2 && s[blocks[0]].Length == 11 && s[blocks[1]] is [1, _, _]
                        && (s[blocks[0]].SequenceEqual("NETSCAPE2.0"u8) || s[blocks[0]].SequenceEqual("ANIMEXTS1.0"u8)))
                        output.Write([0x21, 0xFF, 11, .. s[blocks[0]], 3, .. s[blocks[1]], 0]);
                    break;
                }
                default:
                    return null;
            }
        }
        return frames > 0 ? new Stripped(output.ToArray(), "gif", false) : null;

        // The position after a run of data sub-blocks and its terminator, with each sub-block's data, or -1 when it runs off
        // the end.
        static int SubBlocks(ReadOnlySpan<byte> s, int at, List<Range> blocks)
        {
            blocks.Clear();
            while (at < s.Length)
            {
                var size = s[at++];
                if (size == 0) return at;
                if (at + size > s.Length) return -1;
                blocks.Add(at..(at + size));
                at += size;
            }
            return -1;
        }
    }

    // How many bytes of a GIF image's LZW data a decoder reads (through the end code, or every whole code when there is
    // none), clearing the unused high bits of the last one, or -1 when the codes are invalid or make more than `pixels`.
    // Only each code's length in pixels is tracked, which is all the reading needs.
    private static int LzwLength(byte[] data, int minimum, long pixels)
    {
        var clear = 1 << minimum;
        var end = clear + 1;
        var width = minimum + 1;
        var next = end + 1;
        var previous = -1;
        var lengths = new int[4096];
        long made = 0;
        long position = 0;
        while (position + width <= data.Length * 8L)
        {
            var code = 0;
            for (var bit = 0; bit < width; bit++, position++)
                code |= ((data[position >> 3] >> (int)(position & 7)) & 1) << bit;
            if (code == clear) { width = minimum + 1; next = end + 1; previous = -1; continue; }
            if (code == end) break;
            int length;
            if (previous < 0)
            {
                if (code > clear) return -1;
                length = 1;
            }
            else
            {
                var before = previous < clear ? 1 : lengths[previous];
                if (code < clear) length = 1;
                else if (code < next) length = lengths[code];
                else if (code == next && next < 4096) length = before + 1;
                else return -1;
                if (next < 4096)
                {
                    lengths[next++] = before + 1;
                    if (next == 1 << width && width < 12) width++;
                }
            }
            made += length;
            if (made > pixels) return -1;
            previous = code;
        }
        var kept = (int)((position + 7) / 8);
        if (position % 8 != 0) data[kept - 1] &= (byte)((1 << (int)(position % 8)) - 1);
        return kept;
    }

    // ---- BMP -----------------------------------------------------------------------------------------------------------

    // Rebuilt from its parts: the file header with its reserved words zeroed, the bitmap header, masks, palette and the
    // pixels cut to the size they declare. Whatever sat between the palette and the pixels, or after the pixels, is left
    // behind. A V4/V5 header that links a colour profile names a file on the sender's disk (C:\Users\<name>\...) and one
    // that embeds a profile carries it in that gap, so both are re-encoded; so are run-length data and the compressions that hold a JPEG or PNG.
    private static Stripped? Bmp(byte[] source)
    {
        var s = source.AsSpan();
        if (s.Length < 14 + 40 || s[0] != 'B' || s[1] != 'M') return null;
        var pixelsAt = BinaryPrimitives.ReadUInt32LittleEndian(s[10..]);
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(s[14..]);
        if (headerSize is not (40 or 108 or 124)) return null;
        var width = BinaryPrimitives.ReadInt32LittleEndian(s[18..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(s[22..]);
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(s[26..]);
        var depth = BinaryPrimitives.ReadUInt16LittleEndian(s[28..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(s[30..]);
        var colours = BinaryPrimitives.ReadUInt32LittleEndian(s[46..]);
        if (width <= 0 || height is 0 or int.MinValue || planes != 1 || depth is not (1 or 4 or 8 or 16 or 24 or 32)) return null;
        // Masks follow a 40-byte header for bit fields; V4 and V5 hold them inside the header.
        int masks;
        switch (compression)
        {
            // Run-length data (1 and 2) is not taken: what follows its end-of-bitmap code inside the declared size is never
            // read, and finding that code means decoding it, so such a BMP is re-encoded.
            case 0: masks = 0; break;
            case 3 when depth is 16 or 32: masks = headerSize == 40 ? 12 : 0; break;
            case 6 when depth is 16 or 32: masks = headerSize == 40 ? 16 : 0; break;
            default: return null;
        }
        // A V4/V5 colour space type is calibrated (0), sRGB or Windows': 'LINK' and 'MBED' point at a profile, and any other value is four bytes of a field no decoder has a meaning for,
        // so the file is re-encoded. V5's rendering intent is likewise one of the four intents or none.
        if (headerSize >= 108 && BinaryPrimitives.ReadUInt32LittleEndian(s[(14 + 56)..]) is not (0u or 0x73524742u or 0x57696E20u)) return null;
        if (headerSize == 124 && BinaryPrimitives.ReadUInt32LittleEndian(s[(14 + 108)..]) is not (0u or 1u or 2u or 4u or 8u)) return null;
        // A palette above 8 bits a pixel is only a hint for displays of few colours and nothing draws it: it goes, and its count with it.
        var entries = depth > 8 ? 0u : colours == 0 ? 1u << depth : colours;
        if (entries > 256 || depth <= 8 && entries > 1u << depth) return null;
        var tables = 14 + headerSize + masks + (int)entries * 4;
        var stride = ((long)width * depth + 31) / 32 * 4;
        var rows = Math.Abs((long)height);
        var pixels = stride * rows;
        if (pixels <= 0 || tables > pixelsAt || pixelsAt + pixels > s.Length) return null;
        var bytes = new byte[tables + pixels];
        s[..tables].CopyTo(bytes);
        s.Slice((int)pixelsAt, (int)pixels).CopyTo(bytes.AsSpan(tables));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), (uint)tables);
        // The image size is not read for uncompressed data (it may be 0), and the important-colours count is never drawn from: each a field that can hold anything.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14 + 20), (uint)pixels);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14 + 36), 0);
        if (depth > 8) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14 + 32), 0);
        // V4/V5 hold four colour masks that only BI_BITFIELDS reads.
        if (headerSize >= 108 && compression == 0) bytes.AsSpan(14 + 40, 16).Clear();
        // Bits of a 16 or 32-bit pixel that no colour mask covers are not drawn, but some decoders read them as alpha, so they cannot be zeroed without changing what such a decoder
        // shows, nor kept if they hold anything: the file is kept only when they are one value in every pixel, empty or full, which says nothing, and is re-encoded otherwise.
        if (depth is 16 or 32)
        {
            var covered = compression == 0 ? (depth == 16 ? 0x7FFFu : 0x00FFFFFFu) : 0u;
            // The masks sit at the same place whether the header is the 40-byte one (they follow it) or V4/V5 (they are inside it); the alpha mask is read from V4/V5, and from the
            // 40-byte header only with BI_ALPHABITFIELDS.
            if (compression != 0)
                for (var mask = 0; mask < (headerSize == 40 && compression == 3 ? 3 : 4); mask++)
                {
                    var at = 14 + 40 + mask * 4;
                    var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
                    // A 16-bit pixel has no bit above the 16th, so the upper half of its masks selects nothing.
                    if (depth == 16 && value > 0xFFFF) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value &= 0xFFFF);
                    covered |= value;
                }
            var all = depth == 16 ? 0xFFFFu : 0xFFFFFFFFu;
            var unused = all & ~covered;
            if (unused != 0)
            {
                var first = true;
                var empty = false;
                for (var row = 0L; row < rows; row++)
                    for (var x = 0; x < width; x++)
                    {
                        var at = (int)(tables + row * stride + (long)x * (depth / 8));
                        var bits = (depth == 16 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at)) : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at))) & unused;
                        if (bits != 0 && bits != unused) return null;
                        if (first) { empty = bits == 0; first = false; }
                        else if (empty != (bits == 0)) return null;
                    }
            }
        }
        // V4/V5's end points and gammas are read only for a calibrated colour space (0); otherwise they are 48 bytes nobody
        // reads. V5's profile offset, size and reserved word are unused without a linked or embedded profile.
        if (headerSize >= 108 && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(14 + 56)) != 0) bytes.AsSpan(14 + 60, 48).Clear();
        if (headerSize == 124) bytes.AsSpan(14 + 112, 12).Clear();
        // Each row's padding to four bytes, and the unused low bits of its last byte at 1 or 4 bits a pixel, are never drawn.
        var used = (long)width * depth;
        for (var row = 0L; row < rows; row++)
        {
            var line = bytes.AsSpan((int)(tables + row * stride), (int)stride);
            var free = (int)(used / 8);
            if (used % 8 != 0) line[free++] &= (byte)(0xFF << (int)(8 - used % 8));
            line[free..].Clear();
        }
        // A palette entry is blue, green, red and a reserved byte no decoder draws. Above 8 bits a pixel the palette is only
        // a hint for displays of few colours and nothing draws it at all; at 8 bits or fewer an entry no pixel names is never
        // drawn either. Each could carry text in a file that decodes the same, so they are zeroed: a byte per entry, and the
        // pixels read once (a BMP sent as it is is at most 2 MB).
        var palette = bytes.AsSpan(14 + headerSize + masks, (int)entries * 4);
        if (depth > 8) palette.Clear();
        else if (entries > 0)
        {
            var named = new bool[entries];
            var mask = (1 << depth) - 1;
            for (var row = 0L; row < rows; row++)
            {
                var line = bytes.AsSpan((int)(tables + row * stride), (int)stride);
                for (var x = 0; x < width; x++)
                {
                    var bit = x * depth;
                    var index = (line[bit / 8] >> (8 - depth - bit % 8)) & mask;
                    if (index < entries) named[index] = true;
                }
            }
            for (var entry = 0; entry < entries; entry++)
                if (named[entry]) palette[entry * 4 + 3] = 0;
                else palette.Slice(entry * 4, 4).Clear();
        }
        return new Stripped(bytes, "bmp", false);
    }

    // ---- EXIF orientation ----------------------------------------------------------------------------------------------

    // IFD0's orientation in a TIFF-structured EXIF block: 1 when there is none or it is out of range (viewers then draw the
    // photo as stored), null when the block cannot be read far enough to say.
    internal static ushort? Orientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return null;
        bool big;
        if (tiff[0] == 'M' && tiff[1] == 'M') big = true;
        else if (tiff[0] == 'I' && tiff[1] == 'I') big = false;
        else return null;
        if (U16(tiff, 2, big) != 42) return null;
        var ifd = U32(tiff, 4, big);
        if (ifd < 8 || ifd > tiff.Length - 2) return null;
        var count = U16(tiff, (int)ifd, big);
        if (ifd + 2 + count * 12L > tiff.Length) return null;
        for (var index = 0; index < count; index++)
        {
            var entry = (int)ifd + 2 + index * 12;
            if (U16(tiff, entry, big) != 0x0112) continue;
            var type = U16(tiff, entry + 2, big);
            if (U32(tiff, entry + 4, big) != 1 || type is not (3 or 4)) return null;
            var value = type == 3 ? U16(tiff, entry + 8, big) : U32(tiff, entry + 8, big);
            return value is >= 1 and <= 8 ? (ushort)value : (ushort)1;
        }
        return 1;

        static ushort U16(ReadOnlySpan<byte> bytes, int at, bool big) =>
            big ? BinaryPrimitives.ReadUInt16BigEndian(bytes[at..]) : BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]);
        static uint U32(ReadOnlySpan<byte> bytes, int at, bool big) =>
            big ? BinaryPrimitives.ReadUInt32BigEndian(bytes[at..]) : BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
    }

    // A big-endian TIFF block whose IFD0 holds the orientation alone and links to no further directory.
    internal static byte[] MinimalExif(ushort orientation) =>
        [(byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, (byte)(orientation >> 8), (byte)orientation, 0, 0, 0, 0, 0, 0];
}
