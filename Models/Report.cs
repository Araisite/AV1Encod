using System.Windows;
using System.Windows.Media;

namespace AV1Encod;

public enum Level { Ok, Warn, Error, Info, Head }

public sealed class ReportItem(Level level, string text)
{
    static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    static readonly SolidColorBrush OkBrush = Frozen(22, 128, 61);
    static readonly SolidColorBrush WarnBrush = Frozen(217, 119, 6);
    static readonly SolidColorBrush ErrBrush = Frozen(220, 38, 38);
    static readonly SolidColorBrush InfoBrush = Frozen(107, 114, 128);
    static readonly SolidColorBrush HeadBrush = Frozen(30, 64, 175);
    static readonly SolidColorBrush TextDark = Frozen(31, 41, 55);

    public Level Level { get; } = level;
    public string Text { get; } = text;

    public string Symbol => Level switch { Level.Ok => "✔", Level.Warn => "⚠", Level.Error => "✖", _ => "" };
    public Brush SymbolBrush => Level switch { Level.Ok => OkBrush, Level.Warn => WarnBrush, Level.Error => ErrBrush, _ => InfoBrush };
    public Brush TextBrush => Level switch { Level.Head => HeadBrush, Level.Info => InfoBrush, Level.Error => ErrBrush, _ => TextDark };
    public FontWeight Weight => Level == Level.Head ? FontWeights.SemiBold : FontWeights.Normal;
    public double Size => Level == Level.Head ? 14.5 : 13;
    public Thickness Margin => Level == Level.Head ? new Thickness(0, 14, 0, 4) : new Thickness(0, 1, 0, 1);
    public int TextColumn => Level == Level.Head ? 0 : 1;
    public int TextSpan => Level == Level.Head ? 2 : 1;

    public override string ToString() => Level switch
    {
        Level.Head => $"\n== {Text} ==",
        Level.Info => $"   {Text}",
        _ => $"{Symbol}  {Text}",
    };
}

/// <summary>Accumule les lignes du rapport et compte erreurs / avertissements.</summary>
public sealed class Reporter(IList<ReportItem> items)
{
    public int Errors { get; private set; }
    public int Warnings { get; private set; }

    /// <summary>Si non nul, chaque ligne y est aussi recopiée (mise en cache de la vérif. approfondie).</summary>
    public List<(Level, string)>? Capture { get; set; }

    public void Add(Level level, string text)
    {
        if (level == Level.Error) Errors++;
        else if (level == Level.Warn) Warnings++;
        Capture?.Add((level, text));
        items.Add(new ReportItem(level, text));
    }

    public void Ok(string t) => Add(Level.Ok, t);
    public void Warn(string t) => Add(Level.Warn, t);
    public void Error(string t) => Add(Level.Error, t);
    public void Info(string t) => Add(Level.Info, t);
    public void Head(string t) => Add(Level.Head, t);
}
