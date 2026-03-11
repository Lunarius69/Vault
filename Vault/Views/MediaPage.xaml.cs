using System.Windows.Controls;
using Vault.Models;

namespace Vault.Views
{
    public partial class MediaPage : Page
    {
        private AppSettings _settings;
        private string _mediaType;

        public MediaPage(AppSettings settings, string mediaType)
        {
            InitializeComponent();
            _settings = settings;
            _mediaType = mediaType;
        }
    }
}
