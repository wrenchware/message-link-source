namespace MessageLink.Presentation.Services;

// What a recording's bytes are, for a player that has to be told. Most providers say (Google Voice serves MP3), but an
// Instagram voice message arrives with no media type at all, and telling the player an MP4 recording is MP3 is how it
// ends up saying "Can't play". The provider's own word wins; the bytes decide only where there is none, and MP3 is
// the answer where they say nothing recognisable, as it always was.
public static class RecordingFormat
{
    public const string Fallback = "audio/mpeg";

    public static string MimeType(ReadOnlySpan<byte> bytes, string? declared)
    {
        if (!string.IsNullOrWhiteSpace(declared)) return declared;
        // ISO base media (MP4, M4A, 3GP): a box size, then "ftyp".
        if (bytes.Length >= 8 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8)) return "audio/mp4";
        if (bytes.Length >= 4 && bytes[..4].SequenceEqual("OggS"u8)) return "audio/ogg";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WAVE"u8)) return "audio/wav";
        // AAC in ADTS: a sync word with layer zero, which MP3's frames never have.
        if (bytes.Length >= 2 && bytes[0] == 0xFF && (bytes[1] & 0xF6) == 0xF0) return "audio/aac";
        return Fallback;
    }
}
