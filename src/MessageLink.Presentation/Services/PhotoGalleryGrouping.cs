namespace MessageLink.Presentation.Services;

// Which heading a photo belongs under in the gallery.
//
// Sixty pictures in one unbroken grid is a wall with no landmarks: nothing says whether the picture being looked for
// is from this morning or from March, and the only way to find out is to open one. Dates are the landmark people
// already have for their own photographs, so the grid carries the same headings a phone's camera roll does.
//
// Recent days get their own names because that is how they are spoken about; everything past this month is named by
// its month, and past this year by its month and year, because "August" alone stops meaning one month once there
// have been two of them. A pure function of the photo's time and the current time, so every boundary is read back by
// a check rather than waited for.
public static class PhotoGalleryGrouping
{
    public static string Heading(DateTimeOffset at, DateTimeOffset now)
    {
        var day = at.LocalDateTime.Date;
        var today = now.LocalDateTime.Date;
        // A photo dated ahead of the clock is somebody's device disagreeing, not a category. It reads as today, and
        // it is decided here rather than further down, where "same month as today" would have claimed it first.
        if (day >= today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        // The last week, not the calendar one: on a Monday, "this week" meaning yesterday and nothing else would be
        // a heading that appears and empties by the day of the week it happens to be.
        if (day > today.AddDays(-7)) return "This week";
        if (day.Year == today.Year && day.Month == today.Month) return "This month";
        return day.Year == today.Year
            ? day.ToString("MMMM", System.Globalization.CultureInfo.CurrentCulture)
            : day.ToString("MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
    }

    // What a heading's own line says beside it: how many pictures are under it.
    public static string Count(int items) => items == 1 ? "1 item" : $"{items} items";
}
