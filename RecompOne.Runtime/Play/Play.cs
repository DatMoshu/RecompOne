using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Play;

// Static facade wired into Runtime.PresentFrame (see the call-site comment there
// for the ordering guarantee vs. FrameHook). Default-off: `On` stays false for
// every game unless something calls Play.Start, so PE1 and every other port stay
// byte-identical when nothing is driving.
public static class Play
{
    public static bool On { get; private set; }
    public static PlayRunner? Current { get; private set; }
    public static readonly Watchdog Watchdog = new();

    public static string CurrentGoal => Current?.CurrentGoal ?? "(idle)";
    public static string LastAction => Current?.LastAction ?? "";
    public static bool Done => Current?.Done ?? false;
    public static bool Aborted => Current?.Aborted ?? false;

    public static void Start(string name, IEnumerable<PlayStep> steps, string? resumeBookmarkPath = null)
    {
        Current = new PlayRunner(name, steps, resumeBookmarkPath);
        Watchdog.Reset();
        On = true;
    }

    public static void Stop()
    {
        On = false;
        Host.Tas.PlayForceTurbo = false; // don't leave turbo engaged if stopped mid-FastForward
        // Current is left readable (Done/Aborted/Log) for whoever asked for the run.
    }

    public static void Tick(IMemory m)
    {
        if (!On || Current == null) return;
        Current.Tick(m);
        Watchdog.Tick(m);
        if (Current.Done || Current.Aborted) On = false; // hand control back
    }
}
