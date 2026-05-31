using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vault.Database;
using Vault.Models;

namespace Vault.Services
{
    /// <summary>
    /// Detects intro/outro timestamps using only ffmpeg + ffprobe.
    /// No fpcalc or external fingerprinting tools required.
    ///
    /// Intro detection:
    ///   Extracts a small WAV from the start of each episode, then computes
    ///   a simple amplitude hash per 1-second chunk. Cross-correlates hashes
    ///   across episodes to find the longest common audio segment.
    ///
    /// Outro detection:
    ///   1. MKV/MP4 chapter markers (instant, no processing)
    ///   2. Black-frame detection on the last 8 minutes (ffmpeg blackdetect)
    ///   3. Silence detection near the end as a last resort
    /// </summary>
    public class FingerprintService
    {
        private const int IntroScanSeconds = 300;   // first 5 min — anime OPs never start later
        private const int OutroScanSeconds = 300;   // last 5 min — covers ED + omake
        private const int MinIntroDuration = 20;    // seconds
        private const int MinOutroDuration = 15;
        private const int CompareWindow = 8;        // compare each episode with 8 neighbors

        // ------------------------------------------------------------------ //
        //  Public entry point
        // ------------------------------------------------------------------ //

        public async Task ProcessShowAsync(
            int mediaItemId,
            List<Episode> episodes,
            int currentIndex = 0,
            CancellationToken ct = default)
        {
            // Reset episodes that were marked processed but have no intro data — allows retry
            try
            {
                using var db = new VaultContext();
                var stuck = db.Episodes
                    .Where(e => e.MediaItemId == mediaItemId &&
                                e.FingerprintProcessed &&
                                e.IntroEnd < 0 && e.OutroStart < 0);
                bool any = false;
                foreach (var e in stuck) { e.FingerprintProcessed = false; any = true; }
                if (any) await db.SaveChangesAsync();
            }
            catch { }

            // Process up to 20 episodes nearest to what's currently playing
            int start = Math.Max(0, currentIndex - 5);
            var toProcess = episodes
                .Skip(start)
                .Where(e => !e.FingerprintProcessed &&
                            !string.IsNullOrEmpty(e.FilePath) &&
                            File.Exists(e.FilePath))
                .Take(20)
                .ToList();

            if (toProcess.Count < 2) return;

            await Task.Run(async () =>
            {
                try { await RunAsync(mediaItemId, toProcess, ct); }
                catch (Exception ex)
                { Debug.WriteLine($"[FP] Error: {ex.Message}"); }
            }, ct);
        }

        // ------------------------------------------------------------------ //
        //  Main pipeline
        // ------------------------------------------------------------------ //

