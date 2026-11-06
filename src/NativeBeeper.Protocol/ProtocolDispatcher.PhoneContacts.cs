using System.Text.Json;

namespace NativeBeeper.Protocol;

public sealed partial class ProtocolDispatcher
{
    // Contacts saved through the phone (protocol 29): only to a connection that negotiated it, and only on a host that keeps them (a server's network door). Every
    // request is held to its limits here, so the port sees only what fits.
    private static Dictionary<string, Route> AddPhoneContactsRoutes(Dictionary<string, Route> routes)
    {
        routes[ProtocolMethods.PhoneContactsOffer] = PhoneContactsRoute<PhoneContactsOfferParams, Done>(async (port, offer, ct) =>
        {
            if (offer.On && (string.IsNullOrWhiteSpace(offer.Account) || offer.Account.Length > PhoneContactLimits.Account)) throw PhoneContactsInvalid("That isn't an account.");
            await port.OfferAsync(offer, ct).ConfigureAwait(false);
            return new Done();
        });
        routes[ProtocolMethods.PhoneContactsStatus] = new(null, typeof(PhoneContactsStatus), null, Own: async (connection, _, _, ct) =>
            JsonSerializer.SerializeToUtf8Bytes(await connection.PhoneContactsPort().StatusAsync(ct).ConfigureAwait(false), ProtocolJson.Options));
        routes[ProtocolMethods.PhoneContactsSubmit] = PhoneContactsRoute<PhoneContactRequest, PhoneContactState>((port, request, ct) =>
        {
            if (!PhoneContactRules.Submittable(request, out var why)) throw PhoneContactsInvalid(why);
            return port.SubmitAsync(request, ct);
        });
        routes[ProtocolMethods.PhoneContactsResults] = PhoneContactsRoute<PhoneContactResultsParams, PhoneContactResults>((port, ids, ct) =>
        {
            if (ids.Ids is null || ids.Ids.Count > PhoneContactLimits.ResultIds || ids.Ids.Any(id => id is not { Length: > 0 and <= PhoneContactLimits.Id }))
                throw PhoneContactsInvalid("Those aren't request ids.");
            return port.ResultsAsync(ids, ct);
        });
        routes[ProtocolMethods.PhoneContactsSearch] = PhoneContactsRoute<PhoneContactSearchParams, PhoneContactSearch>((port, search, ct) =>
        {
            if (search.Query is not { } query || query.Trim().Length < 2 || query.Length > PhoneContactLimits.Query) throw PhoneContactsInvalid("Search for at least two letters.");
            return port.SearchAsync(search, ct);
        });
        routes[ProtocolMethods.PhoneContactsTake] = new(null, typeof(PhoneContactTake), null, Own: async (connection, _, _, ct) =>
            JsonSerializer.SerializeToUtf8Bytes(await connection.PhoneContactsPort().TakeAsync(ct).ConfigureAwait(false), ProtocolJson.Options));
        routes[ProtocolMethods.PhoneContactsDone] = PhoneContactsRoute<PhoneContactDoneParams, Done>(async (port, done, ct) =>
        {
            if (done.Id is not { Length: > 0 and <= PhoneContactLimits.Id } || !PhoneContactStates.Answers.Contains(done.Outcome)
                || done.Name is { Length: > PhoneContactLimits.Name * 2 } || done.Matches is { Count: > PhoneContactLimits.Matches }
                || done.Matches?.Any(match => match.Contact is not { Length: > 0 and <= PhoneContactLimits.Contact } || match.Name is null || match.Name.Length > PhoneContactLimits.Name * 2 || match.Numbers < 0) == true)
                throw PhoneContactsInvalid("That isn't an answer to a request.");
            await port.DoneAsync(done, ct).ConfigureAwait(false);
            return new Done();
        });
        return routes;
    }

    private static Route PhoneContactsRoute<TParams, TResult>(Func<IPhoneContacts, TParams, CancellationToken, Task<TResult>> call) where TParams : class
        => new(typeof(TParams), typeof(TResult), null, Own: async (connection, request, _, ct) =>
        {
            var port = connection.PhoneContactsPort();
            if (!ParamsShape.HasRequiredValues(typeof(TParams), request.Params)) throw PhoneContactsInvalid("The request is missing something.");
            return JsonSerializer.SerializeToUtf8Bytes<TResult>(await call(port, ReadParams<TParams>(request), ct).ConfigureAwait(false), ProtocolJson.Options);
        });

    private IPhoneContacts PhoneContactsPort()
    {
        var port = services.PhoneContacts ?? throw new NotSupportedException();
        if (negotiated < ProtocolVersion.PhoneContacts) throw new NotSupportedException();
        return port;
    }

    private static ProtocolException PhoneContactsInvalid(string message) => new(new ProtocolError(ProtocolErrorCodes.InvalidRequest, message));
}

/// <summary>What a request from a PC must be to be kept (protocol 29): the server's door and the PC's dialog hold it to the same rules.</summary>
public static class PhoneContactRules
{
    /// <summary>Whether <paramref name="request"/> is one a PC may submit; <paramref name="why"/> says what is wrong when not, in words a person can read.</summary>
    public static bool Submittable(PhoneContactRequest request, out string why)
    {
        why = "";
        if (request.Id is not { Length: > 0 and <= PhoneContactLimits.Id } || request.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) return No("That isn't a request id.", out why);
        if (!PhoneContactKinds.Submitted.Contains(request.Kind)) return No("That isn't something the phone does.", out why);
        if (Long(request.GivenName, PhoneContactLimits.Name) || Long(request.FamilyName, PhoneContactLimits.Name) || Long(request.Company, PhoneContactLimits.Name)
            || Long(request.Contact, PhoneContactLimits.Contact) || request.Query is not null) return No("Part of the contact is too long.", out why);
        if (string.IsNullOrWhiteSpace(request.Phone) || request.Phone.Length > PhoneContactLimits.Phone || !request.Phone.Any(char.IsAsciiDigit)) return No("Enter a phone number.", out why);
        if (request.PhoneType is { } type && !PhoneContactKinds.PhoneTypes.Contains(type)) return No("That isn't a type of number.", out why);
        switch (request.Kind)
        {
            case PhoneContactKinds.New when string.IsNullOrWhiteSpace(request.GivenName) && string.IsNullOrWhiteSpace(request.FamilyName) && string.IsNullOrWhiteSpace(request.Company):
                return No("Enter a name.", out why);
            case PhoneContactKinds.AddNumber when string.IsNullOrWhiteSpace(request.Contact):
                return No("Choose a contact.", out why);
            case PhoneContactKinds.Photo when request.Photo is { } photo && (photo.Length > (PhoneContactLimits.PhotoBytes + 2) / 3 * 4 || PhotoBytes(photo) is not (> 0 and <= PhoneContactLimits.PhotoBytes)):
                return No("The photo is too large.", out why);
        }
        if (request.Kind != PhoneContactKinds.Photo && request.Photo is not null) return No("Only a photo request carries a photo.", out why);
        return true;

        static bool Long(string? text, int limit) => text is { } value && value.Length > limit;
        static bool No(string text, out string why) { why = text; return false; }
        // The photo's bytes, or -1 when it is not base64.
        static int PhotoBytes(string text)
        {
            var bytes = new byte[(text.Length * 3 + 3) / 4];
            return Convert.TryFromBase64String(text, bytes, out var written) ? written : -1;
        }
    }
}
