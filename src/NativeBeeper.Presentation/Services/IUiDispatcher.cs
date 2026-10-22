namespace NativeBeeper.Presentation.Services;

public interface IUiDispatcher
{
    Task InvokeAsync(Func<Task> action);
}
