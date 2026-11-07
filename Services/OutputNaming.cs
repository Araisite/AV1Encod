using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AV1Encod.Services;

/// <summary>Ce qu'il faut savoir d'un fichier pour lui donner un nom de sortie.</summary>
public sealed class NamingContext
{
    public required string SourcePath { get; init; }
    /// <summary>Résultat de l'analyse ; null avant l'analyse (le nom est alors provisoire).</summary>
    public SourceInfo? Source { get; init; }
    public required EncoderInfo Encoder { get; init; }
    public bool AllAudio { get; init; }
    public bool AllSubs { get; init; }
    /// <summary>Infos TVmaze (titre d'épisode, année, numéro absolu…) si la recherche en ligne est activée.</summary>
    public SeriesMeta? Online { get; init; }
}

public enum NamingKind { Movie, Series, Anime }

public static class OutputNaming
{
    // Formats recommandés (guides TRaSH / Servarr), adaptés par l'utilisateur
    public const string DefaultMovieTemplate =
        "{Movie CleanTitle} {(Release Year)} {tmdb-{TmdbId}} - {edition-{Edition Tags}} {[MediaInfo AudioLanguages]}{[Custom Formats]}" +
        "{[Quality Full]}{[MediaInfo AudioCodec}{ MediaInfo AudioChannels]}{[MediaInfo VideoDynamicRangeType]}{[MediaInfo VideoCodec]}{-Release Group}";

    public const string DefaultSeriesTemplate =
        "{Series TitleYear} - S{season:00}E{episode:00} - {Episode CleanTitle:90} {[Custom Formats]}{[Quality Full]}" +
        "{[Mediainfo AudioCodec}{ Mediainfo AudioChannels]}{[MediaInfo VideoDynamicRangeType]}{[Mediainfo VideoCodec]}{-Release Group}";

    public const string DefaultAnimeTemplate =
        "{Series TitleYear} - S{season:00}E{episode:00} - {absolute:000} - {Episode CleanTitle:90} {[Custom Formats]}{[Quality Full]}" +
        "{[Mediainfo AudioCodec}{ Mediainfo AudioChannels]}{MediaInfo AudioLanguages}{[MediaInfo VideoDynamicRangeType]}" +
        "[{Mediainfo VideoCodec }{MediaInfo VideoBitDepth}bit]{-Release Group}";

    /// <summary>Jetons reconnus (comme dans Radarr / Sonarr), avec une description pour l'aide.</summary>
    public static readonly (string Token, string Help)[] Tokens =
    [
        ("{Movie Title}", "Titre du film"),
        ("{Movie CleanTitle}", "Titre sans apostrophes ni caractères spéciaux"),
        ("{Movie TitleThe}", "« Film, The »"),
        ("{Release Year}", "Année du film"),
        ("{TmdbId}", "Identifiant TMDb (nom du fichier ou du dossier Radarr) — {tmdb-{TmdbId}}"),
        ("{ImdbId}", "Identifiant IMDb (tt…) — {imdb-{ImdbId}}"),
        ("{Edition Tags}", "Extended, Director's Cut, IMAX… — {edition-{Edition Tags}}"),
        ("{Series Title}", "Titre de la série"),
        ("{Series TitleYear}", "« Série (2024) »"),
        ("{Series CleanTitle}", "Titre de la série nettoyé"),
        ("{Series CleanTitleYear}", "« Série 2024 »"),
        ("{Series TitleWithoutYear}", "Titre sans l'année"),
        ("{Series Year}", "Année de la série"),
        ("{season:00}", "Saison : 01"),
        ("{episode:00}", "Épisode : 03 (multi-épisodes : 01-E03)"),
        ("{absolute:000}", "Numéro absolu (animes) : 012"),
        ("{Episode Title}", "Titre de l'épisode"),
        ("{Episode CleanTitle:90}", "Titre de l'épisode nettoyé, coupé à 90 caractères"),
        ("{TvdbId}", "Identifiant TVDB (recherche TVmaze)"),
        ("{TvMazeId}", "Identifiant TVmaze"),
        ("{Quality Full}", "WEBDL-2160p, Bluray-1080p Proper, HDTV-720p v2…"),
        ("{Quality Title}", "Idem sans Proper / Repack / v2"),
        ("{Custom Formats}", "Plateforme (AMZN, NF…), langues (MULTi, VFF…), HDLight… lus dans le nom source"),
        ("{MediaInfo AudioLanguages}", "[FR+EN] (rien si seulement anglais) ; filtre : {MediaInfo AudioLanguages:FR+EN}"),
        ("{MediaInfo AudioLanguagesAll}", "[FR+EN], anglais compris"),
        ("{MediaInfo SubtitleLanguages}", "[FR+EN] des sous-titres gardés"),
        ("{MediaInfo AudioCodec}", "EAC3 Atmos, TrueHD Atmos, DTS-HD MA…"),
        ("{MediaInfo AudioChannels}", "5.1, 7.1…"),
        ("{MediaInfo VideoCodec}", "Codec du fichier encodé : AV1, x265…"),
        ("{MediaInfo VideoBitDepth}", "10 ou 8"),
        ("{MediaInfo VideoDynamicRange}", "HDR (vide en SDR)"),
        ("{MediaInfo VideoDynamicRangeType}", "DV HDR10, HDR10, HDR10Plus, HLG… (ce qui est conservé)"),
        ("{Release Group}", "Groupe de la release source — {-Release Group}"),
        ("{Release Hash}", "Somme de contrôle des releases d'anime [ABCD1234]"),
        ("{Original Title}", "Nom du fichier source, sans extension"),
    ];

