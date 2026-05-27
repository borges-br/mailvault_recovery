using System;
using System.Threading;

namespace MailVault.Core;

public sealed class ProgressThrottler<T> : IDisposable
{
    private readonly Action<T> _target;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private T? _latestUpdate;
    private bool _hasPendingUpdate;
    private readonly ITimer _timer;
    private bool _isDisposed;
    private T? _lastEmitted;

    public ProgressThrottler(Action<T> target, TimeSpan interval, TimeProvider? timeProvider = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _interval = interval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timer = _timeProvider.CreateTimer(OnTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Report(T update)
    {
        lock (_lock)
        {
            if (_isDisposed) return;

            // Deduplicate if identical to last emitted
            if (Equals(update, _lastEmitted))
            {
                return;
            }

            _latestUpdate = update;
            
            if (!_hasPendingUpdate)
            {
                _hasPendingUpdate = true;
                _timer.Change(_interval, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnTimerTick(object? state)
    {
        T update;
        lock (_lock)
        {
            if (_isDisposed || !_hasPendingUpdate) return;
            update = _latestUpdate!;
            _hasPendingUpdate = false;
            _lastEmitted = update;
        }

        _target(update);
    }

    public void Flush()
    {
        T update = default!;
        bool hasUpdate = false;

        lock (_lock)
        {
            if (_isDisposed) return;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            
            if (_hasPendingUpdate)
            {
                update = _latestUpdate!;
                _hasPendingUpdate = false;
                _lastEmitted = update;
                hasUpdate = true;
            }
        }

        if (hasUpdate)
        {
            _target(update);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _timer.Dispose();
        }
    }
}
