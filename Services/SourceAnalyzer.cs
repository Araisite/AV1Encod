using System.Text.Json;
using System.Text.RegularExpressions;

namespace AV1Encod.Services;

public sealed class SourceInfo
{
    public required string Path { get; init; }
    public long Size { get; init; }
    public double Duration { get; init; }
    public string Transfer { get; init; } = "";
    public bool IsHdr { get; init; }
    public bool HasDolbyVision { get; init; }
    public bool HasHdr10Plus { get; init; }
    public int AudioCount { get; init; }
    public int SubtitleCount { get; init; }
    public int SupportedSubtitleCount { get; init; }
    public int ChapterCount { get; init; }
    public int DefaultSubIndex { get; init; }
    /// <summary>Résumé court pour la file d'attente : « HEVC · 2160p · DV 8.1 · 2 audio · 3 ST ».</summary>
    public string Summary { get; init; } = "";
    /// <summary>Pistes de la source ; <see cref="TrackInfo.Selected"/> = gardée quand on ne garde pas tout.</summary>
    public IReadOnlyList<TrackInfo> AudioTracks { get; init; } = [];
    public IReadOnlyList<TrackInfo> SubtitleTracks { get; init; } = [];
}

public sealed class AnalysisRequest
{
    public required string SourcePath { get; init; }
    public required string DestPath { get; init; }
    public EncoderInfo? Encoder { get; init; }
    public HandBrakeInfo? HandBrake { get; init; }
    public string? FfprobePath { get; init; }
    public string? FfmpegPath { get; init; }
    public bool AllAudio { get; init; }
    public bool AllSubs { get; init; }
    public bool KeepDefaultSub { get; init; }
    public bool Chapters { get; init; }
    public bool DeepCheck { get; init; }
    /// <summary>false = une destination déjà présente est signalée comme « déjà encodée » (le fichier sera ignoré).</summary>
    public bool Overwrite { get; init; }
    /// <summary>Appelé avec les pistes trouvées pour y appliquer la sélection mémorisée (audio, sous-titres).</summary>
    public Action<IReadOnlyList<TrackInfo>, IReadOnlyList<TrackInfo>>? SelectTracks { get; init; }
}

public sealed record AnalysisResult(int Errors, int Warnings, SourceInfo? Source, bool DestinationExists);

public static class SourceAnalyzer
{
    static readonly string[] VideoExtensions = [".mkv", ".mp4", ".m4v", ".mov", ".m2ts", ".mts", ".ts", ".avi", ".webm", ".wmv", ".mpg", ".mpeg", ".vob"];
    /// <summary>Codecs audio que HandBrake sait copier tels quels (« -E copy »).</summary>
    static readonly string[] CopyableAudio = ["aac", "ac3", "eac3", "truehd", "dts", "mp2", "mp3", "opus", "vorbis", "flac", "alac"];
    static readonly string[] SupportedSubs = ["subrip", "ass", "ssa", "hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle", "mov_text", "text"];
    /// <summary>Messages bénins juste après un seek dans un flux HEVC/H.264 à GOP ouvert, ou propres au muxer null.</summary>
    static readonly Regex BenignDecodeErrors = new(
        "Could not find ref with POC|Error constructing the frame RPS|Skipping invalid undecodable NALU|missing picture in access unit|co located POCs unavailable|Last message repeated|non monotonically increasing dts",
        RegexOptions.Compiled);

    /// <summary>Résultats de la vérification approfondie, par fichier (chemin + taille + date).</summary>
    static readonly Dictionary<string, List<(Level, string)>> DeepCache = new();

