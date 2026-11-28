namespace MessageLink.Core.Services;

/// <summary>
/// Whether the person is in the middle of something the client shows (a dialog is open), so work that would interrupt them waits: the
/// Google sign-in renewal, which may open a browser. Where the client and the host are one process this is the window's own dialog flag;
/// where they are not, it is the client's last word on it (<c>client.state</c>).
/// </summary>
public interface IClientBusy
{
    bool Busy { get; }
}
