using System.Globalization;

namespace AV1Encod.Services;

/// <param name="VideoRatio">taille de la vidéo encodée / taille de la vidéo source, sur les extraits</param>
/// <param name="PredictedSize">taille estimée du fichier final (vidéo + pistes audio gardées)</param>
/// <param name="Gain">gain estimé sur le fichier entier : 0,62 = 62 % plus petit ; négatif = plus gros</param>
public sealed record GainEstimate(double VideoRatio, long PredictedSize, double Gain, int Samples, TimeSpan Elapsed);

/// <summary>
/// Estime le gain d'un encodage avant de le lancer : on encode quelques extraits avec exactement les mêmes réglages
/// vidéo, et on compare à la taille des mêmes passages dans la source. Évite de passer une heure à produire un
/// fichier plus gros que l'original (source déjà très compressée, faux 4K bruité…).
/// </summary>
public static class GainEstimator
{
    const int SampleCount = 3;
    const int SampleSeconds = 10;

    public static async Task<GainEstimate?> EstimateAsync(string handBrake, string ffprobe, EncodeOptions options, SourceInfo src,
                                                          Action<string> status, CancellationToken ct)
    {
        if (src.Duration < SampleSeconds * SampleCount * 2) return null; // trop court pour que ça ait un sens
        var started = DateTime.Now;
        var dir = Path.Combine(Path.GetTempPath(), "AV1Encod", "estimation");
        Directory.CreateDirectory(dir);

        long srcBytes = 0, encBytes = 0;
        var done = 0;
        try
        {
            for (var i = 0; i < SampleCount; i++)
            {
                // extraits répartis dans le film (20 %, 50 %, 80 %) : génériques et noirs exclus
                var start = src.Duration * (0.2 + 0.3 * i);
                status($"estimation du gain : extrait {i + 1}/{SampleCount}…");
                var output = Path.Combine(dir, $"extrait_{Environment.ProcessId}_{i}.mkv");
                var args = options.VideoOnly(output).BuildArgs();
                args.AddRange(["--start-at", $"seconds:{(int)start}", "--stop-at", $"seconds:{SampleSeconds}"]);

                var r = await ProcessRunner.RunAsync(handBrake, args, TimeSpan.FromMinutes(10), ct);
                if (r.ExitCode != 0 || !File.Exists(output)) continue;
                var enc = new FileInfo(output).Length;
                File.Delete(output);

                var source = await SourceVideoBytesAsync(ffprobe, src.Path, start, SampleSeconds, ct);
                if (source <= 0 || enc <= 0) continue;
                srcBytes += source;
                encBytes += enc;
                done++;
            }
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(dir, $"extrait_{Environment.ProcessId}_*")) { try { File.Delete(f); } catch { } }
        }
        if (done == 0 || srcBytes == 0) return null;

        var ratio = encBytes / (double)srcBytes;
        // la vidéo représente la taille du fichier moins ses pistes audio ; seules les pistes gardées sont recopiées
        long AudioBytes(IEnumerable<TrackInfo> tracks) => (long)tracks.Sum(t => t.BitrateKbps * 1000 / 8 * src.Duration);
        var allAudio = AudioBytes(src.AudioTracks);
        var keptAudio = AudioBytes(options.AllAudio ? src.AudioTracks : src.AudioTracks.Where(t => options.AudioTracks?.Contains(t.Index) ?? t.Index == 1));
        var videoBytes = Math.Max(src.Size - allAudio, src.Size / 2);
        var predicted = (long)(videoBytes * ratio) + keptAudio;
        return new GainEstimate(ratio, predicted, 1 - predicted / (double)src.Size, done, DateTime.Now - started);
    }

    /// <summary>Taille des paquets vidéo de la source sur un intervalle (lecture des en-têtes de paquets, rapide).</summary>
    static async Task<long> SourceVideoBytesAsync(string ffprobe, string file, double start, int seconds, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-read_intervals", $"{start.ToString("0.###", CultureInfo.InvariantCulture)}%+{seconds}",
             "-show_entries", "packet=size", "-of", "csv=p=0", file],
            TimeSpan.FromMinutes(2), ct);
        return Fmt.Lines(r.StdOut).Sum(l => long.TryParse(l.Trim().TrimEnd(','), out var n) ? n : 0);
    }
}