    public static async Task<AnalysisResult> AnalyzeAsync(AnalysisRequest req, Reporter rep, Action<string> status, CancellationToken ct)
    {
        SourceInfo? info = null;
        var destExists = false;
        AnalysisResult Finish() => new(rep.Errors, rep.Warnings, info, destExists);

        var enc = req.Encoder;
        rep.Head("Outils");
        if (req.HandBrake is { } hb) rep.Ok($"HandBrakeCLI {hb.Version}"); else rep.Error("HandBrakeCLI introuvable");
        if (req.FfprobePath is not null) rep.Ok("ffprobe disponible"); else rep.Error("ffprobe introuvable (winget install Gyan.FFmpeg)");
        if (enc is not null) rep.Ok($"Encodeur : {enc.Title}  [{enc.Name}]"); else rep.Error("Aucun encodeur sélectionné");

        // ------------------------------------------------------------------ fichier
        rep.Head("Fichier source");
        if (string.IsNullOrWhiteSpace(req.SourcePath)) { rep.Error("Aucun fichier source sélectionné"); return Finish(); }
        if (!File.Exists(req.SourcePath)) { rep.Error($"Fichier introuvable : {req.SourcePath}"); return Finish(); }
        var fi = new FileInfo(req.SourcePath);
        rep.Info(fi.Name);
        rep.Info($"Taille : {Fmt.Size(fi.Length)}   ·   modifié le {fi.LastWriteTime:dd/MM/yyyy HH:mm}");
        if (fi.Length == 0) { rep.Error("Fichier vide (0 octet)"); return Finish(); }
        if (!VideoExtensions.Contains(fi.Extension.ToLowerInvariant())) rep.Warn($"Extension inhabituelle pour une vidéo : {fi.Extension}");
        await CheckAccessAsync(fi, rep);
        if (req.FfprobePath is not { } ffprobe) return Finish();

        status("Lecture des métadonnées (ffprobe)…");
        var res = await ProcessRunner.RunAsync(ffprobe,
            ["-v", "error", "-show_format", "-show_streams", "-show_chapters", "-of", "json", fi.FullName], TimeSpan.FromMinutes(3), ct);
        if (res.TimedOut) { rep.Error("ffprobe ne répond pas (fichier inaccessible ?)"); return Finish(); }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(res.StdOut); }
        catch
        {
            rep.Error("ffprobe ne parvient pas à lire le fichier (corrompu ou format non supporté). " + Fmt.Lines(res.StdErr).FirstOrDefault());
            return Finish();
        }
        using var _ = doc;
        var root = doc.RootElement;
        var streams = root.A("streams").ToList();
        if (res.ExitCode != 0 || streams.Count == 0)
        {
            rep.Error("ffprobe ne trouve aucune piste lisible. " + Fmt.Lines(res.StdErr).FirstOrDefault());
            return Finish();
        }
        var format = root.P("format") ?? default;
        rep.Ok($"Conteneur lisible : {format.S("format_long_name")}");
        var probeErr = Fmt.Lines(res.StdErr);
        if (probeErr.Length > 0) rep.Warn($"ffprobe signale {probeErr.Length} anomalie(s) : {probeErr[0]}");

        var vids = streams.Where(s => s.S("codec_type") == "video" && s.Disp("attached_pic") != 1).ToList();
        var auds = streams.Where(s => s.S("codec_type") == "audio").ToList();
        var subs = streams.Where(s => s.S("codec_type") == "subtitle").ToList();
        var dur = format.D("duration");
        if (dur <= 0 && vids.Count > 0) dur = Fmt.Hms(vids[0].Tag("DURATION") ?? vids[0].Tag("DURATION-eng"));
        if (dur > 0) rep.Ok($"Durée : {Fmt.Dur(dur)}"); else rep.Warn("Durée inconnue");
        if (dur is > 0 and < 300) rep.Warn("Durée très courte (< 5 min) : sample ou fichier incomplet ?");
        // un chapitre qui commence après la fin de la vidéo (fichier découpé) ne sera pas conservé
        var chapters = root.A("chapters").Count(c => dur <= 0 || c.D("start_time") < dur - 1);
        if (Regex.IsMatch(fi.Name, @"(?i)\bsample\b")) rep.Warn("Le nom du fichier contient « sample »");
        var totalKbps = dur > 0 ? fi.Length * 8 / dur / 1000 : 0;
        if (totalKbps > 0) rep.Info($"Débit global : {totalKbps / 1000:N1} Mb/s");

