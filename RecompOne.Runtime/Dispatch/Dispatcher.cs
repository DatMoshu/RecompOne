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

    public static void Load(string name)
    {
        if (!_registry.TryGetValue(name, out var overlay))
            throw new KeyNotFoundException($"overlay not registered: {name}");

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

    public static void Call(CpuContext c, IMemory m, uint addr)
    {
        if (BiosKernel.TryDispatch(c, m, addr)) return;
        if (!_funcMap.TryGetValue(addr, out var fn))
        {
            if (LenientCalls) { c.V0 = 0; return; } // treat unmapped call as a no-op
            throw new InvalidOperationException($"unmapped call: 0x{addr:X8}");
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
}
