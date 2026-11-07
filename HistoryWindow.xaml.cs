using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AV1Encod.Services;
using Microsoft.Win32;

namespace AV1Encod;

/// <summary>Une ligne du tableau d'historique (textes prêts à afficher + valeurs pour le tri).</summary>
public sealed class HistoryRow(HistoryEntry e)
{
    static SolidColorBrush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
    static readonly Brush Green = Frozen(22, 128, 61), Orange = Frozen(217, 119, 6), Red = Frozen(220, 38, 38), Gray = Frozen(107, 114, 128);

    public HistoryEntry Entry { get; } = e;
    public string OutputName => Path.GetFileName(Entry.OutputPath);
    public string DateText => Entry.Date.ToString("dd/MM/yyyy HH:mm");
    public string EncoderText => Entry.Preset.Length > 0 ? $"{Entry.Encoder} · {Entry.Preset} · q{Entry.Quality}" : $"{Entry.Encoder} · q{Entry.Quality}";
    public string DurationText => Entry.Duration > 0 ? Fmt.Dur(Entry.Duration) : "";
    public string SourceSizeText => Fmt.Size(Entry.SourceSize);
    public string OutputSizeText => Entry.OutputSize > 0 ? Fmt.Size(Entry.OutputSize) : "";
    public double Gain => Entry.OutputSize > 0 && Entry.SourceSize > 0 ? Entry.OutputSize / (double)Entry.SourceSize - 1 : 0;
    public string GainText => Entry.OutputSize > 0 ? $"{Gain:+0%;-0%}" : "";
    public string TimeText => Entry.EncodeSeconds > 0 ? Fmt.Dur(Entry.EncodeSeconds) : "";
    public string FpsText => Entry.AverageFps > 0 ? $"{Entry.AverageFps:N1}" : "";
    public string Tooltip => $"Source : {Entry.SourcePath}\nSortie : {Entry.OutputPath}\n{Entry.AudioTracks} piste(s) audio · {Entry.SubtitleTracks} sous-titre(s)";

    public string ResultText => Entry.Result switch
    {
        "ok" => "✔ Réussi",
        "warning" => $"✔ Réussi (⚠ {Entry.Warnings})",
        "suspect" => "⚠ Sortie suspecte",
        "failed" => "✖ Échec",
        "cancelled" => "Annulé",
        _ => Entry.Result,
    };

    public Brush ResultBrush => Entry.Result switch
    {
        "ok" => Green, "warning" => Orange, "suspect" or "failed" => Red, _ => Gray,
    };
}

public partial class HistoryWindow : Window
{
    List<HistoryEntry> _all = History.Load();
    List<HistoryRow> _visible = [];

    public HistoryWindow()
    {
        InitializeComponent();
        Refresh();
    }

    void Refresh()
    {
        var q = SearchBox.Text.Trim();
        var since = PeriodBox.SelectedIndex switch
        {
            1 => DateTime.Now.AddDays(-7),
            2 => DateTime.Now.AddDays(-30),
            3 => new DateTime(DateTime.Now.Year, 1, 1),
            _ => DateTime.MinValue,
        };
        _visible = _all
            .Where(e => e.Date >= since)
            .Where(e => ResultBox.SelectedIndex switch { 1 => e.Succeeded, 2 => !e.Succeeded, _ => true })
            .Where(e => q.Length == 0 || e.OutputPath.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        e.SourcePath.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Encoder.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Date)
            .Select(e => new HistoryRow(e))
            .ToList();
        Grid.ItemsSource = _visible;
        EmptyText.Visibility = _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _all.Count == 0 ? "Aucun encodage pour le moment : ils apparaîtront ici dès le premier terminé."
                                         : "Aucun encodage ne correspond aux filtres.";
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStats();
        UpdateButtons();
    }

