using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using AV1Encod.Services;

namespace AV1Encod;

public enum QueueStatus { Pending, Analyzing, Ready, Warning, Error, Skipped, Encoding, Done, Failed, Cancelled }

/// <summary>Un fichier de la file d'attente (un film, ou un épisode de série).</summary>
public sealed class QueueItem : INotifyPropertyChanged
{
    static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    static readonly SolidColorBrush Gray = Frozen(107, 114, 128);
    static readonly SolidColorBrush Green = Frozen(22, 128, 61);
    static readonly SolidColorBrush Orange = Frozen(217, 119, 6);
    static readonly SolidColorBrush Red = Frozen(220, 38, 38);
    static readonly SolidColorBrush Blue = Frozen(37, 99, 235);

    public QueueItem(string path)
    {
        SourcePath = path;
        FileName = Path.GetFileName(path);
        try { Size = new FileInfo(path).Length; } catch { }
    }

    public string SourcePath { get; }
    public string FileName { get; }
    public long Size { get; }
    public string SizeText => Fmt.Size(Size);

    /// <summary>Rapport d'analyse puis de vérification de ce fichier.</summary>
    public ObservableCollection<ReportItem> Report { get; } = [];

    /// <summary>true tant que l'utilisateur n'a pas choisi lui-même la destination.</summary>
    public bool DestAuto { get; set; } = true;
    public string? LogPath { get; set; }
    /// <summary>Infos TVmaze de la série (si la recherche en ligne est activée).</summary>
    public SeriesMeta? Online { get; set; }

    string _dest = "";
    public string DestPath { get => _dest; set => Set(ref _dest, value); }

    SourceInfo? _source;
    public SourceInfo? Source
    {
        get => _source;
        set { Set(ref _source, value); Notify(nameof(DurationText)); Notify(nameof(Summary)); }
    }
    public string DurationText => Source is { Duration: > 0 } s ? Fmt.Dur(s.Duration) : "";
    public string Summary => Source?.Summary ?? "";

    QueueStatus _status;
    public QueueStatus Status { get => _status; private set { Set(ref _status, value); Notify(nameof(StatusBrush)); } }

    string _statusText = "En attente";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public void SetStatus(QueueStatus status, string text)
    {
        Status = status;
        StatusText = text;
    }

    public Brush StatusBrush => Status switch
    {
        QueueStatus.Ready or QueueStatus.Done => Green,
        QueueStatus.Warning => Orange,
        QueueStatus.Error or QueueStatus.Failed => Red,
        QueueStatus.Encoding or QueueStatus.Analyzing => Blue,
        _ => Gray,
    };

    /// <summary>Encodable : analysé sans erreur bloquante et pas encore traité.</summary>
    public bool IsRunnable => Status is QueueStatus.Ready or QueueStatus.Warning;

    double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    string _result = "";
    public string Result { get => _result; set => Set(ref _result, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Notify(name!);
    }
}
