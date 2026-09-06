using System.Diagnostics;

namespace RecompOne.Runtime.Host;

/// <summary>"Reset Game" = relaunch the process with the same command-line arguments.
/// This is a clean PROCESS restart, not an in-process reset -- every HLE subsystem
/// (BIOS, SDK, CD, GPU/SPU state) re-initializes because it's a fresh process. A true
/// in-process reset would need each of those to expose its own re-init and is a much
/// larger change; this is the simple, honest version for "the port wedged, get me
/// back to boot" without alt-tabbing to relaunch by hand.</summary>
public static class ProcessRestart
{
    public static void Request()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var args = Environment.GetCommandLineArgs().Skip(1); // [0] is the exe path itself
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Host] restart failed: {e.Message}");
            return;
        }
        Runtime.Shutdown();
        Environment.Exit(0);
    }
}
