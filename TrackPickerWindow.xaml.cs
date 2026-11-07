using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AV1Encod.Services;

namespace AV1Encod;

/// <summary>Fenêtre de choix des pistes audio / sous-titres d'un fichier de la file.</summary>
public partial class TrackPickerWindow : Window
{
    readonly TrackGroup _audio, _subs;
    readonly CheckBox _defSubChk;

    public bool AllAudio { get; private set; }
    public bool AllSubs { get; private set; }
    public bool KeepDefaultSub { get; private set; }
    public bool ApplyToAll { get; private set; }

    public TrackPickerWindow(QueueItem item, bool allAudio, bool allSubs, bool keepDefaultSub, int otherFiles)
    {
        InitializeComponent();
        var src = item.Source!;
        FileText.Text = $"{item.FileName}   ·   {src.Summary}";

        _defSubChk = new CheckBox
        {
            Content = "Garder le sous-titre « par défaut » de la source comme piste par défaut",
            IsChecked = keepDefaultSub,
            Margin = new Thickness(0, 10, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Ex. le sous-titre français « forcé » s'affiche automatiquement pendant les passages en langue étrangère",
        };
        _audio = new TrackGroup(true, src.AudioTracks, allAudio, null);
        _subs = new TrackGroup(false, src.SubtitleTracks, allSubs, _defSubChk);
        AudioGroup.Content = _audio;
        SubsGroup.Content = _subs;

        ApplyAllChk.Visibility = otherFiles > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyAllChk.IsChecked = otherFiles > 0;
        ApplyAllText.Text = $"Appliquer aussi aux {otherFiles} autre(s) fichier(s) de la file — pistes retrouvées par langue, titre et codec (sinon par position)";
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!_audio.KeepAll && _audio.Rows.Count > 0 && !_audio.Rows.Any(r => r.IsChecked) &&
            MessageBox.Show(this, "Aucune piste audio n'est cochée : le fichier n'aura pas de son.\n\nContinuer ?", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _audio.Commit();
        _subs.Commit();
        AllAudio = _audio.KeepAll;
        AllSubs = _subs.KeepAll;
        KeepDefaultSub = _defSubChk.IsChecked == true;
        ApplyToAll = ApplyAllChk.IsChecked == true && ApplyAllChk.Visibility == Visibility.Visible;
        DialogResult = true;
    }
}

sealed class RelayCommand(Action run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run();
}

public abstract class Notifier : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record QuickPick(string Label, string Tooltip, ICommand Command);

/// <summary>Une carte « piste » de la fenêtre.</summary>
public sealed class TrackRow : Notifier
{
    readonly Action _changed;

    public TrackRow(TrackInfo track, Action changed)
    {
        Track = track;
        _changed = changed;
        _checked = track.Selected;
    }

    public TrackInfo Track { get; }

    bool _checked;
    public bool IsChecked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Notify(); _changed(); }
    }

    bool _enabled = true;
    public bool IsEnabled { get => _enabled; set { _enabled = value; Notify(); } }

    public string LangBadge => Lang.Badge(Track.Language);
    public string Title => Track.Title.Length > 0 ? Track.Title : Lang.Name(Track.Language);
    public bool IsDefault => Track.IsDefault;
    public bool IsForced => Track.IsForced;
    public bool HasWarning => !Track.Supported;
    public string WarningText => Track.IsAudio ? "sera réencodée" : "non pris en charge";
    public string Tooltip => Track.Description;

    public string Details
    {
        get
        {
            var parts = new List<string> { $"#{Track.Index}", Lang.Name(Track.Language), PrettyCodec() };
            if (Track.IsAudio)
            {
                if (Track.Channels.Length > 0) parts.Add(Track.Channels.Replace("(side)", "").Replace("stereo", "stéréo"));
                if (Track.BitrateKbps > 0) parts.Add($"{Track.BitrateKbps:N0} kb/s");
            }
            return string.Join("  ·  ", parts);
        }
    }

    string PrettyCodec()
    {
        var c = Track.Codec switch
        {
            "eac3" => "E-AC3", "ac3" => "AC3", "truehd" => "TrueHD", "dts" => "DTS", "aac" => "AAC", "opus" => "Opus",
            "flac" => "FLAC", "mp3" => "MP3", "subrip" => "SRT", "hdmv_pgs_subtitle" => "PGS", "dvd_subtitle" => "VobSub",
            "ass" => "ASS", "ssa" => "SSA", "mov_text" => "TX3G", "dvb_subtitle" => "DVB",
            _ => Track.Codec.ToUpperInvariant(),
        };
        if (Track.Profile.Contains("Atmos", StringComparison.OrdinalIgnoreCase)) c += " Atmos";
        else if (Track.Codec == "dts" && Track.Profile.Length > 0) c = Track.Profile;
        return c;
    }
}

