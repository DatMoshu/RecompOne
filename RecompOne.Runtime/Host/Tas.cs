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

    // Turbo the human asked for, from the Tab hotkey or the Debug menu. A THIRD flag for
    // the same reason PlayForceTurbo is a second one: AutoMap and PlayRunner each assign
    // their own every frame, so a user toggle stored in either would be cleared the next
    // frame by a driver that never knew about it. Survives independently of both.
    public static bool UserTurbo;

    // A FOURTH flag, for a mod that fast-forwards on the game's behalf (PE1's
    // cutscene-skip). Same reasoning as the two above, and one more: this one is owned by
    // a mod that turns it on and off again within a few seconds, so it must not be able to
    // clear a fast-forward the human asked for with Tab. Independent flags OR'd together
    // is the only arrangement where "whoever turns it off last" cannot lose someone
    // else's request. Defaults off; nothing sets it unless a mod is active.
    public static bool ModTurbo;

    public static bool TurboActive => ForceTurbo || PlayForceTurbo || UserTurbo || ModTurbo || (Turbo && Playing && Frame < TurboUntil);

    static string _recPath = "tas.rec";
    static readonly System.Text.StringBuilder _rec = new();
    static ushort _lastRecState = 0xFFFF;
    static byte _lastLx = 0x80, _lastLy = 0x80;
    static long _lastHeartbeatFrame;
    const long HeartbeatFrames = 300; // ~5s at 60fps

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
        }
        Frame++;
    }
}
