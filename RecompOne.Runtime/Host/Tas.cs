using RecompOne.Runtime.Hardware;

namespace RecompOne.Runtime.Host;

// TAS-style input record/replay. Records the physical controller state each
// presented frame (only when it changes, so the file stays compact) and can
// replay that stream deterministically to reproduce a crash or drive the game
// unattended. Record & replay share one presented-frame counter as their clock,
// so alignment holds as long as the run presents the same number of frames.
public static class Tas
{
    public static bool Recording;
    public static bool Playing;
    public static long Frame;                 // presented-frame counter (record + replay clock)

    // Turbo fast-forward: while replaying, skip rendering/audio/frame-pacing so the
    // recorded prefix blasts past in ~seconds, then normal speed resumes the instant
    // the input stream is exhausted (Playing -> false). Optional TurboUntil stops the
    // fast-forward early (at a chosen frame) so the tail plays at real speed.
    public static bool Turbo;
    public static long TurboUntil = long.MaxValue;
    public static bool ForceTurbo;   // fast-forward regardless of replay state (PE1's AutoMap room-mapper)
    // Separate flag for RecompOne.Runtime.Play's FastForward primitive. AutoMap.Tick
    // unconditionally OVERWRITES ForceTurbo every frame (`Tas.ForceTurbo = <expr>`), so
    // sharing one boolean would mean whichever of AutoMap/Play last wrote it that frame
    // wins and the other's turbo request silently drops. AutoMap and Play are not meant
    // to run at once, but ORing two independent flags instead of sharing one costs
    // nothing and removes the failure mode entirely if that assumption is ever wrong.
    public static bool PlayForceTurbo;
    public static bool TurboActive => ForceTurbo || PlayForceTurbo || (Turbo && Playing && Frame < TurboUntil);

    static string _recPath = "tas.rec";
    static readonly System.Text.StringBuilder _rec = new();
    static ushort _lastRecState = 0xFFFF;
    static byte _lastLx = 0x80, _lastLy = 0x80;
    static long _lastHeartbeatFrame;
    const long HeartbeatFrames = 300; // ~5s at 60fps

    // ---- Periodic savestates while recording ----------------------------------------
    // A long playtest that ends in a crash gives us the crash but no cheap way back to
    // just before it: reproducing the last 30 seconds means replaying the whole run.
    // Capturing a state every minute while recording turns "replay 55 minutes" into
    // "load the state from a minute ago and replay the tail".
    //
    // This ONLY arms while Recording (the request is raised inside the `if (Recording)`
    // block below), so a normal play session never writes one. Tas raises a request and
    // nothing more -- it never touches CpuContext and never blocks. The game host applies
    // it at whatever point IT considers safe (see the port's per-frame hook), because only
    // the game knows what a consistent call depth looks like; a state captured at the
    // wrong depth is worse than no state at all.
    public static long AutoStateFrames = 3600;   // ~60 s at 60 fps; 0 disables
    public static int AutoStateKeep = 8;         // retention: how many of the newest to keep
    static long _nextAutoStateFrame;
    static readonly List<string> _autoStates = new();

    /// <summary>Frame-stamped sibling of the recording: `run.rec` -> `run.f003600.sav`, so a
    /// state's filename says which recorded frame it belongs to.</summary>
    static string AutoStatePath(long frame)
    {
        string dir = System.IO.Path.GetDirectoryName(_recPath) ?? "";
        string stem = System.IO.Path.GetFileNameWithoutExtension(_recPath);
        return System.IO.Path.Combine(dir, $"{stem}.f{frame:D6}.sav");
    }

    // Retention. A state is ~3.7 MB; one a minute across an hour is 60 files and 200+ MB,
    // which would quietly fill a disk during exactly the long unattended sessions this
    // feature exists to serve. Keep the newest AutoStateKeep and delete the rest. The list
    // is in creation order and only entries BEFORE the tail are ever removed, so the newest
    // state can never be the one deleted.
    static void PruneAutoStates()
    {
        while (_autoStates.Count > Math.Max(1, AutoStateKeep))
        {
            string old = _autoStates[0];
            _autoStates.RemoveAt(0);
            try { if (System.IO.File.Exists(old)) System.IO.File.Delete(old); } catch { }
        }
    }

