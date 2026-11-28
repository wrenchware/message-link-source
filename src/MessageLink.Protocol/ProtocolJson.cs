using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace MessageLink.Protocol;

/// <summary>
/// The one set of JSON options every host and client uses. Conventions: camelCase property names, enums as camelCase
/// strings (an integer is refused), ISO-8601 dates, null properties omitted, unknown properties ignored, no reference
/// handling, and computed (get-only) properties never written.
/// </summary>
/// <remarks>
/// Compatibility within one protocol version: a new optional property may be added to any record, because an older peer
/// ignores it and a newer peer reads its absence as the default. Removing or renaming a property, changing what one
/// means, or adding an enum value that an older peer has to understand needs a new <see cref="ProtocolVersion"/>.
/// An Android peer in Kotlin follows the same rules; nothing here relies on .NET-only serialization.
/// </remarks>
public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(DropComputed);
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = resolver,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            ReferenceHandler = null,
            // A peer in another language may write the "t" discriminator anywhere in the object.
            AllowOutOfOrderMetadataProperties = true,
            // Not HTML: this is a wire format, and escaping every accent and emoji only bloats it.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly();
        return options;
    }

    // A get-only property is derived (ContactPerson.Label, ServiceMute.Key): writing it would put a value on the wire
    // that the reader cannot set and the other language would have to treat as data.
    private static void DropComputed(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object) return;
        for (var i = info.Properties.Count - 1; i >= 0; i--)
            if (info.Properties[i].Set is null) info.Properties.RemoveAt(i);
    }

    /// <summary>A value as a JSON element, for a frame's <c>params</c> or <c>result</c>.</summary>
    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    /// <summary>A frame's <c>params</c> or <c>result</c> as the type the method names.</summary>
    public static T? FromElement<T>(JsonElement element) => element.Deserialize<T>(Options);
}
