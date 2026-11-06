namespace NativeBeeper.Protocol;

// Contacts saved through the phone (protocol 29; owner, 2026-11-05). A PC never talks to Google about contacts: it hands a request to the server, which keeps it
// until a phone with "Save contacts from your PC" on takes it, wakes that phone with its encrypted push, and keeps the phone's answer for the PC. The phone
// writes the contact into the Google account it was told to use, and Google syncs it to every device.
//
//   phonebook.offer       The calling phone says whether it saves contacts, and into which Google account. Any device; only phones send it.
//   phonebook.status      Which phones save contacts now, by name and account, and whether each is connected. Any device.
//   phonebook.submit      A new contact, a number added to one, or a contact's photo set or removed. Kept by the request's id: the same id again is the same request,
//                          never a second one, so a PC that lost the answer may ask with it again and learn where it stands.
//   phonebook.results     Where requests stand, by id.
//   phonebook.search      The phone's contacts by name, for Existing contact: the phone is woken and has a few seconds to answer; nothing is kept.
//   phonebook.take        The calling phone's waiting requests, searches included. A request taken and not yet answered is offered again on the next take.
//   phonebook.done        The calling phone's answer to one request.
//
// The `phoneContacts` account signal says a phone turned the setting on or off, a request was submitted (an open phone takes it) or was answered (a PC reads its results).
// No call reaches a service. None is journaled: submit is idempotent by its id instead, and nothing is ever resent by the server or the phone without that id.
public static partial class ProtocolMethods
{
    public const string PhoneContactsOffer = "phonebook.offer";
    public const string PhoneContactsStatus = "phonebook.status";
    public const string PhoneContactsSubmit = "phonebook.submit";
    public const string PhoneContactsResults = "phonebook.results";
    public const string PhoneContactsSearch = "phonebook.search";
    public const string PhoneContactsTake = "phonebook.take";
    public const string PhoneContactsDone = "phonebook.done";

    private static Entry[] PhoneContactsEntries() =>
    [
        new(PhoneContactsOffer, "IPhoneContacts.OfferAsync", typeof(PhoneContactsOfferParams), typeof(Done)),
        new(PhoneContactsStatus, "IPhoneContacts.StatusAsync", null, typeof(PhoneContactsStatus)),
        new(PhoneContactsSubmit, "IPhoneContacts.SubmitAsync", typeof(PhoneContactRequest), typeof(PhoneContactState)),
        new(PhoneContactsResults, "IPhoneContacts.ResultsAsync", typeof(PhoneContactResultsParams), typeof(PhoneContactResults)),
        new(PhoneContactsSearch, "IPhoneContacts.SearchAsync", typeof(PhoneContactSearchParams), typeof(PhoneContactSearch)),
        new(PhoneContactsTake, "IPhoneContacts.TakeAsync", null, typeof(PhoneContactTake)),
        new(PhoneContactsDone, "IPhoneContacts.DoneAsync", typeof(PhoneContactDoneParams), typeof(Done)),
    ];
}

/// <summary>The calling phone saves contacts from PCs (<paramref name="On"/>) into the Google account <paramref name="Account"/>, or no longer does.</summary>
public sealed record PhoneContactsOfferParams(bool On, string? Account = null);

/// <summary>The phones that save contacts now, the one requests go to first (the last turned on).</summary>
public sealed record PhoneContactsStatus(IReadOnlyList<ContactsPhone> Phones);

/// <summary>A phone that saves contacts: the name the server lists it by, the Google account it saves into, and whether it is connected now.</summary>
public sealed record ContactsPhone(string Name, string Account, bool Connected);

