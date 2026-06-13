using System;
using System.Threading;

namespace Nexus
{
    public sealed class RedundancyManager : IDisposable
    {
        private readonly IReadWriteDevice _primary;
        private readonly IReadWriteDevice _backup;
        private IReadWriteDevice _active;
        private readonly int _healthCheckIntervalMs;
        private Timer _healthTimer;
        private bool _disposed;

        public IReadWriteDevice ActiveDevice => _active;
        public bool IsUsingBackup => _active == _backup;
        public event EventHandler<bool> OnFailover;

        public RedundancyManager(IReadWriteDevice primary, IReadWriteDevice backup, int healthCheckIntervalMs = 5000)
        {
            _primary = primary ?? throw new ArgumentNullException(nameof(primary));
            _backup = backup ?? throw new ArgumentNullException(nameof(backup));
            _active = primary;
            _healthCheckIntervalMs = healthCheckIntervalMs;
        }

        public void Start()
        {
            _healthTimer = new Timer(CheckHealth, null, 0, _healthCheckIntervalMs);
        }

        public void Stop()
        {
            _healthTimer?.Dispose();
            _healthTimer = null;
        }

        private void CheckHealth(object state)
        {
            if (_disposed) return;
            if (!_active.IsConnected)
            {
                var standby = _active == _primary ? _backup : _primary;
                if (standby.Connect().IsSuccess)
                {
                    _active = standby;
                    OnFailover?.Invoke(this, standby == _backup);
                }
            }
        }

        public void ForceFailover()
        {
            if (_backup.Connect().IsSuccess)
            {
                _primary.Disconnect();
                _active = _backup;
                OnFailover?.Invoke(this, true);
            }
        }

        public void RestorePrimary()
        {
            if (_primary.Connect().IsSuccess)
            {
                _backup.Disconnect();
                _active = _primary;
                OnFailover?.Invoke(this, false);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            try { _primary.Dispose(); } catch { }
            try { _backup.Dispose(); } catch { }
        }
    }
}
