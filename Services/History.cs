using System.Text.Json;

namespace AV1Encod.Services;

/// <summary>Un encodage terminé (réussi, en échec ou annulé).</summary>
public sealed class HistoryEntry
{
    public DateTime Date { get; set; }
    /// <summary>ok, warning (terminé avec avertissements), suspect, failed, cancelled</summary>
    public string Result { get; set; } = "ok";
    public string SourcePath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string Encoder { get; set; } = "";
    public string Preset { get; set; } = "";
    public int Quality { get; set; }
    public long SourceSize { get; set; }
    public long OutputSize { get; set; }
    /// <summary>Durée de la vidéo, en secondes.</summary>
    public double Duration { get; set; }
    /// <summary>Temps d'encodage, en secondes.</summary>
    public double EncodeSeconds { get; set; }
    public double AverageFps { get; set; }
    /// <summary>Résumé de la source : « HEVC · 2160p · DV 8.1 · 2 audio · 3 ST ».</summary>
    public string SourceSummary { get; set; } = "";
    /// <summary>HDR / Dolby Vision du fichier produit (« DV 10.1 », « HDR10 », « SDR »…).</summary>
    public string OutputHdr { get; set; } = "";
    public int AudioTracks { get; set; }
    public int SubtitleTracks { get; set; }
    public int Warnings { get; set; }
    public string? LogPath { get; set; }

    public bool Succeeded => Result is "ok" or "warning";
    public long Saved => Succeeded && OutputSize > 0 ? SourceSize - OutputSize : 0;
}

public static class History
{
    static readonly string FilePath = Path.Combine(AppSettings.AppDir, "history.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static List<HistoryEntry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch { }
        return [];
    }

    public static void Save(List<HistoryEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.AppDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entries, Json));
            File.Move(tmp, FilePath, overwrite: true); // pas de fichier à moitié écrit si le PC s'éteint
        }
        catch { }
    }

    public static void Append(HistoryEntry e)
    {
        var all = Load();
        all.Add(e);
        Save(all);
    }

    /// <summary>Fichiers produits avec succès : ignorés quand on ré-ajoute un dossier.</summary>
    public static HashSet<string> SuccessfulOutputs() =>
        new(Load().Where(e => e.Succeeded).Select(e => e.OutputPath), StringComparer.OrdinalIgnoreCase);
}
