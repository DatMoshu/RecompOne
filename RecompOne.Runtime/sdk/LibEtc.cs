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
        if (mode < 0) { c.V0 = (uint)_vcount; return; }
        if (mode == 1) { c.V0 = 0; return; }

        Runtime.PresentFrame();
        _vcount++;
        c.V0 = 0;
    }

    public static void SaveState(BinaryWriter w) => w.Write(_vcount);
    public static void LoadState(BinaryReader r) => _vcount = r.ReadInt32();
}