    // ---------------------------------------------------------------- nom court

    static readonly Regex Year = new(@"^\(?(19|20)\d{2}\)?$", RegexOptions.Compiled);
    static readonly Regex Episode = new(@"^(?i)S\d{1,2}E\d{1,3}$", RegexOptions.Compiled);
    /// <summary>Premiers tags « techniques » d'un nom de release : tout ce qui suit est retiré.</summary>
    static readonly Regex ReleaseTag = new(
        @"^(?i)(\d{3,4}p|4k|uhd|multi|truefrench|french|vff|vfq|vfi|vf2|vostfr|vo|web|web-?dl|webrip|hdtv|bluray|blu-ray|bdrip|brrip|remux|hdr|hdr10|dv|dovi|x26[45]|h\.?26[45]|hevc|avc|av1|10bit|proper|repack)$",
        RegexOptions.Compiled);

    /// <summary>
    /// « Mon.Film.2026.MULTi.2160p.WEB-DL.H265-GROUPE » → « Mon.Film.2026.AV1 ».
    /// On garde le titre jusqu'à l'année (ou SxxEyy) incluse, sinon jusqu'au premier tag technique.
    /// </summary>
    public static string ShortName(string baseName, string tag)
    {
        var sep = new[] { '.', ' ', '_' }.OrderByDescending(c => baseName.Count(x => x == c)).First();
        var tokens = baseName.Split(['.', ' ', '_'], StringSplitOptions.RemoveEmptyEntries);

        var keep = tokens.Length;
        // l'année ne peut pas être le premier mot (film « 2012 »)
        var marker = Array.FindIndex(tokens, 1, t => Year.IsMatch(t) || Episode.IsMatch(t));
        if (marker > 0) keep = marker + 1;
        else
        {
            var techTag = Array.FindIndex(tokens, 1, t => ReleaseTag.IsMatch(t));
            if (techTag > 0) keep = techTag;
        }

        var title = string.Join(sep, tokens.Take(keep));
        return title.Length > 0 ? $"{title}{sep}{tag}" : $"{baseName}{sep}{tag}";
    }

    // ---------------------------------------------------------------- format Radarr / Sonarr

    static readonly Regex TokenNameRx = new(@"[A-Za-z][A-Za-z ._]*[A-Za-z](?::[-+A-Za-z0-9,]+)?", RegexOptions.Compiled);

    public static NamingKind KindOf(NamingContext ctx)
    {
        var rel = Parse(ctx.SourcePath);
        if (ctx.Online?.IsAnime == true || (rel.AnimeStyle && rel.Absolute is not null)) return NamingKind.Anime;
        return rel.IsEpisode ? NamingKind.Series : NamingKind.Movie;
    }

