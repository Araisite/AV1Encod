using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace AV1Encod.Services;

public sealed record EpisodeMeta(int Season, int Number, int Absolute, string Title)
{
    /// <summary>TVmaze numérote certaines séries par année (« saison 2023 ») : ce n'est pas la numérotation de Sonarr / TVDB.</summary>
    public bool SeasonIsYear => Season >= 1900;
}

/// <summary>Infos d'une série trouvées sur TVmaze (titre officiel, année, identifiants, épisodes).</summary>
public sealed record SeriesMeta(int TvMazeId, string Title, int? Year, string? TvdbId, string? ImdbId, bool IsAnime, IReadOnlyList<EpisodeMeta> Episodes)
{
    public EpisodeMeta? Find(int? season, int? episode, int? absolute) =>
        season is not null && episode is not null ? Episodes.FirstOrDefault(e => e.Season == season && e.Number == episode)
        : absolute is not null ? Episodes.FirstOrDefault(e => e.Absolute == absolute) : null;
}

/// <summary>
/// Recherche (facultative, désactivée par défaut) sur l'API publique TVmaze : gratuite, sans compte ni clé.
/// Seul le titre de la série est envoyé.
/// </summary>
public static class TvMaze
{
    static readonly HttpClient Http = CreateClient();
    static readonly ConcurrentDictionary<string, SeriesMeta?> Cache = new(StringComparer.OrdinalIgnoreCase);

    static HttpClient CreateClient()
    {
        var c = new HttpClient { BaseAddress = new Uri("https://api.tvmaze.com/"), Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"AV1Encod/{Updater.CurrentVersion} (+{Updater.RepoUrl})");
        return c;
    }

    /// <param name="preferAnime">nom de release au format anime (« [Groupe] Titre - 12 ») : on préfère une série d'animation</param>
    public static async Task<SeriesMeta?> FindAsync(string title, int? year, CancellationToken ct, bool preferAnime = false)
    {
        var key = $"{title}|{year}|{preferAnime}";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        using var search = JsonDocument.Parse(await Http.GetStringAsync($"search/shows?q={Uri.EscapeDataString(title)}", ct));
        int? Premiered(JsonElement s) => DateTime.TryParse(s.S("premiered"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Year : null;
        static string Norm(string? s) => new((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        bool Animated(JsonElement s) => s.S("type") == "Animation" || s.A("genres").Any(g => g.GetString() == "Anime");

        // plusieurs séries peuvent porter le même nom (One Piece anime / live action, spin-offs…) :
        // nom exact d'abord, puis année, puis animation pour les releases d'anime, puis le score de TVmaze
        var wanted = Norm(title);
        var show = search.RootElement.EnumerateArray()
            .Select((r, i) => (Show: r.P("show"), Rank: i))
            .Where(x => x.Show is not null)
            .Select(x => (Show: x.Show!.Value, x.Rank))
            .OrderByDescending(x => Norm(x.Show.S("name")) == wanted)
            .ThenByDescending(x => year is not null && Premiered(x.Show) == year)
            .ThenByDescending(x => preferAnime && Animated(x.Show))
            .ThenByDescending(x => x.Show.I("weight")) // popularité TVmaze : la série principale avant ses spin-offs
            .ThenBy(x => x.Rank)
            .Select(x => x.Show)
            .FirstOrDefault();
        if (show.ValueKind == JsonValueKind.Undefined) { Cache[key] = null; return null; }

        var id = show.I("id");
        using var eps = JsonDocument.Parse(await Http.GetStringAsync($"shows/{id}/episodes", ct)); // épisodes « réguliers » seulement
        var list = eps.RootElement.EnumerateArray()
            .Where(e => e.I("season") > 0 && e.I("number") > 0)
            .OrderBy(e => e.I("season")).ThenBy(e => e.I("number"))
            .Select((e, i) => new EpisodeMeta(e.I("season"), e.I("number"), i + 1, e.S("name") ?? ""))
            .ToList();
        var ext = show.P("externals");
        var meta = new SeriesMeta(id, show.S("name") ?? title, Premiered(show), ext?.S("thetvdb"), ext?.S("imdb"),
            show.A("genres").Any(g => string.Equals(g.GetString(), "Anime", StringComparison.OrdinalIgnoreCase)), list);
        Cache[key] = meta;
        return meta;
    }
}
