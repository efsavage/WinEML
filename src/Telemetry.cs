using System.Diagnostics;

namespace WinEML;

/// <summary>
/// Opt-in phase timing for benchmarking. Every mark records milliseconds since
/// the OS process start (so it includes runtime init, not just our own code).
///   WINEML_BENCH=1  → log phases AND auto-exit once the view is rendered
///                     (lets a harness loop many runs).
///   WINEML_DEBUG=1  → log phases only.
/// Output: %TEMP%\WinEML\bench.log  as  "pid<TAB>ms<TAB>phase<TAB>extra".
/// </summary>
internal static class Telemetry
{
    public static readonly bool Bench =
        Environment.GetEnvironmentVariable("WINEML_BENCH") is "1" or "true";
    public static readonly bool Enabled =
        Bench || Environment.GetEnvironmentVariable("WINEML_DEBUG") is "1" or "true";

    private static readonly DateTime ProcStartUtc = GetProcStart();
    private static readonly int Pid = Environment.ProcessId;

    private static DateTime GetProcStart()
    {
        try { return Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
        catch { return DateTime.UtcNow; }
    }

    public static void Mark(string phase, string extra = "")
    {
        if (!Enabled) return;
        double ms = (DateTime.UtcNow - ProcStartUtc).TotalMilliseconds;
        try
        {
            string log = Path.Combine(Path.GetTempPath(), "WinEML", "bench.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"{Pid}\t{ms:F1}\t{phase}\t{extra}\n");
        }
        catch { /* never let telemetry break the app */ }
    }
}