        // ------------------------------------------------------------------ vidéo
        rep.Head("Vidéo");
        if (vids.Count == 0) { rep.Error("Aucune piste vidéo"); return Finish(); }
        if (vids.Count > 1) rep.Warn($"{vids.Count} pistes vidéo : seule la première sera encodée");
        var v = vids[0];
        var fps = Fmt.Rate(v.S("avg_frame_rate"));
        if (fps <= 0) fps = Fmt.Rate(v.S("r_frame_rate"));
        var pixFmt = v.S("pix_fmt") ?? "";
        var bits = Regex.IsMatch(pixFmt, "p12(le|be)?$|^p012") ? 12 : Regex.IsMatch(pixFmt, "p10(le|be)?$|^p010") ? 10 : 8;
        if (v.I("bits_per_raw_sample") > 8) bits = v.I("bits_per_raw_sample");
        var codec = (v.S("codec_name") ?? "?").ToUpperInvariant() + (v.S("profile") is { } prof ? $" ({prof})" : "");
        int width = v.I("width"), height = v.I("height");
        rep.Info($"{codec}   ·   {width}×{height}   ·   {fps:N3} i/s   ·   {pixFmt} ({bits}-bit)");
        if (v.Bitrate() is > 0 and var vbr) rep.Info($"Débit vidéo : {vbr / 1e6:N1} Mb/s");
        var rfr = Fmt.Rate(v.S("r_frame_rate"));
        if (fps > 0 && rfr > 0 && Math.Abs(rfr - fps) / fps > 0.01)
            rep.Info("Cadence possiblement variable (VFR) : HandBrake conservera la cadence de la source");
        if (v.S("field_order") is { } fo && fo is not ("progressive" or "unknown"))
            rep.Warn($"Vidéo entrelacée ({fo}) : aucun désentrelacement appliqué (ajoutez --comb-detect --decomb)");
        if (width % 2 != 0 || height % 2 != 0) rep.Warn($"Dimensions impaires ({width}×{height}) : HandBrake les ajustera");
        if (height >= 2000 && totalKbps is > 0 and < 4000) rep.Warn("Débit très faible pour de la 4K : fichier suspect ou déjà très compressé");
        else if (height is >= 1000 and < 2000 && totalKbps is > 0 and < 1500) rep.Warn("Débit très faible pour du 1080p : fichier suspect ou déjà très compressé");

        var srcTag = v.S("codec_name") switch { "av1" => "AV1", "hevc" => "H265", "h264" => "H264", "vp9" => "VP9", _ => "" };
        if (enc is not null)
        {
            if (srcTag.Length > 0 && srcTag == enc.Tag)
                rep.Warn($"La source est déjà en {enc.Family} : réencoder fera perdre de la qualité pour un gain probablement faible");
            else rep.Ok($"{codec} → {enc.Family} {enc.Bits}-bit");
        }

        // HDR / Dolby Vision
        var trc = v.S("color_transfer") ?? "";
        var isHdr = trc is "smpte2084" or "arib-std-b67";
        var hdrKind = trc switch { "smpte2084" => "HDR10 (PQ)", "arib-std-b67" => "HLG", _ => "SDR" };
        rep.Info($"Couleurs : primaires {v.S("color_primaries")}, transfert {trc}, matrice {v.S("color_space")} → {hdrKind}");

        var frameSideData = await FirstFrameSideDataAsync(ffprobe, fi.FullName, ct);
        var mdcv = frameSideData.FirstOrDefault(s => s.Type.Contains("Mastering display"));
        var cll = frameSideData.FirstOrDefault(s => s.Type.Contains("Content light"));
        var hdr10Plus = frameSideData.Any(s => s.Type.Contains("2094-40") || s.Type.Contains("HDR10+"));
        var dovi = v.A("side_data_list").Cast<JsonElement?>()
            .FirstOrDefault(s => s!.Value.S("side_data_type") is { } t && (t.Contains("DOVI") || t.Contains("Dolby Vision")));

        if (isHdr)
        {
            var t = $"HDR : {hdrKind}";
            if (mdcv.Type is not null) t += $" · mastering {Fmt.Rate(mdcv.Get("max_luminance")):N0} nits";
            if (cll.Type is not null) t += $" · MaxCLL {cll.Get("max_content")} / MaxFALL {cll.Get("max_average")}";
            rep.Ok(t);
        }
        if (hdr10Plus) rep.Ok("HDR10+ (métadonnées dynamiques SMPTE 2094-40) présent");