        private async Task RunAsync(
            int mediaItemId, List<Episode> episodes, CancellationToken ct)
        {
            Debug.WriteLine($"[FP] Starting for {episodes.Count} episodes");

            // Step 1: get durations
            var durations = new Dictionary<int, double>();
            foreach (var ep in episodes)
            {
                if (ct.IsCancellationRequested) return;
                double d = await GetDurationAsync(ep.FilePath!);
                if (d > 0) durations[ep.Id] = d;
            }

            // Step 2a: extract amplitude hashes for intro section (in episode order)
            var introHashes = new List<(int Id, float[] Hash)>();
            foreach (var ep in episodes)
            {
                if (ct.IsCancellationRequested) return;
                if (!durations.TryGetValue(ep.Id, out double dur)) continue;
                double scanLen = Math.Min(IntroScanSeconds, dur);
                var h = await ExtractAmplitudeHashAsync(ep.FilePath!, 0, scanLen, ep.Id);
                if (h != null && h.Length > 0)
                    introHashes.Add((ep.Id, h));
            }

            // Step 2b: extract amplitude hashes for outro section (last OutroScanSeconds)
            var outroHashes = new List<(int Id, float[] Hash)>();
            var outroOffsets = new Dictionary<int, double>();
            foreach (var ep in episodes)
            {
                if (ct.IsCancellationRequested) return;
                if (!durations.TryGetValue(ep.Id, out double dur)) continue;
                double outroOffset = Math.Max(0, dur - OutroScanSeconds);
                double scanLen = Math.Min(OutroScanSeconds, dur);
                var h = await ExtractAmplitudeHashAsync(ep.FilePath!, outroOffset, scanLen, ep.Id);
                if (h != null && h.Length > 0)
                {
                    outroHashes.Add((ep.Id, h));
                    outroOffsets[ep.Id] = outroOffset;
                }
            }

            // Step 3a: find common intro segment across episodes
            var introResults = new Dictionary<int, (double start, double end)>();
            if (introHashes.Count >= 2)
                introResults = FindCommonSegment(introHashes, MinIntroDuration);

            // Step 3b: find common outro/ED segment via cross-correlation
            var outroCorrelationResults = new Dictionary<int, double>();
            if (outroHashes.Count >= 2)
            {
                var rawOutro = FindCommonSegment(outroHashes, MinOutroDuration);
                var candidates = new Dictionary<int, double>();
                foreach (var kvp in rawOutro)
                {
                    if (!outroOffsets.TryGetValue(kvp.Key, out double outroOffset)) continue;
                    if (!durations.TryGetValue(kvp.Key, out double dur)) continue;
                    double absoluteStart = outroOffset + kvp.Value.start;
                    if (absoluteStart > dur / 2 && absoluteStart < dur - 10)
                        candidates[kvp.Key] = absoluteStart;
                }

                // Consistency check: measure how consistently the outro falls at the same
                // distance from the end. Anime with a fixed ED song → low variance (accept).
                // Drama/thriller with recurring theme at variable positions → high variance (reject).
                if (candidates.Count >= 2)
                {
                    var timesFromEnd = candidates
                        .Select(kvp => durations.TryGetValue(kvp.Key, out double d)
                            ? d - kvp.Value : -1)
                        .Where(t => t > 0)
                        .ToList();

                    double mean = timesFromEnd.Average();
                    double stdDev = Math.Sqrt(
                        timesFromEnd.Select(t => (t - mean) * (t - mean)).Average());

                    if (stdDev <= 45)
                    {
                        outroCorrelationResults = candidates;
                        Debug.WriteLine(
                            $"[FP] Outro correlation accepted: σ={stdDev:F1}s, mean={mean:F1}s from end");
                    }
                    else
                    {
                        Debug.WriteLine(
                            $"[FP] Outro correlation rejected: σ={stdDev:F1}s (inconsistent — not a fixed ED)");
                    }
                }
            }

            // Step 4: outro detection per episode — chapter markers → ED cross-correlation → black-frame → silence
            var outroResults = new Dictionary<int, double>();
            foreach (var ep in episodes)
            {
                if (ct.IsCancellationRequested) return;
                if (!durations.TryGetValue(ep.Id, out double dur)) continue;

                // 4a: chapter markers (MKV/MP4)
                var (_, chapOutro) = await ReadChapterMarkersAsync(ep.FilePath!);
                if (chapOutro > 0) { outroResults[ep.Id] = chapOutro; continue; }

                // 4b: ED song cross-correlation (only set when consistency check passed)
                if (outroCorrelationResults.TryGetValue(ep.Id, out double edStart))
                { outroResults[ep.Id] = edStart; continue; }

                // No further audio detection — if cross-correlation was rejected it means
                // the show has no consistent ED, so silence/black-frame would also be unreliable.
                // Fall through to the time-based trigger in the player.
            }

            // Step 5: save results
            await SaveResultsAsync(episodes, introResults, outroResults);
            Debug.WriteLine("[FP] Done.");
        }

        // ------------------------------------------------------------------ //
        //  Amplitude hash extraction via FFmpeg
        //  Exports audio to raw PCM, then computes RMS per 1-second window
        // ------------------------------------------------------------------ //

        private async Task<float[]?> ExtractAmplitudeHashAsync(
            string filePath, double startSec, double durationSec, int epId)
        {
            try
            {
                string tmpPath = Path.Combine(
                    Path.GetTempPath(), $"vault_ah_{epId}.raw");

                string ffmpeg = FindFFmpeg();

                // Export mono 8kHz raw PCM float
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-y -ss {startSec.ToString(Inv)} " +
                                $"-i \"{filePath}\" " +
                                $"-t {durationSec.ToString(Inv)} " +
                                $"-vn -ac 1 -ar 8000 -f f32le \"{tmpPath}\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                var proc = Process.Start(psi);
                if (proc == null) return null;
                await proc.WaitForExitAsync();

                if (!File.Exists(tmpPath)) return null;

                // Read raw floats
                byte[] raw = await File.ReadAllBytesAsync(tmpPath);
                File.Delete(tmpPath);

                int samplesPerSec = 8000;
                int totalSamples = raw.Length / 4;
                int totalWindows = totalSamples / samplesPerSec;
                if (totalWindows == 0) return null;

                var hashes = new float[totalWindows];
                for (int w = 0; w < totalWindows; w++)
                {
                    double rms = 0;
                    int offset = w * samplesPerSec * 4;
                    for (int s = 0; s < samplesPerSec; s++)
                    {
                        float sample = BitConverter.ToSingle(raw, offset + s * 4);
                        rms += sample * sample;
                    }
                    hashes[w] = (float)Math.Sqrt(rms / samplesPerSec);
                }

                // Normalize by mean so volume differences between episodes don't affect matching
                float mean = 0;
                foreach (var v in hashes) mean += v;
                mean /= hashes.Length;
                if (mean > 0.0001f)
                    for (int w = 0; w < hashes.Length; w++)
                        hashes[w] /= mean;

                return hashes;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FP] AmplitudeHash error: {ex.Message}");
                return null;
            }
        }

