using System.Text.Json;

namespace AV1Encod;

public sealed class AppSettings
{
    public static readonly string AppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AV1Encod");
    public static readonly string LogDir = Path.Combine(AppDir, "logs");
    static readonly string FilePath = Path.Combine(AppDir, "settings.json");

    public string Encoder { get; set; } = "svt_av1_10bit";
    public string Preset { get; set; } = "6";
    public int Quality { get; set; } = 28;
    public int CropMode { get; set; }
    public bool AllAudio { get; set; } = true;
    public bool AllSubs { get; set; } = true;
    public bool KeepDefaultSub { get; set; } = true;
    public bool Chapters { get; set; } = true;
    public bool DeepCheck { get; set; } = true;
    public bool LowPriority { get; set; } = true;
    public bool Overwrite { get; set; }
    public string ExtraArgs { get; set; } = "";
    public string SrcDir { get; set; } = "";
    /// <summary>« short » = Titre.Année.AV1 ; « template » = formats Radarr / Sonarr ci-dessous.</summary>
    public string NamingMode { get; set; } = "short";
    public string MovieTemplate { get; set; } = Services.OutputNaming.DefaultMovieTemplate;
    public string SeriesTemplate { get; set; } = Services.OutputNaming.DefaultSeriesTemplate;
    public string AnimeTemplate { get; set; } = Services.OutputNaming.DefaultAnimeTemplate;
    /// <summary>Recherche facultative sur TVmaze (titre d'épisode, année, numéro absolu) : désactivée par défaut.</summary>
    public bool OnlineLookup { get; set; }
    /// <summary>null = pas encore demandé : la question est posée au premier lancement (aucun accès réseau sans accord).</summary>
    public bool? CheckUpdatesAtStartup { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
