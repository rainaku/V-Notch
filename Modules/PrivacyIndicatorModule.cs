using System;
using VNotch.Services;

namespace VNotch.Modules;

public class PrivacyIndicatorModule : NotchModuleBase
{
    public override string ModuleName => "PrivacyIndicator";
    public override TimeSpan? TickInterval => null;

    private readonly PrivacyIndicatorService _service;

    public event EventHandler<PrivacyIndicatorState>? StateChanged;

    public PrivacyIndicatorState CurrentState => _service.CurrentState;

    public PrivacyIndicatorModule(PrivacyIndicatorService service)
    {
        _service = service;
    }

    protected override void OnStart()
    {
        _service.StateChanged += OnServiceStateChanged;
        _service.Start();
    }

    protected override void OnStop()
    {
        _service.StateChanged -= OnServiceStateChanged;
        _service.Stop();
    }

    protected override void OnTick()
    {
    }

    protected override void OnDispose()
    {
        _service.Dispose();
    }

    private void OnServiceStateChanged(object? sender, PrivacyIndicatorState state)
    {
        StateChanged?.Invoke(this, state);
    }
}
