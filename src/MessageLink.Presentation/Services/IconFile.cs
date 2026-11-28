namespace MessageLink.Presentation.Services;

// A Windows .ico file holding PNG images, one per size, which is what the taskbar jump list takes as an entry's picture
// (TaskbarJumpList). Windows has read PNG-compressed icon images at every size since Vista; it picks the size nearest the
// one it draws.
public static class IconFile
{
    public static byte[] FromPngs(IReadOnlyList<(int Size, byte[] Png)> images)
    {
        if (images.Count is 0 or > ushort.MaxValue) throw new ArgumentException("An icon holds one image or more.", nameof(images));
        foreach (var (size, png) in images)
            if (size is < 1 or > 256 || png is not { Length: > 8 } || png[0] != 0x89 || png[1] != (byte)'P')
                throw new ArgumentException("Each image is a PNG from 1 to 256 pixels square.", nameof(images));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1); // icon
        writer.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((byte)0); // no palette
            writer.Write((byte)0);
            writer.Write((ushort)1); // planes
            writer.Write((ushort)32); // bits per pixel
            writer.Write((uint)png.Length);
            writer.Write((uint)offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) writer.Write(png);
        writer.Flush();
        return stream.ToArray();
    }
}
