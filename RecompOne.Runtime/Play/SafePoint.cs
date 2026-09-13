using System;
using System.Collections.Generic;

namespace RecompOne.Runtime.Play;

/// <summary>
/// The savestate safe-point gate, once, for every port.
///
/// A savestate may only be taken or applied at a frame where the recompiled game is at a
/// CONSISTENT CALL DEPTH — the same call path every time. This runtime has no CPU fetch loop to
/// redirect, so restoring RAM and GPRs does not change which native C# call is currently
/// executing: the code that was running keeps running, now over RAM that describes a different
/// moment. That mismatch is what produces the classic "loaded and went black" (PE1, loading
/// during the New Game opening) rather than a crash, and it is why the gate is a correctness
/// requirement and not a nicety.
///
/// PE1 and BFM each found their gate the hard way and then hand-wrote the same ~120 lines of
/// bookkeeping around it. Everything except the gate itself is identical, so it lives here:
/// pending-request stalling with a diagnosable reason, automatic capture at exactly the same
/// gate as F5, the manual save/load, the boot-load path consumed once, and the deliberately
/// unsafe Force Load. A port supplies <see cref="Gate"/> — one function — and optionally
/// <see cref="AfterLoad"/>.
///
/// Two behaviours here are load-bearing and were each paid for:
///   • DECLINE, never half-apply, and never drop. A request that cannot be applied stays
///     pending and lands the instant it is safe. Silently dropping it is what made F5 look
///     like it "did nothing".
///   • Say WHICH half of the gate is false. A stuck request with no reason is undiagnosable,
///     and refusals are the normal case during ordinary play (every cutscene, menu and load).
///
/// Finding a gate for a NEW port: set <see cref="Trace"/> (a port exposes it as --sptrace=N)
/// and play into steady gameplay. The hook is reached at only a handful of stack depths; the
/// one that keeps accumulating samples during gameplay while the others freeze IS the main
/// loop's path. Gate on "the game looks live AND SP has not moved for a few frames" and the
/// capturing path and the applying path are then provably the same path. That measurement is
/// how BFM's gate was chosen, and it beats guessing at globals.
/// </summary>
public static class SafePoint
{
    /// <summary>The port's answer to "is this frame safe?": "" for yes, otherwise the reason to
    /// log, phrased for a human reading a stalled request. Until a port sets this, savestates
    /// are refused with a message saying so — which is the correct state for a port in bring-up,
    /// not a bug.</summary>
    public static Func<string>? Gate;

    /// <summary>Port fixups applied immediately after a successful load, e.g. re-activating
    /// overlays mapped since the state was written, or stopping a stream the state does not
    /// contain. Runs for gated and forced loads alike.</summary>
    public static Action? AfterLoad;

    /// <summary>Extra per-frame context appended to log lines (scene id, frame, whatever the
    /// port would want to see next to a refusal). Optional.</summary>
    public static Func<string>? Describe;

    /// <summary>--sptrace=N: from this frame on, record the per-frame SP histogram. See the
    /// class note — this is the instrument for choosing a gate, not a debug counter.</summary>
    public static long Trace = -1;

    /// <summary>Frames SP must hold still before the depth counts as settled.</summary>
    public static int StableFrames = 8;

    public static uint LastSp { get; private set; }
    public static int SpStable { get; private set; }

    static long _pendingStallFrames;
    static readonly Dictionary<uint, int> _spHist = new();

    /// <summary>Call once per frame from the port's FrameHook, before anything else that might
    /// consume a pending request.</summary>
    public static void Tick(long frame)
    {
        var cpu = Runtime.Cpu;
        // SP stability is only meaningful as "the same place as last frame", so it is sampled
        // every frame whether or not anything is pending.
        if (cpu != null)
        {
            uint sp = cpu.SP;
            SpStable = sp == LastSp ? SpStable + 1 : 0;
            LastSp = sp;
            if (Trace >= 0 && frame >= Trace)
                _spHist[sp] = _spHist.TryGetValue(sp, out var n) ? n + 1 : 1;
        }

        bool manual = Savestate.SavePending || Savestate.LoadPending;
        bool auto = Savestate.AutoSavePath != null;
        if (manual || auto)
        {
            if (!IsSafe(out string why))
            {
                // An automatic refusal is the normal case and must not spam; a manual one is
                // someone waiting on a keypress, so it reports on its own schedule.
                if (auto) RecompOne.Runtime.Host.AutoState.NoteSkipped(why);
                if (manual && ++_pendingStallFrames % 120 == 1)
                    Log($"pending (save={Savestate.SavePending} load={Savestate.LoadPending}) " +
                        $"waiting for a safe frame: {why}");
            }
            else
            {
                _pendingStallFrames = 0;
                Apply(frame);
            }
        }
        else _pendingStallFrames = 0;

        ForceLoadTick(frame);
    }

