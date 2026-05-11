using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using VNotch.Contracts;

namespace VNotch.Services;


public interface IModuleLifecycleManager : IDisposable
{
    IReadOnlyCollection<INotchModule> Modules { get; }

    void Register(INotchModule module);
    void InitializeAll();
    void StartAll();
    void StopAll();

    T? Get<T>() where T : class, INotchModule;
}


public sealed class ModuleLifecycleManager : IModuleLifecycleManager
{
    private readonly List<INotchModule> _modules = new();
    private bool _disposed;
    private bool _startedAll;

    public IReadOnlyCollection<INotchModule> Modules => new ReadOnlyCollection<INotchModule>(_modules);

    public void Register(INotchModule module)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ModuleLifecycleManager));
        if (module == null) throw new ArgumentNullException(nameof(module));

        _modules.Add(module);
        RuntimeLog.Log("MODULE-HOST", $"Registered '{module.ModuleName}'");

        
        if (_startedAll)
        {
            SafeInvoke(module, m => m.Initialize(), "Initialize");
            SafeInvoke(module, m => m.Start(), "Start");
        }
    }

    public void InitializeAll()
    {
        if (_disposed) return;

        foreach (var module in _modules)
        {
            SafeInvoke(module, m => m.Initialize(), "Initialize");
        }
    }

    public void StartAll()
    {
        if (_disposed) return;

        InitializeAll();

        foreach (var module in _modules)
        {
            SafeInvoke(module, m => m.Start(), "Start");
        }

        _startedAll = true;
    }

    public void StopAll()
    {
        if (_disposed) return;

        foreach (var module in _modules)
        {
            SafeInvoke(module, m => m.Stop(), "Stop");
        }

        _startedAll = false;
    }

    public T? Get<T>() where T : class, INotchModule
    {
        foreach (var module in _modules)
        {
            if (module is T typed) return typed;
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;

        StopAll();

        foreach (var module in _modules)
        {
            SafeInvoke(module, m => m.Dispose(), "Dispose");
        }

        _modules.Clear();
        _disposed = true;
    }

    private static void SafeInvoke(INotchModule module, Action<INotchModule> action, string phase)
    {
        try
        {
            action(module);
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("MODULE-HOST", $"'{module.ModuleName}' {phase} failed: {ex}");
        }
    }
}
