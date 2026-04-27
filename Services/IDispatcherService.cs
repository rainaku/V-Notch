namespace VNotch.Services;





public interface IDispatcherService
{
    
    
    
    void BeginInvoke(Action action);

    
    
    
    void Invoke(Action action);

    
    
    
    bool CheckAccess();
}
