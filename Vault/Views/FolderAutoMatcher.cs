using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Vault.Models;

namespace Vault.Services
{
    public enum FolderMatchConfidence { Exact, Fuzzy }

    public class FolderMatchResult
    {
        public MediaItem Item { get; set; } = null!;
        public string FolderPath { get; set; } = "";
        public FolderMatchConfidence Confidence { get; set; }
    }

    /// <summary>
    /// Matches MediaItem titles against subfolder names under a configured
    /// root folder (e.g. Settings' "Movies Folder"), so folders don't have to
    /// be assigned one at a time from each title's detail page.
    /// </summary>
    public static class FolderAutoMatcher
    {
        public static (List<FolderMatchResult> Matches, List<MediaItem> Unmatched) Match(
            IEnumerable<MediaItem> items, string rootFolder)
        {
            var matches = new List<FolderMatchResult>();
            var unmatched = new List<MediaItem>();
            var itemList = items.ToList();

            if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
            {
                unmatched.AddRange(itemList);
                return (matches, unmatched);
            }

            List<string> subfolders;
            try { subfolders = Directory.GetDirectories(rootFolder).ToList(); }
            catch { unmatched.AddRange(itemList); return (matches, unmatched); }

            var normalizedFolders = subfolders
                .Select(f => (Path: f, Normalized: Normalize(Path.GetFileName(f))))
                .Where(f => !string.IsNullOrEmpty(f.Normalized))
                .ToList();

            // Each folder can only be claimed once, even if two titles are
            // similar enough to both fuzzy-match it.
            var claimedFolders = new HashSet<string>();

            foreach (var item in itemList)
            {
                string normTitle = Normalize(item.Title);
                if (string.IsNullOrEmpty(normTitle)) { unmatched.Add(item); continue; }

                var exact = normalizedFolders.FirstOrDefault(f =>
                    f.Normalized == normTitle && !claimedFolders.Contains(f.Path));

                if (exact.Path != null)
                {
                    claimedFolders.Add(exact.Path);
                    matches.Add(new FolderMatchResult
                    {
                        Item = item,
                        FolderPath = exact.Path,
                        Confidence = FolderMatchConfidence.Exact
                    });
                    continue;
                }

                // Fuzzy: folder name contains the title, or vice versa
                // (handles "Alien (1979)" folder vs "Alien" title, or a
                // folder with extra tags like "Alien 1979 1080p BluRay").
                var fuzzy = normalizedFolders
                    .Where(f => !claimedFolders.Contains(f.Path) &&
                                (f.Normalized.Contains(normTitle) || normTitle.Contains(f.Normalized)))
                    .OrderByDescending(f => f.Normalized.Length == normTitle.Length)
                    .ThenBy(f => System.Math.Abs(f.Normalized.Length - normTitle.Length))
                    .FirstOrDefault();

                if (fuzzy.Path != null)
                {
                    claimedFolders.Add(fuzzy.Path);
                    matches.Add(new FolderMatchResult
                    {
                        Item = item,
                        FolderPath = fuzzy.Path,
                        Confidence = FolderMatchConfidence.Fuzzy
                    });
                }
                else
                {
                    unmatched.Add(item);
                }
            }

            return (matches, unmatched);
        }

        // Lowercase, strip a trailing (YYYY) year tag, strip punctuation, collapse whitespace.
        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.ToLowerInvariant();
            s = Regex.Replace(s, @"\(\d{4}\)", "");
            s = Regex.Replace(s, @"[^a-z0-9]+", " ");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }
    }
}
