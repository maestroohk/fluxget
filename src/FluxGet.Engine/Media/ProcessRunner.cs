using System.Diagnostics;
using System.Text;

namespace FluxGet.Engine.Media;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, Action<string>? onLine, CancellationToken ct, bool lowPriority = false, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stdout) stdout.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr)
            {
                if (stderr.Length < 64 * 1024) stderr.AppendLine(e.Data);
            }
            onLine?.Invoke(e.Data);
        };
        process.Start();
        if (lowPriority)
        {
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) limit.CancelAfter(t);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            await process.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("The operation took too long.");
        }
        process.WaitForExit();
        lock (stdout) lock (stderr) return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
