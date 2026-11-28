using System.Collections.Concurrent;
using System.Reflection;

namespace MessageLink.Protocol;

/// <summary>
/// Whether a deserialized params record has every member its declaration says is never null. System.Text.Json leaves a
/// missing non-nullable string null instead of refusing it, and a backend handed that would fail somewhere deep with an
/// internal error; the host answers <c>invalid_request</c> at the door instead. Struct members cannot be told apart from
/// their defaults and are not checked.
/// </summary>
internal static class ParamsShape
{
    private sealed record Member(PropertyInfo Property, bool NotNull, bool Nested);

    private static readonly ConcurrentDictionary<Type, Member[]> Cache = new();

    public static bool IsComplete(object value)
    {
        foreach (var member in Cache.GetOrAdd(value.GetType(), Describe))
        {
            var inner = member.Property.GetValue(value);
            if (inner is null)
            {
                if (member.NotNull) return false;
            }
            else if (member.Nested && !IsComplete(inner)) return false;
        }
        return true;
    }

    private static readonly ConcurrentDictionary<Type, string[]> RequiredValues = new();

    /// <summary>
    /// Whether <paramref name="parameters"/> (a JSON object) names every non-nullable value-type member of <paramref name="type"/>'s
    /// constructor that has no default. <see cref="IsComplete"/> cannot see these, because a missing flag or enum reads as its
    /// default, and for a mutation a flag that silently becomes false asks for something else.
    /// </summary>
    public static bool HasRequiredValues(Type type, System.Text.Json.JsonElement parameters)
    {
        if (parameters.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
        foreach (var name in RequiredValues.GetOrAdd(type, Required))
            if (!parameters.TryGetProperty(name, out _)) return false;
        return true;
    }

    private static string[] Required(Type type) =>
        [.. type.GetConstructors().OrderByDescending(constructor => constructor.GetParameters().Length).First().GetParameters()
            .Where(p => p.ParameterType.IsValueType && Nullable.GetUnderlyingType(p.ParameterType) is null && !p.HasDefaultValue)
            .Select(p => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(p.Name!))];

    private static Member[] Describe(Type type)
    {
        var context = new NullabilityInfoContext();
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0 && property.CanRead && !property.PropertyType.IsValueType)
            .Select(property => new Member(property, context.Create(property).ReadState == NullabilityState.NotNull,
                property.PropertyType.IsClass && property.PropertyType != typeof(string) && property.PropertyType.Namespace?.StartsWith("MessageLink.", StringComparison.Ordinal) == true))
            .ToArray();
    }
}
