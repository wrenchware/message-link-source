namespace MessageLink.Presentation.Services;

// A rectangle in whichever space the caller is working in: screen pixels for a captured thumbnail, viewer
// device-independent pixels for a placement inside the photo viewer.
public readonly record struct PhotoRect(double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
    public bool IsEmpty => !(Width > 0) || !(Height > 0) || !double.IsFinite(X) || !double.IsFinite(Y) ||
        !double.IsFinite(Width) || !double.IsFinite(Height);
}

// The placements the transition runs between, in the viewer's own units. Start and StartPicture are the thumbnail as
// it really appears: the tile's own outline, and the photo drawn inside it, which for a tile that crops is larger
// than the outline. Landing is where the viewer lays the photo out, and there the two coincide. Keeping them apart is
// what lets the handover be invisible: the window takes the tile's shape showing the tile's own framing of the photo,
// rather than a letterboxed copy landing on top of a cropped one.
public readonly record struct PhotoOpenPlan(PhotoRect Start, PhotoRect StartPicture, PhotoRect Landing);

// One moment of the movement: the outline the window clips itself to, and the photo drawn inside it.
public readonly record struct PhotoOpenFrame(PhotoRect Shape, PhotoRect Picture);

// Geometry for opening an inline photo thumbnail into the photo viewer, and for putting it back. The viewer is its
// own window, so the thumbnail's position has to travel as screen pixels and be mapped back into the viewer; keeping
// the arithmetic here, free of Windows types, lets the offline harness check it.
public static class PhotoOpenTransition
{
    // Below this the growth reads as a pop out of nothing rather than as the thumbnail expanding, and above it the
    // thumbnail already sits about where the photo lands, so the movement is noise.
    public const double MinimumScale = 0.06, MaximumScale = 0.9;
    public const double Milliseconds = 320, ClosingMilliseconds = 260, LandingFadeMilliseconds = 140;

    // Where a picture of this aspect sits when it is fit uniformly, centred, in a box of this size.
    public static PhotoRect Fit(double aspect, double boxWidth, double boxHeight)
    {
        if (!double.IsFinite(aspect) || aspect <= 0 || !(boxWidth > 0) || !(boxHeight > 0) ||
            !double.IsFinite(boxWidth) || !double.IsFinite(boxHeight)) return default;
        var width = boxWidth;
        var height = width / aspect;
        if (height > boxHeight) { height = boxHeight; width = height * aspect; }
        if (!(width > 0) || !(height > 0) || !double.IsFinite(width) || !double.IsFinite(height)) return default;
        return new((boxWidth - width) / 2, (boxHeight - height) / 2, width, height);
    }

    // Where a picture of this aspect sits when it covers a box of this size, centred: the placement a tile that crops
    // to fill uses, which overflows the box on one axis.
    public static PhotoRect Cover(double aspect, double boxWidth, double boxHeight)
    {
        if (!double.IsFinite(aspect) || aspect <= 0 || !(boxWidth > 0) || !(boxHeight > 0) ||
            !double.IsFinite(boxWidth) || !double.IsFinite(boxHeight)) return default;
        var width = boxWidth;
        var height = width / aspect;
        if (height < boxHeight) { height = boxHeight; width = height * aspect; }
        if (!(width > 0) || !(height > 0) || !double.IsFinite(width) || !double.IsFinite(height)) return default;
        return new((boxWidth - width) / 2, (boxHeight - height) / 2, width, height);
    }

    // thumbnailOnScreen and viewerOnScreen are screen pixels; scale converts the viewer's pixels to its own layout
    // units. cropsToFill is how the tile draws its photo, and it has to be right: the movement starts as a copy of
    // what is already on screen there, so a cropped tile starts cropped. Returns null when the thumbnail cannot
    // honestly grow into the photo — off the viewer, degenerate, or so close in size that the animation would add
    // nothing. The caller then opens the photo without one.
    public static PhotoOpenPlan? Plan(PhotoRect thumbnailOnScreen, PhotoRect viewerOnScreen, double scale, double aspect, bool cropsToFill)
    {
        if (thumbnailOnScreen.IsEmpty || viewerOnScreen.IsEmpty || !(scale > 0) || !double.IsFinite(scale)) return null;
        var viewport = new PhotoRect(0, 0, viewerOnScreen.Width / scale, viewerOnScreen.Height / scale);
        var landing = Fit(aspect, viewport.Width, viewport.Height);
        if (landing.IsEmpty) return null;
        var box = new PhotoRect((thumbnailOnScreen.X - viewerOnScreen.X) / scale, (thumbnailOnScreen.Y - viewerOnScreen.Y) / scale,
            thumbnailOnScreen.Width / scale, thumbnailOnScreen.Height / scale);
        if (box.IsEmpty) return null;
        // A tile that crops is its whole box, with the photo overflowing it; one that does not letterboxes the photo
        // inside the box and is only as big as that.
        var inside = cropsToFill ? Cover(aspect, box.Width, box.Height) : Fit(aspect, box.Width, box.Height);
        if (inside.IsEmpty) return null;
        inside = inside with { X = inside.X + box.X, Y = inside.Y + box.Y };
        var shape = cropsToFill ? box : inside;
        // The transparent composition host covers the union of the thumbnail and viewer rectangles, so a right- or
        // left-aligned tile may begin outside the final viewer window and still grow continuously from its true place.
        var growth = shape.Width / landing.Width;
        if (!double.IsFinite(growth) || growth < MinimumScale || growth > MaximumScale) return null;
        return new(shape, inside, landing);
    }

    // Eases both sides. The transform on the picture and the region the window is clipped to are worked out from this
    // one number in the same frame, so they cannot drift apart.
    public static double Ease(double progress)
    {
        var t = Math.Clamp(progress, 0, 1);
        return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    // Where things sit partway through, for an already-eased position: 0 is the thumbnail, 1 the laid-out photo. The
    // outline and the photo inside it are interpolated separately, so a cropped tile's framing opens out over the
    // movement instead of the photo jumping from cropped to letterboxed in one frame.
    public static PhotoOpenFrame Frame(PhotoOpenPlan plan, double eased)
    {
        if (plan.Start.IsEmpty || plan.Landing.IsEmpty) return new(plan.Landing, plan.Landing);
        var t = Math.Clamp(eased, 0, 1);
        PhotoRect Between(PhotoRect from, PhotoRect to) => new(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t,
            from.Width + (to.Width - from.Width) * t, from.Height + (to.Height - from.Height) * t);
        var picture = plan.StartPicture.IsEmpty ? plan.Start : plan.StartPicture;
        return new(Between(plan.Start, plan.Landing), Between(picture, plan.Landing));
    }

    // The aspect the viewer opens at: the attachment's own dimensions when the provider reported them, and otherwise
    // the same letterboxed reservation the inline tile used, so the thumbnail and the photo agree.
    public static double Aspect(double? width, double? height)
    {
        if (width is > 0 && height is > 0 && double.IsFinite(width.Value) && double.IsFinite(height.Value))
        {
            var aspect = width.Value / height.Value;
            if (double.IsFinite(aspect) && aspect > 0) return aspect;
        }
        var reserved = ConversationReading.MediaSize(width, height);
        return reserved.Width / reserved.Height;
    }
}
