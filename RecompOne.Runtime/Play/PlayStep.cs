using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Play;

public enum PlayStepKind { Press, Hold, Release, Wait, WaitUntil, PressUntil, WaitForChange, Custom, FastForward, Bookmark }

// One step of a scripted play run, built via the static factories below. Every
// primitive that can stall (WaitUntil / PressUntil / WaitForChange / Custom) takes
// a MANDATORY timeout — unbounded waits are exactly what has wedged sessions
// driving this game today, so the primitive enforces the discipline instead of
// trusting each caller to remember it.
public sealed class PlayStep
{
    public PlayStepKind Kind;
    public string Label = "";
    public ushort Button;                          // active-low bitmask (Hardware.Controller.*)
    public ushort[]? Buttons;                       // PressUntil: cycled button sequence (see PressUntil below)
    public int SwitchAfter;                         // PressUntil: attempts on one button before trying the next
    public int Frames;                              // Press: hold length. PressUntil: tap length per cycle.
    public int Interval;                            // PressUntil: frames between tap starts (>= Frames)
    public int TimeoutFrames;                       // required for WaitUntil/PressUntil/WaitForChange/Custom
    public Func<IMemory, bool>? Predicate;          // WaitUntil / PressUntil stop condition
    public Func<IMemory, long>? ChangeProbe;        // WaitForChange source (baselined on step entry)
    public Func<PlayContext, IMemory, bool>? Tick;  // Custom: return true when done; owns Controller.State itself
    public Action<PlayContext>? OnTimeout;          // optional: log/abort/insert-recovery-steps
    public bool RenderWhileFastForwarding;           // FastForward: keep presenting frames (slower, but visible)
    public string? BookmarkName;                     // Bookmark: milestone name (also the resume key)
    public string? BookmarkPath;                     // Bookmark: file to save to
    public string? SafeProbe;                        // Bookmark: named flag probe gating when a save is safe (null = always)

    public static PlayStep Press(ushort button, int frames, string label = "") =>
        new() { Kind = PlayStepKind.Press, Button = button, Frames = Math.Max(1, frames), Label = label };

    public static PlayStep Hold(ushort button, string label = "") =>
        new() { Kind = PlayStepKind.Hold, Button = button, Label = label };

    public static PlayStep Release(string label = "") =>
        new() { Kind = PlayStepKind.Release, Label = label };

    public static PlayStep Wait(int frames, string label = "") =>
        new() { Kind = PlayStepKind.Wait, Frames = Math.Max(1, frames), Label = label };

    public static PlayStep WaitUntil(Func<IMemory, bool> predicate, int timeoutFrames, string label = "", Action<PlayContext>? onTimeout = null) =>
        new() { Kind = PlayStepKind.WaitUntil, Predicate = predicate, TimeoutFrames = timeoutFrames, Label = label, OnTimeout = onTimeout };

    // Taps `button` for `tapFrames` every `interval` frames until `predicate` is true or timeout.
    // Frame-count scripting for menus/dialogue is brittle (load-time and CD-timing variance);
    // this keeps tapping until the state actually changes instead of guessing how long to wait.
    public static PlayStep PressUntil(ushort button, int tapFrames, int interval, Func<IMemory, bool> predicate, int timeoutFrames, string label = "", Action<PlayContext>? onTimeout = null) =>
        PressUntil(new[] { button }, tapFrames, interval, predicate, timeoutFrames, switchAfter: int.MaxValue, label, onTimeout);

    // Same, but cycles through `buttons` after `switchAfter` unsuccessful taps of the
    // current one instead of hammering a single button forever. A dialogue/menu advance
    // should NOT assume one button dismisses everything — a choice prompt needs a
    // direction, not just a confirm, and a script that only taps Cross can sit forever
    // on exactly that kind of prompt (observed independently on two different ports).
    // This belongs in the primitive rather than duplicated per-game logic.
    public static PlayStep PressUntil(ushort[] buttons, int tapFrames, int interval, Func<IMemory, bool> predicate, int timeoutFrames, int switchAfter = 4, string label = "", Action<PlayContext>? onTimeout = null) =>
        new() { Kind = PlayStepKind.PressUntil, Buttons = buttons.Length > 0 ? buttons : new ushort[] { 0 }, SwitchAfter = Math.Max(1, switchAfter), Frames = Math.Max(1, tapFrames), Interval = Math.Max(Math.Max(1, tapFrames), interval), Predicate = predicate, TimeoutFrames = timeoutFrames, Label = label, OnTimeout = onTimeout };

