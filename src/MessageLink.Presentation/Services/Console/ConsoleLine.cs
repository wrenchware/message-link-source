using CommunityToolkit.Mvvm.ComponentModel;

namespace MessageLink.Presentation.Services;

/// <summary>Command: a command line as it was typed, echoed with the prompt it was typed at.</summary>
public enum ConsoleLineKind { Incoming, Outgoing, Event, Connection, Info, Error, Day, Command }

/// <summary>What became of a text the console sent. None for every line the console did not send itself.</summary>
public enum ConsoleSendState { None, Sending, Sent, NotSent, Unknown, Unconfirmed }

/// <summary>How a piece of console text is inked. Service takes its colour from the span's bubble family.</summary>
public enum ConsoleTone { Default, Dim, Accent, Ok, Warn, Error, Strong, Match, Service, Selected }

/// <summary>A run of console text in one ink.</summary>
public sealed record ConsoleSpan(string Text, ConsoleTone Tone = ConsoleTone.Default, BubbleFamily? Family = null)
{
    public static ConsoleSpan Of(string text, ConsoleTone tone = ConsoleTone.Default) => new(text, tone);
    public static ConsoleSpan For(string text, BubbleFamily? family) =>
        family is null ? new(text, ConsoleTone.Strong) : new(text, ConsoleTone.Service, family);
}

/// <summary>
/// One line of the chat console, decided once when it is appended and never rewritten. Memory only: nothing here is
/// persisted or logged.
/// </summary>
/// <param name="ServiceTag">The short service word the tag column shows (Google, Messenger, Instagram, Voice), or empty.</param>
/// <param name="Family">The bubble family whose colour the tag wears; null for a line with no service.</param>
/// <param name="Body">The body column. May hold newlines; the window wraps them under the body column.</param>
/// <param name="Spoken">The whole line as a screen reader should say it.</param>
/// <param name="SourceChatId">The provider conversation the line came from; never a merged id.</param>
/// <param name="DisplayChatId">Where the conversation is shown: its merged conversation when it belongs to one.</param>
/// <param name="LocalId">The outbox's local id for a text the console sent.</param>
/// <param name="Time">The time column: the culture's short time, with the date before it for a line dated on an
/// earlier day than the lines before it. Empty for a day line.</param>
/// <param name="Spans">The body in inks, when it has more than one; their texts joined are <paramref name="Body"/>.</param>
/// <param name="Wide">Command output: the body runs the whole width from the left margin, with no time or tag column,
/// as a terminal prints what a command writes.</param>
public sealed record ConsoleLine(long Seq, DateTimeOffset At, ConsoleLineKind Kind, string ServiceTag, BubbleFamily? Family,
    string Body, string Spoken, string? SourceChatId, string? DisplayChatId, string? MessageId, string? Network,
    bool Muted, bool Archived, string? LocalId = null, string Time = "", IReadOnlyList<ConsoleSpan>? Spans = null, bool Wide = false);

/// <summary>A console line as the window binds it. Only <see cref="SendState"/> ever changes after a line is appended.</summary>
public sealed partial class ConsoleLineViewModel(ConsoleLine line) : ObservableObject
{
    public ConsoleLine Line { get; } = line;
    [ObservableProperty] private ConsoleSendState sendState;
}