/// <summary>
/// One request from a PC, by its <paramref name="Id"/> (made by the PC, one per action the person took). <paramref name="Kind"/> is one of <see cref="PhoneContactKinds"/>:
/// <list type="bullet">
/// <item><c>new</c>: a contact named <paramref name="GivenName"/> <paramref name="FamilyName"/> with <paramref name="Phone"/> of <paramref name="PhoneType"/>
/// (<see cref="PhoneContactKinds.PhoneTypes"/>) at <paramref name="Company"/>.</item>
/// <item><c>addNumber</c>: <paramref name="Phone"/> of <paramref name="PhoneType"/> added to the phone's contact <paramref name="Contact"/> (a search's match).</item>
/// <item><c>photo</c>: the contact with <paramref name="Phone"/> given <paramref name="Photo"/> (a PNG or JPEG, base64), or its photo removed when there is none.</item>
/// <item><c>search</c> (the phone's take only): the contacts whose name has <paramref name="Query"/>.</item>
/// </list>
/// </summary>
public sealed record PhoneContactRequest(string Id, string Kind, string? GivenName = null, string? FamilyName = null, string? Phone = null, string? PhoneType = null,
    string? Company = null, string? Contact = null, string? Photo = null, string? Query = null);

/// <summary>
/// Where a request stands: <paramref name="State"/> is one of <see cref="PhoneContactStates"/>, <paramref name="Name"/> the contact's name where the phone gave one
/// (the contact a number was already in, or was added to).
/// </summary>
public sealed record PhoneContactState(string Id, string State, string? Name = null);

public sealed record PhoneContactResultsParams(IReadOnlyList<string> Ids);

public sealed record PhoneContactResults(IReadOnlyList<PhoneContactState> Results);

public sealed record PhoneContactSearchParams(string Query);

/// <summary>A search's answer: <paramref name="State"/> one of <see cref="PhoneContactSearchStates"/>; the matches when the phone answered.</summary>
public sealed record PhoneContactSearch(string State, IReadOnlyList<PhoneContactMatch>? Matches = null);

/// <summary>A contact the phone found by name: its key on the phone (for <c>addNumber</c>), its name, and how many numbers it has.</summary>
public sealed record PhoneContactMatch(string Contact, string Name, int Numbers);

public sealed record PhoneContactTake(IReadOnlyList<PhoneContactRequest> Requests);

/// <summary>
/// The phone's answer to request <paramref name="Id"/>: <paramref name="Outcome"/> is one of <see cref="PhoneContactStates"/> other than <c>waiting</c> (a search
/// answers <c>saved</c> with its <paramref name="Matches"/>, or <c>refused</c>), and <paramref name="Name"/> the contact's name where there is one.
/// </summary>
public sealed record PhoneContactDoneParams(string Id, string Outcome, string? Name = null, IReadOnlyList<PhoneContactMatch>? Matches = null);

public static class PhoneContactKinds
{
    public const string New = "new";
    public const string AddNumber = "addNumber";
    public const string Photo = "photo";
    public const string Search = "search";
    /// <summary>The kinds a PC submits; a search is asked with <c>phonebook.search</c>.</summary>
    public static IReadOnlyList<string> Submitted { get; } = [New, AddNumber, Photo];
    public static IReadOnlyList<string> All { get; } = [New, AddNumber, Photo, Search];
    /// <summary>A number's type, as the phone's own Contacts app names them.</summary>
    public static IReadOnlyList<string> PhoneTypes { get; } = ["mobile", "home", "work", "main", "other"];
}

public static class PhoneContactStates
{
    /// <summary>Kept on the server; the phone has not answered yet.</summary>
    public const string Waiting = "waiting";
    /// <summary>Saved: the contact made, the number added, the photo set or removed.</summary>
    public const string Saved = "saved";
    /// <summary>Nothing changed: the number is already a contact's (its name in <see cref="PhoneContactState.Name"/>).</summary>
    public const string Already = "already";
    /// <summary>A photo for a number no contact in the account has, or a number for a contact gone from the phone.</summary>
    public const string NotFound = "notFound";
    /// <summary>A photo for a number several contacts have.</summary>
    public const string Several = "several";
    /// <summary>The phone may not write contacts now (the setting off, the permission withdrawn, the account gone).</summary>
    public const string Refused = "refused";
    /// <summary>The phone tried and could not.</summary>
    public const string Failed = "failed";
    /// <summary>The server forgot it, unanswered, after <see cref="PhoneContactLimits.KeptDays"/> days, or never had it.</summary>
    public const string Unknown = "unknown";
    /// <summary>Not kept: <see cref="PhoneContactLimits.Waiting"/> requests already wait for the phone.</summary>
    public const string Full = "full";
    public static IReadOnlyList<string> All { get; } = [Waiting, Saved, Already, NotFound, Several, Refused, Failed, Unknown, Full];
    /// <summary>What a phone may answer with.</summary>
    /// <para><c>unknown</c> from a phone: it may have written it before it stopped (a photo), so it is not written again; the person checks.</para>
    public static IReadOnlyList<string> Answers { get; } = [Saved, Already, NotFound, Several, Refused, Failed, Unknown];
}

