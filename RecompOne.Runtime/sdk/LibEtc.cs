using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Sdk;

public static class LibEtc
{
    static int _vcount;

    static uint _dbgState = 0xDEADBEEF;

    public static void VSync(CpuContext c, IMemory m)
    {
        int mode = (int)c.A0;
        Log.Sdk($"VSync({mode})");
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
}
