namespace MessageLink.Core.Services;

/// <summary>What a change to the kept photos took out: references from saved messages, the photos left with none, and the
/// bytes those photos held on disk.</summary>
public readonly record struct SavedPhotoChange(int References, int Photos, long Bytes)
{
    public static readonly SavedPhotoChange None = new(0, 0, 0);
}

/// <summary>Whether a photo was kept, and what the budget took out to make room for it.</summary>
public readonly record struct SavedPhotoKeep(bool Kept, SavedPhotoChange Evicted);

/// <summary>
/// The full-size photos of saved messages, kept on this PC apart from the media cache, so that the cache's eviction and
/// Settings > Storage's Clear pictures never take them (SavedPhotos). A photo is kept once however many saved messages show
/// it, and goes when the last of them is unsaved. Keys are one-way digests; which conversation a reference belongs to
/// (its owner) is kept protected, and is read back only to find an account's photos when it signs out.
/// </summary>
public interface ISavedPhotoStore
{
    /// <summary>The budget for every kept photo together, in bytes on disk.</summary>
    long Capacity { get; }

    Task<bool> HoldsAsync(string photo, CancellationToken token = default);

    /// <summary>The photo's bytes, or null when it is not kept (or cannot be read).</summary>
    Task<byte[]?> ReadAsync(string photo, CancellationToken token = default);

    /// <summary>Notes that a saved message shows a photo already kept, which counts as saving it again for the budget's
    /// order. False, and nothing written, when the photo is not kept.</summary>
    Task<bool> ReferAsync(string reference, string photo, string owner, CancellationToken token = default);

    /// <summary>Keeps a photo and the saved message's reference to it, then takes out the photos saved longest ago while
    /// everything kept is over <see cref="Capacity"/>; never the one just kept. A photo larger than the whole budget is not
    /// kept.</summary>
    Task<SavedPhotoKeep> KeepAsync(string reference, string photo, string owner, byte[] bytes, CancellationToken token = default);

    /// <summary>A saved message was unsaved: its references go, and each photo no other saved message shows.</summary>
    Task<SavedPhotoChange> ReleaseAsync(string reference, CancellationToken token = default);

    /// <summary>Every reference whose owner matches goes, and each photo then left with none (an account signing out).</summary>
    Task<SavedPhotoChange> ForgetAsync(Func<string, bool> owner, CancellationToken token = default);

    /// <summary>Every kept photo and reference goes. Saved messages themselves are not touched.</summary>
    Task<SavedPhotoChange> ClearAsync(CancellationToken token = default);

    Task<(int Photos, long Bytes)> UsageAsync(CancellationToken token = default);
}
