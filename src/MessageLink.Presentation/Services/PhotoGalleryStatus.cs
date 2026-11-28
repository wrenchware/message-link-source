namespace MessageLink.Presentation.Services;

// The one line under a conversation's photo gallery.
//
// A picture the provider no longer holds is taken out of the grid rather than left as a tile with nothing in it: a
// wall of pictures with holes in it says only that something is broken. It is still counted, because "63 photos" for
// a conversation that plainly had more is its own kind of wrong — the honest thing is to show what there is and say
// what is gone. Written here rather than in the dialog so every branch of it can be read back by a check.
public static class PhotoGalleryStatus
{
    private const string More = " · scroll for earlier messages";

    // noun: what the grid is showing, singular. "photo" while it holds only photographs, "item" once a video is in
    // it, "video" while it is filtered to those — counting videos as photos would be a small lie repeated every time
    // the gallery opens.
    public static string Describe(int shown, int lost, bool hasMore, string noun = "photo")
    {
        var many = noun + "s";
        // The hint is only true while there is something to scroll. An empty grid cannot be scrolled at all, so
        // promising it there would be an instruction that does nothing; the gallery fills that case by itself.
        var more = hasMore && shown > 0 ? More : "";
        if (shown == 0 && lost == 0)
            return hasMore
                ? $"No {many} in the messages loaded so far."
                : $"No {many} are available in this conversation.";
        if (shown == 0)
            return (lost == 1
                ? $"The only {noun} here is no longer stored by its service."
                : $"None of the {lost} {many} here are still stored by their service.") + more;
        var counted = shown == 1 ? $"1 {noun}" : $"{shown} {many}";
        if (lost == 0) return counted + more;
        return counted + (lost == 1
            ? " · 1 is no longer stored by its service"
            : $" · {lost} are no longer stored by their service") + more;
    }
}
