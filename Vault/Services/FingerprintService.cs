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
        private const int IntroScanSeconds = 600;   // first 10 min
        private const int OutroScanSeconds = 480;   // last 8 min
        private const int MinIntroDuration = 20;    // seconds
        private const int MinOutroDuration = 15;

        // ------------------------------------------------------------------ //
        //  Public entry point
        // ------------------------------------------------------------------ //

        public async Task ProcessShowAsync(
            int mediaItemId,
            List<Episode> episodes,
            CancellationToken ct = default)
        {
            var toProcess = episodes
                .Where(e => !e.FingerprintProcessed &&
                            !string.IsNullOrEmpty(e.FilePath) &&
                            File.Exists(e.FilePath))
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

            // Step 2: extract amplitude hashes for intro section
            var hashes = new Dictionary<int, float[]>();
            foreach (var ep in episodes)
            {
                if (ct.IsCancellationRequested) return;
                if (!durations.TryGetValue(ep.Id, out double dur)) continue;
                double scanLen = Math.Min(IntroScanSeconds, dur);
                var h = await ExtractAmplitudeHashAsync(ep.FilePath!, 0, scanLen, ep.Id);
                if (h != null && h.Length > 0)
                    hashes[ep.Id] = h;
            }

            // Step 3: find common intro segment across episodes
            var introResults = new Dictionary<int, (double start, double end)>();
            if (hashes.Count >= 2)
                introResults = FindCommonSegment(hashes);

            // Step 4: outro detection per episode
            var outroResults = new Dictionary<int, double>();
            foreach (var ep in episodes)
            {
                if (ct.IsCancellationRequested) return;
                if (!durations.TryGetValue(ep.Id, out double dur)) continue;

                // 4a: chapter markers (MKV/MP4)
                var (_, chapOutro) = await ReadChapterMarkersAsync(ep.FilePath!);
                if (chapOutro > 0) { outroResults[ep.Id] = chapOutro; continue; }

                // 4b: black-frame detection
                double? bf = await DetectBlackFrameOutroAsync(ep.FilePath!, dur);
                if (bf.HasValue) { outroResults[ep.Id] = bf.Value; continue; }

                // 4c: silence detection fallback
                double? sil = await DetectSilenceOutroAsync(ep.FilePath!, dur);
                if (sil.HasValue) outroResults[ep.Id] = sil.Value;
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

        private static Dictionary<int, (double start, double end)> FindCommonSegment(
            Dictionary<int, float[]> hashes)
        {
            var results = new Dictionary<int, (double start, double end)>();
            var epIds = hashes.Keys.ToList();

            for (int i = 0; i < epIds.Count; i++)
            {
                int idA = epIds[i];
                float[] hashA = hashes[idA];

                int bestRunLen = 0;
                int bestOffsetA = 0;

                for (int j = 0; j < epIds.Count; j++)
                {
                    if (i == j) continue;
                    float[] hashB = hashes[epIds[j]];

                    (int offset, int runLen) = SlideMatch(hashA, hashB);
                    if (runLen > bestRunLen)
                    {
                        bestRunLen = runLen;
                        bestOffsetA = offset;
                    }
                }

                if (bestRunLen >= MinIntroDuration)
                {
                    results[idA] = (
                        start: bestOffsetA,
                        end: bestOffsetA + bestRunLen
                    );
                    Debug.WriteLine(
                        $"[FP] Ep{idA} intro {bestOffsetA}s → {bestOffsetA + bestRunLen}s");
                }
            }

            return results;
        }

        /// <summary>
        /// Slides hashB over hashA, returns (bestOffsetInA, bestMatchRunSeconds).
        /// Two 1-second windows "match" if their RMS difference is below threshold.
        /// </summary>
        private static (int offset, int runLen) SlideMatch(float[] hashA, float[] hashB)
        {
            const float Threshold = 0.04f; // RMS difference tolerance
            const int MinRun = 5;     // minimum consecutive matches to count

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

                return starts
                    .Where(t => t < totalDuration - 10)
                    .OrderByDescending(t => t)
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

                if (introResults.TryGetValue(ep.Id, out var intro))
                {
                    dbEp.IntroStart = ep.IntroStart = intro.start;
                    dbEp.IntroEnd = ep.IntroEnd = intro.end;
                }

                if (outroResults.TryGetValue(ep.Id, out double outro))
                    dbEp.OutroStart = ep.OutroStart = outro;

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