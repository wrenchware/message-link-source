namespace MessageLink.Core.Services;

/// <summary>Whether anybody could be looking at the application, for a provider that can poll less while nobody is.</summary>
public interface IAppPresence
{
    bool Present { get; }

    /// <summary>Raised with the new answer, only when it changes, on whichever thread reported the change.</summary>
    event Action<bool>? Changed;
}
