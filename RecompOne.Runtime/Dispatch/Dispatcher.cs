using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using BiosKernel = RecompOne.Runtime.Bios.Bios;

namespace RecompOne.Runtime.Dispatch;

public static class Dispatcher
{
    static readonly Dictionary<string, IOverlay> _registry = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<int, string> _lbaToName = [];
    static readonly List<string> _active = [];
    static readonly Dictionary<uint, Action<CpuContext, IMemory>> _funcMap = [];

    public static void Register(string name, IOverlay overlay)
    {
        _registry[name] = overlay;
        if (overlay.LbaStart >= 0) _lbaToName[overlay.LbaStart] = name;
    }

    public static void LoadByLba(int lba)
    {
        if (_lbaToName.TryGetValue(lba, out var name)) Load(name);
    }

    public static readonly HashSet<string> SkipOverlays = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loading an overlay unloads any active overlay whose address range it covers.</summary>
    public static bool EvictOverlapping;

    public static void Load(string name)
    {
        if (SkipOverlays.Contains(name)) return;
        if (!_registry.TryGetValue(name, out var overlay))
            throw new KeyNotFoundException($"overlay not registered: {name}");

        // Slot semantics: a module loaded over another module's address range replaces it.
        // Evict active overlays (never the main EXE table) whose function range overlaps
        // the incoming one, so stale entries of the previous occupant can't be called.
        // Opt-in: a game whose overlays deliberately co-exist in one region (a resident
        // module plus per-room chunks sharing a base) must keep both sets callable.
        if (EvictOverlapping && overlay.Functions.Count > 0)
        {
            uint lo = overlay.Functions.Keys.Min(), hi = overlay.Functions.Keys.Max();
            foreach (var other in _active.Where(n => n != name && n != "main").ToList())
            {
                var f = _registry[other].Functions;
                if (f.Count == 0) continue;
                if (f.Keys.Min() <= hi && f.Keys.Max() >= lo)
                {
                    _active.Remove(other);
                    Runtime.OverlayLog.Record(other, OverlayEventKind.Unloaded);
                    Console.WriteLine($"[Dispatcher] evicted overlay: {other} (slot taken by {name})");
                }
            }
            Rebuild();
        }
        bool already = _active.Remove(name);
        _active.Add(name);
        foreach (var (addr, fn) in overlay.Functions)
            _funcMap[addr] = fn;

        if (already) return;
        Runtime.OverlayLog.Record(name, OverlayEventKind.Loaded);
        Console.WriteLine($"[Dispatcher] loaded overlay: {name}");
    }

    public static void TryLoad(string name)
    {
        if (_registry.ContainsKey(name))
            Load(name);
    }

    public static void Unload(string name)
    {
        if (!_active.Remove(name)) return;
        Rebuild();
        Runtime.OverlayLog.Record(name, OverlayEventKind.Unloaded);
    }

    public static bool LenientCalls;

    // Host-installed unmapped-call observer (e.g. a room auto-mapper): return true to
    // treat the call as a no-op (V0=0) and keep running, false to throw as usual.
    public static Func<uint, bool>? OnUnmapped;

    // Used by the jr-jumptable `default` path: an indirect jump whose target the
    // analyzer couldn't resolve to a local label is dispatched as a call, but the
    // surrounding function's epilogue is skipped. MIPS ABI requires callee-saved
    // registers (s0-s7, fp, gp, sp) survive the call, so snapshot/restore them to
    // keep a stray target from corrupting the caller's frame.
    public static void CallKeepSaved(CpuContext c, IMemory m, uint addr)
    {
        uint s0 = c.S0, s1 = c.S1, s2 = c.S2, s3 = c.S3, s4 = c.S4, s5 = c.S5,
             s6 = c.S6, s7 = c.S7, fp = c.FP, gp = c.GP, sp = c.SP;
        Call(c, m, addr);
        c.S0 = s0; c.S1 = s1; c.S2 = s2; c.S3 = s3; c.S4 = s4; c.S5 = s5;
        c.S6 = s6; c.S7 = s7; c.FP = fp; c.GP = gp; c.SP = sp;
    }

    public static bool TraceGs1;
    public static void Call(CpuContext c, IMemory m, uint addr)
    {
        if (TraceGs1 && (addr == 0x801216C4u || addr == 0x80121A00u || addr == 0x80121C04u ||
                         addr == 0x801223A8u || addr == 0x80122040u || addr == 0x80014F60u))
        {
            uint ptr = m.ReadU32(0x801227E4u);
            bool has = _funcMap.ContainsKey(addr);
            Console.WriteLine($"[gs1trace] Call 0x{addr:X8} mapped={has} [0x801227E4]=0x{ptr:X8}");
        }
        if (BiosKernel.TryDispatch(c, m, addr)) return;
        if (!_funcMap.TryGetValue(addr, out var fn))
        {
            if (LenientCalls || (OnUnmapped != null && OnUnmapped(addr))) { c.V0 = 0; return; } // treat unmapped call as a no-op
            throw new InvalidOperationException($"unmapped call: 0x{addr:X8}  [{Sdk.LibDs.DescribeLoadFor(addr)}]");
        }
        fn(c, m);
    }

    static void Rebuild()
    {
        _funcMap.Clear();
        foreach (var name in _active)
            foreach (var (addr, fn) in _registry[name].Functions)
                _funcMap[addr] = fn;
    }

    // Savestate: capture the resident overlay set (in load order) and restore it by
    // re-Load()ing each so _funcMap is rebuilt exactly as it was.
    public static List<string> CaptureActive() => new(_active);

    public static void RestoreActive(IEnumerable<string> names)
    {
        _active.Clear();
        _funcMap.Clear();
        foreach (var name in names) Load(name);
    }
}
