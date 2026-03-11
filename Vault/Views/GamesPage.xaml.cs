using System.Windows.Controls;
using Vault.Models;

namespace Vault.Views
{
    public partial class GamesPage : Page
    {
        private AppSettings _settings;

        public GamesPage(AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;
        }
    }
}