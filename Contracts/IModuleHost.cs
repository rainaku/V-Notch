namespace VNotch.Contracts;

public interface IModuleHost
{
    void InvokeOnUiThread(Action action);
}
