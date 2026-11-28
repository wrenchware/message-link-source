using System.Text.Json;

namespace MessageLink.App.Services.GoogleMessages;

/// <summary>
/// Whether Google Messages is the paired phone's default SMS app, as the phone last said. A phone where it is not
/// refuses every send, and until this was kept the refusal read as a connection problem.
/// </summary>
/// <remarks>
/// Only what arrives is used. The phone's settings event carries the answer (the helper's settings.go, read as upstream
/// reads it), and a send the phone refused for this reason carries it too (send.go, FAILURE_4). Nothing asks the phone.
/// Null is unknown, and unknown says nothing: an event that leaves the field out is not an answer, so the last answer
/// stands across reconnects of the same pairing, as upstream keeps it. A new pairing may be a different phone, so it
/// starts over unknown.
/// </remarks>
internal sealed class GoogleDefaultSmsApp
{
    private readonly object sync = new();
    private bool? isDefault;

    /// <summary>What the composer says on a Google Messages conversation while the phone says it is not the default.</summary>
    /// <remarks>
    /// Upstream's wording (connector/bridgestate.go) has SMS and RCS both stop, and that is how Android behaves: only the
    /// default SMS app may send, and Google Messages turns its RCS chats off when it is not the default. So this does not
    /// say RCS may still work.
    /// </remarks>
    public const string ComposerNotice =
        "Google Messages isn't the default SMS app on your phone, so SMS, MMS and RCS can't send. Make it the default SMS app on your phone.";

    /// <summary>The one line Settings' Google Messages card shows while that is so.</summary>
    public const string SettingsLine = "Not the default SMS app on your phone, so messages can't send.";

    /// <summary>The helper's code for a send the phone refused because of it (send.go).</summary>
    public const string RefusalCode = "not_default_sms_app";

    public bool? IsDefault { get { lock (sync) return isDefault; } }

    /// <summary>The caution for a composer, or null when there is nothing to say (the default, or not known).</summary>
    public string? Caution => IsDefault == false ? ComposerNotice : null;

    /// <summary>Settings' line, or null when there is nothing to say.</summary>
    public string? Line => IsDefault == false ? SettingsLine : null;

    /// <summary>Takes a settings event from the helper. True when what is known changed.</summary>
    public bool Apply(JsonElement settings) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("defaultSmsApp", out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False && Set(value.GetBoolean());

    /// <summary>A send the phone refused with <see cref="RefusalCode"/>: the same answer, heard another way.</summary>
    public bool Refused() => Set(false);

    /// <summary>A new pairing, which may be another phone. True when something was known.</summary>
    public bool Forget() => Set(null);

    private bool Set(bool? value)
    {
        lock (sync)
        {
            if (isDefault == value) return false;
            isDefault = value;
            return true;
        }
    }
}
