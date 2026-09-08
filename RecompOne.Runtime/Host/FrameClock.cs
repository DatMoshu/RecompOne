using System.Diagnostics;

namespace RecompOne.Runtime.Host;

/// <summary>
/// Emulation pacing for every port. One presented frame is one PS1 vblank, so pacing the
/// presented-frame rate IS pacing emulated time: at Speed 1 the clock releases 60 frames a
/// second and the game runs at hardware speed; at Speed 2 it releases 120 and everything --
/// logic, animation, CD streaming, audio -- runs twice as fast.
///
/// Speed lives here rather than in a game host so it applies to every port with no wiring:
/// the only place any game reaches a presented frame is Runtime.PresentFrame, and the only
/// thing gating that is this class.
/// </summary>
public static class FrameClock
{
    const double FrameMs = 1000.0 / 60.0;

    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static double _nextFrameMs;

    /// <summary>Emulation speed multiplier at normal running speed. 1.0 = hardware (60
    /// presented frames a second). 0 (or negative) = uncapped: run as fast as the host can.</summary>
    public static double Speed = 1.0;

    /// <summary>Speed used while fast-forwarding (Tas.TurboActive). 0 = uncapped, which is
    /// what Tab/turbo has always meant; set it to a finite value for a bounded fast-forward
    /// that is still watchable.</summary>
    public static double TurboSpeed;

    /// <summary>Cap on how often the frame is actually drawn while running faster than real
    /// time. The emulation still advances every frame; only the redraw is skipped, which is
    /// what keeps a 10x fast-forward from becoming GPU-bound. Rendering is never skipped at
    /// normal speed.</summary>
    public static double MaxRenderHz = 60.0;

    /// <summary>The multiplier in force this frame.</summary>
    public static double Effective => Tas.TurboActive ? TurboSpeed : Speed;

    /// <summary>True when emulated time is running faster than hardware, by either route.
    /// Callers use it to decide whether to skip a redraw, not whether to skip a frame.</summary>
    public static bool FasterThanRealtime
    {
        get { double s = Effective; return s <= 0 || s > 1.0001; }
    }

    /// <summary>Print emulated fps to the console every N seconds (--fps-log[=N]). The title
    /// bar already shows it, but a headless or unattended run has no title bar to read, and
    /// "how fast does this actually go" is the first question a speed complaint asks.</summary>
    public static double FpsLogSeconds;

    static double _nextRenderMs;

    /// <summary>Should this frame be drawn? Always yes at normal speed; while fast-forwarding,
    /// yes at most MaxRenderHz times a second. Consumes the slot, so call it once per frame.</summary>
    public static bool ShouldRender()
    {
        if (!FasterThanRealtime) return true;
        double now = _clock.Elapsed.TotalMilliseconds;
        if (now < _nextRenderMs) return false;
        double step = MaxRenderHz > 0 ? 1000.0 / MaxRenderHz : 0;
        _nextRenderMs = (now > _nextRenderMs + step ? now : _nextRenderMs) + step;
        return true;
    }

    //maybe not the best but it seens to work for now
    public static void Throttle()
    {
        double speed = Effective;
        if (speed <= 0)
        {
            // Uncapped. Re-anchor rather than letting the accumulator fall arbitrarily far
            // behind, or the first throttled frame after returning to normal speed would be
            // handed a huge backlog and run flat out until it caught up.
            _nextFrameMs = _clock.Elapsed.TotalMilliseconds;
            return;
        }
        _nextFrameMs += FrameMs / speed;
        double now = _clock.Elapsed.TotalMilliseconds;
        double wait = _nextFrameMs - now;
        if (wait > 1) Thread.Sleep((int)wait);
        else if (wait < -100) _nextFrameMs = now;
    }

    /// <summary>
    /// Engine-level <c>--speed=</c> / <c>--turbo-speed=</c> / <c>--max-render-hz=</c>, read
    /// straight from the process command line for the same reason HostWindow.RunLabel is:
    /// every port -- including ones added later -- gets them with no per-game wiring and no
    /// chance of one silently lacking them. Accepts a multiplier ("2", "1.5") or the word
    /// "max"/"uncapped" for no limit.
    /// </summary>
    public static void ApplyCommandLine()
    {
        try
        {
            foreach (var a in Environment.GetCommandLineArgs())
            {
                if (a.StartsWith("--speed=", StringComparison.Ordinal)) Speed = Parse(a["--speed=".Length..], Speed);
                else if (a.StartsWith("--turbo-speed=", StringComparison.Ordinal)) TurboSpeed = Parse(a["--turbo-speed=".Length..], TurboSpeed);
                else if (a.StartsWith("--max-render-hz=", StringComparison.Ordinal)) MaxRenderHz = Parse(a["--max-render-hz=".Length..], MaxRenderHz);
                else if (a == "--fast-audio") Audio.MuteWhileFast = false; // keep (sped-up) sound while fast-forwarding
                else if (a == "--fps-log") FpsLogSeconds = 2.0;
                else if (a.StartsWith("--fps-log=", StringComparison.Ordinal)) FpsLogSeconds = Parse(a["--fps-log=".Length..], 2.0);
            }
        }
        catch { /* a malformed speed flag must never be the thing that stops a port booting */ }
    }

    static double Parse(string s, double fallback)
    {
        s = s.Trim().Trim('"');
        if (s is "max" or "uncapped" or "unlimited" or "0") return 0;
        return double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : fallback;
    }
}
