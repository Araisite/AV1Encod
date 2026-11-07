using System.ComponentModel;

namespace AV1Encod.Services;

/// <summary>Une piste audio ou de sous-titres de la source, que l'utilisateur peut garder ou non.</summary>
public sealed class TrackInfo : INotifyPropertyChanged
{
    public required bool IsAudio { get; init; }
    /// <summary>Index 1-based parmi les pistes du même type (numérotation de HandBrake -a / -s).</summary>
    public int Index { get; init; }
    public string Codec { get; init; } = "";
    public string Language { get; init; } = "";
    public string Title { get; init; } = "";
    public string Channels { get; init; } = "";
    public bool IsDefault { get; init; }
    public bool IsForced { get; init; }
    /// <summary>Audio : copiable tel quel. Sous-titres : format importable par HandBrake.</summary>
    public bool Supported { get; init; }
    public string Description { get; init; } = "";
    public string Profile { get; init; } = "";
    public double BitrateKbps { get; init; }

    /// <summary>Sert à retrouver « la même piste » dans les autres épisodes.</summary>
    public string Signature => $"{Language}|{Codec}|{Title}|{Channels}|{IsForced}";

    public string Label => $"#{Index}   {Description}" +
        (Supported ? "" : IsAudio ? "   ⚠ sera réencodée" : "   ⚠ format non pris en charge");

    bool _selected;
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Codes langue ISO 639-2 (mkv) → badge court et nom lisible.</summary>
public static class Lang
{
    static readonly Dictionary<string, (string Badge, string Name)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fre"] = ("FR", "Français"), ["fra"] = ("FR", "Français"), ["eng"] = ("EN", "Anglais"),
        ["spa"] = ("ES", "Espagnol"), ["ger"] = ("DE", "Allemand"), ["deu"] = ("DE", "Allemand"),
        ["ita"] = ("IT", "Italien"), ["por"] = ("PT", "Portugais"), ["jpn"] = ("JA", "Japonais"),
        ["kor"] = ("KO", "Coréen"), ["chi"] = ("ZH", "Chinois"), ["zho"] = ("ZH", "Chinois"),
        ["rus"] = ("RU", "Russe"), ["ara"] = ("AR", "Arabe"), ["dut"] = ("NL", "Néerlandais"),
        ["nld"] = ("NL", "Néerlandais"), ["pol"] = ("PL", "Polonais"), ["swe"] = ("SV", "Suédois"),
        ["nor"] = ("NO", "Norvégien"), ["dan"] = ("DA", "Danois"), ["fin"] = ("FI", "Finnois"),
        ["tur"] = ("TR", "Turc"), ["hin"] = ("HI", "Hindi"), ["heb"] = ("HE", "Hébreu"),
        ["gre"] = ("EL", "Grec"), ["ell"] = ("EL", "Grec"), ["cze"] = ("CS", "Tchèque"), ["ces"] = ("CS", "Tchèque"),
        ["hun"] = ("HU", "Hongrois"), ["rum"] = ("RO", "Roumain"), ["ron"] = ("RO", "Roumain"),
        ["tha"] = ("TH", "Thaï"), ["vie"] = ("VI", "Vietnamien"), ["ukr"] = ("UK", "Ukrainien"),
        ["und"] = ("?", "Langue indéterminée"),
    };

    public static string Badge(string code) =>
        string.IsNullOrEmpty(code) ? "?" : Known.TryGetValue(code, out var k) ? k.Badge : code.ToUpperInvariant();

    public static string Name(string code) =>
        string.IsNullOrEmpty(code) ? "Langue inconnue" : Known.TryGetValue(code, out var k) ? k.Name : code;
}

/// <summary>Choix d'une piste, réutilisable d'un fichier à l'autre.</summary>
public sealed record TrackPick(string Signature, int Index, bool Selected);

public static class TrackSelection
{
    public static List<TrackPick> Capture(IEnumerable<TrackInfo> tracks) =>
        tracks.Select(t => new TrackPick(t.Signature, t.Index, t.Selected)).ToList();

    /// <summary>
    /// Reporte une sélection : par signature (langue, codec, titre…) si au moins une piste correspond,
    /// sinon par position.
    /// </summary>
    /// <returns>true si la correspondance s'est faite par signature</returns>
    public static bool Apply(IReadOnlyList<TrackInfo> target, IReadOnlyList<TrackPick> picks)
    {
        var bySignature = target.Any(t => picks.Any(p => p.Signature == t.Signature));
        foreach (var t in target)
            t.Selected = bySignature
                ? picks.Any(p => p.Selected && p.Signature == t.Signature)
                : picks.Any(p => p.Selected && p.Index == t.Index);
        return bySignature;
    }

    /// <summary>Position (1-based) dans la liste gardée du sous-titre marqué « par défaut », 0 si aucun ou incertain.</summary>
    public static int DefaultSubPosition(IReadOnlyList<TrackInfo> kept)
    {
        // une piste non prise en charge serait sautée par HandBrake et décalerait l'index
        if (kept.Any(t => !t.Supported)) return 0;
        for (var i = 0; i < kept.Count; i++)
            if (kept[i].IsDefault) return i + 1;
        return 0;
    }
}
