using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Play;

// Value registry: a game host registers named probes once — an address, a struct
// walk, whatever it takes to read live state — and both PlayRunner/Watchdog below
// and the (separate, display-only) GameInfo panel read them back by name. The
// engine itself never hardcodes a game address; ParasiteEve.PC etc. own that.
//
// Typed delegate storage (long / bool), not Func<IMemory, object>: the watchdog
// polls every registered probe every frame for the life of an unattended run, so
// boxing there would be per-frame garbage for hours. Callers that need an int32,
// uint32, or bool can all route through the `long` numeric probe (widen on
// register, narrow at the call site if needed).
public static class Probes
{
    static readonly Dictionary<string, Func<IMemory, long>> _num = new();
    static readonly Dictionary<string, Func<IMemory, bool>> _flag = new();

    public static void Register(string name, Func<IMemory, long> reader) => _num[name] = reader;
    public static void RegisterFlag(string name, Func<IMemory, bool> reader) => _flag[name] = reader;

    public static bool TryNum(string name, IMemory m, out long value)
    {
        if (_num.TryGetValue(name, out var f)) { value = f(m); return true; }
        value = 0;
        return false;
    }

    public static bool TryFlag(string name, IMemory m, out bool value)
    {
        if (_flag.TryGetValue(name, out var f)) { value = f(m); return true; }
        value = false;
        return false;
    }

    public static long Num(string name, IMemory m) =>
        TryNum(name, m, out var v) ? v : throw new KeyNotFoundException($"probe '{name}' not registered");

    public static bool Flag(string name, IMemory m) => TryFlag(name, m, out var v) && v;

    public static IReadOnlyCollection<string> NumNames => _num.Keys;
    public static IReadOnlyCollection<string> FlagNames => _flag.Keys;

    // For tests / re-registering across a hot-reloaded game host in the same process.
    public static void Clear() { _num.Clear(); _flag.Clear(); }
}
