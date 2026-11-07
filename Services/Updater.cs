using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace AV1Encod.Services;

public sealed record ReleaseInfo(Version Version, string Tag, string Name, string Notes, string? AssetUrl, long AssetSize, string PageUrl);

/// <summary>Mises à jour via les « Releases » GitHub du projet : on télécharge le nouvel exe et on remplace l'actuel.</summary>
public static class Updater
{
    public const string Repo = "Araisite/AV1Encod";
    public const string RepoUrl = "https://github.com/" + Repo;
    public const string ReleasesUrl = RepoUrl + "/releases";
    const string AssetName = "AV1Encod.exe";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"AV1Encod/{CurrentVersion}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version CurrentVersion => Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>Dernière version publiée, ou null s'il n'y en a pas encore.</summary>
    public static async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", cts.Token);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
        var r = doc.RootElement;
        var tag = r.S("tag_name") ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        var asset = r.A("assets").Cast<JsonElement?>().FirstOrDefault(a => string.Equals(a!.Value.S("name"), AssetName, StringComparison.OrdinalIgnoreCase));
        return new ReleaseInfo(Normalize(version), tag, r.S("name") ?? tag, r.S("body") ?? "",
            asset?.S("browser_download_url"), (long)(asset?.D("size") ?? 0), r.S("html_url") ?? ReleasesUrl);
    }

    public static bool IsNewer(ReleaseInfo r) => r.Version > CurrentVersion;

    /// <summary>
    /// Télécharge le nouvel exe à côté de l'actuel, échange les fichiers (un exe en cours d'exécution peut être
    /// renommé sous Windows) puis relance l'application.
    /// </summary>
    public static async Task DownloadAndRestartAsync(ReleaseInfo r, IProgress<double> progress, CancellationToken ct = default)
    {
        if (r.AssetUrl is null) throw new InvalidOperationException("Cette version ne contient pas de fichier AV1Encod.exe.");
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Chemin de l'application introuvable.");
        var tmp = exe + ".new";

        using (var resp = await Http.GetAsync(r.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? r.AssetSize;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress.Report((double)done / total);
            }
        }

        // garde-fous : taille annoncée et en-tête d'exécutable Windows
        var info = new FileInfo(tmp);
        var header = new byte[2];
        await using (var fs = File.OpenRead(tmp)) fs.ReadExactly(header);
        if ((r.AssetSize > 0 && info.Length != r.AssetSize) || header[0] != 'M' || header[1] != 'Z')
        {
            File.Delete(tmp);
            throw new InvalidDataException("Le fichier téléchargé est incomplet ou invalide.");
        }

        // si la version installée est signée, la nouvelle doit l'être par le même éditeur
        // (empêche l'installation d'un exe modifié ou provenant d'ailleurs)
        if (Authenticode.TrustedSigner(exe) is { } signer && Authenticode.TrustedSigner(tmp) != signer)
        {
            File.Delete(tmp);
            throw new InvalidDataException("la nouvelle version n'est pas signée par le même éditeur : mise à jour refusée par sécurité. " +
                                           "Téléchargez-la depuis la page GitHub du projet.");
        }

        var old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        File.Move(tmp, exe);
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    /// <summary>Supprime l'ancien exe laissé par une mise à jour précédente.</summary>
    public static void CleanupPreviousVersion()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && File.Exists(exe + ".old")) File.Delete(exe + ".old");
        }
        catch { } // l'ancien processus n'est peut-être pas encore fermé : ce sera fait au prochain lancement
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
