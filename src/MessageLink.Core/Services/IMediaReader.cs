using MessageLink.Core.Messaging;

namespace MessageLink.Core.Services;

public interface IMediaReader
{
    // inline: the conversation is drawing this small, so the smaller copy will do and the full one is not worth
    // fetching. Opening it asks without this, and gets the photograph itself.
    Task<byte[]> ReadImageAsync(MessageAttachment attachment, CancellationToken cancellationToken = default, bool inline = false);
}