public static class PhoneContactSearchStates
{
    public const string Answered = "answered";
    /// <summary>No phone saves contacts now.</summary>
    public const string NoPhone = "noPhone";
    /// <summary>The phone did not answer in time.</summary>
    public const string NoAnswer = "noAnswer";
    /// <summary>The phone answered that it may not read contacts now.</summary>
    public const string Refused = "refused";
    public static IReadOnlyList<string> All { get; } = [Answered, NoPhone, NoAnswer, Refused];
}

public static class PhoneContactLimits
{
    public const int Id = 64;
    public const int Name = 128;
    public const int Phone = 64;
    public const int Account = 320;
    public const int Contact = 128;
    public const int Query = 128;
    /// <summary>A photo's bytes, before base64.</summary>
    public const int PhotoBytes = 1024 * 1024;
    public const int Matches = 25;
    /// <summary>Ids one <c>phonebook.results</c> asks after.</summary>
    public const int ResultIds = 50;
    /// <summary>Requests the server keeps unanswered; past it a submit is refused.</summary>
    public const int Waiting = 50;
    /// <summary>How long the server keeps a request, answered or not.</summary>
    public const int KeptDays = 14;
}

/// <summary>Contacts saved through the phone (protocol 29), for the connection that asks: a network connection's carries its device.</summary>
public interface IPhoneContacts
{
    Task OfferAsync(PhoneContactsOfferParams offer, CancellationToken cancellationToken = default);
    Task<PhoneContactsStatus> StatusAsync(CancellationToken cancellationToken = default);
    Task<PhoneContactState> SubmitAsync(PhoneContactRequest request, CancellationToken cancellationToken = default);
    Task<PhoneContactResults> ResultsAsync(PhoneContactResultsParams ids, CancellationToken cancellationToken = default);
    Task<PhoneContactSearch> SearchAsync(PhoneContactSearchParams search, CancellationToken cancellationToken = default);
    Task<PhoneContactTake> TakeAsync(CancellationToken cancellationToken = default);
    Task DoneAsync(PhoneContactDoneParams done, CancellationToken cancellationToken = default);
}

/// <summary>What a PC asks of a server's phone contacts (protocol 29). Nothing is kept here.</summary>
public sealed class PhoneContactsProxy(ProtocolClient client)
{
    /// <summary>Whether the connection speaks protocol 29: below, a server answers every call as not supported.</summary>
    public bool Spoken => client.Version >= ProtocolVersion.PhoneContacts;

    public Task<PhoneContactsStatus> StatusAsync(CancellationToken cancellationToken = default) =>
        client.CallAsync<PhoneContactsStatus>(ProtocolMethods.PhoneContactsStatus, cancellationToken);

    public Task<PhoneContactState> SubmitAsync(PhoneContactRequest request, CancellationToken cancellationToken = default) =>
        client.CallAsync<PhoneContactRequest, PhoneContactState>(ProtocolMethods.PhoneContactsSubmit, request, cancellationToken);

    public Task<PhoneContactResults> ResultsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default) =>
        client.CallAsync<PhoneContactResultsParams, PhoneContactResults>(ProtocolMethods.PhoneContactsResults, new PhoneContactResultsParams(ids), cancellationToken);

    public Task<PhoneContactSearch> SearchAsync(string query, CancellationToken cancellationToken = default) =>
        client.CallAsync<PhoneContactSearchParams, PhoneContactSearch>(ProtocolMethods.PhoneContactsSearch, new PhoneContactSearchParams(query), cancellationToken);
}