    /// <summary>Applique le modèle film, série ou anime selon le fichier.</summary>
    public static string FromTemplates(string movie, string series, string anime, NamingContext ctx) =>
        Render(KindOf(ctx) switch { NamingKind.Anime => anime, NamingKind.Series => series, _ => movie }, ctx, out _);

    /// <summary>Remplit le modèle. Un bloc « {…} » dont le jeton est vide disparaît avec son texte autour, comme dans Radarr / Sonarr.</summary>
    public static string Render(string template, NamingContext ctx, out List<string> unknownTokens)
    {
        unknownTokens = [];
        var rel = Parse(ctx.SourcePath);
        var sb = new StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c != '{') { sb.Append(c); i++; continue; }

            // bloc de premier niveau, éventuellement avec un jeton imbriqué : {tmdb-{TmdbId}}
            var depth = 0;
            var j = i;
            for (; j < template.Length; j++)
            {
                if (template[j] == '{') depth++;
                else if (template[j] == '}' && --depth == 0) break;
            }
            if (j >= template.Length) { sb.Append(template, i, template.Length - i); break; }
            var content = template.Substring(i + 1, j - i - 1);
            sb.Append(RenderBlock(content, rel, ctx, unknownTokens));
            i = j + 1;
        }
        return Clean(sb.ToString());
    }

    static string RenderBlock(string content, ReleaseName rel, NamingContext ctx, List<string> unknown)
    {
        var inner = Regex.Match(content, @"\{([^{}]+)\}");
        if (inner.Success)
        {
            // {tmdb-{TmdbId}} → « {tmdb-12345} » (accolades conservées, c'est la syntaxe Radarr) ; caché si vide
            var v = Value(inner.Groups[1].Value, rel, ctx, unknown);
            return v.Length == 0 ? "" : "{" + content[..inner.Index] + v + content[(inner.Index + inner.Length)..] + "}";
        }
        var m = TokenNameRx.Match(content);
        if (!m.Success) return "";
        var value = Value(m.Value, rel, ctx, unknown);
        return value.Length == 0 ? "" : content[..m.Index] + value + content[(m.Index + m.Length)..];
    }

    static string Value(string token, ReleaseName rel, NamingContext ctx, List<string> unknown)
    {
        var colon = token.IndexOf(':');
        var fmt = colon >= 0 ? token[(colon + 1)..] : null;
        var name = colon >= 0 ? token[..colon] : token;
        // séparateur choisi dans le jeton : {Series.CleanTitleYear} → mots séparés par des points
        var sep = name.Contains('.') ? "." : name.Contains('_') ? "_" : " ";
        var key = Regex.Replace(name, "[ ._]", "").ToLowerInvariant();
        var s = ctx.Source;
        var enc = ctx.Encoder;
        var online = ctx.Online;
        var ep = online?.Find(rel.Season, rel.Episode, rel.Absolute);

        string Words(string text) => sep == " " ? text : Regex.Replace(text.Trim(), @"\s+", sep);
        string Pad(int? n) => n is null ? "" : n.Value.ToString(new string('0', Math.Max(1, fmt?.Length ?? 1)), CultureInfo.InvariantCulture);
        string Range(int? first, int? last, string prefix) => first is null ? "" : last is null || last == first ? Pad(first) : $"{Pad(first)}-{prefix}{Pad(last)}";

        var seriesTitle = online?.Title ?? rel.Title;
        var seriesYear = online?.Year ?? rel.Year;
        string WithYear(string t, bool parens) =>
            seriesYear is null || Regex.IsMatch(t, $@"\(?{seriesYear}\)?$") ? t : parens ? $"{t} ({seriesYear})" : $"{t} {seriesYear}";
        var episodeTitle = ep is { Title.Length: > 0 } ? ep.Title : rel.EpisodeTitle ?? "";

        switch (key)
        {
            // films
            case "movietitle" or "title": return Words(rel.Title);
            case "moviecleantitle" or "cleantitle": return Words(CleanTitle(rel.Title));
            case "movietitlethe": return Words(TitleThe(rel.Title));
            case "moviecleantitlethe": return Words(TitleThe(CleanTitle(rel.Title)));
            case "movietitlefirstcharacter" or "seriestitlefirstcharacter": return FirstCharacter(key.StartsWith("series") ? seriesTitle : rel.Title);
            case "releaseyear" or "year" or "movieyear": return rel.Year?.ToString(CultureInfo.InvariantCulture) ?? "";
            case "tmdbid": return rel.TmdbId ?? "";
            case "imdbid": return rel.ImdbId ?? online?.ImdbId ?? "";
            case "tvdbid": return rel.TvdbId ?? online?.TvdbId ?? "";
            case "tvmazeid": return online?.TvMazeId.ToString(CultureInfo.InvariantCulture) ?? "";
            case "editiontags" or "edition": return string.Join(' ', rel.Editions);

            // séries
            case "seriestitle": return Words(seriesTitle);
            case "seriescleantitle": return Words(CleanTitle(seriesTitle));
            case "seriestitleyear": return Words(WithYear(seriesTitle, true));
            case "seriescleantitleyear": return Words(WithYear(CleanTitle(seriesTitle), false));
            case "seriestitlewithoutyear": return Words(StripYear(seriesTitle));
            case "seriescleantitlewithoutyear": return Words(CleanTitle(StripYear(seriesTitle)));
            case "seriestitlethe": return Words(TitleThe(seriesTitle));
            case "seriescleantitlethe": return Words(TitleThe(CleanTitle(seriesTitle)));
            case "seriestitletheyear": return Words(WithYear(TitleThe(seriesTitle), true));
            case "seriescleantitletheyear": return Words(WithYear(TitleThe(CleanTitle(seriesTitle)), false));
            case "seriesyear": return seriesYear?.ToString(CultureInfo.InvariantCulture) ?? "";
            case "season": return Pad(rel.Season ?? (ep is { SeasonIsYear: false } ? ep.Season : null));
            case "episode": return Range(rel.Episode ?? (ep is { SeasonIsYear: false } ? ep.Number : null), rel.EpisodeLast, "E");
            case "absolute": return Range(rel.Absolute ?? ep?.Absolute, rel.AbsoluteLast, "");
            case "episodetitle": return Words(Truncate(episodeTitle, fmt));
            case "episodecleantitle": return Words(Truncate(CleanTitle(episodeTitle), fmt));

            // qualité et formats
            case "qualityfull":
            case "qualitytitle":
                var q = Quality(rel, s);
                if (key == "qualityfull" && q.Length > 0)
                {
                    if (rel.Proper || rel.Repack) q += rel.Repack ? " Repack" : " Proper";
                    if (rel.Revision is > 1) q += $" v{rel.Revision}";
                }
                return q;
            case "customformats": return Filter(CustomFormats(rel), fmt, ' ');
            case "customformat": return fmt is not null && CustomFormats(rel).FirstOrDefault(f => f.Equals(fmt, StringComparison.OrdinalIgnoreCase)) is { } cf ? cf : "";

            // MediaInfo
            case "mediainfoaudiolanguages": return Languages(KeptAudio(ctx), fmt, hideEnglishOnly: true);
            case "mediainfoaudiolanguagesall": return Languages(KeptAudio(ctx), fmt, hideEnglishOnly: false);
            case "mediainfosubtitlelanguages": return Languages(KeptSubs(ctx), fmt, hideEnglishOnly: true);
            case "mediainfosubtitlelanguagesall": return Languages(KeptSubs(ctx), fmt, hideEnglishOnly: false);
            case "mediainfoaudiocodec" or "mediainfoaudio": return KeptAudio(ctx).FirstOrDefault() is { } a ? AudioCodec(a) : "";
            case "mediainfoaudiochannels": return KeptAudio(ctx).FirstOrDefault() is { } a2 ? Channels(a2.Channels) : "";
            case "mediainfovideocodec" or "mediainfovideo": return VideoCodec(enc);
            case "mediainfovideobitdepth": return s is null ? "" : enc.Bits.ToString(CultureInfo.InvariantCulture);
            case "mediainfovideodynamicrange": return DynamicRangeType(s, enc).Length > 0 ? "HDR" : "";
            case "mediainfovideodynamicrangetype": return DynamicRangeType(s, enc);
            case "mediainfosimple": return Join(' ', VideoCodec(enc), KeptAudio(ctx).FirstOrDefault() is { } a3 ? AudioCodec(a3) : "");
            case "mediainfofull":
                return Join(' ', VideoCodec(enc), KeptAudio(ctx).FirstOrDefault() is { } a4 ? AudioCodec(a4) : "", Languages(KeptAudio(ctx), fmt, true));

            case "releasegroup": return rel.Group ?? "";
            case "releasehash": return rel.Hash ?? "";
            case "originaltitle" or "originalfilename": return Path.GetFileNameWithoutExtension(ctx.SourcePath);
            default:
                unknown.Add(token);
                return "";
        }
    }

    static string Join(char sep, params string[] parts) => string.Join(sep, parts.Where(p => p.Length > 0));

    static ReleaseName Parse(string sourcePath) =>
        ReleaseName.Parse(Path.GetFileNameWithoutExtension(sourcePath), Path.GetFileName(Path.GetDirectoryName(sourcePath)));

    /// <summary>Comme Radarr / Sonarr : « & » → « and », apostrophes, deux-points, virgules… retirés.</summary>
    static string CleanTitle(string t)
    {
        t = t.Replace("&", "and").Replace("/", " ").Replace("\\", " ");
        t = Regex.Replace(t, @"(?<=\s)(,|<|>|/|\\|;|:|'|""|\||`|~|!|\?|@|\$|%|\^|\*|-|_|=){1}(?=\s)|('|:|\?|,)(?=(?:(?:s|m)\s)|\s|$)|(\(|\)|\[|\]|\{|\})", "");
        t = t.Replace("'", "").Replace("’", "");
        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    static string TitleThe(string t) =>
        Regex.Match(t, @"^(The|An|A)\s+(.+?)((?:\s*\([^)]+\))*)$", RegexOptions.IgnoreCase) is { Success: true } m
            ? $"{m.Groups[2].Value}, {m.Groups[1].Value}{m.Groups[3].Value}" : t;

    static string StripYear(string t) => Regex.Replace(t, @"\s*\((?:19|20)\d{2}\)\s*$", "");

    static string FirstCharacter(string t) =>
        TitleThe(CleanTitle(t)).FirstOrDefault(char.IsLetterOrDigit) is var c && c != default ? char.ToUpperInvariant(c).ToString() : "";

    /// <summary>{Episode CleanTitle:90} : coupé à 90 caractères (points de suspension inclus).</summary>
    static string Truncate(string text, string? fmt)
    {
        if (!int.TryParse(fmt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max) || max <= 3 || text.Length <= max) return text;
        return text[..(max - 3)].TrimEnd() + "...";
    }

    static string Quality(ReleaseName rel, SourceInfo? s)
    {
        var res = s is { Width: > 0 } ? ResolutionLabel(s.Width, s.Height) : rel.Resolution ?? "";
        if (rel.Source is null) return res;
        return rel.Source == "DVD" ? "DVD" : res.Length > 0 ? $"{rel.Source}-{res}" : rel.Source;
    }

    public static string ResolutionLabel(int width, int height) =>
        width >= 3200 || height >= 2000 ? "2160p" : width >= 1800 || height >= 1000 ? "1080p" :
        width >= 1200 || height >= 700 ? "720p" : height >= 560 ? "576p" : "480p";

    /// <summary>
    /// Nous ne connaissons pas les « custom formats » de Radarr : on reprend ce qu'ils détectent d'habitude dans le nom
    /// (plateforme de streaming, langues, HDLight…), dans cet ordre.
    /// </summary>
    static List<string> CustomFormats(ReleaseName rel)
    {
        var list = new List<string>();
        if (rel.Streaming is not null) list.Add(rel.Streaming);
        list.AddRange(rel.LanguageTags);
        list.AddRange(rel.OtherTags);
        return list;
    }

    /// <summary>{Custom Formats:A,B} garde A et B ; {Custom Formats:-A,B} les retire.</summary>
    static string Filter(List<string> values, string? fmt, char sep)
    {
        if (!string.IsNullOrEmpty(fmt))
        {
            var exclude = fmt.StartsWith('-');
            var names = fmt.TrimStart('-').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            values = values.Where(v => names.Contains(v, StringComparer.OrdinalIgnoreCase) != exclude).ToList();
        }
        return string.Join(sep, values);
    }

    static IEnumerable<TrackInfo> KeptAudio(NamingContext c) =>
        c.Source is null ? [] : c.Source.AudioTracks.Where(t => c.AllAudio || t.Selected);

    static IEnumerable<TrackInfo> KeptSubs(NamingContext c) =>
        c.Source is null ? [] : c.Source.SubtitleTracks.Where(t => c.AllSubs || t.Selected);

    /// <summary>
    /// « [FR+EN] » comme Sonarr / Radarr (les crochets font partie du jeton). Sans « All », rien si la seule langue est
    /// l'anglais. Filtre : « :FR+EN » ne garde que ces langues, « :-EN » retire l'anglais.
    /// </summary>
    static string Languages(IEnumerable<TrackInfo> tracks, string? fmt, bool hideEnglishOnly)
    {
        var langs = tracks.Select(t => Lang.Badge(t.Language)).Where(b => b != "?").Distinct().ToList();
        if (!string.IsNullOrEmpty(fmt))
        {
            var exclude = fmt.StartsWith('-');
            var wanted = fmt.TrimStart('-').TrimEnd('+').Split('+', StringSplitOptions.RemoveEmptyEntries).Select(x => x.ToUpperInvariant()).ToList();
            langs = langs.Where(l => wanted.Contains(l) != exclude).ToList();
        }
        if (langs.Count == 0 || (hideEnglishOnly && langs is ["EN"])) return "";
        return "[" + string.Join('+', langs) + "]";
    }

    static string AudioCodec(TrackInfo a)
    {
        var atmos = a.Profile.Contains("Atmos", StringComparison.OrdinalIgnoreCase);
        var c = a.Codec switch
        {
            "eac3" => "EAC3", "ac3" => "AC3", "truehd" => "TrueHD", "aac" => "AAC", "flac" => "FLAC", "opus" => "Opus",
            "mp3" => "MP3", "vorbis" => "Vorbis", "alac" => "ALAC",
            "dts" => a.Profile.Contains("MA", StringComparison.Ordinal) ? "DTS-HD MA"
                   : a.Profile.Contains(":X", StringComparison.OrdinalIgnoreCase) ? "DTS-X"
                   : a.Profile.Contains("HRA", StringComparison.Ordinal) ? "DTS-HD HRA" : "DTS",
            _ when a.Codec.StartsWith("pcm", StringComparison.Ordinal) => "PCM",
            _ => a.Codec.ToUpperInvariant(),
        };
        return atmos ? c + " Atmos" : c;
    }

    static string Channels(string layout)
    {
        var l = layout.ToLowerInvariant();
        if (Regex.Match(l, @"(\d)\.(\d)") is { Success: true } m) return $"{m.Groups[1].Value}.{m.Groups[2].Value}";
        return l switch
        {
            "stereo" or "2" => "2.0", "mono" or "1" => "1.0", "6" => "5.1", "8" => "7.1",
            _ => int.TryParse(l, out var n) ? $"{n}.0" : "",
        };
    }

    static string VideoCodec(EncoderInfo e) => e.Tag switch
    {
        "AV1" => "AV1",
        "H265" => e.IsHardware ? "h265" : "x265",
        "H264" => e.IsHardware ? "h264" : "x264",
        _ => e.Tag,
    };

    /// <summary>Plage dynamique du fichier <b>encodé</b> : ce que l'encodeur conserve de la source (valeurs Radarr / Sonarr).</summary>
    static string DynamicRangeType(SourceInfo? s, EncoderInfo e)
    {
        if (s is null || e.Bits < 10) return "";
        var dynamicKept = e.KeepsDynamicHdr == true;
        var dv = s.HasDolbyVision && dynamicKept;
        var hdr10Plus = s.HasHdr10Plus && dynamicKept;
        var pq = s.Transfer == "smpte2084";
        var hlg = s.Transfer == "arib-std-b67";
        if (dv)
        {
            if (s.DvProfile == 5) return "DV";
            if (hdr10Plus) return "DV HDR10Plus";
            return pq ? "DV HDR10" : hlg ? "DV HLG" : "DV SDR";
        }
        return hdr10Plus ? "HDR10Plus" : pq ? "HDR10" : hlg ? "HLG" : "";
    }

    /// <summary>
    /// Nettoie le résultat : deux-points « intelligents » (« : » → « - »), caractères interdits retirés, crochets doublés
    /// (« {[MediaInfo AudioLanguages]} » donne « [FR+EN] »), espaces doublés et « - » orphelins.
    /// </summary>
    static string Clean(string name)
    {
        var n = Regex.Replace(name, @":\s", " - ").Replace(":", "-");
        n = Regex.Replace(n, @"[\\/<>?*|""]", "");
        foreach (var ch in Path.GetInvalidFileNameChars()) n = n.Replace(ch.ToString(), "");
        n = n.Replace("[[", "[").Replace("]]", "]");
        n = Regex.Replace(n, @"\[\s*bit\]", "");          // « [{VideoCodec }{VideoBitDepth}bit] » sans infos
        n = Regex.Replace(n, @"\[([^\[\]]*?[^\d\s\[])\s*bit\]", "[$1]");   // profondeur inconnue (avant l'analyse) : « [AV1 bit] » → « [AV1] »
        n = Regex.Replace(n, @"\s+-\s+SE(?=\s+-\s+|\s|$)", "");            // anime sans saison ni recherche en ligne : pas de « SE » vide
        n = Regex.Replace(n, @"\(\s*\)|\[\s*\]", "");
        n = Regex.Replace(n, @"\s{2,}", " ");
        n = Regex.Replace(n, @"(\s+-)+\s+(?=\[|$)", " ");  // « Titre (2026) - [FR] » quand l'édition / le titre d'épisode manque
        n = Regex.Replace(n, @"\s+-(\s+-)+\s+", " - ");      // « S01E03 -  - 003 » → « S01E03 - 003 »
        return n.Trim(' ', '.', '-');
    }

    /// <summary>Exemple fictif pour l'aperçu des paramètres quand la file ne contient pas ce genre de fichier.</summary>
    public static NamingContext Sample(EncoderInfo encoder, NamingKind kind)
    {
        TrackInfo Audio(int i, string lang, string title, string profile, string codec = "eac3") => new()
        {
            IsAudio = true, Index = i, Codec = codec, Language = lang, Title = title, Channels = "5.1(side)",
            Profile = profile, Supported = true, IsDefault = i == 1, Selected = true,
        };
        var path = kind switch
        {
            NamingKind.Series => @"C:\Séries\Ma Série\Saison 01\Ma.Serie.2024.S01E03.Le.Grand.Depart.MULTi.1080p.AMZN.WEB-DL.H264-GROUPE.mkv",
            NamingKind.Anime => @"C:\Animes\Mon Anime\[Groupe] Mon Anime (2023) - 12v2 (1080p) [ABCD1234].mkv",
            _ => @"C:\Films\Mon Film (2026) {tmdb-123456}\Mon.Film.2026.MULTi.VFF.2160p.WEB-DL.DV.HDR.H265-GROUPE.mkv",
        };
        var src = new SourceInfo
        {
            Path = path,
            Width = kind == NamingKind.Movie ? 3840 : 1920, Height = kind == NamingKind.Movie ? 2160 : 1080,
            Transfer = kind == NamingKind.Movie ? "smpte2084" : "bt709", IsHdr = kind == NamingKind.Movie,
            HasDolbyVision = kind == NamingKind.Movie, DvProfile = kind == NamingKind.Movie ? 8 : 0,
            AudioTracks = kind == NamingKind.Anime
                ? [Audio(1, "jpn", "Japonais", "", "aac"), Audio(2, "fre", "Français", "", "aac")]
                : [Audio(1, "fre", "Français", "Dolby Digital Plus + Dolby Atmos"), Audio(2, "eng", "English", "")],
        };
        return new NamingContext { SourcePath = src.Path, Source = src, Encoder = encoder, AllAudio = true, AllSubs = true };
    }
}
