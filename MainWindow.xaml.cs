using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Threading;
using AV1Encod.Services;
using Microsoft.Win32;

namespace AV1Encod;

public partial class MainWindow : Window
{
    const string AppTitle = "AV1 Encod";
    static readonly Regex ProgressRe = new(
        @"Encoding: task (\d+) of (\d+), ([\d.]+) %(?: \(([\d.]+) fps, avg ([\d.]+) fps, ETA (\d+)h(\d+)m(\d+)s\))?", RegexOptions.Compiled);

    readonly AppSettings _settings = AppSettings.Load();
    readonly ObservableCollection<QueueItem> _queue = [];
    readonly ObservableCollection<ReportItem> _noReport = [];
    readonly string[] _initialPaths;

    HandBrakeInfo? _hb;
    string? _ffprobe, _ffmpeg;
    string _idleStatus = "Prêt.";

    /// <summary>Dernière sélection de pistes faite à la main : reprise par les fichiers ajoutés ensuite.</summary>
    List<TrackPick>? _audioTemplate, _subTemplate;
    /// <summary>Garder toutes les pistes audio / sous-titres, ou seulement celles choisies (fenêtre « Pistes »).</summary>
    bool _allAudio, _allSubs, _keepDefaultSub;

    bool _ready, _busy, _encoding, _settingDst, _restoring, _stopAfterCurrent;
    int _encoderChangeSeq;
    CancellationTokenSource? _cts;

    // Encodage en cours
    readonly ConcurrentQueue<string> _outQ = new(), _errQ = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly Stopwatch _itemWatch = new(), _batchWatch = new();
    StreamWriter? _log;
    string? _lastLogPath;
    QueueItem? _current;
    double _lastAverageFps;
    int _currentIndex, _currentTotal;

    public MainWindow(string[] initialPaths)
    {
        InitializeComponent();
        _initialPaths = initialPaths;
        QueueList.ItemsSource = _queue;
        ReportList.ItemsSource = _noReport;
        _timer.Tick += (_, _) => PumpEncoderOutput();

        var work = SystemParameters.WorkArea;
        Height = Math.Min(Height, work.Height);
        Width = Math.Min(Width, work.Width);

        CropBox.SelectedIndex = Math.Clamp(_settings.CropMode, 0, 2);
        _allAudio = _settings.AllAudio;
        _allSubs = _settings.AllSubs;
        _keepDefaultSub = _settings.KeepDefaultSub;
        ChaptersChk.IsChecked = _settings.Chapters;
        DeepChk.IsChecked = _settings.DeepCheck;
        LowPrioChk.IsChecked = _settings.LowPriority;
        OverwriteChk.IsChecked = _settings.Overwrite;
        ExtraBox.Text = _settings.ExtraArgs;
        _ready = true;
        UpdateQueueHeader();
        UpdatePreview();
        UpdateTracksSummary();
    }

    // ====================================================================== démarrage

