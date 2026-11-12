using SphereNet.Game.PlayerBots;

namespace SphereNet.BotBrain;

/// <summary>Owns a background model load and its published brain. Shutdown waits
/// for loading as well as disposal, so a late load cannot resurrect the service.</summary>
public sealed class BotBrainHost : IDisposable
{
    private readonly object _gate = new();
    private readonly object _shutdownGate = new();
    private readonly Action<IBotBrain?> _publish;
    private readonly Task _load;
    private IBotBrain? _brain;
    private bool _stopping;
    private bool _disposed;

    public BotBrainHost(Func<IBotBrain?> load, Action<IBotBrain?> publish, Action<Exception>? failed = null)
    {
        _publish = publish;
        _load = Task.Run(() =>
        {
            IBotBrain? brain = null;
            try
            {
                brain = load();
                if (brain == null) return;
                lock (_gate)
                {
                    if (!_stopping)
                    {
                        _publish(brain);
                        _brain = brain;
                        brain = null; // ownership transferred
                    }
                }
            }
            catch (Exception ex) { failed?.Invoke(ex); }
            finally { (brain as IDisposable)?.Dispose(); }
        });
    }

    public void Dispose()
    {
        lock (_shutdownGate)
        {
            if (_disposed) return;
            IBotBrain? brain;
            lock (_gate)
            {
                _stopping = true;
                brain = _brain;
                _brain = null;
                _publish(null);
            }
            try
            {
                _load.GetAwaiter().GetResult();
                (brain as IDisposable)?.Dispose();
            }
            finally { _disposed = true; }
        }
    }
}
