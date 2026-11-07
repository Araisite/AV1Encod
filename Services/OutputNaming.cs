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
}

public static class OutputNaming
{
    public const string DefaultMovieTemplate =
        "{Movie CleanTitle} {(Release Year)} {tmdb-{TmdbId}} - {edition-{Edition Tags}} {[MediaInfo AudioLanguages]}{[Custom Formats]}" +
        "{[Quality Full]}{[MediaInfo AudioCodec}{ MediaInfo AudioChannels]}{[MediaInfo VideoDynamicRangeType]}{[MediaInfo VideoCodec]}{-Release Group}";

    public const string DefaultSeriesTemplate =
        "{Series Title} - S{season:00}E{episode:00} {[MediaInfo AudioLanguages]}{[Custom Formats]}" +
        "{[Quality Full]}{[MediaInfo AudioCodec}{ MediaInfo AudioChannels]}{[MediaInfo VideoDynamicRangeType]}{[MediaInfo VideoCodec]}{-Release Group}";

    /// <summary>Jetons reconnus (comme dans Radarr / Sonarr), avec une description pour l'aide.</summary>
    public static readonly (string Token, string Help)[] Tokens =
    [
        ("{Movie Title}", "Titre du film"),
        ("{Movie CleanTitle}", "Titre du film sans caractères spéciaux"),
        ("{Release Year}", "Année"),
        ("{TmdbId}", "Identifiant TMDb (lu dans le nom du fichier ou du dossier Radarr)"),
        ("{ImdbId}", "Identifiant IMDb (tt…)"),
        ("{Edition Tags}", "Extended, Director's Cut, IMAX…"),
        ("{Series Title}", "Titre de la série"),
        ("{season:00}", "Numéro de saison (01)"),
        ("{episode:00}", "Numéro d'épisode (03)"),
        ("{Quality Full}", "Source et résolution : WEBDL-2160p, Bluray-1080p, Remux-2160p…"),
        ("{Quality Title}", "Idem sans Proper / Repack"),
        ("{Custom Formats}", "Balises de langue de la source : MULTi, VFF, TRUEFRENCH, VOSTFR…"),
        ("{MediaInfo AudioLanguages}", "Langues audio gardées : FR+EN"),
        ("{MediaInfo SubtitleLanguages}", "Langues des sous-titres gardés"),
        ("{MediaInfo AudioCodec}", "Codec de la 1re piste audio : EAC3 Atmos, TrueHD, DTS-HD MA…"),
        ("{MediaInfo AudioChannels}", "Canaux de la 1re piste audio : 5.1, 7.1…"),
        ("{MediaInfo VideoCodec}", "Codec du fichier encodé : AV1, x265…"),
        ("{MediaInfo VideoBitDepth}", "10 ou 8"),
        ("{MediaInfo VideoDynamicRange}", "HDR (vide en SDR)"),
        ("{MediaInfo VideoDynamicRangeType}", "DV HDR10, HDR10, HDR10Plus, HLG… (ce qui est conservé)"),
        ("{Release Group}", "Groupe de la release source"),
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

    static readonly Regex TokenNameRx = new(@"[A-Za-z][A-Za-z ._]*[A-Za-z](?::0+)?", RegexOptions.Compiled);
    static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    /// <summary>Applique le modèle film ou série selon que la source est un épisode (SxxEyy).</summary>
    public static string FromTemplates(string movieTemplate, string seriesTemplate, NamingContext ctx)
    {
        var rel = Parse(ctx.SourcePath);
        return Render(rel.IsEpisode ? seriesTemplate : movieTemplate, ctx, out _);
    }

    /// <summary>Remplit le modèle. Un bloc « {…} » dont le jeton est vide disparaît, comme dans Radarr.</summary>
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
            // {tmdb-{TmdbId}} → « {tmdb-12345} » (accolades conservées, c'est la syntaxe Radarr)
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
        var fmt = token.Contains(':') ? token[(token.IndexOf(':') + 1)..] : null;
        var name = token.Contains(':') ? token[..token.IndexOf(':')] : token;
        // séparateur demandé par l'utilisateur : {Movie.CleanTitle} → mots séparés par des points
        var sep = name.Contains('.') ? "." : name.Contains('_') ? "_" : " ";
        var key = Regex.Replace(name, "[ ._]", "").ToLowerInvariant();
        var s = ctx.Source;
        var enc = ctx.Encoder;

        string Words(string text) => sep == " " ? text : Regex.Replace(text.Trim(), @"\s+", sep);
        string Num(int? n) => n is null ? "" : n.Value.ToString(new string('0', Math.Max(1, fmt?.Length ?? 1)), CultureInfo.InvariantCulture);

        switch (key)
        {
            case "movietitle" or "seriestitle" or "title": return Words(rel.Title);
            case "moviecleantitle" or "seriescleantitle" or "cleantitle": return Words(CleanTitle(rel.Title));
            case "releaseyear" or "year" or "movieyear" or "seriesyear": return rel.Year?.ToString(CultureInfo.InvariantCulture) ?? "";
            case "tmdbid": return rel.TmdbId ?? "";
            case "imdbid": return rel.ImdbId ?? "";
            case "editiontags" or "edition": return string.Join(' ', rel.Editions);
            case "season": return Num(rel.Season);
            case "episode": return Num(rel.Episode);
            case "qualityfull":
            case "qualitytitle":
                var q = Quality(rel, s);
                if (key == "qualityfull" && q.Length > 0 && (rel.Proper || rel.Repack)) q += rel.Proper ? " Proper" : " Repack";
                return q;
            case "customformats": return string.Join(' ', rel.LanguageTags);
            case "mediainfoaudiolanguages": return Languages(KeptAudio(ctx));
            case "mediainfosubtitlelanguages": return Languages(KeptSubs(ctx));
            case "mediainfoaudiocodec": return KeptAudio(ctx).FirstOrDefault() is { } a ? AudioCodec(a) : "";
            case "mediainfoaudiochannels": return KeptAudio(ctx).FirstOrDefault() is { } a2 ? Channels(a2.Channels) : "";
            case "mediainfovideocodec": return VideoCodec(enc);
            case "mediainfovideobitdepth": return s is null ? "" : enc.Bits.ToString(CultureInfo.InvariantCulture);
            case "mediainfovideodynamicrange": return DynamicRangeType(s, enc).Length > 0 ? "HDR" : "";
            case "mediainfovideodynamicrangetype": return DynamicRangeType(s, enc);
            case "releasegroup": return rel.Group ?? "";
            case "originaltitle" or "originalfilename": return Path.GetFileNameWithoutExtension(ctx.SourcePath);
            default:
                unknown.Add(token);
                return "";
        }
    }