    /// <summary>Called by the game host once it has actually written the file, so retention
    /// tracks states that exist rather than ones that were merely requested (a request the
    /// safety gate declines is retried, not written).</summary>
    public static void NoteAutoStateWritten(string path)
    {
        _autoStates.Remove(path);
        _autoStates.Add(path);
        PruneAutoStates();
    }

    // replay keyframes (frame, state, leftX, leftY), sorted by frame
    static readonly List<(long f, ushort st, byte lx, byte ly)> _play = new();
    static int _playIdx;

    public static void StartRecord(string path)
    {
        _recPath = path;
        _rec.Clear();
        _rec.AppendLine("# PE1 TAS input record: <frame> <state hex, active-low> <leftX> <leftY>");
        Recording = true;
        _lastHeartbeatFrame = Frame;
        _nextAutoStateFrame = AutoStateFrames > 0 ? Frame + AutoStateFrames : long.MaxValue;
    }

    public static void StartReplay(string path)
    {
        foreach (var raw in System.IO.File.ReadAllLines(path))
        {
            var t = raw.Trim();
            if (t.Length == 0 || t[0] == '#') continue;
            var p = t.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2) continue;
            long f = long.Parse(p[0]);
            ushort st = System.Convert.ToUInt16(p[1], 16);
            byte lx = p.Length > 2 ? byte.Parse(p[2]) : (byte)0x80;
            byte ly = p.Length > 3 ? byte.Parse(p[3]) : (byte)0x80;
            _play.Add((f, st, lx, ly));
        }
        Playing = _play.Count > 0;
    }

    // Once per presented frame, from PresentFrame. On replay this sets the pad for
    // the upcoming frame (InputManager.Poll is suppressed while Playing); on record
    // it captures whatever the game saw this frame.
    public static void Tick()
    {
        if (Playing)
        {
            while (_playIdx < _play.Count && _play[_playIdx].f <= Frame)
            {
                var e = _play[_playIdx];
                Controller.State = e.st;
                Controller.LeftX = e.lx;
                Controller.LeftY = e.ly;
                _playIdx++;
            }
            if (_playIdx >= _play.Count) Playing = false; // stream exhausted -> hand control back
        }
        if (Recording)
        {
            ushort st = Controller.State;
            bool changed = st != _lastRecState || Controller.LeftX != _lastLx || Controller.LeftY != _lastLy;
            // Heartbeat: a real per-change write already hits disk synchronously below (no
            // buffered-until-exit loss to guard against), so this is NOT a crash-safety net —
            // it exists so a recording process that stops receiving input (e.g. another
            // process grabbed the pad/keyboard focus first, as happened 2026-09-06 with two
            // instances launched at once) is diagnosable from the file itself: its mtime and
            // size stop advancing immediately instead of looking fine until someone tries to
            // replay an hour of silence. Re-recording the SAME unchanged state is enough to
            // prove liveness; it does not need to look like real new input.
            bool heartbeat = !changed && Frame - _lastHeartbeatFrame >= HeartbeatFrames;
            if (changed || heartbeat)
            {
                _rec.AppendLine($"{Frame} {st:X4} {Controller.LeftX} {Controller.LeftY}");
                _lastRecState = st; _lastLx = Controller.LeftX; _lastLy = Controller.LeftY;
                _lastHeartbeatFrame = Frame;
                try { System.IO.File.WriteAllText(_recPath, _rec.ToString()); } catch { }
            }
            // Periodic savestate request. Raised here and only here, so it cannot arm on a
            // run that is not recording. Newest-wins: if a previous request is still parked
            // waiting for a safe frame, overwrite it rather than queueing -- the freshest
            // recoverable point is the useful one.
            if (AutoStateFrames > 0 && Frame >= _nextAutoStateFrame)
            {
                _nextAutoStateFrame = Frame + AutoStateFrames;
                Savestate.AutoSavePath = AutoStatePath(Frame);
            }
        }
        Frame++;
    }
}
