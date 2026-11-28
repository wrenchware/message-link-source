using System.Globalization;

namespace MessageLink.Presentation.Services;

/// <summary>
/// What Export conversation says before and after it writes: the count and dates of what it would write, whether more could be read from the services, and
/// the file's own first line. Kept here, apart from the window, so the checks read the same words the person does.
/// </summary>
public static class ConversationExportText
{
    /// <summary>One conversation's part of an export: its service, what is in hand, and whether the service has more and may be asked for it.</summary>
    public sealed record Part(string Service, bool Complete, bool MayReadOlder);

    public static string Count(int messages) => messages == 1 ? "1 message" : messages.ToString("N0", CultureInfo.CurrentCulture) + " messages";

    public static string Day(DateTimeOffset at) => at.ToString("MMMM d, yyyy", CultureInfo.CurrentCulture);

    public static string Span(DateTimeOffset first, DateTimeOffset last) => Day(first) == Day(last) ? Day(first) : $"{Day(first)} to {Day(last)}";

    /// <summary>"4,212 messages, May 26, 2023 to November 19, 2026." or "No messages." when there are none.</summary>
    public static string Summary(int messages, DateTimeOffset? first, DateTimeOffset? last) =>
        messages == 0 || first is null || last is null ? "No messages." : $"{Count(messages)}, {Span(first.Value, last.Value)}.";

    /// <summary>What the dialog says when the server cannot answer from its copy and the window's loaded messages are all there is to write.</summary>
    public static string LoadedSummary(int messages, DateTimeOffset? first, DateTimeOffset? last) =>
        messages == 0 || first is null || last is null ? "No messages are loaded in this window. Scroll up before exporting to include more."
        : $"{Count(messages)}, {Span(first.Value, last.Value)}, loaded in this window. Scroll up before exporting to include more.";

    /// <summary>Whether every part holds its conversation from the beginning.</summary>
    public static bool Whole(IReadOnlyList<Part> parts) => parts.Count > 0 && parts.All(part => part.Complete);

    /// <summary>The services that have older messages and may be asked for them, in the app's service order; empty when there is nothing to offer.</summary>
    public static IReadOnlyList<string> ReadableServices(IReadOnlyList<Part> parts) =>
        Ordered(parts.Where(part => !part.Complete && part.MayReadOlder).Select(part => part.Service));

    /// <summary>The services shown under the title, in the app's service order.</summary>
    public static string Services(IEnumerable<string> services) => string.Join(", ", Ordered(services));

    /// <summary>What the option that reads further back says it does.</summary>
    public static string ReadOlderDetail(IReadOnlyList<Part> parts)
    {
        var services = ReadableServices(parts);
        var text = $"Also reads older messages from {Join(services)}. This can take several minutes.";
        if (parts.Any(part => !part.Complete && !part.MayReadOlder)) text += " Older Instagram messages aren't included.";
        return text;
    }

    /// <summary>The line under a lone summary, when nothing more can be read: the whole conversation, or what is missing.</summary>
    public static string? HeldOnlyNote(IReadOnlyList<Part> parts) =>
        Whole(parts) ? "This is the whole conversation."
        : parts.Any(part => !part.Complete && !part.MayReadOlder) ? "Older Instagram messages aren't included."
        : null;

    /// <summary>How the file describes what it contains.</summary>
    public static string Contains(int messages, DateTimeOffset? first, DateTimeOffset? last, bool whole, bool loadedOnly)
    {
        if (messages == 0 || first is null || last is null) return loadedOnly ? "No messages are loaded in this window." : "No messages.";
        var span = Span(first.Value, last.Value);
        if (loadedOnly) return $"Contains the {Count(messages)} loaded in this window, {span}.";
        return whole ? $"Contains the whole conversation: {Count(messages)}, {span}." : $"Contains {Count(messages)}, {span}. Earlier messages aren't included.";
    }

    private static readonly string[] Order = ["Google Messages", "Messenger", "Google Voice", "Instagram", "Hermes"];

    private static IReadOnlyList<string> Ordered(IEnumerable<string> services) => services.Where(service => !string.IsNullOrWhiteSpace(service))
        .Distinct(StringComparer.Ordinal).OrderBy(service => Array.IndexOf(Order, service) is var at and >= 0 ? at : Order.Length)
        .ThenBy(service => service, StringComparer.Ordinal).ToArray();

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}