    async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Updater.CleanupPreviousVersion();
        // juste après une mise à jour, l'ancien processus peut encore tenir l'ancien exe : on réessaie un peu plus tard
        _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ => Updater.CleanupPreviousVersion());
        await InitializeToolsAsync();
        if (ToolInstaller.Missing().Count > 0) await OfferToolInstallAsync();
        if (_settings.CheckUpdatesAtStartup is null)
        {
            _settings.CheckUpdatesAtStartup = MessageBox.Show(this,
                "Voulez-vous qu'AV1 Encod vérifie à chaque démarrage si une nouvelle version est disponible ?\n\n" +
                "Il interroge simplement la page des versions du projet sur GitHub ; aucune donnée personnelle n'est envoyée. " +
                "Modifiable à tout moment dans ⚙ Paramètres → Mises à jour.",
                AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            _settings.Save();
        }
        if (_settings.CheckUpdatesAtStartup == true) _ = CheckForUpdateAsync();
        if (_initialPaths.Length > 0) await AddPathsAsync(_initialPaths);
    }

    async Task InitializeToolsAsync()
    {
        SetBusy(true);
        SetStatus("Recherche de HandBrakeCLI, ffprobe et ffmpeg…");
        var (hb, probe, mpeg) = await Task.Run(() =>
            (ToolLocator.Find("HandBrakeCLI"), ToolLocator.Find("ffprobe"), ToolLocator.Find("ffmpeg")));
        _ffprobe = probe;
        _ffmpeg = mpeg;
        if (hb is null)
        {
            _hb = null;
            SetBusy(false);
            ToolsText.Text = "HandBrakeCLI introuvable   ·   ⚙ Paramètres pour l'installer";
            SetIdle("HandBrakeCLI est introuvable : installez-le depuis ⚙ Paramètres → Outils externes.");
            return;
        }

        SetStatus("Détection des encodeurs disponibles sur ce poste…");
        try { _hb = await HandBrake.DetectAsync(hb); }
        catch (Exception ex)
        {
            SetBusy(false);
            SetIdle("Impossible d'interroger HandBrakeCLI : " + ex.Message);
            return;
        }

        var hw = _hb.Encoders.Where(x => x.IsHardware).Select(x => x.Impl).Distinct().ToList();
        var gpus = await Task.Run(Native.GpuNames);
        var tools = new List<string> { $"HandBrake {_hb.Version}", _ffprobe is null ? "ffprobe ✖" : "ffprobe ✔", _ffmpeg is null ? "ffmpeg ✖" : "ffmpeg ✔" };
        if (gpus.Count > 0) tools.Add("GPU : " + string.Join(", ", gpus));
        ToolsText.Text = string.Join("   ·   ", tools);

        SetBusy(false);
        _restoring = true;
        EncoderBox.ItemsSource = _hb.Encoders;
        EncoderBox.SelectedItem = _hb.Encoders.FirstOrDefault(x => x.Name == _settings.Encoder)
                                  ?? _hb.Encoders.FirstOrDefault(x => x.Name == "svt_av1_10bit")
                                  ?? _hb.Encoders.FirstOrDefault();
        _restoring = false;

        var summary = $"{_hb.Encoders.Count} encodeurs détectés";
        summary += hw.Count > 0 ? $", dont matériels : {string.Join(", ", hw)}" : " (aucun encodeur matériel utilisable)";
        if (_ffprobe is null) summary += "   ·   ⚠ ffprobe introuvable : installez FFmpeg depuis ⚙ Paramètres";
        SetIdle(summary + ".");
    }

    // ====================================================================== outils externes

    /// <summary>Premier lancement (ou outil supprimé) : propose l'installation automatique.</summary>
    async Task OfferToolInstallAsync()
    {
        var missing = ToolInstaller.Missing();
        var list = string.Join("\n", missing.Select(p => "•  " + p.Label));
        if (ToolInstaller.FindWinget() is null)
        {
            MessageBox.Show(this, $"Il manque des outils indispensables :\n\n{list}\n\nwinget n'est pas disponible sur ce PC : installez-les à la main " +
                "(liens dans ⚙ Paramètres → Outils externes), puis relancez AV1 Encod.", AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this, $"Bienvenue ! AV1 Encod a besoin de ces outils gratuits :\n\n{list}\n\n" +
                "Les installer automatiquement maintenant ? (winget, sans droits administrateur, 1 à 3 minutes)",
                AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            SetIdle("Outils manquants : installez-les depuis ⚙ Paramètres → Outils externes.");
            return;
        }
        await InstallMissingToolsAsync(SetStatus);
    }

    /// <summary>Installe les paquets manquants avec winget, puis relance la détection.</summary>
    internal async Task<bool> InstallMissingToolsAsync(Action<string> report)
    {
        if (_busy) { report("Patientez : une analyse ou un encodage est en cours."); return false; }
        var missing = ToolInstaller.Missing();
        if (missing.Count == 0) return true;
        SetBusy(true);
        Progress.IsIndeterminate = true;
        var ok = true;
        try
        {
            foreach (var p in missing)
            {
                report($"Installation de {p.Label}…");
                var label = p.Label;
                try
                {
                    var done = await ToolInstaller.InstallAsync(p, line =>
                    {
                        // winget affiche des barres de progression : on ne garde que les lignes lisibles
                        var l = line.Trim();
                        if (l.Length > 3 && l.Any(char.IsLetter))
                            Dispatcher.InvokeAsync(() => report($"{label} : {l}"));
                    }, CancellationToken.None);
                    if (!done) { ok = false; report($"{label} : installation échouée — essayez à la main : {p.ManualUrl}"); }
                }
                catch (Exception ex)
                {
                    ok = false;
                    report($"{label} : {ex.Message}");
                }
            }
        }
        finally
        {
            Progress.IsIndeterminate = false;
            SetBusy(false);
        }
        await InitializeToolsAsync();
        if (ok) report("Outils installés, tout est prêt.");
        return ok;
    }

    internal IReadOnlyList<(string Name, string? Path, string Detail)> ToolStatus() =>
    [
        ("HandBrakeCLI", _hb?.Path, _hb is null ? "" : $"version {_hb.Version}   ·   {ShortPath(_hb.Path)}"),
        ("ffprobe", _ffprobe, ShortPath(_ffprobe)),
        ("ffmpeg", _ffmpeg, ShortPath(_ffmpeg)),
    ];

    /// <summary>« C:\Users\x\AppData\Local\… » → « %LOCALAPPDATA%\… » (plus lisible).</summary>
    static string ShortPath(string? path)
    {
        if (path is null) return "";
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return path.StartsWith(local, StringComparison.OrdinalIgnoreCase) ? "%LOCALAPPDATA%" + path[local.Length..] : path;
    }

    // ====================================================================== mises à jour

    ReleaseInfo? _availableUpdate;

    async Task CheckForUpdateAsync()
    {
        try
        {
            var r = await Updater.GetLatestAsync();
            if (r is null || !Updater.IsNewer(r)) return;
            _availableUpdate = r;
            BannerText.Text = $"Une nouvelle version d'AV1 Encod est disponible : {r.Version}  (vous avez la {Updater.CurrentVersion}).";
            BannerUpdateBtn.IsEnabled = r.AssetUrl is not null;
            UpdateBanner.Visibility = Visibility.Visible;
        }
        catch { } // pas de réseau : on réessaiera au prochain lancement
    }

    /// <summary>Télécharge la nouvelle version, puis ferme l'application (la nouvelle se lance toute seule).</summary>
    internal async Task ApplyUpdateAsync(ReleaseInfo r, IProgress<double> progress)
    {
        if (_busy) throw new InvalidOperationException("une analyse ou un encodage est en cours, réessayez à la fin.");
        SaveSettings();
        await Updater.DownloadAndRestartAsync(r, progress);
        Application.Current.Shutdown();
    }

    async void BannerUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is not { } r) return;
        BannerUpdateBtn.IsEnabled = false;
        try
        {
            await ApplyUpdateAsync(r, new Progress<double>(p => BannerText.Text = $"Téléchargement de la version {r.Version}… {p:P0}"));
        }
        catch (Exception ex)
        {
            BannerText.Text = "La mise à jour a échoué : " + ex.Message;
            BannerUpdateBtn.IsEnabled = true;
        }
    }

    void BannerNotes_Click(object sender, RoutedEventArgs e) => Updater.OpenUrl(_availableUpdate?.PageUrl ?? Updater.ReleasesUrl);
    void BannerLater_Click(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

    void History_Click(object sender, RoutedEventArgs e) => new HistoryWindow { Owner = this }.ShowDialog();

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(this, _settings, _availableUpdate) { Owner = this }.ShowDialog();
    }
    // ====================================================================== encodeur

    async void EncoderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EncoderBox.SelectedItem is not EncoderInfo enc || _hb is null) return;
        var restore = _restoring && enc.Name == _settings.Encoder;
        var seq = ++_encoderChangeSeq;

        SetStatus($"Lecture des presets de {enc.Name}…");
        string[] presets;
        try { presets = await HandBrake.GetPresetsAsync(_hb.Path, enc.Name); }
        catch { presets = []; }
        if (seq != _encoderChangeSeq) return; // l'utilisateur a déjà changé d'encodeur

        PresetBox.ItemsSource = presets;
        if (presets.Length > 0)
        {
            var want = restore && presets.Contains(_settings.Preset) ? _settings.Preset
                     : enc.PreferredPresets.FirstOrDefault(presets.Contains) ?? presets[presets.Length / 2];
            PresetBox.SelectedItem = want;
        }
        PresetBox.IsEnabled = presets.Length > 0;

        QSlider.Maximum = enc.MaxQuality;
        QSlider.Value = Math.Clamp(restore ? _settings.Quality : enc.DefaultQuality, 0, enc.MaxQuality);
        QLabel.Text = $"{enc.QualityName} (0–{enc.MaxQuality}) — plus bas = meilleure qualité";

        var info = $"{enc.Impl}";
        info += presets.Length > 0 ? $"   ·   presets {presets[0]} → {presets[^1]} (du plus rapide au plus lent / efficace)" : "   ·   pas de preset";
        info += enc.KeepsDynamicHdr switch
        {
            true => "   ·   HDR10, HDR10+ et Dolby Vision conservés",
            null => "   ·   HDR10 conservé, Dolby Vision / HDR10+ non garantis",
            _ when enc.IsMain && enc.Bits >= 10 => "   ·   HDR10 conservé, Dolby Vision / HDR10+ perdus",
            _ => enc.IsMain ? "   ·   8-bit : HDR perdu" : "",
        };
        EncInfo.Text = info;

        AssignDestinations();
        UpdatePreview();
        SetStatus(_idleStatus);
    }

    // ====================================================================== file d'attente

    EncoderInfo? SelectedEncoder => EncoderBox.SelectedItem as EncoderInfo;
    QueueItem? Selected => QueueList.SelectedItem as QueueItem;

    async Task AddPathsAsync(IEnumerable<string> paths)
    {
        if (_busy) return;
        var list = paths.ToList();
        SetStatus("Recherche des vidéos…");
        var scan = await Task.Run(() => QueueScanner.Expand(list, History.SuccessfulOutputs()));
        var existing = new HashSet<string>(_queue.Select(q => q.SourcePath), StringComparer.OrdinalIgnoreCase);
        var added = scan.Files.Where(f => !existing.Contains(f)).Select(f => new QueueItem(f)).ToList();
        foreach (var a in added) _queue.Add(a);
        try
        {
            var first = list.FirstOrDefault();
            if (first is not null) _settings.SrcDir = Directory.Exists(first) ? Path.GetDirectoryName(first.TrimEnd('\\')) ?? first : Path.GetDirectoryName(first) ?? _settings.SrcDir;
        }
        catch { }
        AssignDestinations();
        UpdateQueueHeader();

        var msg = added.Count == 0 ? "Aucune nouvelle vidéo trouvée" : $"{added.Count} fichier(s) ajouté(s)";
        var already = scan.Files.Count - added.Count;
        if (already > 0) msg += $", {already} déjà dans la file";
        if (scan.SkippedSamples > 0) msg += $", {scan.SkippedSamples} sample(s) ignoré(s)";
        if (scan.SkippedOutputs > 0) msg += $", {scan.SkippedOutputs} fichier(s) déjà encodé(s) ignoré(s)";
        if (added.Count == 0) { SetIdle(msg + "."); return; }

        QueueList.SelectedItem = added[0];
        QueueList.ScrollIntoView(added[0]);
        await AnalyzeItemsAsync(added, msg);
    }

    /// <summary>
    /// Destination par défaut, à côté de la source : nom court (« Titre.Année.AV1 ») ou format Radarr / Sonarr
    /// (⚙ Paramètres). Si deux fichiers de la file tombent sur le même nom, le suivant est numéroté « (2) ».
    /// </summary>
    void AssignDestinations()
    {
        if (!_ready || SelectedEncoder is null) return;
        var autos = _queue.Where(q => q.DestAuto && q.Status is not (QueueStatus.Encoding or QueueStatus.Done)).ToList();
        foreach (var a in autos) a.DestPath = DestinationFor(a, a.Source);

        foreach (var g in _queue.GroupBy(q => q.DestPath, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            var n = 1;
            foreach (var c in g.Where(autos.Contains).Skip(g.Any(q => !autos.Contains(q)) ? 0 : 1))
                c.DestPath = Path.Combine(Path.GetDirectoryName(c.DestPath)!, $"{Path.GetFileNameWithoutExtension(c.DestPath)} ({++n}).mkv");
        }

        if (Selected is { DestAuto: true } s) ShowDestination(s);
    }

    string DestinationFor(QueueItem q, SourceInfo? info)
    {
        var enc = SelectedEncoder!;
        var dir = Path.GetDirectoryName(q.SourcePath) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(q.SourcePath);
        var name = _settings.NamingMode == "template"
            ? OutputNaming.FromTemplates(_settings.MovieTemplate, _settings.SeriesTemplate, NamingFor(q.SourcePath, info))
            : OutputNaming.ShortName(baseName, enc.Tag);
        if (string.IsNullOrWhiteSpace(name)) name = OutputNaming.ShortName(baseName, enc.Tag);
        var p = Path.Combine(dir, name + ".mkv");
        return string.Equals(p, q.SourcePath, StringComparison.OrdinalIgnoreCase) ? Path.Combine(dir, name + ".reencode.mkv") : p;
    }

    NamingContext NamingFor(string sourcePath, SourceInfo? info) => new()
    {
        SourcePath = sourcePath, Source = info, Encoder = SelectedEncoder!, AllAudio = _allAudio, AllSubs = _allSubs,
    };

    // ---- utilisé par ⚙ Paramètres → Nommage

    internal EncoderInfo? CurrentEncoder => SelectedEncoder;

    /// <summary>Fichier analysé servant d'exemple pour l'aperçu du nommage (film ou épisode).</summary>
    internal QueueItem? NamingSample(bool series) =>
        new[] { Selected }.Concat(_queue).FirstOrDefault(q => q?.Source is not null &&
            ReleaseName.Parse(Path.GetFileNameWithoutExtension(q.SourcePath)).IsEpisode == series);

    internal NamingContext NamingContextFor(QueueItem q) => NamingFor(q.SourcePath, q.Source);

    internal void OnNamingChanged()
    {
        AssignDestinations();
        UpdatePreview();
    }

    void ShowDestination(QueueItem? item)
    {
        _settingDst = true;
        DstBox.Text = item?.DestPath ?? "";
        _settingDst = false;
    }

    void UpdateQueueHeader()
    {
        QueueEmptyHint.Visibility = _queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_queue.Count == 0) QueueCountRun.Text = "";
        else
        {
            var dur = _queue.Sum(q => q.Source?.Duration ?? 0);
            QueueCountRun.Text = $"   ·   {_queue.Count} fichier(s)   ·   {Fmt.Size(_queue.Sum(q => q.Size))}" + (dur > 0 ? $"   ·   {Fmt.Dur(dur)}" : "");
        }
        var todo = _queue.Count(q => q.Status is not (QueueStatus.Done or QueueStatus.Skipped or QueueStatus.Error));
        EncodeBtn.Content = todo > 1 ? $"▶  Encoder la file ({todo})" : "▶  Lancer l'encodage";
    }

    void QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var it = Selected;
        ReportList.ItemsSource = it?.Report ?? _noReport;
        ReportTab.Header = it is null ? "  Rapport  " : $"  Rapport : {it.FileName}  ";
        ShowDestination(it);
        ReportScroll.ScrollToTop();
        UpdatePreview();
        UpdateTracksSummary();
    }

    void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // double-clic sur une ligne (pas sur l'en-tête / la barre de défilement) → choix des pistes
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(QueueList, d) is ListViewItem)
            ChooseTracks_Click(sender, e);
    }

    void QueueList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && !_busy) Remove_Click(sender, e);
    }

    async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Ajouter des vidéos",
            Multiselect = true,
            Filter = "Vidéos|" + string.Join(';', QueueScanner.VideoExtensions.Select(x => "*" + x)) + "|Tous les fichiers|*.*",
        };
        if (Directory.Exists(_settings.SrcDir)) dlg.InitialDirectory = _settings.SrcDir;
        if (dlg.ShowDialog(this) == true) await AddPathsAsync(dlg.FileNames);
    }

    async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Dossier de la série (sous-dossiers compris) — plusieurs dossiers possibles",
            Multiselect = true,
        };
        if (Directory.Exists(_settings.SrcDir)) dlg.InitialDirectory = _settings.SrcDir;
        if (dlg.ShowDialog(this) == true) await AddPathsAsync(dlg.FolderNames);
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        foreach (var it in QueueList.SelectedItems.Cast<QueueItem>().ToList()) _queue.Remove(it);
        AssignDestinations();
        UpdateQueueHeader();
        UpdatePreview();
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _queue.Clear();
        UpdateQueueHeader();
        UpdatePreview();
        SetIdle("File d'attente vidée.");
    }

    // ====================================================================== commande

    EncodeOptions BuildOptions(QueueItem item)
    {
        var src = item.Source;
        var allSubs = _allSubs;
        var keptSubs = src?.SubtitleTracks.Where(t => allSubs || t.Selected).ToList() ?? [];
        return new EncodeOptions
        {
            Source = item.SourcePath,
            Destination = item.DestPath,
            Encoder = SelectedEncoder,
            Preset = PresetBox.SelectedItem as string,
            Quality = (int)QSlider.Value,
            CropMode = CropBox.SelectedIndex,
            AllAudio = _allAudio,
            AllSubs = allSubs,
            KeepDefaultSub = _keepDefaultSub,
            Chapters = ChaptersChk.IsChecked == true,
            ExtraArgs = ExtraBox.Text,
            AudioTracks = src?.AudioTracks.Where(t => t.Selected).Select(t => t.Index).ToList(),
            SubtitleTracks = src?.SubtitleTracks.Where(t => t.Selected).Select(t => t.Index).ToList(),
            DefaultSubIndex = TrackSelection.DefaultSubPosition(keptSubs),
        };
    }

    void UpdatePreview()
    {
        if (!_ready) return;
        CmdBox.Text = Selected is { } it ? "HandBrakeCLI " + EncodeOptions.Display(BuildOptions(it).BuildArgs())
                    : _queue.Count == 0 ? "(ajoutez un film ou le dossier d'une série)" : "(sélectionnez un fichier de la file)";
    }

    void Option_Changed(object sender, RoutedEventArgs e) => UpdatePreview();
    void QSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdatePreview();

    void DstBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _settingDst || Selected is not { } it) return;
        var t = DstBox.Text.Trim().Trim('"');
        if (t.Length == 0)
        {
            // champ vidé : on revient au nom automatique
            it.DestAuto = true;
            AssignDestinations();
        }
        else
        {
            it.DestAuto = false;
            it.DestPath = t;
        }
        UpdatePreview();
    }

    // ====================================================================== état de l'interface

    void SetBusy(bool busy, bool cancellable = false)
    {
        _busy = busy;
        QueueToolbar.IsEnabled = !busy;
        DestRow.IsEnabled = !busy;
        ParamsCard.IsEnabled = !busy;
        AnalyzeBtn.IsEnabled = !busy;
        EncodeBtn.IsEnabled = !busy && _hb is not null;
        CancelBtn.IsEnabled = busy && cancellable;
        Cursor = busy && !cancellable ? Cursors.AppStarting : null;
        UpdateTracksSummary();
    }

    void SetStatus(string text) => StatusText.Text = text;

    void SetIdle(string text)
    {
        _idleStatus = text;
        SetStatus(text);
    }

    // ====================================================================== analyse

    AnalysisRequest BuildRequest(QueueItem item) => new()
    {
        SourcePath = item.SourcePath,
        DestPath = item.DestPath,
        Encoder = SelectedEncoder,
        HandBrake = _hb,
        FfprobePath = _ffprobe,
        FfmpegPath = _ffmpeg,
        AllAudio = _allAudio,
        AllSubs = _allSubs,
        KeepDefaultSub = _keepDefaultSub,
        Chapters = ChaptersChk.IsChecked == true,
        DeepCheck = DeepChk.IsChecked == true,
        Overwrite = OverwriteChk.IsChecked == true,
        SelectTracks = (audio, subs) => SelectTracksFor(item, audio, subs),
        // le nom au format Radarr dépend de l'analyse (langues, HDR…) : on le recalcule avant de vérifier la destination
        ResolveDestination = info =>
        {
            if (item.DestAuto) item.DestPath = DestinationFor(item, info);
            return item.DestPath;
        },
    };

    /// <summary>
    /// Sélection appliquée aux pistes d'un fichier qu'on (ré)analyse : celle déjà faite sur ce fichier,
    /// sinon la dernière faite à la main (épisode précédent), sinon le défaut de l'analyse.
    /// </summary>
    void SelectTracksFor(QueueItem item, IReadOnlyList<TrackInfo> audio, IReadOnlyList<TrackInfo> subs)
    {
        var prevAudio = item.Source is { } s ? TrackSelection.Capture(s.AudioTracks) : _audioTemplate;
        var prevSubs = item.Source is { } s2 ? TrackSelection.Capture(s2.SubtitleTracks) : _subTemplate;
        if (prevAudio is not null) TrackSelection.Apply(audio, prevAudio);
        if (prevSubs is not null) TrackSelection.Apply(subs, prevSubs);
    }

    /// <returns>false si l'analyse a été annulée</returns>
    async Task<bool> AnalyzeItemsAsync(IList<QueueItem> items, string? prefix = null)
    {
        if (items.Count == 0) return true;
        if (_busy) return false;
        _cts = new CancellationTokenSource();
        SetBusy(true, cancellable: true);
        Progress.Value = 0;
        QueueItem? cur = null;
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                cur = items[i];
                cur.SetStatus(QueueStatus.Analyzing, "Analyse…");
                cur.Report.Clear();
                cur.Progress = 0;
                cur.Result = "";
                var rep = new Reporter(cur.Report);
                var label = items.Count > 1 ? $"Analyse {i + 1}/{items.Count} · {cur.FileName}" : $"Analyse · {cur.FileName}";
                SetStatus(label);

                AnalysisResult res;
                try { res = await SourceAnalyzer.AnalyzeAsync(BuildRequest(cur), rep, s => SetStatus($"{label} — {s}"), _cts.Token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    rep.Error("Erreur pendant l'analyse : " + ex.Message);
                    res = new AnalysisResult(rep.Errors, rep.Warnings, null, false);
                }
                cur.Source = res.Source;
                ApplyAnalysis(cur, res, rep);
                Progress.Value = 100.0 * (i + 1) / items.Count;
                if (cur == Selected) { UpdatePreview(); UpdateTracksSummary(); }
            }
            cur = null;
            AssignDestinations(); // gère les noms identiques entre fichiers analysés
            SetIdle((prefix is null ? "" : prefix + "   ·   ") + "Analyse : " + Describe(items));
            return true;
        }
        catch (OperationCanceledException)
        {
            if (cur is not null)
            {
                cur.SetStatus(QueueStatus.Pending, "Analyse annulée");
                cur.Report.Add(new ReportItem(Level.Warn, "Analyse annulée"));
            }
            SetIdle("Analyse annulée.");
            return false;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
            Progress.Value = 0;
            UpdateQueueHeader();
            UpdatePreview();
        }
    }

    static void ApplyAnalysis(QueueItem item, AnalysisResult res, Reporter rep)
    {
        rep.Head("Résultat");
        if (res.Errors > 0)
        {
            rep.Error($"{res.Errors} erreur(s), {res.Warnings} avertissement(s) : fichier ignoré à l'encodage");
            item.SetStatus(QueueStatus.Error, $"✖ {res.Errors} erreur{(res.Errors > 1 ? "s" : "")}");
        }
        else if (res.DestinationExists)
        {
            rep.Info("Déjà encodé : fichier ignoré");
            item.SetStatus(QueueStatus.Skipped, "Déjà encodé");
        }
        else if (res.Warnings > 0)
        {
            rep.Warn($"{res.Warnings} avertissement(s) : encodage possible après confirmation");
            item.SetStatus(QueueStatus.Warning, $"⚠ {res.Warnings} avertissement{(res.Warnings > 1 ? "s" : "")}");
        }
        else
        {
            rep.Ok("Tout est bon, prêt à encoder");
            item.SetStatus(QueueStatus.Ready, "✔ Prêt");
        }
    }

    static string Describe(IEnumerable<QueueItem> items)
    {
        var l = items.ToList();
        var parts = new List<string>();
        void Add(int n, string what) { if (n > 0) parts.Add($"{n} {what}"); }
        Add(l.Count(q => q.Status == QueueStatus.Ready), "prêt(s)");
        Add(l.Count(q => q.Status == QueueStatus.Warning), "avec avertissement(s)");
        Add(l.Count(q => q.Status == QueueStatus.Error), "en erreur");
        Add(l.Count(q => q.Status == QueueStatus.Skipped), "déjà encodé(s)");
        return parts.Count > 0 ? string.Join(", ", parts) + "." : "rien à signaler.";
    }

    // ====================================================================== encodage

    async Task StartQueueAsync()
    {
        if (_busy || _hb is null || SelectedEncoder is null) return;
        var todo = _queue.Where(q => q.Status != QueueStatus.Done).ToList();
        if (todo.Count == 0)
        {
            MessageBox.Show(this, _queue.Count == 0 ? "Ajoutez d'abord un film, des fichiers ou le dossier d'une série." : "Tous les fichiers de la file sont déjà encodés.",
                AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Analyse fraîche avec les réglages actuels (la vérification approfondie est mémorisée)
        if (!await AnalyzeItemsAsync(todo)) return;
        var run = todo.Where(q => q.IsRunnable).ToList();
        int errors = todo.Count(q => q.Status == QueueStatus.Error), skipped = todo.Count(q => q.Status == QueueStatus.Skipped);
        int warned = run.Count(q => q.Status == QueueStatus.Warning);
        if (run.Count == 0)
        {
            MessageBox.Show(this, $"Rien à encoder : {errors} fichier(s) en erreur, {skipped} déjà encodé(s).\nConsultez le rapport de chaque fichier.",
                AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (run.Count > 1 || warned > 0 || errors > 0 || skipped > 0)
        {
            var sb = new StringBuilder($"{run.Count} fichier(s) à encoder ({Fmt.Size(run.Sum(q => q.Size))}, {Fmt.Dur(run.Sum(q => q.Source?.Duration ?? 0))} de vidéo).\n");
            if (warned > 0) sb.Append($"\n⚠  {warned} avec avertissement(s) : voir leur rapport");
            if (errors > 0) sb.Append($"\n✖  {errors} en erreur : ignoré(s)");
            if (skipped > 0) sb.Append($"\n•  {skipped} déjà encodé(s) : ignoré(s)");
            foreach (var g in run.GroupBy(q => Path.GetPathRoot(q.DestPath) ?? "", StringComparer.OrdinalIgnoreCase))
            {
                var free = Native.FreeSpace(Path.GetDirectoryName(g.First().DestPath) ?? g.Key);
                var need = g.Sum(q => q.Size) * 0.5;
                if (free >= 0 && free < need) sb.Append($"\n⚠  Espace peut-être insuffisant sur {g.Key} : {Fmt.Size(free)} libres pour ~{Fmt.Size(need)} estimés");
            }
            sb.Append("\n\nLancer l'encodage ?");
            if (MessageBox.Show(this, sb.ToString(), AppTitle, MessageBoxButton.YesNo, warned > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
        }

        SaveSettings();
        _cts = new CancellationTokenSource();
        _stopAfterCurrent = false;
        _encoding = true;
        SetBusy(true, cancellable: true);
        LogBox.Clear();
        Progress.Value = 0;
        Native.KeepAwake();
        _batchWatch.Restart();
        int done = 0, failed = 0;
        long saved = 0;
        var cancelled = false;
        try
        {
            for (var i = 0; i < run.Count && !_stopAfterCurrent; i++)
            {
                var item = run[i];
                _current = item;
                _currentIndex = i;
                _currentTotal = run.Count;
                QueueList.SelectedItem = item;
                QueueList.ScrollIntoView(item);
                var outcome = await EncodeItemAsync(item, _cts.Token);
                if (outcome == QueueStatus.Done)
                {
                    done++;
                    try { saved += item.Size - new FileInfo(item.DestPath).Length; } catch { }
                }
                else if (outcome == QueueStatus.Failed) failed++;
                else { cancelled = true; break; }
            }
        }
        finally
        {
            _timer.Stop();
            Native.AllowSleep();
            _batchWatch.Stop();
            _current = null;
            _cts.Dispose();
            _cts = null;
            _encoding = false;
            Title = AppTitle;
            SetBusy(false);
            UpdateQueueHeader();
        }

        var remaining = run.Count(q => q.IsRunnable || q.Status == QueueStatus.Cancelled);
        var msg = cancelled ? $"Encodage annulé après {Fmt.Dur(_batchWatch.Elapsed.TotalSeconds)}"
                            : $"Terminé en {Fmt.Dur(_batchWatch.Elapsed.TotalSeconds)}";
        msg += $" : {done} encodé(s)";
        if (failed > 0) msg += $", {failed} en échec";
        if (remaining > 0) msg += $", {remaining} non traité(s)";
        if (saved > 0) msg += $"   ·   {Fmt.Size(saved)} économisés";
        SetIdle(msg + ".");
        Progress.Value = cancelled ? 0 : 100;
        Taskbar.ProgressState = failed > 0 ? TaskbarItemProgressState.Error : TaskbarItemProgressState.None;
        if (!cancelled) (failed > 0 ? System.Media.SystemSounds.Hand : System.Media.SystemSounds.Asterisk).Play();
    }

    /// <returns>Done, Failed ou Cancelled</returns>
    async Task<QueueStatus> EncodeItemAsync(QueueItem item, CancellationToken ct)
    {
        var enc = SelectedEncoder!;
        var src = item.Source!;
        var opts = BuildOptions(item);
        var dst = Path.GetFullPath(opts.Destination);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

        var tenBit = enc.Bits >= 10;
        var expect = new EncodeExpectation
        {
            Source = src, Destination = dst, Encoder = enc,
            Audio = opts.AllAudio ? src.AudioCount : opts.AudioTracks?.Count ?? Math.Min(1, src.AudioCount),
            Subtitles = opts.AllSubs ? src.SupportedSubtitleCount : src.SubtitleTracks.Count(t => t.Selected && t.Supported),
            Chapters = opts.Chapters ? src.ChapterCount : 0,
            Hdr = src.IsHdr && tenBit, DolbyVision = src.HasDolbyVision && tenBit, Hdr10Plus = src.HasHdr10Plus && tenBit,
        };

        var args = opts.BuildArgs();
        Directory.CreateDirectory(AppSettings.LogDir);
        var safeName = Regex.Replace(Path.GetFileNameWithoutExtension(dst), @"[^\w.\-]", "_");
        if (safeName.Length > 80) safeName = safeName[..80];
        item.LogPath = _lastLogPath = Path.Combine(AppSettings.LogDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{safeName}.log");
        _log = new StreamWriter(item.LogPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        var cmdLine = $"\"{_hb!.Path}\" {EncodeOptions.Display(args)}";
        _log.WriteLine($"# {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        _log.WriteLine($"# {cmdLine}");
        if (LogBox.Text.Length > 0) LogBox.AppendText(Environment.NewLine);
        LogBox.AppendText($"===== [{_currentIndex + 1}/{_currentTotal}] {item.FileName} ====={Environment.NewLine}{cmdLine}{Environment.NewLine}{Environment.NewLine}");

        _outQ.Clear();
        _errQ.Clear();
        item.SetStatus(QueueStatus.Encoding, "Démarrage…");
        _lastAverageFps = 0;
        item.Progress = 0;
        SetStatus($"{ItemPrefix()}démarrage de HandBrake (scan de la source)…");
        if (_currentIndex == 0) Taskbar.ProgressState = TaskbarItemProgressState.Indeterminate;
        _itemWatch.Restart();
        _timer.Start();

        int exitCode;
        try
        {
            exitCode = await ProcessRunner.RunStreamingAsync(_hb.Path, args, _outQ.Enqueue, _errQ.Enqueue, LowPrioChk.IsChecked == true, ct);
        }
        catch (Exception ex)
        {
            exitCode = -1;
            _errQ.Enqueue("Impossible de lancer HandBrakeCLI : " + ex.Message);
        }
        finally
        {
            _timer.Stop();
            PumpEncoderOutput();
            _itemWatch.Stop();
            _log?.Dispose();
            _log = null;
        }

        var elapsed = Fmt.Dur(_itemWatch.Elapsed.TotalSeconds);
        var entry = new HistoryEntry
        {
            Date = DateTime.Now, SourcePath = item.SourcePath, OutputPath = dst, Encoder = enc.Name, Preset = opts.Preset ?? "",
            Quality = opts.Quality, SourceSize = item.Size, Duration = src.Duration, EncodeSeconds = _itemWatch.Elapsed.TotalSeconds,
            // HandBrake n'annonce sa vitesse moyenne qu'après quelques secondes : sinon images ÷ temps
            AverageFps = _lastAverageFps > 0 ? _lastAverageFps
                       : _itemWatch.Elapsed.TotalSeconds > 0 ? src.Duration * src.Fps / _itemWatch.Elapsed.TotalSeconds : 0,
            SourceSummary = src.Summary, AudioTracks = expect.Audio, SubtitleTracks = expect.Subtitles,
            LogPath = item.LogPath,
        };

        if (ct.IsCancellationRequested)
        {
            item.SetStatus(QueueStatus.Cancelled, "Annulé");
            item.Progress = 0;
            entry.Result = "cancelled";
            History.Append(entry);
            if (File.Exists(dst) &&
                MessageBox.Show(this, $"Encodage annulé.\n\nSupprimer le fichier partiel ?\n{dst}\n\n(s'il est conservé, il sera considéré comme « déjà encodé »)",
                    AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                try { File.Delete(dst); } catch { }
            }
            return QueueStatus.Cancelled;
        }

        var rep = new Reporter(item.Report);
        if (exitCode != 0)
        {
            rep.Head("Encodage");
            rep.Error($"HandBrake a échoué (code {exitCode}) après {elapsed} — voir l'onglet « Journal HandBrake »");
            // un fichier partiel serait pris pour « déjà encodé » au prochain passage
            try { if (File.Exists(dst)) { File.Delete(dst); rep.Info("Fichier partiel supprimé"); } } catch { }
            item.SetStatus(QueueStatus.Failed, $"✖ Échec (code {exitCode})");
            item.Progress = 0;
            entry.Result = "failed";
            History.Append(entry);
            return QueueStatus.Failed;
        }

        item.Progress = 100;
        item.SetStatus(QueueStatus.Encoding, "Vérification…");
        SetStatus($"{ItemPrefix()}vérification du fichier encodé…");
        entry.OutputHdr = await OutputVerifier.VerifyAsync(expect, _ffprobe, item.LogPath, rep);
        try
        {
            entry.OutputSize = new FileInfo(dst).Length;
            item.Result = $"{entry.OutputSize / (double)item.Size - 1:+0%;-0%} · {Fmt.Size(entry.OutputSize)}";
        }
        catch { }
        entry.Warnings = rep.Warnings;

        if (rep.Errors > 0)
        {
            rep.Warn("Le fichier produit semble incorrect : supprimez-le ou cochez « Écraser » pour le refaire");
            item.SetStatus(QueueStatus.Failed, "⚠ Sortie suspecte");
            entry.Result = "suspect";
            History.Append(entry);
            return QueueStatus.Failed;
        }
        item.SetStatus(QueueStatus.Done, rep.Warnings > 0 ? $"✔ Fait (⚠ {rep.Warnings})" : $"✔ Fait en {elapsed}");
        entry.Result = rep.Warnings > 0 ? "warning" : "ok";
        History.Append(entry);
        return QueueStatus.Done;
    }
    string ItemPrefix() =>
        (_currentTotal > 1 ? $"Fichier {_currentIndex + 1}/{_currentTotal} · " : "") + (_stopAfterCurrent ? "(arrêt après ce fichier) · " : "");

    /// <summary>Vide les files de sortie de HandBrake : progression → barres, le reste → journal.</summary>
    void PumpEncoderOutput()
    {
        string? last = null;
        var sb = new StringBuilder();
        while (_outQ.TryDequeue(out var line))
        {
            if (line.TrimStart().StartsWith("Encoding: task")) last = line;
            else if (line.Contains("Muxing"))
            {
                SetStatus($"{ItemPrefix()}finalisation du fichier (muxing)…");
                _current?.SetStatus(QueueStatus.Encoding, "Finalisation…");
            }
            else if (line.Trim().Length > 0) { sb.AppendLine(line); _log?.WriteLine(line); }
        }
        while (_errQ.TryDequeue(out var line))
        {
            sb.AppendLine(line);
            _log?.WriteLine(line);
        }
        if (sb.Length > 0)
        {
            LogBox.AppendText(sb.ToString());
            LogBox.ScrollToEnd();
        }
        if (last is null || _current is null || ProgressRe.Match(last) is not { Success: true } m) return;

        var pct = Math.Clamp(double.Parse(m.Groups[3].Value, Fmt.Inv), 0, 100);
        var overall = (_currentIndex + pct / 100) / Math.Max(1, _currentTotal);
        _current.Progress = pct;
        _current.SetStatus(QueueStatus.Encoding, $"Encodage {pct:N0} %");
        Progress.Value = overall * 100;
        Taskbar.ProgressState = TaskbarItemProgressState.Normal;
        Taskbar.ProgressValue = overall;

        var text = $"{ItemPrefix()}{pct:N1} %";
        if (int.Parse(m.Groups[2].Value) > 1) text += $" (passe {m.Groups[1].Value}/{m.Groups[2].Value})";
        if (m.Groups[5].Success) _lastAverageFps = double.Parse(m.Groups[5].Value, Fmt.Inv);
        if (m.Groups[4].Success)
            text += $"   ·   {m.Groups[4].Value} i/s (moy. {m.Groups[5].Value})   ·   reste {m.Groups[6].Value}h{m.Groups[7].Value}m{m.Groups[8].Value}s";
        text += $"   ·   écoulé {Fmt.Dur(_itemWatch.Elapsed.TotalSeconds)}";
        if (_currentTotal > 1) text += $"   ·   file : {overall:P0}";
        SetStatus(text);
        Title = _currentTotal > 1 ? $"[{_currentIndex + 1}/{_currentTotal}] {pct:N0} % — {AppTitle}" : $"{pct:N0} % — {AppTitle}";
    }

    // ====================================================================== pistes

    /// <summary>Fichier dont on montre / choisit les pistes : la sélection, sinon le 1er fichier analysé.</summary>
    QueueItem? TracksItem => Selected?.Source is not null ? Selected : _queue.FirstOrDefault(q => q.Source is not null);

    /// <summary>Résumé des pistes gardées, dans la section Encodage.</summary>
    void UpdateTracksSummary()
    {
        if (!_ready) return;
        var src = TracksItem?.Source;
        AudioSummaryRun.Text = Summarize(src?.AudioTracks, _allAudio, true);
        SubsSummaryRun.Text = Summarize(src?.SubtitleTracks, _allSubs, false);
        ChooseTracksBtn.IsEnabled = !_busy && src is not null;
    }

    static string Summarize(IReadOnlyList<TrackInfo>? tracks, bool all, bool audio)
    {
        var everything = audio ? "toutes" : "tous";
        if (tracks is null) return all ? everything : audio ? "1re piste" : "aucun";
        if (tracks.Count == 0) return "aucune piste";
        if (all) return $"{everything} ({tracks.Count})   {Langs(tracks)}";
        var kept = tracks.Where(t => t.Selected).ToList();
        if (kept.Count == 0) return audio ? "aucune — pas de son !" : "aucun";
        return $"{kept.Count} sur {tracks.Count}   {Langs(kept)}";
    }

    static string Langs(IReadOnlyCollection<TrackInfo> tracks)
    {
        var s = string.Join(" · ", tracks.GroupBy(t => Lang.Badge(t.Language)).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key));
        var forced = tracks.Count(t => t.IsForced);
        return forced > 0 ? $"{s}  (dont {forced} forcé{(forced > 1 ? "s" : "")})" : s;
    }

    void ChooseTracks_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (TracksItem is not { Source: { } src } item)
        {
            SetIdle("Ajoutez d'abord un fichier : ses pistes sont lues pendant l'analyse.");
            return;
        }
        var others = _queue.Count(q => q != item && q.Source is not null && q.Status is not (QueueStatus.Done or QueueStatus.Encoding));
        var dlg = new TrackPickerWindow(item, _allAudio, _allSubs, _keepDefaultSub, others) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        _allAudio = dlg.AllAudio;
        _allSubs = dlg.AllSubs;
        _keepDefaultSub = dlg.KeepDefaultSub;
        // modèle repris par les fichiers ajoutés ensuite (épisodes suivants)
        _audioTemplate = TrackSelection.Capture(src.AudioTracks);
        _subTemplate = TrackSelection.Capture(src.SubtitleTracks);

        var msg = $"Pistes mises à jour pour {item.FileName}";
        if (dlg.ApplyToAll)
        {
            var (applied, byPosition) = ApplyTracksToQueue(item);
            msg = $"Pistes appliquées à {applied + 1} fichier(s)" +
                  (byPosition > 0 ? $" — {byPosition} par position (pistes différentes du modèle : vérifiez-les)" : "");
        }
        SetIdle(msg + ".");
        AssignDestinations(); // les langues gardées font partie du nom au format Radarr
        UpdatePreview();
        UpdateTracksSummary();
        SaveSettings();
    }

    /// <summary>Reporte la sélection de <paramref name="model"/> sur les autres fichiers de la file.</summary>
    (int Applied, int ByPosition) ApplyTracksToQueue(QueueItem model)
    {
        var audio = TrackSelection.Capture(model.Source!.AudioTracks);
        var subs = TrackSelection.Capture(model.Source.SubtitleTracks);
        int applied = 0, byPosition = 0;
        foreach (var q in _queue.Where(q => q != model && q.Source is not null && q.Status is not (QueueStatus.Done or QueueStatus.Encoding)))
        {
            var okA = TrackSelection.Apply(q.Source!.AudioTracks, audio);
            var okS = TrackSelection.Apply(q.Source.SubtitleTracks, subs);
            applied++;
            if ((!okA && q.Source.AudioTracks.Count > 0) || (!okS && q.Source.SubtitleTracks.Count > 0)) byPosition++;
        }
        return (applied, byPosition);
    }

    // ====================================================================== boutons

    async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        var items = _queue.Where(q => q.Status is not (QueueStatus.Done or QueueStatus.Encoding)).ToList();
        if (items.Count == 0) { SetIdle(_queue.Count == 0 ? "Ajoutez d'abord un film ou le dossier d'une série." : "Rien à analyser."); return; }
        await AnalyzeItemsAsync(items);
    }

    async void Encode_Click(object sender, RoutedEventArgs e) => await StartQueueAsync();

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is null) return;
        if (_encoding)
        {
            var remaining = _currentTotal - _currentIndex - 1;
            if (remaining > 0 && !_stopAfterCurrent)
            {
                var r = MessageBox.Show(this,
                    "Arrêter l'encodage ?\n\n" +
                    "Oui : arrêter maintenant (le fichier en cours est interrompu)\n" +
                    $"Non : terminer le fichier en cours, puis s'arrêter ({remaining} fichier(s) restant(s) non encodé(s))\n" +
                    "Annuler : continuer",
                    AppTitle, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.No) { _stopAfterCurrent = true; SetStatus($"{ItemPrefix()}l'encodage s'arrêtera après ce fichier."); }
                if (r != MessageBoxResult.Yes) return;
            }
            else if (MessageBox.Show(this, "Arrêter l'encodage en cours ?", AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
        }
        _cts?.Cancel();
    }

    void BrowseDst_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } it) return;
        var dlg = new SaveFileDialog
        {
            Title = "Fichier de destination",
            Filter = "Matroska (*.mkv)|*.mkv",
            DefaultExt = "mkv",
            OverwritePrompt = false,
        };
        try
        {
            var d = Path.GetDirectoryName(it.DestPath);
            if (Directory.Exists(d)) dlg.InitialDirectory = d;
            dlg.FileName = Path.GetFileName(it.DestPath);
        }
        catch { }
        if (dlg.ShowDialog(this) == true) DstBox.Text = dlg.FileName;
    }

    void CopyCmd_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(CmdBox.Text);
        SetStatus("Commande copiée dans le presse-papiers.");
    }

    void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } it) return;
        Clipboard.SetText((it.FileName + Environment.NewLine + string.Join(Environment.NewLine, it.Report.Select(r => r.ToString()))).Trim());
        SetStatus("Rapport copié dans le presse-papiers.");
    }

    void OpenOut_Click(object sender, RoutedEventArgs e)
    {
        var dst = Selected?.DestPath ?? _queue.FirstOrDefault()?.DestPath;
        if (string.IsNullOrEmpty(dst)) return;
        try
        {
            if (File.Exists(dst)) Process.Start("explorer.exe", $"/select,\"{dst}\"");
            else if (Path.GetDirectoryName(dst) is { } d && Directory.Exists(d)) Process.Start("explorer.exe", $"\"{d}\"");
            else MessageBox.Show(this, "Le dossier de sortie n'existe pas encore.", AppTitle);
        }
        catch { }
    }

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var log = Selected?.LogPath ?? _lastLogPath;
            if (log is not null && File.Exists(log)) Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
            else
            {
                Directory.CreateDirectory(AppSettings.LogDir);
                Process.Start("explorer.exe", $"\"{AppSettings.LogDir}\"");
            }
        }
        catch { }
    }

    // ====================================================================== glisser-déposer

    void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !_busy ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_busy || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        await AddPathsAsync(paths);
    }

    // ====================================================================== fermeture

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_encoding)
        {
            if (MessageBox.Show(this, "Un encodage est en cours. L'arrêter et quitter ?", AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            Native.AllowSleep();
        }
        _cts?.Cancel(); // tue HandBrake / ffmpeg en cours
        SaveSettings();
    }

    void SaveSettings()
    {
        if (SelectedEncoder is { } enc) _settings.Encoder = enc.Name;
        if (PresetBox.SelectedItem is string p) _settings.Preset = p;
        _settings.Quality = (int)QSlider.Value;
        _settings.CropMode = CropBox.SelectedIndex;
        _settings.AllAudio = _allAudio;
        _settings.AllSubs = _allSubs;
        _settings.KeepDefaultSub = _keepDefaultSub;
        _settings.Chapters = ChaptersChk.IsChecked == true;
        _settings.DeepCheck = DeepChk.IsChecked == true;
        _settings.LowPriority = LowPrioChk.IsChecked == true;
        _settings.Overwrite = OverwriteChk.IsChecked == true;
        _settings.ExtraArgs = ExtraBox.Text;
        _settings.Save();
    }
}
