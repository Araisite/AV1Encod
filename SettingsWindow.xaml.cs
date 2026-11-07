using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AV1Encod.Services;

namespace AV1Encod;

public partial class SettingsWindow : Window
{
    public sealed record ToolRow(string Name, bool Found, string Detail)
    {
        static readonly Brush Green = Frozen(22, 128, 61), Red = Frozen(220, 38, 38);
        static SolidColorBrush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
        public string Symbol => Found ? "✔" : "✖";
        public Brush Brush => Found ? Green : Red;
    }

    readonly MainWindow _main;
    readonly AppSettings _settings;
    ReleaseInfo? _release;
    bool _ready;

    public SettingsWindow(MainWindow main, AppSettings settings, ReleaseInfo? knownRelease)
    {
        InitializeComponent();
        _main = main;
        _settings = settings;
        VersionText.Text = $"Version {Updater.CurrentVersion}";
        CopyrightText.Text = (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "© Araisite") + "   ·   Licence GNU GPL v3";
        var signer = Environment.ProcessPath is { } exe ? Authenticode.TrustedSigner(exe) : null;
        SignatureText.Text = signer is null ? "Exécutable non signé"
            : "Signé par " + (System.Text.RegularExpressions.Regex.Match(signer, "CN=([^,]+)") is { Success: true } m ? m.Groups[1].Value.Trim('"') : signer);
        AutoCheckChk.IsChecked = settings.CheckUpdatesAtStartup == true;
        MovieTemplateBox.Text = settings.MovieTemplate;
        SeriesTemplateBox.Text = settings.SeriesTemplate;
        AnimeTemplateBox.Text = settings.AnimeTemplate;
        OnlineChk.IsChecked = settings.OnlineLookup;
        TokensList.ItemsSource = OutputNaming.Tokens.Select(t => new { t.Token, t.Help }).ToList();
        (settings.NamingMode == "template" ? TemplateModeRb : ShortModeRb).IsChecked = true;
        TemplatePanel.IsEnabled = settings.NamingMode == "template";
        RefreshTools();
        if (knownRelease is not null) ShowRelease(knownRelease);
        _ready = true;
        UpdateNamingPreview();
    }

    // ------------------------------------------------------------------ nommage

    TextBox? _lastTemplateBox;

