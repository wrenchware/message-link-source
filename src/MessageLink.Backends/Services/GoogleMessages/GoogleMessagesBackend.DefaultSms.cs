using System.Text.Json;
using MessageLink.Core.Services;

namespace MessageLink.App.Services.GoogleMessages;

// Whether Google Messages is the phone's default SMS app (GoogleDefaultSmsApp). Read-only: it is taken from what the
// phone already sends, and it changes what the composer and Settings say, never whether a send is attempted.
internal sealed partial class GoogleMessagesBackend
{
    private readonly GoogleDefaultSmsApp defaultSms = new();

    /// <summary>What the phone last said, or null when it has not said. Settings reads it.</summary>
    internal bool? IsDefaultSmsApp => defaultSms.IsDefault;
    internal string? DefaultSmsLine => defaultSms.Line;

    /// <summary>Raised off the interface thread when what is known changes, so open composers and Settings can say it.</summary>
    internal event EventHandler? DefaultSmsAppChanged;

    private void NoteSettings(JsonElement value)
    {
        if (defaultSms.Apply(value)) DefaultSmsChanged("settings");
    }

    /// <summary>
    /// For a rejected send: the phone's reason when it refused because Google Messages is not its default SMS app, and
    /// that noted; null for any other refusal. The helper's wording is its own fixed sentence (send.go).
    /// </summary>
    internal string? NotDefaultSmsRefusal(JsonElement result)
    {
        if (!result.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String || code.GetString() != GoogleDefaultSmsApp.RefusalCode) return null;
        if (defaultSms.Refused()) DefaultSmsChanged("send");
        return result.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String && reason.GetString() is { Length: > 0 } said
            ? said : "Not sent. " + GoogleDefaultSmsApp.ComposerNotice;
    }

    private void ForgetDefaultSms()
    {
        if (defaultSms.Forget()) DefaultSmsChanged("pairing");
    }

    private void DefaultSmsChanged(string source)
    {
        GoogleMessagesLog.Write($"settings default_sms_app={defaultSms.IsDefault switch { true => "yes", false => "no", _ => "unknown" }} source={source}");
        DefaultSmsAppChanged?.Invoke(this, EventArgs.Empty);
    }

    // An allowed send carries the caution while the phone says it is not the default. Not a refusal: the phone decides,
    // and a setting changed on the phone since it last said so must not leave the composer shut.
    private SendPermission WithDefaultSmsCaution(SendPermission permission) =>
        permission.Allowed && defaultSms.Caution is { } caution ? permission with { Caution = caution } : permission;
}
