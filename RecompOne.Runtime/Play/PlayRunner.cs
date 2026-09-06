using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Play;

// Executes one scripted play run: a queue of PlayStep, ticked once per presented
// frame from Play.Tick (see Runtime.PresentFrame for the ordering guarantee). Writes
// Controller.State exactly like the existing debug drivers (AutoMap/Gizmo) already
// do — no CPU/dispatcher/PSMemory access, so this stays a pure input-and-probe layer.
public sealed class PlayRunner
{
    public readonly string Name;
    readonly List<PlayStep> _steps;
    readonly PlayContext _ctx;
    int _idx;
    int _stepFrame;
    long _frame;
    long _changeBaseline;
    bool _haveBaseline;
    readonly string? _resumeBookmarkPath;
    bool _resumeAttempted;
    int _resumeWaitFrames;

    public bool Done { get; private set; }
    public bool Aborted { get; private set; }
    public string CurrentGoal => Done ? "(done)" : Aborted ? "(aborted)" : _idx < _steps.Count ? Describe(_steps[_idx]) : "(idle)";
    public string LastAction { get; private set; } = "";

    readonly List<string> _log = new();
    public IReadOnlyList<string> Log => _log;

    static string Describe(PlayStep s) => s.Label.Length > 0 ? s.Label : s.Kind.ToString();

    // `resumeBookmarkPath`: if a valid (version-matching) bookmark exists there when
    // this runner first ticks, load it and skip straight past every step up to and
    // including the Bookmark step whose name matches the one the file was saved under
    // — resuming a script rather than re-driving from boot. A version mismatch or
    // missing file (Bookmarks.TryLoad's job) just means run the script from the top.
    public PlayRunner(string name, IEnumerable<PlayStep> steps, string? resumeBookmarkPath = null)
    {
        Name = name;
        _steps = new List<PlayStep>(steps);
        _resumeBookmarkPath = resumeBookmarkPath;
        _ctx = new PlayContext(this);
    }

    public void Abort(string reason)
    {
        if (Done || Aborted) return;
        Aborted = true;
        Controller.State = 0xFFFF;
        Host.Tas.PlayForceTurbo = false; // never leave turbo engaged past this runner's lifetime
        LogLine($"ABORT: {reason}");
    }

    void LogLine(string s)
    {
        s = $"f{_frame} #{_idx} {s}";
        _log.Add(s);
        if (_log.Count > 2000) _log.RemoveAt(0);
        LastAction = s;
        Console.WriteLine($"[play:{Name}] {s}");
    }

    void Advance(string why)
    {
        LogLine($"{Describe(_steps[_idx])} -> {why}");
        _idx++;
        _stepFrame = 0;
        _haveBaseline = false;
    }

    void Timeout(PlayStep step)
    {
        LogLine($"TIMEOUT after {step.TimeoutFrames}f: {Describe(step)}");
        if (step.OnTimeout != null)
        {
            _ctx.StepIndex = _idx; _ctx.StepFrame = _stepFrame; _ctx.Frame = _frame;
            step.OnTimeout(_ctx);
        }
        else
        {
            Abort($"unhandled timeout at step {_idx} ({step.Label})");
        }
        // A timeout handler that neither aborted, finished, nor jumped elsewhere would
        // otherwise refire every remaining frame of this same step — skip it instead.
        if (!Aborted && !Done && _idx < _steps.Count && ReferenceEquals(_steps[_idx], step))
            Advance("timeout (no recovery action taken, skipping)");
    }

    // Runner is constructed (Play.Start) before the game's CpuContext necessarily
    // exists (Program.cs typically calls Start before Recompiled.Entry.Run), so this
    // can't run at construction time — it runs once, lazily, on the first real Tick.
    void TryResumeFromBookmark()
    {
        if (_resumeAttempted || _resumeBookmarkPath == null) return;
        if (Runtime.Cpu is not { } cpu)
        {
            if (++_resumeWaitFrames < 600) return; // give the game a few seconds to spin up
            _resumeAttempted = true;
            LogLine("resume: gave up waiting for CpuContext, driving from boot");
            return;
        }
        _resumeAttempted = true;
        if (!Bookmarks.TryLoad(_resumeBookmarkPath, cpu, out var milestone)) return; // logged inside; run from the top

        // A bookmark loads DURING the boot-driving phase, long before something like
        // Runtime.FastStart's own front-end scene check would ever see its "committed"
        // state and self-clear — left engaged, it would keep forcing Start/Up/Cross
        // input into a live gameplay scene forever. Any successful load implies we're
        // past the front-end, so this responsibility belongs here, generically.
        Runtime.FastStart = false;

        int found = _steps.FindIndex(s => s.Kind == PlayStepKind.Bookmark && s.BookmarkName == milestone);
        if (found < 0)
        {
            LogLine($"resume: loaded bookmark '{milestone}' but no matching Bookmark step in this script — running from the top on top of the loaded state");
            return;
        }
        _idx = found + 1;
        _stepFrame = 0; _haveBaseline = false;
        LogLine($"resume: skipped to step {_idx} (past bookmark '{milestone}')");
    }

