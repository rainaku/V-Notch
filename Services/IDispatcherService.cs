namespace VNotch.Services;

/// <summary>
/// Abstraction for UI thread dispatching.
/// Allows ViewModels to marshal work to the UI thread without depending on System.Windows.
/// </summary>
public interface IDispatcherService
{
    /// <summary>
    /// Invoke an action on the UI thread asynchronously.
    /// </summary>
    void BeginInvoke(Action action);

    /// <summary>
    /// Invoke an action on the UI thread synchronously.
    /// </summary>
    void Invoke(Action action);

    /// <summary>
    /// Check if the current thread is the UI thread.
    /// </summary>
    bool CheckAccess();
}
