namespace MessageLink.Presentation.Services;

public interface IUiDispatcher
{
    Task InvokeAsync(Func<Task> action);
}
