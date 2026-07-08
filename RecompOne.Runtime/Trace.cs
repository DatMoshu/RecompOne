namespace RecompOne.Runtime;

// Lightweight ring buffer of the last N function entries, emitted by the
// recompiler's debug codegen (Trace.Enter per function). Dump() prints the
// buffer so a stall's spinning loop is visible without the massive per-call
// Console.WriteLine spam of raw debug mode.
public static class Trace
{
    public static bool On;
    const int Size = 1024, Mask = Size - 1;
    static readonly uint[] _ring = new uint[Size];
    static long _idx;

    public static void Enter(uint addr)
    {
        if (!On) return;
        _ring[_idx & Mask] = addr;
        _idx++;
    }

    public static void Dump(string tag)
    {
        long n = _idx < Size ? _idx : Size;
        long start = _idx - n;
        System.Console.WriteLine($"[trace:{tag}] last {n} function entries (oldest->newest):");
        for (long i = start; i < _idx; i++)
            System.Console.WriteLine($"  0x{_ring[i & Mask]:X8}");
    }
}