    void NamingMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.NamingMode = TemplateModeRb.IsChecked == true ? "template" : "short";
        TemplatePanel.IsEnabled = _settings.NamingMode == "template";
        SaveNaming();
    }

    void Template_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_ready) return;
        _settings.MovieTemplate = MovieTemplateBox.Text;
        _settings.SeriesTemplate = SeriesTemplateBox.Text;
        _settings.AnimeTemplate = AnimeTemplateBox.Text;
        SaveNaming();
    }

    void Online_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.OnlineLookup = OnlineChk.IsChecked == true;
        _settings.Save();
    }

    void SaveNaming()
    {
        _settings.Save();
        _main.OnNamingChanged();
        UpdateNamingPreview();
    }

    void Template_GotFocus(object sender, RoutedEventArgs e) => _lastTemplateBox = sender as TextBox;

    void InsertToken_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string token }) return;
        var box = _lastTemplateBox ?? MovieTemplateBox;
        var pos = box.CaretIndex;
        box.Text = box.Text.Insert(pos, token);
        box.CaretIndex = pos + token.Length;
        box.Focus();
    }

    void ResetTemplates_Click(object sender, RoutedEventArgs e)
    {
        MovieTemplateBox.Text = OutputNaming.DefaultMovieTemplate;
        SeriesTemplateBox.Text = OutputNaming.DefaultSeriesTemplate;
        AnimeTemplateBox.Text = OutputNaming.DefaultAnimeTemplate;
    }

    /// <summary>Aperçu sur un fichier analysé de la file (sinon sur un exemple fictif).</summary>
    void UpdateNamingPreview()
    {
        if (_main.CurrentEncoder is not { } enc) { MoviePreview.Text = SeriesPreview.Text = AnimePreview.Text = ""; return; }
        MoviePreview.Text = Preview(MovieTemplateBox.Text, NamingKind.Movie, enc);
        SeriesPreview.Text = Preview(SeriesTemplateBox.Text, NamingKind.Series, enc);
        AnimePreview.Text = Preview(AnimeTemplateBox.Text, NamingKind.Anime, enc);
    }

    string Preview(string template, NamingKind kind, EncoderInfo enc)
    {
        var sample = _main.NamingSample(kind);
        var ctx = sample is not null ? _main.NamingContextFor(sample) : OutputNaming.Sample(enc, kind);
        var name = OutputNaming.Render(template, ctx, out var unknown);
        var text = "→ " + (string.IsNullOrWhiteSpace(name) ? "(vide : le nom court sera utilisé)" : name + ".mkv");
        if (sample is null) text += "   (exemple)";
        if (unknown.Count > 0) text += "\n⚠ Jeton(s) inconnu(s) ignoré(s) : " + string.Join(", ", unknown.Distinct());
        return text;
    }

    void RefreshTools()
    {
        var rows = _main.ToolStatus().Select(t => new ToolRow(t.Name, t.Path is not null, t.Path is null ? "introuvable" : t.Detail)).ToList();
        ToolsList.ItemsSource = rows;
        var missing = rows.Count(r => !r.Found);
        InstallBtn.IsEnabled = missing > 0;
        InstallBtn.Content = missing > 0 ? "Installer les outils manquants" : "Tous les outils sont installés";
    }

    // ------------------------------------------------------------------ mises à jour

    async void Check_Click(object sender, RoutedEventArgs e)
    {
        CheckBtn.IsEnabled = false;
        UpdateStatus.Text = "Recherche en cours…";
        try
        {
            var r = await Updater.GetLatestAsync();
            if (r is null) { UpdateStatus.Text = "Aucune version publiée pour le moment."; return; }
            ShowRelease(r);
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "Impossible de vérifier les mises à jour (connexion internet ?) : " + ex.Message;
        }
        finally { CheckBtn.IsEnabled = true; }
    }

    void ShowRelease(ReleaseInfo r)
    {
        _release = r;
        var newer = Updater.IsNewer(r);
        UpdateStatus.Text = newer
            ? $"Nouvelle version disponible : {r.Version} (vous avez la {Updater.CurrentVersion})."
            : $"Vous avez la dernière version ({Updater.CurrentVersion}).";
        UpdateBtn.Visibility = newer ? Visibility.Visible : Visibility.Collapsed;
        UpdateBtn.IsEnabled = r.AssetUrl is not null;
        NotesText.Text = string.IsNullOrWhiteSpace(r.Notes) ? r.Name : r.Notes.Trim();
        NotesBox.Visibility = newer ? Visibility.Visible : Visibility.Collapsed;
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_release is null) return;
        UpdateBtn.IsEnabled = CheckBtn.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateStatus.Text = $"Téléchargement de la version {_release.Version}…";
        try
        {
            await _main.ApplyUpdateAsync(_release, new Progress<double>(p => UpdateProgress.Value = p));
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "La mise à jour a échoué : " + ex.Message;
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateBtn.IsEnabled = CheckBtn.IsEnabled = true;
        }
    }

    void AutoCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.CheckUpdatesAtStartup = AutoCheckChk.IsChecked == true;
        _settings.Save();
    }

    // ------------------------------------------------------------------ outils

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        InstallBtn.IsEnabled = false;
        InstallStatus.Visibility = Visibility.Visible;
        var ok = await _main.InstallMissingToolsAsync(s => InstallStatus.Text = s);
        InstallStatus.Text = ok ? "Outils installés." : InstallStatus.Text;
        RefreshTools();
    }

    void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(AppSettings.LogDir);
    void OpenSettingsDir_Click(object sender, RoutedEventArgs e) => OpenFolder(AppSettings.AppDir);

    static void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start("explorer.exe", $"\"{dir}\"");
        }
        catch { }
    }

    void OpenRepo_Click(object sender, RoutedEventArgs e) => Updater.OpenUrl(Updater.RepoUrl);
    void OpenReleases_Click(object sender, RoutedEventArgs e) => Updater.OpenUrl(Updater.ReleasesUrl);
    void OpenIssues_Click(object sender, RoutedEventArgs e) => Updater.OpenUrl(Updater.RepoUrl + "/issues");
    void OpenLicense_Click(object sender, RoutedEventArgs e) => Updater.OpenUrl(Updater.RepoUrl + "/blob/main/LICENSE");
    void OpenPrivacy_Click(object sender, RoutedEventArgs e) => Updater.OpenUrl(Updater.RepoUrl + "#confidentialité");
    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
