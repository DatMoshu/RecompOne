using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Play;

public enum StallKind { None, GameStuck, GameStopped }

// Unattended-run health check, default-off (On). TWO INDEPENDENT conditions, because
// they mean different things and want different responses:
//   - GameStopped: Runtime.FrameCount hasn't advanced in StopSeconds -> the port
//     itself has stopped presenting frames (wedged/dead). Not recoverable by more
//     input; this is an abort-and-report case.
//   - GameStuck: frames ARE presenting, but no registered probe's value has changed
//     in StuckSeconds -> the game is alive but the driver isn't making progress (a
//     menu it doesn't know how to dismiss, an in-game wedge). Potentially
//     recoverable — a script's OnStall can retry input before giving up.
// Checked separately so the log (and any recovery policy) can tell "running but
// stuck" from "stopped" apart, which for unattended/streaming runs is the
// difference between a recoverable retry and an abort.
public sealed class Watchdog
{
    public bool On;
    public double StuckSeconds = 20.0;
    public double StopSeconds = 10.0;
    public Action<StallKind, string>? OnStall;
    public StallKind Last { get; private set; }

    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    long _lastFrameCount = -1;
    double _lastFrameChangeAt;
    double _lastProbeChangeAt;
    readonly Dictionary<string, long> _lastNum = new();
    readonly Dictionary<string, bool> _lastFlag = new();
    bool _primed;

    public void Reset()
    {
        _primed = false;
        _lastNum.Clear();
        _lastFlag.Clear();
        Last = StallKind.None;
    }

    public void Tick(IMemory m)
    {
        if (!On) return;
        double now = _clock.Elapsed.TotalSeconds;
        long fc = Runtime.FrameCount;

        if (!_primed)
        {
            _lastFrameCount = fc; _lastFrameChangeAt = now; _lastProbeChangeAt = now; _primed = true;
            return;
        }

        if (fc != _lastFrameCount) { _lastFrameCount = fc; _lastFrameChangeAt = now; }
        else if (now - _lastFrameChangeAt >= StopSeconds)
        {
            Fire(StallKind.GameStopped, $"no presented frame in {now - _lastFrameChangeAt:F1}s (FrameCount stuck at {fc})", now);
            return; // stopped implies stuck too; report the more specific one only
        }

        bool changed = false;
        foreach (var name in Probes.NumNames)
        {
            long v = Probes.Num(name, m);
            if (_lastNum.TryGetValue(name, out var old) && old != v) changed = true;
            _lastNum[name] = v;
        }
        foreach (var name in Probes.FlagNames)
        {
            bool v = Probes.Flag(name, m);
            if (_lastFlag.TryGetValue(name, out var old) && old != v) changed = true;
            _lastFlag[name] = v;
        }

        if (changed) _lastProbeChangeAt = now;
        else if (now - _lastProbeChangeAt >= StuckSeconds)
            Fire(StallKind.GameStuck, $"no probe changed in {now - _lastProbeChangeAt:F1}s ({Probes.NumNames.Count + Probes.FlagNames.Count} probes watched)", now);
    }

    void Fire(StallKind kind, string msg, double now)
    {
        Last = kind;
        Console.WriteLine($"[watchdog] {kind}: {msg}");
        OnStall?.Invoke(kind, msg);
        // Don't refire every frame while the condition persists; a fresh Fire needs a
        // full window to elapse again.
        _lastProbeChangeAt = now;
        _lastFrameChangeAt = now;
    }
}
