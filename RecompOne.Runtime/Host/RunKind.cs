namespace RecompOne.Runtime.Host;

/// <summary>
/// Whether this process is a person playing or an agent driving, decided once from the
/// command line.
///
/// This was already being decided -- HostWindow.RunLabel has classified runs for the title
/// bar since the day two sessions confused each other's windows. It lives here now because
/// the answer turned out to matter for more than a caption: an agent run and a human run
/// share a game's output directory, so they also shared `autostates/`, and a handful of
/// short diagnostic runs could evict the states from an hour of real play. One definition,
/// two consumers, no chance of the title bar and the retention policy disagreeing about
/// whose session this is.
///
/// Read from <see cref="Environment.GetCommandLineArgs"/> rather than plumbed through each
/// port's Program.cs, so a game added later inherits it with no wiring.
///
/// FAILS SAFE TOWARD "AGENT": an unlabelled run carrying any self-driving flag is treated
/// as an agent. Misfiling an agent run as human would let automation prune the user's
/// states, which is the failure this class exists to prevent; misfiling a human run as an
/// agent only puts their autostates in the other folder, where they are still there.
/// </summary>
public static class RunKind
{
    /// <summary>Flags that mean nobody is holding the controller. Kept here rather than in
    /// the window so a headless run -- which never constructs a window at all -- still
    /// classifies correctly.</summary>
    public static readonly string[] AutomationFlags =
    [
        "--replay", "--automap", "--newgame", "--exit-at", "--padtest", "--btnsweep",
        "--autostart", "--autocross", "--savetest", "--snap", "--driveto", "--selftest",
        "--export-room", "--export-bg", "--export-accum", "--probe", "--warp",
    ];

    static bool? _isHuman;

    /// <summary>True when a person launched this window. <c>--human</c> asserts it outright
    /// and wins over the heuristic, because the admin launcher's Snaps button passes
    /// <c>--snap-window</c> and the flag scan would otherwise mislabel a window the user
    /// opened themselves.</summary>
    public static bool IsHuman
    {
        get
        {
            if (_isHuman is { } v) return v;
            bool human = true;
            try
            {
                var args = Environment.GetCommandLineArgs();
                foreach (var a in args)
                    if (a == "--human") { _isHuman = true; return true; }
                foreach (var a in args)
                {
                    if (a.StartsWith("--run-label=", StringComparison.Ordinal)) { human = false; break; }
                    bool automated = false;
                    foreach (var f in AutomationFlags)
                        if (a.StartsWith(f, StringComparison.Ordinal)) { automated = true; break; }
                    if (automated) { human = false; break; }
                }
            }
            catch { /* classification must never be the thing that stops a port booting */ }
            _isHuman = human;
            return human;
        }
    }

    /// <summary>"human" / "agent" -- for log lines and directory names.</summary>
    public static string Name => IsHuman ? "human" : "agent";
}
