using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AV1Encod.Services;

/// <summary>Un encodeur vidéo proposé par HandBrakeCLI, avec des valeurs par défaut raisonnables.</summary>
public sealed class EncoderInfo
{
    public string Name { get; private set; } = "";
    public string Family { get; private set; } = "";
    /// <summary>Étiquette mise dans le nom du fichier de sortie (AV1, H265…).</summary>
    public string Tag { get; private set; } = "REENC";
    public string Impl { get; private set; } = "CPU";
    public bool IsHardware { get; private set; }
    public int Bits { get; private set; } = 8;
    public int DefaultQuality { get; private set; } = 22;
    public int MaxQuality { get; private set; } = 51;
    public string QualityName { get; private set; } = "Qualité";
    public string[] PreferredPresets { get; private set; } = ["medium"];
    /// <summary>true = Dolby Vision / HDR10+ conservés par HandBrake ≥ 1.9, null = non garanti, false = perdus.</summary>
    public bool? KeepsDynamicHdr { get; private set; } = false;
    public bool IsMain { get; private set; }
    int Rank { get; set; } = 9;

    public string Title => IsMain ? $"{Family} {Bits}-bit  ·  {Impl}" : $"{Family}  ·  {Impl}  (intermédiaire)";
    public int Order => Rank * 100 + (Bits >= 10 ? 0 : 10) + (IsHardware ? 1 : 0);
    public override string ToString() => $"{Title}  [{Name}]";

    public static EncoderInfo FromName(string n)
    {
        var l = n.ToLowerInvariant();
        var e = new EncoderInfo { Name = n, Family = n };
        e.Bits = l.Contains("12bit") ? 12 : l.Contains("10bit") ? 10 : 8;

        if (l.Contains("nvenc")) { e.Impl = "NVENC (GPU NVIDIA)"; e.IsHardware = true; e.PreferredPresets = ["slow", "medium"]; }
        else if (l.Contains("qsv")) { e.Impl = "QuickSync (GPU Intel)"; e.IsHardware = true; e.PreferredPresets = ["quality", "balanced", "medium"]; }
        else if (l.Contains("vce") || l.Contains("amf")) { e.Impl = "AMF/VCE (GPU AMD)"; e.IsHardware = true; e.PreferredPresets = ["quality", "balanced", "medium"]; }
        else if (l.StartsWith("mf_")) { e.Impl = "Media Foundation"; e.IsHardware = true; }
        else if (l.Contains("svt")) { e.Impl = "SVT-AV1 (CPU)"; e.PreferredPresets = ["6"]; }
        else if (l.Contains("x265")) { e.Impl = "x265 (CPU)"; e.PreferredPresets = ["slow", "medium"]; }
        else if (l.Contains("x264")) { e.Impl = "x264 (CPU)"; e.PreferredPresets = ["slow", "medium"]; }
        else if (l.StartsWith("vp8") || l.StartsWith("vp9")) { e.Impl = "libvpx (CPU)"; }

        if (l.Contains("av1"))
        {
            e.Family = "AV1"; e.Tag = "AV1"; e.Rank = 1; e.IsMain = true;
            if (e.IsHardware) { e.DefaultQuality = 30; e.MaxQuality = 51; e.QualityName = "CQ"; }
            else { e.DefaultQuality = 28; e.MaxQuality = 63; e.QualityName = "CRF"; }
        }
        else if (Regex.IsMatch(l, "x265|h265|hevc"))
        {
            e.Family = "HEVC"; e.Tag = "H265"; e.Rank = 2; e.IsMain = true;
            (e.DefaultQuality, e.QualityName) = e.IsHardware ? (24, "CQ") : (20, "RF");
        }
        else if (Regex.IsMatch(l, "x264|h264"))
        {
            e.Family = "H.264"; e.Tag = "H264"; e.Rank = 3; e.IsMain = true;
            (e.DefaultQuality, e.QualityName) = e.IsHardware ? (24, "CQ") : (20, "RF");
        }
        else if (l.StartsWith("vp9"))
        {
            e.Family = "VP9"; e.Tag = "VP9"; e.Rank = 4; e.IsMain = true; e.DefaultQuality = 31; e.MaxQuality = 63; e.QualityName = "CQ";
        }
        else
        {
            (e.Family, e.Tag) = l switch
            {
                _ when l.StartsWith("vp8") => ("VP8", "VP8"),
                _ when l.StartsWith("ffv1") => ("FFV1 (sans perte)", "FFV1"),
                _ when l.Contains("prores") => ("ProRes", "PRORES"),
                _ when l.StartsWith("dnxhr") => ("DNxHR", "DNXHR"),
                _ when l.StartsWith("mpeg4") => ("MPEG-4", "MPEG4"),
                _ when l.StartsWith("mpeg2") => ("MPEG-2", "MPEG2"),
                _ when l.StartsWith("theora") => ("Theora", "THEORA"),
                _ => (n, "REENC"),
            };
            if (e.Tag == "VP8") { e.DefaultQuality = 31; e.MaxQuality = 63; }
        }

        // Vérifié avec HandBrake 1.11 : SVT-AV1 / x265 10-12 bit gardent le DV, NVENC ne garde que l'HDR10 de base
        if (l is "svt_av1_10bit" or "x265_10bit" or "x265_12bit") e.KeepsDynamicHdr = true;
        else if (e.Bits >= 10 && e.IsMain && !e.IsHardware) e.KeepsDynamicHdr = null;
        return e;
    }
}