    /// <summary>"" when this frame is safe, otherwise why not. Public so a port can show the
    /// same answer in a panel or a trace without duplicating the rule.</summary>
    public static bool IsSafe(out string why)
    {
        if (Runtime.Cpu == null) { why = "no cpu context"; return false; }
        if (RecompOne.Runtime.Interrupts.InDeliver) { why = "inside an interrupt handler"; return false; }
        if (Gate == null)
        {
            why = "this port has not supplied a savestate safe-point gate (Play.SafePoint.Gate)";
            return false;
        }
        string portWhy = Gate();
        if (portWhy.Length != 0) { why = portWhy; return false; }
        if (SpStable < StableFrames)
        {
            why = $"call depth not settled (sp=0x{LastSp:X8} stable {SpStable}/{StableFrames})";
            return false;
        }
        why = "";
        return true;
    }

    static void Apply(long frame)
    {
        // Automatic capture uses the SAME gate as F5 deliberately: a capture taken at an unsafe
        // depth produces exactly the unusable state this whole mechanism exists to prevent.
        if (Savestate.AutoSavePath is { } autoPath)
        {
            Savestate.AutoSavePath = null;
            RecompOne.Runtime.Host.AutoState.Capture(autoPath, Runtime.Cpu!);
        }

        if (Savestate.SavePending)
        {
            Savestate.SavePending = false;
            try
            {
                Savestate.SaveToFile(Savestate.SlotPath, Runtime.Cpu!);
                Log($"saved -> {Savestate.SlotPath} @frame {frame} sp=0x{LastSp:X8}");
            }
            catch (Exception ex) { Log($"save failed: {ex.Message}"); }
        }

        if (Savestate.LoadPending)
        {
            Savestate.LoadPending = false;
            DoLoad(frame, force: false);
        }
    }

    /// <summary>The Savestates panel's "Force Load": apply on the very next frame with NO gate.
    /// Kept separate and deliberately loud — this is the affordance that CAN land a load
    /// mid-cutscene and break the run, which is the point of having it. It reports what the gate
    /// WOULD have said, so a bad result twenty minutes later is traceable back to here.</summary>
    static void ForceLoadTick(long frame)
    {
        if (!Savestate.ForceLoadPending || Runtime.Cpu == null) return;
        Savestate.ForceLoadPending = false;
        IsSafe(out string why);
        Log($"FORCE LOAD (unsafe, user override) <- {Savestate.LoadPathPending} @frame {frame} " +
            $"gateWouldHaveSaid=\"{(why.Length == 0 ? "safe" : why)}\"");
        DoLoad(frame, force: true);
    }

    static void DoLoad(long frame, bool force)
    {
        // A boot load (--loadstate) reads BootLoadPath; F7 reads the user's slot. Consumed once,
        // here, and never written: a LOAD must not repoint the SAVE target. See the note on
        // Savestate.BootLoadPath for the data loss that made that separation necessary.
        string from = Savestate.LoadPathPending;
        Savestate.BootLoadPath = null;
        try
        {
            // TryLoad takes either on-disk format (plain savestate or bookmarks) and applies the
            // build-stamp policy, naming which half moved. It never touches the machine on a
            // refusal. `force` also overrides the stamp: being blocked by a second, quieter
            // check after arming Force Load would just look broken.
            if (!StateLoad.TryLoad(from, Runtime.Cpu!, out _, force))
            {
                Log($"load rejected <- {from} (see the previous line for why)");
                return;
            }
            AfterLoad?.Invoke();
            Log($"{(force ? "FORCE LOAD applied" : "loaded")} <- {from} @frame {frame} sp=0x{LastSp:X8}");
        }
        catch (Exception ex) { Log($"load failed: {ex.Message}"); }
    }

    /// <summary>Dump the SP histogram gathered under --sptrace, busiest depth first. This is the
    /// output a port reads to choose its gate.</summary>
    public static void DumpTrace()
    {
        if (_spHist.Count == 0) { Console.WriteLine("[safepoint] no SP samples (--sptrace not armed?)"); return; }
        var rows = new List<KeyValuePair<uint, int>>(_spHist);
        rows.Sort((a, b) => b.Value.CompareTo(a.Value));
        Console.WriteLine($"[safepoint] SP histogram ({rows.Count} distinct depths); the depth that keeps " +
                          "growing during steady gameplay is the main loop's path:");
        for (int i = 0; i < rows.Count && i < 12; i++)
            Console.WriteLine($"[safepoint]   sp=0x{rows[i].Key:X8}  {rows[i].Value} frames");
    }

    static void Log(string s) =>
        Console.WriteLine($"[savestate] {s}{(Describe is { } d ? "  " + d() : "")}");
}