    public void Tick(IMemory m)
    {
        if (Done || Aborted) return;
        TryResumeFromBookmark();
        _frame++;
        if (_idx >= _steps.Count)
        {
            Done = true;
            Controller.State = 0xFFFF;
            Host.Tas.PlayForceTurbo = false;
            LogLine("script complete");
            return;
        }
        _stepFrame++;
        var step = _steps[_idx];
        switch (step.Kind)
        {
            case PlayStepKind.Press:
                Controller.State = (ushort)~step.Button;
                if (_stepFrame >= step.Frames) { Controller.State = 0xFFFF; Advance("held"); }
                break;

            case PlayStepKind.Hold:
                Controller.State = (ushort)~step.Button;
                Advance("set"); // one-shot: state persists until the next step changes it
                break;

            case PlayStepKind.Release:
                Controller.State = 0xFFFF;
                Advance("released");
                break;

            case PlayStepKind.Wait:
                if (_stepFrame >= step.Frames) Advance("elapsed");
                break;

            case PlayStepKind.WaitUntil:
                if (step.Predicate!(m)) Advance("predicate true");
                else if (_stepFrame >= step.TimeoutFrames) Timeout(step);
                break;

            case PlayStepKind.PressUntil:
                {
                    int cyc = _stepFrame % step.Interval;
                    int attempt = _stepFrame / step.Interval;
                    var buttons = step.Buttons!;
                    ushort btn = buttons[(attempt / step.SwitchAfter) % buttons.Length];
                    if (cyc == 0 && attempt > 0 && attempt % step.SwitchAfter == 0 && buttons.Length > 1)
                        LogLine($"no progress after {attempt} attempts, switching to next input in sequence (now 0x{btn:X4})");
                    Controller.State = cyc < step.Frames ? (ushort)~btn : (ushort)0xFFFF;
                    if (step.Predicate!(m)) { Controller.State = 0xFFFF; Advance("predicate true"); }
                    else if (_stepFrame >= step.TimeoutFrames) Timeout(step);
                }
                break;

            case PlayStepKind.WaitForChange:
                if (!_haveBaseline) { _changeBaseline = step.ChangeProbe!(m); _haveBaseline = true; }
                else if (step.ChangeProbe!(m) != _changeBaseline) Advance("changed");
                else if (_stepFrame >= step.TimeoutFrames) Timeout(step);
                break;

            case PlayStepKind.Custom:
                _ctx.StepIndex = _idx; _ctx.StepFrame = _stepFrame; _ctx.Frame = _frame;
                if (step.Tick!(_ctx, m)) Advance("custom done");
                else if (_stepFrame >= step.TimeoutFrames) Timeout(step);
                break;

            case PlayStepKind.FastForward:
                Host.Tas.PlayForceTurbo = !step.RenderWhileFastForwarding;
                if (step.Predicate!(m)) { Host.Tas.PlayForceTurbo = false; Advance("predicate true"); }
                else if (_stepFrame >= step.TimeoutFrames) { Host.Tas.PlayForceTurbo = false; Timeout(step); }
                break;

            case PlayStepKind.Bookmark:
                bool safe = step.SafeProbe == null || Probes.Flag(step.SafeProbe, m);
                if (safe && Runtime.Cpu is { } cpu)
                {
                    Bookmarks.Save(step.BookmarkPath!, cpu, step.BookmarkName!);
                    Advance("saved");
                }
                else if (_stepFrame >= step.TimeoutFrames) Timeout(step);
                break;
        }
    }
}