    // Snapshots `probe` on entry, advances the instant it reads something different —
    // for a scene-id transition or similar where the target value isn't known up front.
    public static PlayStep WaitForChange(Func<IMemory, long> probe, int timeoutFrames, string label = "", Action<PlayContext>? onTimeout = null) =>
        new() { Kind = PlayStepKind.WaitForChange, ChangeProbe = probe, TimeoutFrames = timeoutFrames, Label = label, OnTimeout = onTimeout };

    // Escape hatch for game-specific multi-frame logic that doesn't fit the generic
    // primitives (e.g. PE1's per-room player-actor self-detection: hold each D-Pad
    // direction in turn and diff every actor's position, whichever one moves is the
    // player). The callback owns Controller.State for the duration and returns true
    // once its goal is met; the engine never sees why.
    public static PlayStep Custom(Func<PlayContext, IMemory, bool> tick, int timeoutFrames, string label = "", Action<PlayContext>? onTimeout = null) =>
        new() { Kind = PlayStepKind.Custom, Tick = tick, TimeoutFrames = timeoutFrames, Label = label, OnTimeout = onTimeout };

    // Fast-forwards (Host.Tas.PlayForceTurbo — skips rendering/audio/frame-pacing, see
    // Runtime.PresentFrame) until `until` is true or the timeout expires, then restores
    // normal speed regardless of outcome — so a script can drop back to real-time
    // rendering for the part actually under test, the same pattern AutoMap already uses
    // (it drops turbo for its last settle frames so a snapshot has something rendered).
    // `render: true` skips engaging turbo at all (equivalent to WaitUntil) for a step
    // that specifically needs to see frames drawn while it waits.
    public static PlayStep FastForward(Func<IMemory, bool> until, int timeoutFrames, bool render = false, string label = "", Action<PlayContext>? onTimeout = null) =>
        new() { Kind = PlayStepKind.FastForward, Predicate = until, TimeoutFrames = timeoutFrames, RenderWhileFastForwarding = render, Label = label, OnTimeout = onTimeout };

    // Snapshots the machine to `path` under the name `milestone` once it's safe to do
    // so, then advances immediately (one-shot, like Hold). `milestone` doubles as the
    // resume key: PlayRunner's resumeBookmarkPath, if given, looks for a Bookmark step
    // with a matching name to know where in the script a restored run should continue.
    //
    // `safeProbe` names a registered flag probe (Play/Probes.cs) the game uses to say
    // "this frame's call depth is a valid save point" — the same idea as the existing
    // gameplay-phase-0 gate that already protects manual F5/F7 saves, exposed through
    // the probe registry instead of the engine guessing or duplicating that logic. Pass
    // null only if every frame this step can be reached on is known to be safe.
    public static PlayStep Bookmark(string milestone, string path, string? safeProbe, int timeoutFrames = 1800, string label = "") =>
        new() { Kind = PlayStepKind.Bookmark, BookmarkName = milestone, BookmarkPath = path, SafeProbe = safeProbe, TimeoutFrames = timeoutFrames, Label = label.Length > 0 ? label : $"bookmark '{milestone}'" };
}

// Passed to OnTimeout callbacks and Custom step ticks: run state a game-specific
// callback can act on (abort, or just read StepFrame to phase its own sub-cycle)
// without the engine knowing anything about why.
public sealed class PlayContext
{
    public PlayRunner Runner { get; }
    public int StepIndex { get; internal set; }
    public int StepFrame { get; internal set; }
    public long Frame { get; internal set; }
    internal PlayContext(PlayRunner runner) => Runner = runner;
}