    static ReleaseName Parse(string sourcePath) =>
        ReleaseName.Parse(Path.GetFileNameWithoutExtension(sourcePath), Path.GetFileName(Path.GetDirectoryName(sourcePath)));

    static string CleanTitle(string t) => Regex.Replace(Regex.Replace(t, @"[:;,!?'""&*/\\|<>]", ""), @"\s+", " ").Trim();

    static string Quality(ReleaseName rel, SourceInfo? s)
    {
        var res = s is { Width: > 0 } ? ResolutionLabel(s.Width, s.Height) : rel.Resolution ?? "";
        if (rel.Source is null) return res;
        return rel.Source == "DVD" ? "DVD" : res.Length > 0 ? $"{rel.Source}-{res}" : rel.Source;
    }

    public static string ResolutionLabel(int width, int height) =>
        width >= 3200 || height >= 2000 ? "2160p" : width >= 1800 || height >= 1000 ? "1080p" :
        width >= 1200 || height >= 700 ? "720p" : height >= 560 ? "576p" : "480p";

    static IEnumerable<TrackInfo> KeptAudio(NamingContext c) =>
        c.Source is null ? [] : c.Source.AudioTracks.Where(t => c.AllAudio || t.Selected);

    static IEnumerable<TrackInfo> KeptSubs(NamingContext c) =>
        c.Source is null ? [] : c.Source.SubtitleTracks.Where(t => c.AllSubs || t.Selected);

    static string Languages(IEnumerable<TrackInfo> tracks) =>
        string.Join('+', tracks.Select(t => Lang.Badge(t.Language)).Where(b => b != "?").Distinct());

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

    /// <summary>Plage dynamique du fichier <b>encodé</b> : ce que l'encodeur conserve de la source.</summary>
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
            return pq ? "DV HDR10" : hlg ? "DV HLG" : "DV";
        }
        return hdr10Plus ? "HDR10Plus" : pq ? "HDR10" : hlg ? "HLG" : "";
    }

    /// <summary>Nettoie le résultat : caractères interdits, espaces doublés, « - » orphelins.</summary>
    static string Clean(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(ch == ':' ? " -" : Array.IndexOf(Invalid, ch) >= 0 ? ' ' : ch);
        var n = Regex.Replace(sb.ToString(), @"\s{2,}", " ");
        n = Regex.Replace(n, @"\s+-\s+(?=\[|$)", " ");   // « Titre (2026) - [FR] » quand l'édition est vide
        n = Regex.Replace(n, @"\s+-\s*-\s+", " - ");
        n = Regex.Replace(n, @"\(\s*\)|\[\s*\]", "");
        return n.Trim(' ', '.', '-');
    }

    /// <summary>Exemple fictif (film 4K DV / épisode) pour l'aperçu des paramètres quand la file est vide.</summary>
    public static NamingContext Sample(EncoderInfo encoder, bool series)
    {
        TrackInfo Audio(int i, string lang, string title, string profile) => new()
        {
            IsAudio = true, Index = i, Codec = "eac3", Language = lang, Title = title, Channels = "5.1(side)",
            Profile = profile, Supported = true, IsDefault = i == 1, Selected = true,
        };
        var src = new SourceInfo
        {
            Path = series ? @"C:\Séries\Ma Série\Saison 01\Ma.Serie.S01E03.MULTi.1080p.WEB.H264-GROUPE.mkv"
                          : @"C:\Films\Mon Film (2026) {tmdb-123456}\Mon.Film.2026.MULTi.VFF.2160p.WEB-DL.DV.HDR.H265-GROUPE.mkv",
            Width = series ? 1920 : 3840, Height = series ? 1080 : 2160,
            Transfer = series ? "bt709" : "smpte2084", IsHdr = !series, HasDolbyVision = !series, DvProfile = series ? 0 : 8,
            AudioTracks = [Audio(1, "fre", "Français", "Dolby Digital Plus + Dolby Atmos"), Audio(2, "eng", "English", "")],
        };
        return new NamingContext { SourcePath = src.Path, Source = src, Encoder = encoder, AllAudio = true, AllSubs = true };
    }
}
