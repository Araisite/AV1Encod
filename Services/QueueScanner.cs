using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace AV1Encod.Services;

public static class QueueScanner
{
    public static readonly string[] VideoExtensions = [".mkv", ".mp4", ".m4v", ".mov", ".m2ts", ".mts", ".ts", ".avi", ".webm", ".wmv", ".mpg", ".mpeg", ".vob"];
    static readonly Regex Sample = new(@"(?i)(^|[.\s_\-\[(])sample([.\s_\-\])]|$)", RegexOptions.Compiled);
    /// <summary>Sorties d'un passage précédent : « Titre.S01E01.AV1.mkv ».</summary>
    static readonly Regex OwnOutput = new(@"(?i)[.\s_](AV1|H265|H264|VP9)$", RegexOptions.Compiled);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int StrCmpLogicalW(string a, string b);

    /// <summary>Tri « naturel » comme l'Explorateur : E2 avant E10.</summary>
    public static readonly Comparer<string> NaturalOrder = Comparer<string>.Create(StrCmpLogicalW);

    public sealed record ScanResult(List<string> Files, int SkippedSamples, int SkippedOutputs);

    /// <summary>
    /// Développe les fichiers / dossiers déposés en liste de vidéos (sous-dossiers compris, ex. « Saison 01 »).
    /// Dans un dossier, on ignore les samples et les fichiers déjà encodés par l'appli ; un fichier
    /// déposé explicitement est toujours pris.
    /// </summary>
    public static ScanResult Expand(IEnumerable<string> paths)
    {
        var files = new List<string>();
        int samples = 0, outputs = 0;
        foreach (var p in paths)
        {
            if (File.Exists(p)) { files.Add(Path.GetFullPath(p)); continue; }
            if (!Directory.Exists(p)) continue;
            IEnumerable<string> found;
            try { found = Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).ToList(); }
            catch { continue; }
            foreach (var f in found.Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                var folder = Path.GetFileName(Path.GetDirectoryName(f)) ?? "";
                if (Sample.IsMatch(name) || folder.Equals("sample", StringComparison.OrdinalIgnoreCase)
                    || folder.Equals("samples", StringComparison.OrdinalIgnoreCase)) { samples++; continue; }
                if (OwnOutput.IsMatch(name)) { outputs++; continue; }
                files.Add(Path.GetFullPath(f));
            }
        }
        var distinct = files.Distinct(StringComparer.OrdinalIgnoreCase).Order(NaturalOrder).ToList();
        return new ScanResult(distinct, samples, outputs);
    }
}