        // ------------------------------------------------------------------ //
        //  Cross-correlation of amplitude hashes
        // ------------------------------------------------------------------ //

        // orderedHashes must be in episode order so neighbor comparison stays within the same arc/OP
        private static Dictionary<int, (double start, double end)> FindCommonSegment(
            List<(int Id, float[] Hash)> orderedHashes, int minDuration)
        {
            var results = new Dictionary<int, (double start, double end)>();

            for (int i = 0; i < orderedHashes.Count; i++)
            {
                var (idA, hashA) = orderedHashes[i];
                int bestRunLen = 0, bestOffsetA = 0;

                int jMin = Math.Max(0, i - CompareWindow);
                int jMax = Math.Min(orderedHashes.Count - 1, i + CompareWindow);

                for (int j = jMin; j <= jMax; j++)
                {
                    if (j == i) continue;
                    (int offset, int runLen) = SlideMatch(hashA, orderedHashes[j].Hash);
                    if (runLen > bestRunLen) { bestRunLen = runLen; bestOffsetA = offset; }
                }

                if (bestRunLen >= minDuration)
                    results[idA] = (start: bestOffsetA, end: bestOffsetA + bestRunLen);
            }

            return results;
        }

        /// <summary>
        /// Slides hashB over hashA, returns (bestOffsetInA, bestMatchRunSeconds).
        /// Two 1-second windows "match" if their RMS difference is below threshold.
        /// </summary>
        private static (int offset, int runLen) SlideMatch(float[] hashA, float[] hashB)
        {
            const float Threshold = 0.07f; // RMS difference tolerance
            const int MinRun = 3;     // minimum consecutive matches to count

            int bestRun = 0, bestOff = 0;

            for (int slide = -(hashB.Length - 1); slide < hashA.Length; slide++)
            {
                int run = 0, maxRun = 0;
                for (int k = 0; k < hashB.Length; k++)
                {
                    int ia = slide + k;
                    if (ia < 0 || ia >= hashA.Length) { run = 0; continue; }

                    float diff = Math.Abs(hashA[ia] - hashB[k]);
                    if (diff <= Threshold)
                    {
                        run++;
                        maxRun = Math.Max(maxRun, run);
                    }
                    else
                    {
                        run = 0;
                    }
                }

                if (maxRun >= MinRun && maxRun > bestRun)
                {
                    bestRun = maxRun;
                    bestOff = Math.Max(0, slide);
                }
            }

            return (bestOff, bestRun);
        }

        // ------------------------------------------------------------------ //
        //  Black-frame outro detection
        // ------------------------------------------------------------------ //

        private async Task<double?> DetectBlackFrameOutroAsync(
            string filePath, double totalDuration)
        {
            try
            {
                double offset = Math.Max(0, totalDuration - OutroScanSeconds);
                var psi = new ProcessStartInfo
                {
                    FileName = FindFFmpeg(),
                    Arguments = $"-ss {offset.ToString(Inv)} -i \"{filePath}\" " +
                                $"-vf \"blackdetect=d=0.3:pix_th=0.10\" " +
                                $"-an -f null -",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                var proc = Process.Start(psi);
                if (proc == null) return null;
                string output = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();

                var starts = new List<double>();
                foreach (var line in output.Split('\n'))
                {
                    if (!line.Contains("black_start")) continue;
                    var m = System.Text.RegularExpressions.Regex.Match(
                        line, @"black_start:(\d+\.?\d*)");
                    if (m.Success && double.TryParse(m.Groups[1].Value,
                            System.Globalization.NumberStyles.Float, Inv, out double bs))
                        starts.Add(offset + bs);
                }

                // Take the earliest black frame in the scan window — that's the transition
                // into the outro sequence, not a later transition within it (e.g. omake start).
                return starts
                    .Where(t => t < totalDuration - 10)
                    .OrderBy(t => t)
                    .Cast<double?>()
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FP] BlackFrame error: {ex.Message}");
                return null;
            }
        }

        // ------------------------------------------------------------------ //
        //  Silence outro detection (last resort)
        // ------------------------------------------------------------------ //

