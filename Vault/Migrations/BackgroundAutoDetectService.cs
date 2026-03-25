// Services/BackgroundAutoDetectService.cs
using System;
using System.Threading;
using System.Threading.Tasks;
using Vault.Database;
using Vault.Models;

namespace Vault.Services
{
    public class BackgroundAutoDetectService
    {
        private readonly AppSettings _settings;
        private readonly Timer _timer;
        private bool _isRunning;

        public BackgroundAutoDetectService(AppSettings settings)
        {
            _settings = settings;
            // Run every 24 hours after initial 5 minute delay
            _timer = new Timer(OnTimerTick, null, TimeSpan.FromMinutes(5), TimeSpan.FromHours(24));
        }

        private async void OnTimerTick(object? state)
        {
            if (_isRunning) return;
            _isRunning = true;

            try
            {
                var detector = new AutoDetectService(_settings);
                await detector.ScanAndUpdateAsync();
            }
            catch { }
            finally
            {
                _isRunning = false;
            }
        }

        public void Stop() => _timer?.Dispose();
    }
}