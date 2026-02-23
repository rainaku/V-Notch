using System.Windows.Threading;

namespace VNotch.Services;

/// <summary>
/// WPF implementation of IDispatcherService using the application dispatcher.
/// </summary>
public class DispatcherService : IDispatcherService
{
    private readonly Dispatcher _dispatcher;

    public DispatcherService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void BeginInvoke(Action action)
    {
        _dispatcher.BeginInvoke(action);
    }

    public void Invoke(Action action)
    {
        _dispatcher.Invoke(action);
    }

    public bool CheckAccess()
    {
        return _dispatcher.CheckAccess();
    }
}
