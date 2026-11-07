using System.Diagnostics;
using System.Text;

namespace AV1Encod.Services;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

public static class ProcessRunner
{
    static ProcessStartInfo Psi(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Lance un processus et capture toute sa sortie.</summary>
    public static async Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        using var p = Process.Start(Psi(exe, args)) ?? throw new InvalidOperationException($"Impossible de lancer {exe}");
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(p);
            await p.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            timedOut = true;
        }
        return new ProcessResult(p.ExitCode, await outTask, await errTask, timedOut);
    }

    /// <summary>
    /// Lance un processus long en remontant chaque ligne (découpée sur \r ET \n, car HandBrake
    /// réécrit sa progression avec \r). Les callbacks sont appelés depuis un thread de fond.
    /// </summary>
    public static async Task<int> RunStreamingAsync(string exe, IEnumerable<string> args, Action<string> onOut, Action<string> onErr,
                                                    bool lowPriority, CancellationToken ct)
    {
        using var p = Process.Start(Psi(exe, args)) ?? throw new InvalidOperationException($"Impossible de lancer {exe}");
        if (lowPriority)
        {
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        }
        var tOut = PumpAsync(p.StandardOutput, onOut);
        var tErr = PumpAsync(p.StandardError, onErr);
        using (ct.Register(() => TryKill(p)))
        {
            await p.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(tOut, tErr);
        }
        return p.ExitCode;
    }

    static async Task PumpAsync(StreamReader sr, Action<string> onLine)
    {
        var buf = new char[8192];
        var sb = new StringBuilder();
        int n;
        while ((n = await sr.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < n; i++)
            {
                var c = buf[i];
                if (c is '\r' or '\n')
                {
                    if (sb.Length > 0) { onLine(sb.ToString()); sb.Clear(); }
                }
                else sb.Append(c);
            }
        }
        if (sb.Length > 0) onLine(sb.ToString());
    }

    static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }
}
