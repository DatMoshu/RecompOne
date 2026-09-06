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
            // Inside an interrupt handler the vblank counter cannot advance (the VBLANK ISR
            // can't preempt another ISR), so a VSync(-1) poll from a CD/DMA callback must
            // return a constant and must NOT present a frame or pump CD sectors — doing so
            // re-entered libcd's getintr loop with a fresh INT1 and nested libcdstream's
            // ready callback one level per sector until the stack was gone (BFM STR hang).
            if (PollDrivesFrame && !Interrupts.InDeliver)
            {
                double now = _pollClock.Elapsed.TotalMilliseconds;
                if (now >= _nextPollTickMs)
                {
                    Runtime.PresentFrame(paced: true); // this gate already waited out the vblank
                    Advance(now);
                }
            }
            c.V0 = (uint)_vcount; return;
        }
        if (mode == 1) { c.V0 = 0; return; }

        // VSync(0) waits for the next vblank; VSync(n>1) waits until n vblanks have passed
        // since the previous VSync wait (PSYQ libetc). Since no vblanks elapse here between
        // calls, that is n presented frames: PE1 paces the field with VSync(2) (30 fps) and
        // FMV/loading with VSync(4) — treating them as 1 ran the game at double speed.
        int n = mode == 0 ? 1 : mode;
        for (int i = 0; i < n; i++)
        {
            Runtime.PresentFrame();
            Advance(_pollClock.Elapsed.TotalMilliseconds);
        }
        c.V0 = 0;
    }

    // One vblank has now been consumed, whichever path produced it. Both the blocking wait
    // and the VSync(-1) poll gate go through here so a game that does both (Brave Fencer
    // Musashi polls, then blocks once it reaches the title) still advances at 60 Hz overall
    // instead of one frame per path per period.
    static void Advance(double nowMs)
    {
        _vcount++;
        _nextPollTickMs = (nowMs > _nextPollTickMs ? nowMs : _nextPollTickMs) + VBlankMs;
    }

    public static void SaveState(BinaryWriter w) => w.Write(_vcount);
    public static void LoadState(BinaryReader r) => _vcount = r.ReadInt32();
}
