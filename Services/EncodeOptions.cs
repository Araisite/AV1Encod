using System.Text;

namespace AV1Encod.Services;

public sealed class EncodeOptions
{
    public string Source { get; init; } = "";
    public string Destination { get; init; } = "";
    public EncoderInfo? Encoder { get; init; }
    public string? Preset { get; init; }
    public int Quality { get; init; }
    /// <summary>0 = aucun (0:0:0:0), 1 = auto, 2 = auto conservateur</summary>
    public int CropMode { get; init; }
    public bool AllAudio { get; init; }
    public bool AllSubs { get; init; }
    public bool KeepDefaultSub { get; init; }
    public bool Chapters { get; init; }
    public string ExtraArgs { get; init; } = "";
    /// <summary>Position (1-based) du sous-titre par défaut dans la liste gardée, 0 si aucun / inconnu.</summary>
    public int DefaultSubIndex { get; init; }
    /// <summary>Pistes audio gardées si <see cref="AllAudio"/> est faux (index 1-based) ; null = 1re piste (source pas encore analysée).</summary>
    public IReadOnlyList<int>? AudioTracks { get; init; }
    /// <summary>Sous-titres gardés si <see cref="AllSubs"/> est faux ; null ou vide = aucun.</summary>
    public IReadOnlyList<int>? SubtitleTracks { get; init; }

    public List<string> BuildArgs()
    {
        var a = new List<string> { "-i", Source, "-o", Destination, "-f", "av_mkv" };
        if (Encoder is not null) a.AddRange(["-e", Encoder.Name]);
        if (!string.IsNullOrEmpty(Preset)) a.AddRange(["--encoder-preset", Preset]);
        a.AddRange(["-q", Quality.ToString(Fmt.Inv)]);
        switch (CropMode)
        {
            case 0: a.AddRange(["--crop", "0:0:0:0"]); break;
            case 1: a.AddRange(["--crop-mode", "auto"]); break;
            case 2: a.AddRange(["--crop-mode", "conservative"]); break;
        }
        if (AllAudio) a.AddRange(["--all-audio", "-E", "copy"]);
        else if (AudioTracks is null) a.AddRange(["-a", "1", "-E", "copy"]);
        else if (AudioTracks.Count == 0) a.AddRange(["-a", "none"]);
        else a.AddRange(["-a", string.Join(',', AudioTracks), "-E", string.Join(',', AudioTracks.Select(_ => "copy"))]);
        // sans ça HandBrake nomme les pistes sans titre « Stereo » / « Surround », même une 5.1 copiée
        if (AudioTracks is not { Count: 0 }) a.AddRange(["--automatic-naming-behaviour", "off"]);

        if (AllSubs) a.Add("--all-subtitles");
        else if (SubtitleTracks is { Count: > 0 }) a.AddRange(["-s", string.Join(',', SubtitleTracks)]);
        if ((AllSubs || SubtitleTracks is { Count: > 0 }) && KeepDefaultSub && DefaultSubIndex > 0)
            a.Add($"--subtitle-default={DefaultSubIndex}");
        a.Add(Chapters ? "-m" : "--no-markers");
        a.AddRange(SplitArgs(ExtraArgs));
        return a;
    }

    public static string Display(IEnumerable<string> args) =>
        string.Join(' ', args.Select(x => x.Length == 0 || x.Any(char.IsWhiteSpace) ? $"\"{x}\"" : x));

    /// <summary>Découpe une ligne d'arguments en respectant les guillemets.</summary>
    public static IEnumerable<string> SplitArgs(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) yield break;
        var sb = new StringBuilder();
        bool inQuotes = false, any = false;
        foreach (var c in s)
        {
            if (c == '"') { inQuotes = !inQuotes; any = true; }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (any) { yield return sb.ToString(); sb.Clear(); any = false; }
            }
            else { sb.Append(c); any = true; }
        }
        if (any) yield return sb.ToString();
    }
}
