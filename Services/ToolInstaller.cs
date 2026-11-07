namespace AV1Encod.Services;

/// <summary>Installe les outils externes (HandBrakeCLI, FFmpeg) avec winget, sans droits administrateur.</summary>
public static class ToolInstaller
{
    public sealed record Package(string WingetId, string Label, string[] Provides, string ManualUrl);

    public static readonly Package[] Packages =
    [
        new("HandBrake.HandBrake.CLI", "HandBrake CLI", ["HandBrakeCLI"], "https://handbrake.fr/downloads2.php"),
        new("Gyan.FFmpeg", "FFmpeg (ffprobe + ffmpeg)", ["ffprobe", "ffmpeg"], "https://www.gyan.dev/ffmpeg/builds/"),
    ];

    public static string? FindWinget() => ToolLocator.Find("winget");

    /// <summary>Paquets dont au moins un outil est introuvable.</summary>
    public static List<Package> Missing() =>
        Packages.Where(p => p.Provides.Any(t => ToolLocator.Find(t) is null)).ToList();

    /// <returns>true si les outils du paquet sont trouvés après l'installation</returns>
    public static async Task<bool> InstallAsync(Package p, Action<string> onLine, CancellationToken ct)
    {
        var winget = FindWinget() ?? throw new InvalidOperationException("winget est introuvable sur ce PC.");
        await ProcessRunner.RunStreamingAsync(winget,
            ["install", "-e", "--id", p.WingetId, "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity", "--silent"],
            onLine, onLine, false, ct);
        // le code de sortie de winget n'est pas fiable (« déjà installé »…) : on vérifie directement
        return p.Provides.All(t => ToolLocator.Find(t) is not null);
    }
}
