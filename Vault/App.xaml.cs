using System.Configuration;
using System.Data;
using System.Windows;
using Vault.Database;

namespace Vault
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            VaultContext.EnsureSchema();
        }
    }
}