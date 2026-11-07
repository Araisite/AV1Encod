namespace AV1Encod.Services;

public static class ToolLocator
{
    /// <summary>Cherche un exécutable : dossier de l'appli, PATH, emplacements d'installation courants, paquets WinGet.</summary>
    public static string? Find(string name)
    {
        var exe = name + ".exe";
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, exe),
            Path.Combine(AppContext.BaseDirectory, "tools", exe),
        };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim().Trim('"'), exe)));

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.AddRange([
            Path.Combine(pf, "HandBrake", exe),
            Path.Combine(pf, "HandBrakeCLI", exe),
            Path.Combine(pf, "ffmpeg", "bin", exe),
            Path.Combine(@"C:\ffmpeg\bin", exe),
            Path.Combine(local, "Microsoft", "WinGet", "Links", exe),
        ]);

        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return c; } catch { }
        }

        var winget = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        try
        {
            if (Directory.Exists(winget))
                return Directory.EnumerateFiles(winget, exe, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { }
        return null;
    }
}
