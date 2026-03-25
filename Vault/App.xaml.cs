// App.xaml.cs
using System.Windows;
using Vault.Database;
using Vault.Models;
using Vault.Services;

namespace Vault
{
    public partial class App : Application
    {
        private BackgroundAutoDetectService? _backgroundService;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Run schema updates
            await Task.Run(() => VaultContext.EnsureSchema());

            // Optional: Enrich game data on first run
            try
            {
                var settings = AppSettings.Load();
                var enricher = new GameDataEnricher();
                await enricher.EnrichMissingGenresAsync();
            }
            catch { /* Ignore errors */ }

            // Start background services
            try
            {
                var settings = AppSettings.Load();
                _backgroundService = new BackgroundAutoDetectService(settings);
            }
            catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _backgroundService?.Stop();
            base.OnExit(e);
        }
    }
}