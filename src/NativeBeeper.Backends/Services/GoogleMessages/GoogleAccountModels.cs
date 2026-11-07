using NativeBeeper.Core.Messaging;

namespace NativeBeeper.App.Services.GoogleMessages;

internal sealed class GoogleAccountModels(AccountNamespace ids)
{
    public string Account => ids.Qualify(GoogleModels.Account);
    public string PendingPrefix => ids.Qualify(GoogleModels.PendingPrefix);
    public string Qualify(string value) => ids.Qualify(GoogleModels.Qualify(value));
    public string Native(string value) => GoogleModels.Native(ids.Native(value));
    public bool Owns(string value) => GoogleModels.Owns(value) && AccountNamespace.KeyOf(value) == ids.Key;
    public ChatSummary Map(GoogleChat value) => ids.Map(GoogleModels.Map(value));
    public ChatMessage Map(GoogleMessage value, GoogleChat chat) => ids.Map(GoogleModels.Map(value, chat));
}
