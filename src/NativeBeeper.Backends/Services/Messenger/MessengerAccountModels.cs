using NativeBeeper.Core.Messaging;

namespace NativeBeeper.App.Services.Messenger;

internal sealed class MessengerAccountModels(AccountNamespace ids)
{
    public string Account => ids.Qualify(MessengerModels.Account);
    public string Qualify(string value) => ids.Qualify(MessengerModels.Qualify(value));
    public string Native(string value) => MessengerModels.Native(ids.Native(value));
    public bool Owns(string value) => MessengerModels.Owns(value) && AccountNamespace.KeyOf(value) == ids.Key;
    public string QualifyMedia(string value) => ids.Qualify(MessengerModels.QualifyMedia(value));
    public string NativeMedia(MessageAttachment value) => MessengerModels.NativeMedia(value with
    {
        Id = value.Id is null ? null : ids.Native(value.Id),
        SourceUrl = ids.NativeMediaValue(value.SourceUrl)
    });
    public ChatSummary Map(MessengerChat value) => ids.Map(MessengerModels.Map(value));
    public ChatMessage Map(MessengerMessage value) => ids.Map(MessengerModels.Map(value));
}