/// <summary>Une colonne : audio ou sous-titres.</summary>
public sealed class TrackGroup : Notifier
{
    readonly bool _audio;
    bool[]? _custom; // sélection personnalisée mise de côté pendant que « tout garder » est actif
    bool _keepAll, _bulk;

    public TrackGroup(bool audio, IReadOnlyList<TrackInfo> tracks, bool keepAll, object? footer)
    {
        _audio = audio;
        Footer = footer;
        Rows = new ObservableCollection<TrackRow>(tracks.Select(t => new TrackRow(t, OnRowChanged)));

        var picks = new List<QuickPick>
        {
            new("Tout", "Cocher toutes les pistes", new RelayCommand(() => SetAll(_ => true))),
            new(audio ? "Aucune" : "Aucun", "Tout décocher", new RelayCommand(() => SetAll(_ => false))),
        };
        foreach (var g in tracks.GroupBy(t => t.Language))
        {
            var lang = g.Key;
            picks.Add(new QuickPick($"{Lang.Badge(lang)}  {g.Count()}", $"Cocher / décocher les pistes {Lang.Name(lang).ToLowerInvariant()}",
                new RelayCommand(() => Toggle(r => r.Track.Language == lang))));
        }
        if (!audio && tracks.Any(t => t.IsForced))
            picks.Add(new QuickPick("Forcés", "Cocher / décocher les sous-titres forcés", new RelayCommand(() => Toggle(r => r.Track.IsForced))));
        QuickPicks = picks;

        KeepAll = keepAll;
    }

    public string Header => _audio ? "Audio" : "Sous-titres";
    public string KeepAllLabel => _audio ? "Garder toutes les pistes audio" : "Garder tous les sous-titres";
    public string EmptyText => _audio ? "Aucune piste audio dans ce fichier." : "Aucun sous-titre dans ce fichier.";
    public ObservableCollection<TrackRow> Rows { get; }
    public IReadOnlyList<QuickPick> QuickPicks { get; }
    public object? Footer { get; }
    public bool IsEmpty => Rows.Count == 0;
    public bool CanPick => !KeepAll && Rows.Count > 0;

    public string Summary
    {
        get
        {
            if (Rows.Count == 0) return "aucune piste";
            if (KeepAll) return $"toutes ({Rows.Count})";
            var n = Rows.Count(r => r.IsChecked);
            return n == 0 ? $"aucune sur {Rows.Count}" : $"{n} sur {Rows.Count}";
        }
    }

    public bool KeepAll
    {
        get => _keepAll;
        set
        {
            if (value && !_keepAll)
            {
                _custom = Rows.Select(r => r.IsChecked).ToArray();
                Bulk(() => { foreach (var r in Rows) { r.IsChecked = true; r.IsEnabled = false; } });
            }
            else if (!value && _keepAll)
            {
                Bulk(() =>
                {
                    for (var i = 0; i < Rows.Count; i++)
                    {
                        Rows[i].IsChecked = _custom?[i] ?? Rows[i].Track.Selected;
                        Rows[i].IsEnabled = true;
                    }
                });
            }
            _keepAll = value;
            Notify();
            Notify(nameof(CanPick));
            Notify(nameof(Summary));
        }
    }

    /// <summary>Écrit le choix dans les pistes (en mode « tout », on garde la sélection perso pour plus tard).</summary>
    public void Commit()
    {
        for (var i = 0; i < Rows.Count; i++)
            Rows[i].Track.Selected = KeepAll ? _custom?[i] ?? Rows[i].Track.Selected : Rows[i].IsChecked;
    }

    void SetAll(Func<TrackRow, bool> on) => Bulk(() => { foreach (var r in Rows) r.IsChecked = on(r); });

    void Toggle(Func<TrackRow, bool> match)
    {
        var rows = Rows.Where(match).ToList();
        var allOn = rows.All(r => r.IsChecked);
        Bulk(() => { foreach (var r in rows) r.IsChecked = !allOn; });
    }

    void Bulk(Action a)
    {
        _bulk = true;
        try { a(); }
        finally { _bulk = false; }
        Notify(nameof(Summary));
    }

    void OnRowChanged()
    {
        if (!_bulk) Notify(nameof(Summary));
    }
}
