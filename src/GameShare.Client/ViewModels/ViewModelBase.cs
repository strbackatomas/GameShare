using CommunityToolkit.Mvvm.ComponentModel;
using GameShare.Client.Services;

namespace GameShare.Client.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>
    /// Runs something that talks to the agent. The agent's own explanation of a failure is shown as it is,
    /// anything unexpected is shown with its message, and neither can crash the window.
    /// </summary>
    /// <returns>True when it succeeded.</returns>
    protected static async Task<bool> TryAsync(Func<Task> work, Action<string> onError)
    {
        try
        {
            await work().ConfigureAwait(true);
            return true;
        }
        catch (AgentException ex) { onError(ex.Message); }
        catch (Exception ex) when (ex is not OperationCanceledException) { onError("Neočekávaná chyba: " + ex.Message); }
        return false;
    }
}
