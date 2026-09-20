using WinIsland.Core;

namespace WinIsland.Core.Plugins;

public sealed class ScopedSettings : ISettingsStore
{
    private readonly SettingsService _inner;
    private readonly string _prefix;
    private readonly PluginScope _scope;
    private readonly Action<Action> _postToUI;
    private readonly object _gate = new();
    private Action<string>? _changed;
    private bool _subscribed;

    public ScopedSettings(SettingsService inner, string pluginId, PluginScope scope, Action<Action> postToUI)
    {
        _inner = inner;
        _prefix = pluginId + ".";
        _scope = scope;
        _postToUI = postToUI;
    }

    public event Action<string>? Changed
    {
        add
        {
            lock (_gate)
            {
                _changed += value;
            }

            EnsureSubscribed();
        }
        remove
        {
            lock (_gate)
            {
                _changed -= value;
            }
        }
    }

    public T Get<T>(string key, T defaultValue) => _inner.Get(_prefix + key, defaultValue);

    public void Set<T>(string key, T value) => _inner.Set(_prefix + key, value);

    private void EnsureSubscribed()
    {
        lock (_gate)
        {
            if (_subscribed || _scope.IsRevoked)
            {
                return;
            }

            _subscribed = true;
        }

        _inner.Changed += OnInnerChanged;
        _scope.Register(new ActionDisposable(() =>
        {
            lock (_gate)
            {
                _subscribed = false;
            }

            _inner.Changed -= OnInnerChanged;
        }));
    }

    private void OnInnerChanged(string key)
    {
        if (_scope.IsRevoked || !key.StartsWith(_prefix, StringComparison.Ordinal))
        {
            return;
        }

        var localKey = key.Substring(_prefix.Length);
        Action<string>? handler;
        lock (_gate)
        {
            handler = _changed;
        }

        if (handler == null)
        {
            return;
        }

        _postToUI(() =>
        {
            if (_scope.IsRevoked)
            {
                return;
            }

            handler(localKey);
        });
    }
}
