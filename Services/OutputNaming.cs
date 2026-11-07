using System.Text.RegularExpressions;

namespace AV1Encod.Services;

public static class OutputNaming
{
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
}
