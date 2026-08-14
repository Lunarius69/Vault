using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Vault.Services;

namespace Vault.Views
{
    public partial class FolderMatchWindow : Window
    {
        public List<FolderMatchResult> ApprovedResults { get; private set; } = new();

        private readonly ObservableCollection<MatchRowViewModel> _rows = new();

        public FolderMatchWindow(List<FolderMatchResult> matches, List<Models.MediaItem> unmatched,
            string categoryLabel, string rootFolder)
        {
            InitializeComponent();

            TxtSummary.Text = matches.Count > 0
                ? $"Found {matches.Count} match{(matches.Count != 1 ? "es" : "")} for {categoryLabel}"
                : $"No matches found for {categoryLabel}";
            TxtRootFolder.Text = $"Searched: {rootFolder}";

            foreach (var m in matches)
                _rows.Add(new MatchRowViewModel(m));

            MatchesList.ItemsSource = _rows;

            if (unmatched.Count > 0)
            {
                UnmatchedExpander.Header = $"No match found ({unmatched.Count})";
                UnmatchedExpander.Visibility = Visibility.Visible;
                UnmatchedList.ItemsSource = unmatched.Select(m => m.Title).OrderBy(t => t).ToList();
            }

            BtnApply.Content = $"Apply Selected ({_rows.Count(r => r.IsChecked)})";
            foreach (var row in _rows)
                row.PropertyChanged += (s, e) =>
                    BtnApply.Content = $"Apply Selected ({_rows.Count(r => r.IsChecked)})";
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _rows) row.IsChecked = true;
        }

        private void BtnSelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _rows) row.IsChecked = false;
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            ApprovedResults = _rows.Where(r => r.IsChecked).Select(r => r.Result).ToList();
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }
    }

    public class MatchRowViewModel : INotifyPropertyChanged
    {
        public FolderMatchResult Result { get; }

        public MatchRowViewModel(FolderMatchResult result)
        {
            Result = result;
            // Exact matches start checked (safe to trust); fuzzy matches
            // start unchecked so the user reviews them before applying.
            _isChecked = result.Confidence == FolderMatchConfidence.Exact;
        }

        public string Title => Result.Item.Title;
        public string FolderName => Path.GetFileName(Result.FolderPath);
        public string ConfidenceLabel => Result.Confidence == FolderMatchConfidence.Exact ? "EXACT" : "FUZZY";
        public Brush BadgeColor => Result.Confidence == FolderMatchConfidence.Exact
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00b894"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#fdcb6e"));

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}