        var dvProfile = 0;
        var dvCompat = 0;
        if (dovi is { } dv)
        {
            dvProfile = dv.I("dv_profile");
            dvCompat = dv.I("dv_bl_signal_compatibility_id");
            rep.Info($"Dolby Vision profil {dvProfile}.{dvCompat} · niveau {dv.S("dv_level")} · RPU={dv.S("rpu_present_flag")} EL={dv.S("el_present_flag")} BL={dv.S("bl_present_flag")}");
            switch (dvProfile)
            {
                case 5:
                    rep.Warn("DV profil 5 : pas de couche HDR10 compatible. Lecture correcte uniquement sur un appareil Dolby Vision (couleurs vertes/violettes sinon)");
                    break;
                case 7:
                    rep.Warn("DV profil 7 (Blu-ray double couche) : la couche d'amélioration (EL/FEL) sera abandonnée, seul le RPU est conservé");
                    break;
                case 8:
                    var compatName = dvCompat switch { 1 => "HDR10", 2 => "SDR", 4 => "HLG", 6 => "HDR10 (BD)", _ => $"id {dvCompat}" };
                    rep.Ok($"DV profil 8.{dvCompat} : couche de base compatible {compatName}, lisible partout");
                    break;
                default:
                    rep.Warn($"DV profil {dvProfile} : prise en charge incertaine");
                    break;
            }
        }

        if (enc is not null)
        {
            if (bits > 8 && enc.Bits == 8) rep.Warn($"Source {bits}-bit vers un encodeur 8-bit : risque de banding");
            if (isHdr && enc.Bits == 8) rep.Warn("Source HDR vers un encodeur 8-bit : l'HDR sera dégradé, choisissez un encodeur 10-bit");
            if (dovi is not null || hdr10Plus)
            {
                var what = string.Join(" et ", new[] { dovi is not null ? "Dolby Vision" : null, hdr10Plus ? "HDR10+" : null }.OfType<string>());
                if (enc.KeepsDynamicHdr == true)
                {
                    if (req.HandBrake?.AtLeast(1, 9) == true)
                    {
                        var target = "";
                        if (dovi is not null)
                        {
                            var c = dvProfile == 7 ? 1 : dvCompat;
                            if (enc.Tag == "AV1") target = $" (DV profil cible : 10.{c})";
                            else if (dvProfile == 7) target = " (DV profil cible : 8.1)";
                        }
                        rep.Ok($"{what} sera conservé par {enc.Name}{target}");
                    }
                    else rep.Warn($"HandBrake {req.HandBrake?.Version} < 1.9 : {what} ne sera pas conservé");
                }
                else if (enc.KeepsDynamicHdr is null) rep.Warn($"Conservation de {what} non garantie avec {enc.Name} (vérifiée après l'encodage)");
                else if (enc.Bits >= 10) rep.Warn($"{what} sera perdu avec {enc.Name}, seul l'HDR10 de base est conservé (choisissez svt_av1_10bit pour garder le DV)");
                else rep.Warn($"{what} sera perdu (encodeur 8-bit)");
            }
        }

        // ------------------------------------------------------------------ audio
        var audioTracks = auds.Select((s, i) => MakeTrack(s, i + 1, true)).ToList();
        var subTracks = subs.Select((s, i) => MakeTrack(s, i + 1, false)).ToList();
        // Sélection par défaut quand on ne garde pas tout : 1re piste audio, aucun sous-titre.
        // La fenêtre la remplace ensuite par la sélection mémorisée (fichier déjà vu, ou épisode précédent).
        foreach (var t in audioTracks) t.Selected = t.Index == 1;
        req.SelectTracks?.Invoke(audioTracks, subTracks);

        rep.Head($"Audio ({auds.Count} piste(s))");
        if (auds.Count == 0) rep.Warn("Aucune piste audio");
        foreach (var t in audioTracks)
        {
            var d = $"#{t.Index}  {t.Description}";
            if (!req.AllAudio && !t.Selected) rep.Info($"{d}   (non incluse)");
            else if (t.Supported) rep.Ok($"{d}   → copie");
            else rep.Warn($"{d}   → copie impossible, sera réencodée (codec de secours HandBrake)");
        }
        if (!req.AllAudio && auds.Count > 0)
        {
            if (!audioTracks.Any(t => t.Selected)) rep.Warn("Aucune piste audio sélectionnée : le fichier n'aura pas de son (onglet « Pistes »)");
            else rep.Info($"{audioTracks.Count(t => t.Selected)} piste(s) gardée(s) sur {auds.Count} (onglet « Pistes » pour changer)");
        }