        private async Task<double?> DetectSilenceOutroAsync(
            string filePath, double totalDuration)
        {
            try
            {
                double offset = Math.Max(0, totalDuration - OutroScanSeconds);
                var psi = new ProcessStartInfo
                {
                    FileName = FindFFmpeg(),
                    Arguments = $"-ss {offset.ToString(Inv)} -i \"{filePath}\" " +
                                $"-af \"silencedetect=n=-40dB:d=1.0\" -f null -",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                var proc = Process.Start(psi);
                if (proc == null) return null;
                string output = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();

                var starts = new List<double>();
                foreach (var line in output.Split('\n'))
                {
                    if (!line.Contains("silence_start")) continue;
                    var m = System.Text.RegularExpressions.Regex.Match(
                        line, @"silence_start:\s*(\d+\.?\d*)");
                    if (m.Success && double.TryParse(m.Groups[1].Value,
                            System.Globalization.NumberStyles.Float, Inv, out double ss))
                        starts.Add(offset + ss);
                }

                return starts
                    .Where(t => t < totalDuration - 5)
                    .OrderByDescending(t => t)
                    .Cast<double?>()
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FP] Silence error: {ex.Message}");
                return null;
            }
        }

        // ------------------------------------------------------------------ //
        //  Chapter markers (MKV/MP4)
        // ------------------------------------------------------------------ //

        public static async Task<(double introEnd, double outroStart)>
            ReadChapterMarkersAsync(string filePath)
        {
            double introEnd = -1, outroStart = -1;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = FindFFprobe(),
                    Arguments = $"-v quiet -print_format json -show_chapters \"{filePath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                var proc = Process.Start(psi);
                if (proc == null) return (introEnd, outroStart);
                string json = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();

                var matches = System.Text.RegularExpressions.Regex.Matches(
                    json,
                    @"""title""\s*:\s*""([^""]+)""[^}]*?""start_time""\s*:\s*""([^""]+)""");

                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    string title = m.Groups[1].Value.ToLower();
                    if (!double.TryParse(m.Groups[2].Value,
                            System.Globalization.NumberStyles.Float, Inv,
                            out double time)) continue;

                    if (title.Contains("intro") || title.Contains("opening") ||
                        title.Contains("recap") || title == "op")
                        introEnd = time;

                    if (title.Contains("credit") || title.Contains("ending") ||
                        title.Contains("outro") || title == "ed")
                        outroStart = time;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FP] Chapter error: {ex.Message}");
            }
            return (introEnd, outroStart);
        }

        // ------------------------------------------------------------------ //
        //  Duration via ffprobe
        // ------------------------------------------------------------------ //

        private static async Task<double> GetDurationAsync(string filePath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = FindFFprobe(),
                    Arguments = $"-v error -show_entries format=duration " +
                                $"-of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                var p = Process.Start(psi);
                if (p == null) return 0;
                string o = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();
                return double.TryParse(o.Trim(),
                    System.Globalization.NumberStyles.Float, Inv, out double d) ? d : 0;
            }
            catch { return 0; }
        }

        // ------------------------------------------------------------------ //
        //  Save to DB
        // ------------------------------------------------------------------ //

        private static async Task SaveResultsAsync(
            List<Episode> episodes,
            Dictionary<int, (double start, double end)> introResults,
            Dictionary<int, double> outroResults)
        {
            using var db = new VaultContext();
            foreach (var ep in episodes)
            {
                var dbEp = await db.Episodes.FindAsync(ep.Id);
                if (dbEp == null) continue;

                bool gotIntro = introResults.TryGetValue(ep.Id, out var intro);
                bool gotOutro = outroResults.TryGetValue(ep.Id, out double outro);

                if (gotIntro)
                {
                    dbEp.IntroStart = ep.IntroStart = intro.start;
                    dbEp.IntroEnd = ep.IntroEnd = intro.end;
                }

                if (gotOutro)
                    dbEp.OutroStart = ep.OutroStart = outro;

                // Only mark as processed if we actually found something — otherwise keep
                // FingerprintProcessed = false so the next session can retry.
                if (gotIntro || gotOutro)
                    dbEp.FingerprintProcessed = ep.FingerprintProcessed = true;
            }
            await db.SaveChangesAsync();
        }

        // ------------------------------------------------------------------ //
        //  Tool paths
        // ------------------------------------------------------------------ //

        private static string FindFFmpeg()
        {
            string[] c = {
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\ffmpeg-8.1\bin\ffmpeg.exe",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"),
                "ffmpeg"
            };
            return c.FirstOrDefault(File.Exists) ?? "ffmpeg";
        }

        private static string FindFFprobe()
        {
            string[] c = {
                @"C:\ffmpeg\bin\ffprobe.exe",
                @"C:\ffmpeg-8.1\bin\ffprobe.exe",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffprobe.exe"),
                "ffprobe"
            };
            return c.FirstOrDefault(File.Exists) ?? "ffprobe";
        }

        private static readonly System.Globalization.CultureInfo Inv =
            System.Globalization.CultureInfo.InvariantCulture;
    }
}