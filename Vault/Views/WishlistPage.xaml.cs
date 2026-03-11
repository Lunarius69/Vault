using System.Windows.Controls;
using Vault.Models;

namespace Vault.Views
{
    public partial class WishlistPage : Page
    {
        private AppSettings _settings;

        public WishlistPage(AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;
        }
    }
}