        // ------------------------------------------------------------------ sous-titres
        rep.Head($"Sous-titres ({subs.Count} piste(s))");
        var keptSubs = subTracks.Where(t => req.AllSubs || t.Selected).ToList();
        foreach (var t in subTracks)
        {
            var d = $"#{t.Index}  {t.Description}";
            if (!keptSubs.Contains(t)) rep.Info($"{d}   (non inclus)");
            else if (t.Supported) rep.Ok(d);
            else rep.Warn($"{d}   → format peut-être non pris en charge par HandBrake (piste ignorée)");
        }
        if (!req.AllSubs && subs.Count > 0)
            rep.Info(keptSubs.Count == 0 ? "Aucun sous-titre gardé (onglet « Pistes » pour en choisir)"
                                         : $"{keptSubs.Count} sous-titre(s) gardé(s) sur {subs.Count} (onglet « Pistes » pour changer)");
        var supported = keptSubs.Count(t => t.Supported);
        var defSub = TrackSelection.DefaultSubPosition(keptSubs);
        var srcDefault = subTracks.FirstOrDefault(t => t.IsDefault);
        if (req.KeepDefaultSub && keptSubs.Count > 0)
        {
            if (defSub > 0) rep.Ok($"La piste #{keptSubs[defSub - 1].Index} restera le sous-titre par défaut (--subtitle-default={defSub})");
            else if (srcDefault is not null && keptSubs.Contains(srcDefault))
                rep.Warn("Une piste sera ignorée : le drapeau « par défaut » ne sera pas appliqué");
            else if (srcDefault is not null) rep.Info($"Le sous-titre par défaut de la source (#{srcDefault.Index}) n'est pas gardé");
            else rep.Info("Aucun sous-titre marqué « par défaut » dans la source");
        }
        if (!req.KeepDefaultSub) defSub = 0;

        // ------------------------------------------------------------------ chapitres
        rep.Head("Chapitres");
        if (chapters > 0) rep.Add(req.Chapters ? Level.Ok : Level.Info,
            req.Chapters ? $"{chapters} chapitre(s), conservés" : $"{chapters} chapitre(s), non conservés (option décochée)");
        else rep.Info("Aucun chapitre");

        info = new SourceInfo
        {
            Path = fi.FullName, Size = fi.Length, Duration = dur, Transfer = trc, IsHdr = isHdr,
            HasDolbyVision = dovi is not null, HasHdr10Plus = hdr10Plus, AudioCount = auds.Count,
            SubtitleCount = subs.Count, SupportedSubtitleCount = supported, ChapterCount = chapters, DefaultSubIndex = defSub,
            AudioTracks = audioTracks, SubtitleTracks = subTracks,
            Summary = BuildSummary((v.S("codec_name") ?? "?").ToUpperInvariant(), width, height, hdrKind,
                                   dovi is not null ? $"{dvProfile}.{dvCompat}" : null, hdr10Plus, auds.Count, subs.Count),
        };

        // ------------------------------------------------------------------ intégrité
        if (req.DeepCheck)
        {
            rep.Head("Intégrité (décodage de 5 segments répartis sur le film)");
            var key = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
            if (DeepCache.TryGetValue(key, out var cached))
            {
                rep.Info("(résultat mémorisé de la vérification précédente)");
                foreach (var (l, t) in cached) rep.Add(l, t);
            }
            else
            {
                rep.Capture = [];
                try
                {
                    await DeepCheckAsync(req.FfmpegPath, fi.FullName, dur, fps, rep, status, ct);
                    DeepCache[key] = rep.Capture;
                }
                finally { rep.Capture = null; }
            }
        }