    void UpdateStats()
    {
        var ok = _visible.Select(r => r.Entry).Where(e => e.Succeeded && e.OutputSize > 0).ToList();
        var failed = _visible.Count(r => !r.Entry.Succeeded);
        StatCount.Text = ok.Count.ToString("N0");
        StatCountLabel.Text = (ok.Count > 1 ? "encodages réussis" : "encodage réussi") + (failed > 0 ? $"  ·  {failed} échec(s) / annulé(s)" : "");

        long src = ok.Sum(e => e.SourceSize), outp = ok.Sum(e => e.OutputSize);
        StatSaved.Text = Fmt.Size(Math.Max(0, src - outp));
        StatSavedLabel.Text = ok.Count > 0 ? $"économisés  ·  {Fmt.Size(src)} → {Fmt.Size(outp)}" : "économisés";
        StatRatio.Text = src > 0 ? $"{outp / (double)src - 1:+0%;-0%}" : "—";
        StatRatioLabel.Text = "taille moyenne (pondérée)" + (ok.Count > 0 ? $"  ·  meilleur {ok.Min(e => e.OutputSize / (double)e.SourceSize) - 1:+0%;-0%}" : "");

        var secs = _visible.Sum(r => r.Entry.EncodeSeconds);
        var video = ok.Sum(e => e.Duration);
        StatTime.Text = Fmt.Dur(secs);
        var byEncoder = ok.GroupBy(e => e.Encoder).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ×{g.Count()}").Take(3);
        StatTimeLabel.Text = "d'encodage" + (secs > 0 && video > 0 ? $"  ·  ×{video / secs:N2} temps réel" : "") +
                             (ok.Count > 0 ? "  ·  " + string.Join(", ", byEncoder) : "");
    }

    void UpdateButtons()
    {
        var one = Grid.SelectedItem is HistoryRow;
        OpenBtn.IsEnabled = one;
        LogBtn.IsEnabled = Grid.SelectedItem is HistoryRow { Entry.LogPath: { } l } && File.Exists(l);
        DeleteBtn.IsEnabled = Grid.SelectedItems.Count > 0;
    }

    void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) Refresh();
    }

    void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Open_Click(sender, e);

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not HistoryRow r) return;
        try
        {
            if (File.Exists(r.Entry.OutputPath)) Process.Start("explorer.exe", $"/select,\"{r.Entry.OutputPath}\"");
            else if (Path.GetDirectoryName(r.Entry.OutputPath) is { } d && Directory.Exists(d)) Process.Start("explorer.exe", $"\"{d}\"");
            else MessageBox.Show(this, "Ce fichier n'existe plus.", Title);
        }
        catch { }
    }

    void Log_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is HistoryRow { Entry.LogPath: { } l } && File.Exists(l))
            Process.Start(new ProcessStartInfo(l) { UseShellExecute = true });
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Exporter l'historique",
            Filter = "CSV (Excel)|*.csv",
            FileName = $"AV1Encod-historique-{DateTime.Now:yyyy-MM-dd}.csv",
        };
        if (dlg.ShowDialog(this) != true) return;
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder();
        sb.AppendLine("Date;Résultat;Source;Sortie;Encodeur;Preset;Qualité;Durée vidéo (s);Taille source (octets);Taille encodée (octets);Gain (%);Temps d'encodage (s);Images/s;HDR;Pistes audio;Sous-titres;Avertissements");
        foreach (var r in _visible)
        {
            var x = r.Entry;
            sb.AppendLine(string.Join(';',
                x.Date.ToString("yyyy-MM-dd HH:mm:ss"), x.Result, Q(x.SourcePath), Q(x.OutputPath), x.Encoder, Q(x.Preset), x.Quality,
                x.Duration.ToString("0"), x.SourceSize, x.OutputSize, x.OutputSize > 0 ? (r.Gain * 100).ToString("0.0") : "",
                x.EncodeSeconds.ToString("0"), x.AverageFps.ToString("0.0"), Q(x.OutputHdr), x.AudioTracks, x.SubtitleTracks, x.Warnings));
        }
        try
        {
            // BOM UTF-8 pour qu'Excel affiche bien les accents
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Export impossible : " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        var rows = Grid.SelectedItems.Cast<HistoryRow>().ToList();
        if (rows.Count == 0) return;
        if (MessageBox.Show(this, $"Retirer {rows.Count} ligne(s) de l'historique ?\n(les fichiers vidéo ne sont pas supprimés)", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var set = rows.Select(r => r.Entry).ToHashSet();
        _all = _all.Where(x => !set.Contains(x)).ToList();
        History.Save(_all);
        Refresh();
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_all.Count == 0) return;
        if (MessageBox.Show(this, $"Effacer tout l'historique ({_all.Count} encodage(s)) ?\n(les fichiers vidéo ne sont pas supprimés)", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _all = [];
        History.Save(_all);
        Refresh();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
