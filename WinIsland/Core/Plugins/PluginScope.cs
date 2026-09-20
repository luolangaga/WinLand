namespace WinIsland.Core.Plugins;

public sealed class PluginScope
{
    private readonly List<IDisposable> _disposables = new();
    private readonly Action<Exception>? _onError;
    private readonly object _gate = new();

    public PluginScope(string pluginId, Action<Exception>? onError = null)
    {
        PluginId = pluginId;
        _onError = onError;
    }

    public string PluginId { get; }

    public bool IsRevoked { get; private set; }

    public T Register<T>(T disposable) where T : IDisposable
    {
        lock (_gate)
        {
            if (IsRevoked)
            {
                TryDispose(disposable);
                return disposable;
            }

            _disposables.Add(disposable);
        }

        return disposable;
    }

    public void Revoke()
    {
        IDisposable[] items;
        lock (_gate)
        {
            if (IsRevoked)
            {
                return;
            }

            IsRevoked = true;
            items = _disposables.ToArray();
            _disposables.Clear();
        }

        for (var i = items.Length - 1; i >= 0; i--)
        {
            TryDispose(items[i]);
        }
    }

    private void TryDispose(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
        }
    }
}
