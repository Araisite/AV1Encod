using System.Globalization;
using System.Text.RegularExpressions;

namespace AV1Encod.Services;

/// <summary>
/// Informations tirées d'un nom de release (« Mon.Film.2026.MULTi.VFF.2160p.WEB-DL.DV.H265-GROUPE ») ou d'un nom
/// déjà renommé par Radarr / Sonarr (« Mon Film (2026) {tmdb-123} [Bluray-1080p]… »).
/// </summary>
public sealed record ReleaseName(
    string Title, int? Year, int? Season, int? Episode, string? Source, string? Resolution, bool Proper, bool Repack,
    IReadOnlyList<string> Editions, IReadOnlyList<string> LanguageTags, string? Group, string? TmdbId, string? ImdbId)
{
    public bool IsEpisode => Season is not null && Episode is not null;

    static readonly Regex TmdbRx = new(@"(?i)(?:\{tmdb-|\[tmdb(?:id)?-|tmdbid[-=])(\d+)", RegexOptions.Compiled);
    static readonly Regex ImdbRx = new(@"(?i)\b(tt\d{7,9})\b", RegexOptions.Compiled);
    static readonly Regex YearRx = new(@"^\(?((?:19|20)\d{2})\)?$", RegexOptions.Compiled);
    static readonly Regex EpisodeRx = new(@"^(?i)S(\d{1,2})E(\d{1,3})(?:-?E\d{1,3})*$", RegexOptions.Compiled);
    static readonly Regex ResolutionRx = new(@"^(?i)(2160|1080|720|576|480)[pi]$", RegexOptions.Compiled);
    static readonly Regex TechTagRx = new(
        @"^(?i)(\d{3,4}[pi]|4k|uhd|multi|truefrench|french|vf[fqi2]?|vof|vostfr|vost|subfrench|web|web-?dl|webrip|hdtv|bluray|blu-ray|bdrip|brrip|" +
        @"remux|hdr|hdr10|hdr10plus|dv|dovi|x26[45]|h\.?26[45]|hevc|avc|av1|10bits?|proper|repack|hdlight|4klight|ddp?5\.1|atmos|dts|truehd|aac|eac3|ac3)$",
        RegexOptions.Compiled);
    static readonly Regex LanguageTagRx = new(
        @"^(?i)(multi|truefrench|french|vff|vfq|vfi|vf2|vf|vof|vostfr|vost|subfrench|english|german|spanish|italian|japanese|dual)$", RegexOptions.Compiled);
    static readonly Regex EditionRx = new(
        @"(?i)\b(extended(?:[ .](?:cut|edition))?|director'?s[ .]cut|unrated|uncut|theatrical(?:[ .]cut)?|imax|remastered|ultimate(?:[ .]edition)?|" +
        @"criterion|special[ .]edition|final[ .]cut|anniversary(?:[ .]edition)?|open[ .]matte)\b", RegexOptions.Compiled);
    static readonly Regex GroupRx = new(@"-\[?([A-Za-z0-9]{2,})\]?$", RegexOptions.Compiled);

    public static ReleaseName Parse(string baseName, string? folderName = null)
    {
        var tmdb = TmdbRx.Match(baseName) is { Success: true } t ? t.Groups[1].Value
                 : folderName is not null && TmdbRx.Match(folderName) is { Success: true } tf ? tf.Groups[1].Value : null;
        var imdb = ImdbRx.Match(baseName) is { Success: true } i ? i.Groups[1].Value
                 : folderName is not null && ImdbRx.Match(folderName) is { Success: true } fi ? fi.Groups[1].Value : null;

        // on retire les identifiants « {tmdb-…} », « {edition-…} », « [imdb-…] » avant d'analyser le reste
        var name = Regex.Replace(baseName, @"\{[^}]*\}|\[(?:tmdb|imdb)[^\]]*\]", " ").Trim();

        string? group = null;
        if (GroupRx.Match(name) is { Success: true } g && !Regex.IsMatch(name, @"(?i)web-dl$|blu-ray$") &&
            g.Groups[1].Value.ToUpperInvariant() is not ("DL" or "RAY" or "RIP"))
        {
            group = g.Groups[1].Value;
            name = name[..g.Index];
        }

        var tokens = Regex.Split(name, @"[.\s_\[\]()]+").Where(x => x.Length > 0).ToList();
        int yearIdx = -1, epIdx = -1;
        for (var k = 1; k < tokens.Count; k++) // l'année ne peut pas être le 1er mot (film « 2012 »)
        {
            if (yearIdx < 0 && YearRx.IsMatch(tokens[k])) yearIdx = k;
            if (epIdx < 0 && EpisodeRx.IsMatch(tokens[k])) epIdx = k;
        }
        if (epIdx == 0 || (tokens.Count > 0 && EpisodeRx.IsMatch(tokens[0]))) epIdx = 0;

        var titleEnd = new[] { yearIdx, epIdx }.Where(x => x > 0).DefaultIfEmpty(-1).Min();
        if (titleEnd < 0)
        {
            titleEnd = tokens.FindIndex(1, x => TechTagRx.IsMatch(x));
            if (titleEnd < 0) titleEnd = tokens.Count;
        }
        var title = string.Join(' ', tokens.Take(titleEnd)).Trim(' ', '-');
        int? year = yearIdx > 0 ? int.Parse(YearRx.Match(tokens[yearIdx]).Groups[1].Value, CultureInfo.InvariantCulture) : null;
        int? season = null, episode = null;
        if (epIdx >= 0 && EpisodeRx.Match(tokens[epIdx]) is { Success: true } e)
        {
            season = int.Parse(e.Groups[1].Value, CultureInfo.InvariantCulture);
            episode = int.Parse(e.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        var lower = name.ToLowerInvariant();
        var source =
            Regex.IsMatch(lower, @"\bremux\b") ? "Remux" :
            Regex.IsMatch(lower, @"\b(blu-?ray|bdrip|brrip|bd)\b") ? "Bluray" :
            Regex.IsMatch(lower, @"\bweb-?rip\b") ? "WEBRip" :
            Regex.IsMatch(lower, @"\b(web-?dl|web)\b") ? "WEBDL" :
            Regex.IsMatch(lower, @"\bhdtv\b") ? "HDTV" :
            Regex.IsMatch(lower, @"\bdvd(rip)?\b") ? "DVD" : null;

        var resolution = tokens.Select(x => ResolutionRx.Match(x)).FirstOrDefault(m => m.Success)?.Groups[1].Value + "p";
        if (resolution == "p") resolution = null;

        var editions = EditionRx.Matches(name).Select(m => Titleize(m.Value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var languages = tokens.Skip(Math.Max(0, titleEnd)).Where(x => LanguageTagRx.IsMatch(x))
                              .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return new ReleaseName(title.Length > 0 ? title : baseName, year, season, episode, source, resolution,
            tokens.Any(x => x.Equals("PROPER", StringComparison.OrdinalIgnoreCase)),
            tokens.Any(x => x.Equals("REPACK", StringComparison.OrdinalIgnoreCase)),
            editions, languages, group, tmdb, imdb);
    }

    static string Titleize(string s) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.Replace('.', ' ').ToLowerInvariant())
            .Replace("Imax", "IMAX").Replace("Director'S", "Director's");
}
