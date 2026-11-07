using System.Globalization;
using System.Text.RegularExpressions;

namespace AV1Encod.Services;

/// <summary>
/// Informations tirées d'un nom de release (« Mon.Film.2026.MULTi.VFF.2160p.WEB-DL.DV.H265-GROUPE »,
/// « Ma.Serie.S01E03.Titre.Episode.1080p.AMZN.WEB-DL… », « [SubsPlease] Mon Anime - 12 (1080p) [ABCD1234] »)
/// ou d'un nom déjà renommé par Radarr / Sonarr (« Ma Série (2024) - S01E03 - 003 - Titre [WEBDL-1080p]… »).
/// </summary>
public sealed record ReleaseName
{
    public string Title { get; init; } = "";
    public int? Year { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }
    /// <summary>Dernier épisode d'un fichier multi-épisodes (S01E01-E03), sinon null.</summary>
    public int? EpisodeLast { get; init; }
    /// <summary>Numéro absolu (animes) : « - 012 - » ou « [Groupe] Titre - 12 ».</summary>
    public int? Absolute { get; init; }
    public int? AbsoluteLast { get; init; }
    public string? EpisodeTitle { get; init; }
    public string? Source { get; init; }
    public string? Resolution { get; init; }
    public bool Proper { get; init; }
    public bool Repack { get; init; }
    /// <summary>Révision des releases d'anime : « v2 ».</summary>
    public int? Revision { get; init; }
    public IReadOnlyList<string> Editions { get; init; } = [];
    public IReadOnlyList<string> LanguageTags { get; init; } = [];
    /// <summary>Plateforme de streaming (AMZN, NF, ATVP…) : un « custom format » courant dans les guides TRaSH.</summary>
    public string? Streaming { get; init; }
    /// <summary>Autres balises souvent utilisées comme custom formats : HDLight, 4KLight, Hybrid, iNTERNAL…</summary>
    public IReadOnlyList<string> OtherTags { get; init; } = [];
    public string? Group { get; init; }
    public string? Hash { get; init; }
    public string? TmdbId { get; init; }
    public string? ImdbId { get; init; }
    public string? TvdbId { get; init; }
    /// <summary>Nom au format des releases d'anime (« [Groupe] Titre - 12 »).</summary>
    public bool AnimeStyle { get; init; }

    public bool IsEpisode => (Season is not null && Episode is not null) || Absolute is not null;

    static readonly Regex TmdbRx = new(@"(?i)(?:\{tmdb-|\[tmdb(?:id)?-|tmdbid[-=])(\d+)", RegexOptions.Compiled);
    static readonly Regex TvdbRx = new(@"(?i)(?:\{tvdb-|\[tvdb(?:id)?-|tvdbid[-=])(\d+)", RegexOptions.Compiled);
    static readonly Regex ImdbRx = new(@"(?i)\b(tt\d{7,9})\b", RegexOptions.Compiled);
    static readonly Regex YearRx = new(@"^\(?((?:19|20)\d{2})\)?$", RegexOptions.Compiled);
    static readonly Regex EpisodeRx = new(@"^(?i)S(\d{1,3})E(\d{1,4})((?:-?E\d{1,4})*)$", RegexOptions.Compiled);
    static readonly Regex ResolutionRx = new(@"^(?i)(2160|1080|720|576|480)[pi]$", RegexOptions.Compiled);
    static readonly Regex TechTagRx = new(
        @"^(?i)(\d{3,4}[pi]|4k|uhd|multi|truefrench|french|vf[fqi2]?|vof|vostfr|vost|subfrench|web|web-?dl|webrip|hdtv|bluray|blu-ray|bdrip|brrip|" +
        @"remux|hdr|hdr10|hdr10plus|dv|dovi|x26[45]|h\.?26[45]|hevc|avc|av1|10bits?|proper|repack|hdlight|4klight|ddp?5\.1|atmos|dts|truehd|aac|eac3|ac3|" +
        @"amzn|nf|atvp|dsnp|hmax|max|hulu|pcok|pmtp|it|ma|crav|stan|cr|adn|internal|hybrid|v\d)$",
        RegexOptions.Compiled);
    static readonly Regex LanguageTagRx = new(
        @"^(?i)(multi|truefrench|french|vff|vfq|vfi|vf2|vf|vof|vostfr|vost|subfrench|english|german|spanish|italian|japanese|dual)$", RegexOptions.Compiled);
    static readonly Regex EditionRx = new(
        @"(?i)\b(extended(?:[ .](?:cut|edition))?|director'?s[ .]cut|unrated|uncut|theatrical(?:[ .]cut)?|imax|remastered|ultimate(?:[ .]edition)?|" +
        @"criterion|special[ .]edition|final[ .]cut|anniversary(?:[ .]edition)?|open[ .]matte)\b", RegexOptions.Compiled);
    static readonly Regex GroupRx = new(@"-\[?([A-Za-z0-9]{2,})\]?$", RegexOptions.Compiled);
    /// <summary>« [Groupe] Titre - 12v2 (1080p) [ABCD1234] » ou « [Groupe] Titre - 12-13 [1080p] ».</summary>
    static readonly Regex AnimeRx = new(
        @"^\[(?<group>[^\]]+)\]\s*(?<title>.+?)\s+-\s+(?<abs>\d{1,4})(?:-(?<abs2>\d{1,4}))?(?:v(?<rev>\d))?(?=\s|$|\[|\()", RegexOptions.Compiled);
    static readonly string[] StreamingServices =
        ["AMZN", "NF", "ATVP", "DSNP", "DSNY", "HMAX", "MAX", "HULU", "PCOK", "PMTP", "iT", "MA", "CRAV", "STAN", "CR", "ADN", "FUNi", "SHO", "ROKU", "TVNZ", "iP"];
    static readonly string[] OtherTagNames = ["HDLight", "4KLight", "mHD", "Hybrid", "iNTERNAL", "Remastered"];