        destExists = CheckDestination(req.DestPath, fi, req.Overwrite, rep);
        return Finish();
    }

    // ---------------------------------------------------------------------- helpers

    static TrackInfo MakeTrack(JsonElement s, int index, bool audio) => new()
    {
        IsAudio = audio,
        Index = index,
        Codec = s.S("codec_name") ?? "",
        Language = s.Tag("language") ?? "",
        Title = s.Tag("title") ?? "",
        Channels = s.S("channel_layout") ?? s.S("channels") ?? "",
        IsDefault = s.Disp("default") == 1,
        IsForced = s.Disp("forced") == 1,
        Supported = audio ? CopyableAudio.Contains(s.S("codec_name")) : SupportedSubs.Contains(s.S("codec_name")),
        Description = Describe(s),
        Profile = s.S("profile") is { } p && p is not ("unknown" or "LC") ? p : "",
        BitrateKbps = audio ? s.Bitrate() / 1000 : 0,
    };

    static string BuildSummary(string codec, int width, int height, string hdrKind, string? dv, bool hdr10Plus, int audio, int subs)
    {
        var res = width >= 3200 || height >= 2000 ? "2160p" : width >= 1800 || height >= 1000 ? "1080p"
                : width >= 1200 || height >= 700 ? "720p" : $"{height}p";
        var hdr = dv is not null ? $"DV {dv}" : hdr10Plus ? "HDR10+" : hdrKind.Split(' ')[0];
        if (dv is not null && hdr10Plus) hdr += " · HDR10+";
        return $"{codec} · {res} · {hdr} · {audio} audio · {subs} ST";
    }

    static string Describe(JsonElement s)
    {
        var parts = new List<string>();
        var c = (s.S("codec_name") ?? "?").ToUpperInvariant();
        if (s.S("profile") is { } p && p is not ("unknown" or "LC")) c += $" ({p})";
        parts.Add(c);
        if (s.S("codec_type") == "audio") parts.Add(s.S("channel_layout") ?? $"{s.S("channels")} canaux");
        if (s.Tag("language") is { } lang) parts.Add(lang);
        if (s.Tag("title") is { } title) parts.Add($"« {title} »");
        if (s.S("codec_type") != "subtitle" && s.Bitrate() is > 0 and var br) parts.Add($"{br / 1000:N0} kb/s");
        var flags = new List<string>();
        if (s.Disp("default") == 1) flags.Add("défaut");
        if (s.Disp("forced") == 1) flags.Add("forcé");
        var text = string.Join("  ·  ", parts);
        return flags.Count > 0 ? $"{text}   [{string.Join(", ", flags)}]" : text;
    }

    static async Task CheckAccessAsync(FileInfo fi, Reporter rep)
    {
        var (level, msg) = await Task.Run(() =>
        {
            try
            {
                using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                var n = (int)Math.Min(65536, fi.Length);
                var buf = new byte[n];
                fs.Seek(-n, SeekOrigin.End);
                fs.ReadExactly(buf);
                return buf.Any(b => b != 0)
                    ? (Level.Ok, "Fichier accessible et non verrouillé")
                    : (Level.Warn, "Les 64 derniers Ko du fichier sont vides : téléchargement incomplet ?");
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
            {
                return (Level.Warn, "Fichier ouvert en écriture par un autre programme (téléchargement, seed ou import en cours ?)");
            }
            catch (Exception ex)
            {
                return (Level.Error, $"Lecture impossible : {ex.Message}");
            }
        });
        rep.Add(level, msg);
    }

    public readonly record struct SideData(string Type, Dictionary<string, string?> Fields)
    {
        public string? Get(string k) => Fields.GetValueOrDefault(k);
    }

    /// <summary>Métadonnées attachées à la première image (mastering display, MaxCLL, HDR10+).</summary>
    public static async Task<List<SideData>> FirstFrameSideDataAsync(string ffprobe, string file, CancellationToken ct)
    {
        var list = new List<SideData>();
        try
        {
            var r = await ProcessRunner.RunAsync(ffprobe,
                ["-v", "error", "-select_streams", "v:0", "-read_intervals", "%+#1", "-show_frames", "-of", "json", file], TimeSpan.FromSeconds(90), ct);
            using var doc = JsonDocument.Parse(r.StdOut);
            var frame = doc.RootElement.A("frames").FirstOrDefault();
            foreach (var sd in frame.A("side_data_list"))
            {
                var fields = sd.EnumerateObject().ToDictionary(p => p.Name, p => sd.S(p.Name));
                list.Add(new SideData(sd.S("side_data_type") ?? "", fields));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return list;
    }

    static async Task DeepCheckAsync(string? ffmpeg, string src, double dur, double fps, Reporter rep, Action<string> status, CancellationToken ct)
    {
        if (ffmpeg is null) { rep.Warn("ffmpeg introuvable : vérification du décodage ignorée"); return; }
        if (dur <= 0) { rep.Warn("Durée inconnue : vérification du décodage ignorée"); return; }
        const int seg = 4;
        var points = new[] { 0, 0.25, 0.5, 0.75 }.Select(f => dur * f).Append(Math.Max(0, dur - 8)).ToArray();
        for (var i = 0; i < points.Length; i++)
        {
            var t = points[i];
            status($"Vérification du décodage : segment {i + 1}/{points.Length} ({Fmt.Dur(t)})…");
            var r = await ProcessRunner.RunAsync(ffmpeg,
                ["-hide_banner", "-nostdin", "-v", "error", "-ss", t.ToString("0.000", Fmt.Inv), "-i", src, "-t", seg.ToString(Fmt.Inv),
                 "-map", "0:v:0", "-map", "0:a?", "-f", "null", "-progress", "pipe:1", "-nostats", "-"],
                TimeSpan.FromMinutes(3), ct);
            var frames = Fmt.Lines(r.StdOut).Select(l => Regex.Match(l, @"^frame=(\d+)")).Where(m => m.Success)
                            .Select(m => int.Parse(m.Groups[1].Value)).LastOrDefault();
            var errs = Fmt.Lines(r.StdErr).Where(l => !BenignDecodeErrors.IsMatch(l)).Select(l => l.Trim()).ToList();
            var label = $"Segment à {Fmt.Dur(t)}";
            var expected = Math.Max(1, (int)(seg * fps * 0.5));
            if (r.TimedOut) rep.Error($"{label} : ffmpeg ne répond pas");
            else if (r.ExitCode != 0 || frames == 0)
                rep.Error($"{label} : décodage impossible ({frames} image(s))" + (errs.Count > 0 ? " — " + errs[0] : ""));
            else if (errs.Count > 0) rep.Warn($"{label} : {frames} images, {errs.Count} erreur(s) — {errs[0]}");
            else if (frames < expected) rep.Warn($"{label} : seulement {frames} images décodées");
            else rep.Ok($"{label} : {frames} images décodées sans erreur");
        }
    }

    /// <returns>true si le fichier de destination existe déjà</returns>
    static bool CheckDestination(string dst, FileInfo src, bool overwrite, Reporter rep)
    {
        rep.Head("Destination");
        if (string.IsNullOrWhiteSpace(dst)) { rep.Error("Aucun fichier de destination"); return false; }
        if (!Path.IsPathFullyQualified(dst)) { rep.Error("Le chemin de destination doit être absolu"); return false; }
        string full;
        try { full = Path.GetFullPath(dst); }
        catch { rep.Error($"Chemin de destination invalide : {dst}"); return false; }
        if (!Path.GetExtension(full).Equals(".mkv", StringComparison.OrdinalIgnoreCase)) { rep.Error("La destination doit être un fichier .mkv (format av_mkv)"); return false; }
        if (string.Equals(full, src.FullName, StringComparison.OrdinalIgnoreCase)) { rep.Error("La destination est identique à la source"); return false; }
        var dir = Path.GetDirectoryName(full)!;
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { rep.Error($"Lecteur ou partage inaccessible : {root}"); return false; }
        rep.Info(Path.GetFileName(full));
        if (Directory.Exists(dir))
        {
            rep.Ok($"Dossier : {dir}");
            try
            {
                var probe = Path.Combine(dir, $".av1encod_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                rep.Ok("Écriture autorisée dans le dossier");
            }
            catch { rep.Error($"Impossible d'écrire dans {dir}"); }
        }
        else rep.Info($"Le dossier sera créé : {dir}");
        var exists = File.Exists(full);
        if (exists && !overwrite)
        {
            rep.Info("Le fichier de destination existe déjà : fichier ignoré (cochez « Écraser les fichiers déjà encodés » pour le refaire)");
            return true;
        }
        if (exists) rep.Warn("Le fichier de destination existe déjà et sera écrasé");

        var free = Native.FreeSpace(dir);
        if (free < 0) { rep.Info("Espace libre inconnu"); return exists; }
        var t = $"Espace libre : {Fmt.Size(free)} (source : {Fmt.Size(src.Length)})";
        if (free < src.Length * 0.15) rep.Error($"{t} — insuffisant");
        else if (free < src.Length * 0.6) rep.Warn($"{t} — risque de manquer de place");
        else rep.Ok(t);
        return exists;
    }
}
