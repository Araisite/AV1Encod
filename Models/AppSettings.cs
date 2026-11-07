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
    public bool CheckUpdatesAtStartup { get; set; } = true;

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
