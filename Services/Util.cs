using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AV1Encod.Services;

public static class Fmt
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Size(double b) =>
        b >= 1L << 40 ? $"{b / (1L << 40):N2} To" :
        b >= 1 << 30 ? $"{b / (1 << 30):N2} Go" :
        b >= 1 << 20 ? $"{b / (1 << 20):N1} Mo" :
        $"{b / 1024:N0} Ko";

    public static string Dur(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    public static double Num(string? s) =>
        double.TryParse(s, NumberStyles.Float, Inv, out var d) ? d : 0;

    /// <summary>"24000/1001" → 23.976</summary>
    public static double Rate(string? r)
    {
        if (string.IsNullOrEmpty(r)) return 0;
        var parts = r.Split('/');
        if (parts.Length == 2)
        {
            var den = Num(parts[1]);
            return den == 0 ? 0 : Num(parts[0]) / den;
        }
        return Num(r);
    }

    /// <summary>"02:25:00.650000000" → secondes</summary>
    public static double Hms(string? s)
    {
        var m = Regex.Match(s ?? "", @"^(\d+):(\d+):(\d+(?:\.\d+)?)");
        return m.Success ? int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + Num(m.Groups[3].Value) : 0;
    }

    public static string[] Lines(string? text) =>
        (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Where(l => l.Trim().Length > 0).ToArray();
}

/// <summary>Accès tolérant au JSON de ffprobe (nombres parfois en chaîne, champs optionnels).</summary>
public static class Json
{
    public static JsonElement? P(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : null;

    public static string? S(this JsonElement e, string name)
    {
        if (e.P(name) is not { } v) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => null,
        };
    }

    public static double D(this JsonElement e, string name) => Fmt.Num(e.S(name));
    public static int I(this JsonElement e, string name) => (int)e.D(name);
    public static string? Tag(this JsonElement e, string name) => e.P("tags")?.S(name);
    public static int Disp(this JsonElement e, string name) => e.P("disposition")?.I(name) ?? 0;

    public static IEnumerable<JsonElement> A(this JsonElement e, string name) =>
        e.P(name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];

    public static double Bitrate(this JsonElement s) =>
        Fmt.Num(s.S("bit_rate") ?? s.Tag("BPS") ?? s.Tag("BPS-eng"));
}

public static class Native
{
    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);

    /// <summary>Empêche la mise en veille pendant l'encodage (ES_CONTINUOUS | ES_SYSTEM_REQUIRED).</summary>
    public static void KeepAwake() => SetThreadExecutionState(0x80000001);
    public static void AllowSleep() => SetThreadExecutionState(0x80000000);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetDiskFreeSpaceEx(string dir, out ulong freeAvail, out ulong total, out ulong totalFree);

    /// <summary>Espace libre du volume contenant <paramref name="path"/> (marche aussi en UNC), -1 si inconnu.</summary>
    public static long FreeSpace(string path)
    {
        try
        {
            string? d = path;
            while (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) d = Path.GetDirectoryName(d);
            if (string.IsNullOrEmpty(d)) return -1;
            return GetDiskFreeSpaceEx(d, out var a, out _, out _) ? (long)a : -1;
        }
        catch { return -1; }
    }

    /// <summary>Noms des cartes graphiques installées (registre, sans WMI).</summary>
    public static IReadOnlyList<string> GpuNames()
    {
        var names = new List<string>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return names;
            foreach (var sub in cls.GetSubKeyNames().Where(n => Regex.IsMatch(n, @"^\d{4}$")))
            {
                try
                {
                    using var k = cls.OpenSubKey(sub);
                    if (k?.GetValue("DriverDesc") is string desc && !desc.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                        && !names.Contains(desc))
                        names.Add(desc);
                }
                catch { }
            }
        }
        catch { }
        return names;
    }
}
