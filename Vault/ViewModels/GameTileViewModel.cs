using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using System.Windows;
using Vault.Models;

namespace Vault.ViewModels
{
    public class GameTileViewModel : INotifyPropertyChanged
    {
        private Game _game;
        private string? _boxArtPath;

        public GameTileViewModel(Game game)
        {
            _game = game;
            _boxArtPath = game.BoxArtPath;
        }

        public int Id => _game.Id;
        public string Title => _game.Title;
        public string Platform => _game.Platform;
        public string Status => _game.Status;
        public bool IsDownloaded => _game.IsDownloaded;
        public Game Game => _game;

        public string? BoxArtPath
        {
            get => _boxArtPath;
            set
            {
                _boxArtPath = value;
                _game.BoxArtPath = value;
                OnPropertyChanged(nameof(BoxArtPath));
                OnPropertyChanged(nameof(HasBoxArt));
            }
        }

        public bool HasBoxArt =>
            !string.IsNullOrEmpty(_boxArtPath) && File.Exists(_boxArtPath);

        public string PlatformShort => GetPlatformShort(_game.Platform);

        public Brush PlatformColor =>
            new SolidColorBrush(GetPlatformColor(_game.Platform));

        public Brush StatusColor =>
            new SolidColorBrush(GetStatusColor(_game.Status));

        private static System.Windows.Media.Color GetPlatformColor(string platform)
        {
            var c = System.Windows.Media.ColorConverter.ConvertFromString;
            return platform?.ToLower() switch
            {
                var p when p.Contains("pc") => (System.Windows.Media.Color)c("#0078d4"),
                var p when p.Contains("ps5") => (System.Windows.Media.Color)c("#003087"),
                var p when p.Contains("ps4") => (System.Windows.Media.Color)c("#003087"),
                var p when p.Contains("ps3") => (System.Windows.Media.Color)c("#003087"),
                var p when p.Contains("ps2") => (System.Windows.Media.Color)c("#003087"),
                var p when p.Contains("switch") => (System.Windows.Media.Color)c("#e60012"),
                var p when p.Contains("xbox") => (System.Windows.Media.Color)c("#107c10"),
                var p when p.Contains("gamecube") || p.Contains("wii") => (System.Windows.Media.Color)c("#6a0dad"),
                var p when p.Contains("psp") || p.Contains("vita") => (System.Windows.Media.Color)c("#003087"),
                _ => (System.Windows.Media.Color)c("#2d3561")
            };
        }

        private static string GetPlatformShort(string platform)
        {
            return platform?.ToLower() switch
            {
                var p when p.Contains("playstation 5") || p.Contains("ps5") => "PS5",
                var p when p.Contains("playstation 4") || p.Contains("ps4") => "PS4",
                var p when p.Contains("playstation 3") || p.Contains("ps3") => "PS3",
                var p when p.Contains("playstation 2") || p.Contains("ps2") => "PS2",
                var p when p.Contains("switch 2") => "NSW2",
                var p when p.Contains("switch") => "NSW",
                var p when p.Contains("xbox 360") => "X360",
                var p when p.Contains("xbox") => "XBOX",
                var p when p.Contains("gamecube") => "GCN",
                var p when p.Contains("wii u") => "WiiU",
                var p when p.Contains("wii") => "Wii",
                var p when p.Contains("pc") => "PC",
                var p when p.Contains("psp") => "PSP",
                var p when p.Contains("vita") => "Vita",
                var p when p.Contains("3ds") => "3DS",
                var p when p.Contains("ds") => "DS",
                _ => platform?.Length > 6 ? platform[..6] : platform ?? "?"
            };
        }

        private static System.Windows.Media.Color GetStatusColor(string status)
        {
            var c = System.Windows.Media.ColorConverter.ConvertFromString;
            return status?.ToLower() switch
            {
                "playing" => (System.Windows.Media.Color)c("#00b894"),
                "completed" => (System.Windows.Media.Color)c("#0984e3"),
                "not started" => (System.Windows.Media.Color)c("#636e72"),
                "on hold" => (System.Windows.Media.Color)c("#fdcb6e"),
                "dropped" => (System.Windows.Media.Color)c("#d63031"),
                _ => (System.Windows.Media.Color)c("#636e72")
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
