using System.IO;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Sdk;

public static class LibEtc
{
    static int _vcount;
    public static bool VSyncCallerLog;
    static uint _lastVsRa;

    static uint _dbgState = 0xDEADBEEF;

    // Some games (e.g. Brave Fencer Musashi) never call VSync(0)/VSync(1); they
    // pace frames by busy-polling VSync(-1) and waiting for the free-running
    // vblank counter to advance, which on hardware is driven asynchronously by
    // the VBLANK IRQ. Under static recompilation nothing advances it, so the
    // wait spins forever (stable black screen). When PollDrivesFrame is set, the
    // VSync(-1) query drives one real frame (present + VBLANK dispatch + counter
    // tick) per vblank period, wall-clock gated so the tight poll stays cheap.
    // Off by default so games that call VSync(0) (e.g. PE1) are unaffected.
    public static bool PollDrivesFrame;
    static readonly System.Diagnostics.Stopwatch _pollClock = System.Diagnostics.Stopwatch.StartNew();
    static double _nextPollTickMs;
    const double VBlankMs = 1000.0 / 60.0;

    public static void VSync(CpuContext c, IMemory m)
    {
        int mode = (int)c.A0;
        Log.Sdk($"VSync({mode})");
        if (VSyncCallerLog && c.RA != _lastVsRa)
        { Console.WriteLine($"[vswait] VSync({mode}) RA=0x{c.RA:X8} scene=0x{m.ReadU32(0x8009D280u):X8} cd8=0x{m.ReadU32(0x800B0CD8u):X8}"); _lastVsRa = c.RA; }
        if (Log.SdkOn)
        {
            uint st = m.ReadU32(0x8009D280u); // PE1 main scene/state selector
            if (st != _dbgState) { Console.WriteLine($"[STATE] 0x{_dbgState:X8} -> 0x{st:X8}"); _dbgState = st; }
        }
        if (mode < 0)
        {
            if (PollDrivesFrame)
            {
                double now = _pollClock.Elapsed.TotalMilliseconds;
                if (now >= _nextPollTickMs)
                {
                    _nextPollTickMs = (now > _nextPollTickMs + 100 ? now : _nextPollTickMs) + VBlankMs;
                    Runtime.PresentFrame();
                    _vcount++;
                }
            }
            c.V0 = (uint)_vcount; return;
        }
        if (mode == 1) { c.V0 = 0; return; }

        Runtime.PresentFrame();
        _vcount++;
        c.V0 = 0;
    }

    public static void SaveState(BinaryWriter w) => w.Write(_vcount);
    public static void LoadState(BinaryReader r) => _vcount = r.ReadInt32();
}