public sealed class HandBrakeInfo
{
    public required string Path { get; init; }
    public string? Version { get; init; }
    public required IReadOnlyList<EncoderInfo> Encoders { get; init; }

    public bool AtLeast(int major, int minor) =>
        System.Version.TryParse(Version, out var v) && v >= new Version(major, minor);
}

public static class HandBrake
{
    static readonly ConcurrentDictionary<string, string[]> PresetCache = new();

    /// <summary>
    /// Liste les encodeurs vidéo de HandBrakeCLI --help. HandBrake n'y affiche les encodeurs
    /// matériels (NVENC, QSV, VCE) que s'ils sont réellement utilisables sur ce poste.
    /// </summary>
    public static async Task<HandBrakeInfo> DetectAsync(string path)
    {
        var helpTask = ProcessRunner.RunAsync(path, ["--help"], TimeSpan.FromSeconds(30));
        var verTask = ProcessRunner.RunAsync(path, ["--version"], TimeSpan.FromSeconds(30));
        var help = await helpTask;
        var ver = await verTask;

        var vm = Regex.Match(ver.StdOut + "\n" + ver.StdErr, @"HandBrake\s+(\d+\.\d+(?:\.\d+)?)");
        var m = Regex.Match(help.StdOut + "\n" + help.StdErr, @"Select video encoder:\s*(.*?)\r?\n\s*--encoder-preset", RegexOptions.Singleline);
        var encoders = m.Groups[1].Value
            .Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(EncoderInfo.FromName)
            .OrderBy(e => e.Order).ThenBy(e => e.Name)
            .ToList();

        return new HandBrakeInfo { Path = path, Version = vm.Success ? vm.Groups[1].Value : null, Encoders = encoders };
    }

    /// <summary>Valeurs acceptées par --encoder-preset (HandBrake les écrit sur stderr).</summary>
    public static async Task<string[]> GetPresetsAsync(string path, string encoder)
    {
        if (PresetCache.TryGetValue(encoder, out var cached)) return cached;
        var r = await ProcessRunner.RunAsync(path, ["--encoder-preset-list", encoder], TimeSpan.FromSeconds(30));
        var list = new List<string>();
        var inList = false;
        foreach (var line in Fmt.Lines(r.StdErr + "\n" + r.StdOut))
        {
            if (line.Contains("Available --encoder-preset values")) { inList = true; continue; }
            if (!inList) continue;
            if (line.Contains("HandBrake has exited") || line.TrimStart().StartsWith('[')) break;
            list.Add(line.Trim());
        }
        var arr = list.ToArray();
        PresetCache[encoder] = arr;
        return arr;
    }
}