    public static ReleaseName Parse(string baseName, string? folderName = null)
    {
        string? Id(Regex rx) => rx.Match(baseName) is { Success: true } m ? m.Groups[1].Value
            : folderName is not null && rx.Match(folderName) is { Success: true } f ? f.Groups[1].Value : null;
        var tmdb = Id(TmdbRx);
        var tvdb = Id(TvdbRx);
        var imdb = Id(ImdbRx);

        // on retire les identifiants « {tmdb-…} », « {edition-…} », « [imdb-…] » avant d'analyser le reste
        var name = Regex.Replace(baseName, @"\{[^}]*\}|\[(?:tmdb|imdb|tvdb)[^\]]*\]", " ").Trim();

        string? hash = null;
        if (Regex.Match(name, @"\s*\[([0-9A-Fa-f]{8})\]$") is { Success: true } h)
        {
            hash = h.Groups[1].Value;
            name = name[..h.Index].TrimEnd();
        }

        var anime = AnimeRx.Match(name);
        string? group = null;
        if (anime.Success) group = anime.Groups["group"].Value.Trim();
        else if (GroupRx.Match(name) is { Success: true } g && !Regex.IsMatch(name, @"(?i)web-dl$|blu-ray$") &&
                 g.Groups[1].Value.ToUpperInvariant() is not ("DL" or "RAY" or "RIP"))
        {
            group = g.Groups[1].Value;
            name = name[..g.Index];
        }

        var analysed = anime.Success ? name[(anime.Index + anime.Length)..] : name;
        var tokens = Regex.Split(analysed, @"[.\s_\[\]()]+").Where(x => x.Length > 0).ToList();

        string title;
        int? year = null, season = null, episode = null, episodeLast = null, absolute = null, absoluteLast = null, revision = null;
        string? episodeTitle = null;
        var techStart = 0;

        if (anime.Success)
        {
            title = anime.Groups["title"].Value.Trim();
            if (Regex.Match(title, @"\s*\(((?:19|20)\d{2})\)$") is { Success: true } ty)
            {
                year = int.Parse(ty.Groups[1].Value, CultureInfo.InvariantCulture);
                title = title[..ty.Index];
            }
            absolute = int.Parse(anime.Groups["abs"].Value, CultureInfo.InvariantCulture);
            if (anime.Groups["abs2"].Success) absoluteLast = int.Parse(anime.Groups["abs2"].Value, CultureInfo.InvariantCulture);
            if (anime.Groups["rev"].Success) revision = int.Parse(anime.Groups["rev"].Value, CultureInfo.InvariantCulture);
        }
        else
        {
            int yearIdx = -1, epIdx = -1;
            for (var k = 0; k < tokens.Count; k++)
            {
                if (k > 0 && yearIdx < 0 && YearRx.IsMatch(tokens[k])) yearIdx = k; // l'année ne peut pas être le 1er mot (film « 2012 »)
                if (epIdx < 0 && EpisodeRx.IsMatch(tokens[k])) epIdx = k;
            }
            var titleEnd = new[] { yearIdx, epIdx }.Where(x => x > 0).DefaultIfEmpty(-1).Min();
            if (titleEnd < 0)
            {
                titleEnd = tokens.FindIndex(Math.Min(1, tokens.Count), x => TechTagRx.IsMatch(x));
                if (titleEnd < 0) titleEnd = tokens.Count;
            }
            title = string.Join(' ', tokens.Take(titleEnd)).Trim(' ', '-');
            if (yearIdx > 0 && (epIdx < 0 || yearIdx < epIdx || yearIdx == titleEnd))
                year = int.Parse(YearRx.Match(tokens[yearIdx]).Groups[1].Value, CultureInfo.InvariantCulture);
            techStart = Math.Max(0, titleEnd);

            if (epIdx >= 0 && EpisodeRx.Match(tokens[epIdx]) is { Success: true } e)
            {
                season = int.Parse(e.Groups[1].Value, CultureInfo.InvariantCulture);
                episode = int.Parse(e.Groups[2].Value, CultureInfo.InvariantCulture);
                if (Regex.Matches(e.Groups[3].Value, @"\d+") is { Count: > 0 } more)
                    episodeLast = int.Parse(more[^1].Value, CultureInfo.InvariantCulture);

                // format Sonarr : « S01E03 - 003 - Titre de l'épisode [WEBDL-1080p]… »
                var sonarr = Regex.Match(name, @"(?i)S\d{1,3}E\d{1,4}(?:-?E\d{1,4})*\s+-\s+(?:(?<abs>\d{2,4})(?:-(?<abs2>\d{2,4}))?\s+-\s+)?(?<title>[^\[\{]+?)\s*(?=\[|\{|$)");
                if (sonarr.Success)
                {
                    if (sonarr.Groups["abs"].Success) absolute = int.Parse(sonarr.Groups["abs"].Value, CultureInfo.InvariantCulture);
                    if (sonarr.Groups["abs2"].Success) absoluteLast = int.Parse(sonarr.Groups["abs2"].Value, CultureInfo.InvariantCulture);
                    var t = sonarr.Groups["title"].Value.Trim();
                    if (!Regex.IsMatch(t, @"^\d+$")) episodeTitle = t;
                }
                else
                {
                    // format scène : « Serie.S01E03.Titre.De.L.Episode.1080p.WEB-DL… »
                    var words = tokens.Skip(epIdx + 1).TakeWhile(x => !TechTagRx.IsMatch(x) && !LanguageTagRx.IsMatch(x) && !YearRx.IsMatch(x))
                                      .Where(x => x != "-").ToList();
                    if (words.Count > 0) episodeTitle = string.Join(' ', words);
                }
            }
        }

        var lower = name.ToLowerInvariant();
        var source =
            Regex.IsMatch(lower, @"\bremux\b") ? "Remux" :
            Regex.IsMatch(lower, @"\b(blu-?ray|bdrip|brrip|bd)\b") ? "Bluray" :
            Regex.IsMatch(lower, @"\bweb-?rip\b") ? "WEBRip" :
            Regex.IsMatch(lower, @"\b(web-?dl|web)\b") ? "WEBDL" :
            Regex.IsMatch(lower, @"\bhdtv\b") ? "HDTV" :
            Regex.IsMatch(lower, @"\bdvd(rip)?\b") ? "DVD" : null;

        var all = Regex.Split(name, @"[.\s_\[\]()]+").Where(x => x.Length > 0).ToList();
        var resolution = all.Select(x => ResolutionRx.Match(x)).FirstOrDefault(m => m.Success) is { } rm ? rm.Groups[1].Value + "p" : null;
        if (revision is null && all.Select(x => Regex.Match(x, @"^(?i)v(\d)$")).FirstOrDefault(m => m.Success) is { } rv)
            revision = int.Parse(rv.Groups[1].Value, CultureInfo.InvariantCulture);

        // plateforme : seulement juste avant WEB / WEB-DL / WEBRip (évite « DTS-HD.MA »)
        string? streaming = null;
        for (var k = 0; k + 1 < all.Count && streaming is null; k++)
            if (Regex.IsMatch(all[k + 1], @"^(?i)web(-?dl|-?rip)?(-\d{3,4}p)?$"))
                streaming = StreamingServices.FirstOrDefault(s => s.Equals(all[k], StringComparison.OrdinalIgnoreCase));

        var other = OtherTagNames.Where(t => all.Any(x => x.Equals(t, StringComparison.OrdinalIgnoreCase))).ToList();
        var editions = EditionRx.Matches(name).Select(m => Titleize(m.Value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var languages = tokens.Skip(techStart).Where(x => LanguageTagRx.IsMatch(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return new ReleaseName
        {
            Title = title.Length > 0 ? title : baseName, Year = year, Season = season, Episode = episode, EpisodeLast = episodeLast,
            Absolute = absolute, AbsoluteLast = absoluteLast, EpisodeTitle = episodeTitle, Source = source, Resolution = resolution,
            Proper = all.Any(x => x.Equals("PROPER", StringComparison.OrdinalIgnoreCase)),
            Repack = all.Any(x => x.Equals("REPACK", StringComparison.OrdinalIgnoreCase)),
            Revision = revision, Editions = editions, LanguageTags = languages, Streaming = streaming,
            OtherTags = other.Where(t => !editions.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList(),
            Group = group, Hash = hash, TmdbId = tmdb, TvdbId = tvdb, ImdbId = imdb, AnimeStyle = anime.Success,
        };
    }

    static string Titleize(string s) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.Replace('.', ' ').ToLowerInvariant())
            .Replace("Imax", "IMAX").Replace("Director'S", "Director's");
}
