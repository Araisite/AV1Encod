using System.Text.Json;

namespace AV1Encod.Services;

/// <summary>Ce que l'on s'attend à retrouver dans le fichier encodé.</summary>
public sealed class EncodeExpectation
{
    public required SourceInfo Source { get; init; }
    public required string Destination { get; init; }
    public required EncoderInfo Encoder { get; init; }
    public int Audio { get; init; }
    public int Subtitles { get; init; }
    public int Chapters { get; init; }
    public bool Hdr { get; init; }
    public bool DolbyVision { get; init; }
    public bool Hdr10Plus { get; init; }
}

public static class OutputVerifier
{
    /// <returns>HDR / Dolby Vision trouvés dans le fichier produit : « DV 10.1 · HDR10 », « HDR10 », « SDR »… (vide si illisible)</returns>
    public static async Task<string> VerifyAsync(EncodeExpectation e, string? ffprobe, string? logPath, Reporter rep)
    {
        rep.Head("Vérification du fichier encodé");
        if (!File.Exists(e.Destination)) { rep.Error("Le fichier de sortie n'existe pas"); return ""; }
        var fo = new FileInfo(e.Destination);
        if (fo.Length == 0) { rep.Error("Le fichier de sortie est vide"); return ""; }
        var ratio = fo.Length / (double)e.Source.Size;
        var t = $"Taille : {Fmt.Size(fo.Length)}  ({ratio:P0} de la source, {Fmt.Size(Math.Max(0, e.Source.Size - fo.Length))} économisés)";
        if (ratio >= 1) rep.Warn($"{t} — plus gros que la source !"); else rep.Ok(t);
        if (ffprobe is null) return "";

        var res = await ProcessRunner.RunAsync(ffprobe,
            ["-v", "error", "-show_format", "-show_streams", "-show_chapters", "-of", "json", fo.FullName], TimeSpan.FromMinutes(2));
        JsonDocument doc;
        try { doc = JsonDocument.Parse(res.StdOut); }
        catch { rep.Error("ffprobe ne parvient pas à lire le fichier de sortie"); return ""; }
        using var _ = doc;
        var root = doc.RootElement;
        var streams = root.A("streams").ToList();
        var video = streams.Where(s => s.S("codec_type") == "video").Cast<JsonElement?>().FirstOrDefault();
        var nAudio = streams.Count(s => s.S("codec_type") == "audio");
        var nSubs = streams.Count(s => s.S("codec_type") == "subtitle");
        var nChap = root.A("chapters").Count();

        var expCodec = e.Encoder.Tag switch { "AV1" => "av1", "H265" => "hevc", "H264" => "h264", "VP9" => "vp9", "VP8" => "vp8", _ => "" };
        if (video is not { } v) { rep.Error("Aucune piste vidéo dans le fichier de sortie"); return ""; }
        if (expCodec.Length > 0 && v.S("codec_name") != expCodec) rep.Warn($"Codec vidéo inattendu : {v.S("codec_name")}");
        else rep.Ok($"Vidéo : {v.S("codec_name")?.ToUpperInvariant()} · {v.S("width")}×{v.S("height")} · {v.S("pix_fmt")}");

        var d1 = (root.P("format") ?? default).D("duration");
        var d0 = e.Source.Duration;
        if (d0 > 0 && d1 > 0)
        {
            if (Math.Abs(d1 - d0) <= Math.Max(2, d0 * 0.005)) rep.Ok($"Durée identique à la source ({Fmt.Dur(d1)})");
            else rep.Error($"Durée différente : {Fmt.Dur(d1)} au lieu de {Fmt.Dur(d0)} (encodage tronqué ?)");
        }

        var trc = v.S("color_transfer");
        var hdr = trc switch { "smpte2084" => "HDR10", "arib-std-b67" => "HLG", _ => "SDR" };
        var dvOut = v.A("side_data_list").Cast<JsonElement?>()
            .FirstOrDefault(s => s!.Value.S("side_data_type") is { } st && (st.Contains("DOVI") || st.Contains("Dolby Vision")));
        if (dvOut is { } dvo) hdr = $"DV {dvo.S("dv_profile")}.{dvo.S("dv_bl_signal_compatibility_id")}" + (hdr == "SDR" ? "" : " · " + hdr);

        if (e.Hdr)
        {
            if (v.S("color_transfer") == e.Source.Transfer) rep.Ok($"HDR conservé (transfert {v.S("color_transfer")})");
            else rep.Warn($"HDR non retrouvé (transfert : {v.S("color_transfer")})");
        }
        if (e.DolbyVision)
        {
            var dv = v.A("side_data_list").Cast<JsonElement?>()
                .FirstOrDefault(s => s!.Value.S("side_data_type") is { } st && (st.Contains("DOVI") || st.Contains("Dolby Vision")));
            if (dv is { } d) rep.Ok($"Dolby Vision conservé (profil {d.S("dv_profile")}.{d.S("dv_bl_signal_compatibility_id")})");
            else rep.Warn("Dolby Vision absent du fichier de sortie");
        }
        if (e.Hdr10Plus)
        {
            var sd = await SourceAnalyzer.FirstFrameSideDataAsync(ffprobe, fo.FullName, CancellationToken.None);
            if (sd.Any(s => s.Type.Contains("2094-40") || s.Type.Contains("HDR10+"))) { rep.Ok("HDR10+ conservé"); hdr += " · HDR10+"; }
            else rep.Warn("HDR10+ non retrouvé dans le fichier de sortie");
        }

        if (nAudio == e.Audio) rep.Ok($"{nAudio} piste(s) audio"); else rep.Warn($"{nAudio} piste(s) audio au lieu de {e.Audio}");
        if (nSubs == e.Subtitles) rep.Ok($"{nSubs} sous-titre(s)"); else rep.Warn($"{nSubs} sous-titre(s) au lieu de {e.Subtitles}");
        if (e.Chapters > 0)
        {
            if (nChap >= e.Chapters) rep.Ok($"{nChap} chapitre(s)"); else rep.Warn($"{nChap} chapitre(s) au lieu de {e.Chapters}");
        }
        rep.Info($"Fichier : {fo.FullName}");
        if (logPath is not null) rep.Info($"Journal : {logPath}");
        return hdr;
    }
}
