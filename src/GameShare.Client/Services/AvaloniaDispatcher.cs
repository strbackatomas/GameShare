using Avalonia.Threading;

namespace GameShare.Client.Services;

public sealed class AvaloniaDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
