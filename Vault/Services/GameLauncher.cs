using Vault.Models;
namespace Vault.Services
{
    public class GameLauncher
    {
        private AppSettings _settings;
        public GameLauncher(AppSettings settings) => _settings = settings;
        public void Launch(Game game) { } // Full implementation next step
    }
}