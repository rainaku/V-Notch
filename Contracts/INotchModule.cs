namespace VNotch.Contracts;

public interface INotchModule
{
    string ModuleName { get; }
    void Initialize();
    void Start();
    void Stop();
}
