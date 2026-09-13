using System.Text;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Verify;

/// <summary>
/// Records what a recompiled function actually does, one invocation at a time.
///
/// The recompiler emits a call to <see cref="Gate"/> under a function's PUBLIC name for any
/// function configured with patch mode "verify", keeping the original body under a private
/// name. That placement is what makes this work at all: calls between two functions of the
/// same overlay are emitted as direct static calls and never pass through the dispatcher, so
/// wrapping the dispatcher's function map would miss almost every call to a main-EXE function.
///
/// A recorded sample is a complete specification of one invocation -- entry registers, the
/// bytes consumed, the bytes produced, the calls made -- and is therefore replayable against a
/// candidate reimplementation with no game running. This class only RECORDS; comparison is a
/// separate offline step that consumes these files.
/// </summary>
public static class Oracle
{
    /// <summary>Fast path guard. False costs one static bool read per gated call.</summary>
    public static bool Any;

    /// <summary>True while inside a recorded invocation, so the dispatcher knows to log calls.</summary>
    public static bool Active => _stack.Count > 0;

    /// <summary>Unique inputs retained per function. Invocations keep being counted past this.</summary>
    public static int MaxSamples = 256;

    public static string DumpDir = "verify";

    sealed class FnState
    {
        public string Name = "";
        public long Invocations;
        public int Truncated;
        public readonly HashSet<ulong> Seen = [];
        public readonly List<Sample> Samples = [];
        public readonly Dictionary<uint, int> ReadCounts = [];
        public readonly Dictionary<uint, int> WriteCounts = [];
        public readonly Dictionary<uint, int> CallCounts = [];
    }

    static readonly Dictionary<uint, FnState> _fns = [];
    static readonly List<Sample> _stack = [];

    public static void Arm(uint addr, string name = "")
    {
        _fns[addr] = new FnState { Name = name };
        Any = true;
        Console.WriteLine($"[verify] armed 0x{addr:X8}{(name.Length > 0 ? $" ({name})" : "")}");
    }

    public static bool IsArmed(uint addr) => _fns.ContainsKey(addr);

    /// <summary>
    /// Entry point emitted in place of a verified function's body. When disarmed this is a
    /// bool test, a dictionary miss and a delegate call.
    /// </summary>
    public static void Gate(uint addr, CpuContext c, IMemory m, Action<CpuContext, IMemory> body)
    {
        if (!Any || !_fns.TryGetValue(addr, out var st)) { body(c, m); return; }

        st.Invocations++;

        var (entryGpr, entryHi, entryLo) = c.Snapshot();
        uint entrySp = c.SP;
        var jm = new JournalMemory(m, entrySp);
        var sample = new Sample
        {
            Addr = addr,
            Frame = Runtime.FrameCount,
            EntrySp = entrySp,
            EntryGpr = entryGpr,
            EntryHi = entryHi,
            EntryLo = entryLo,
        };

        _stack.Add(sample);
        try
        {
            body(c, jm);
        }
        finally
        {
            _stack.RemoveAt(_stack.Count - 1);
        }

        var (exitGpr, exitHi, exitLo) = c.Snapshot();
        sample.ExitGpr = exitGpr;
        sample.ExitHi = exitHi;
        sample.ExitLo = exitLo;
        sample.Reads = jm.Reads;
        sample.Writes = jm.Writes;
        sample.Truncated = jm.Truncated;
        if (jm.Truncated) st.Truncated++;

        // Aggregate the contract regardless of whether the sample is kept: the union of
        // addresses touched is the analysis product, and it should reflect every invocation,
        // not only the ones that survived deduplication.
        foreach (var a in sample.Reads.Keys) st.ReadCounts[a] = st.ReadCounts.GetValueOrDefault(a) + 1;
        foreach (var a in sample.Writes.Keys) st.WriteCounts[a] = st.WriteCounts.GetValueOrDefault(a) + 1;
        foreach (var call in sample.Calls) st.CallCounts[call.Target] = st.CallCounts.GetValueOrDefault(call.Target) + 1;

        sample.InputHash = HashInput(sample);
        if (!st.Seen.Add(sample.InputHash)) return;   // same inputs as a sample already held
        if (st.Samples.Count < MaxSamples) st.Samples.Add(sample);
    }

    /// <summary>
    /// Identity of an invocation's INPUTS, used to keep one sample per distinct input rather
    /// than sixty identical ones per second.
    ///
    /// Only the argument registers are hashed, not the whole context: SP varies with call depth
    /// and RA with call site, and including either would make every call site look like a fresh
    /// input. For the same reason a stack address is hashed as its offset from the entry SP
    /// rather than absolutely -- the same argument passed on the stack must hash equal wherever
    /// the stack happened to be.
    /// </summary>
    static ulong HashInput(Sample s)
    {
        ulong h = 0xcbf2_9ce4_8422_2325;
        void Mix(ulong v)
        {
            for (int i = 0; i < 8; i++) h = (h ^ (byte)(v >> (8 * i))) * 0x0000_0100_0000_01B3;
        }

        Mix(s.EntryGpr[4]); Mix(s.EntryGpr[5]); Mix(s.EntryGpr[6]); Mix(s.EntryGpr[7]);

        var keys = s.Reads.Keys.ToArray();
        Array.Sort(keys);
        foreach (var a in keys)
        {
            if (Sample.Classify(a, s.EntrySp) == Region.Stack) { Mix(0x5741); Mix(s.EntrySp - a); }
            else Mix(a);
            Mix(s.Reads[a]);
        }
        return h;
    }

    /// <summary>
    /// Dispatcher-routed call logging. Recorded into EVERY frame on the stack, so each frame's
    /// list is the transitive set of calls made beneath it -- which is what an offline replay
    /// needs when it mocks callees instead of running them.
    /// </summary>
    public static void WrapCall(CpuContext c, IMemory m, uint addr, Action<CpuContext, IMemory, uint> inner)
    {
        uint a0 = c.A0, a1 = c.A1, a2 = c.A2, a3 = c.A3;
        inner(c, m, addr);
        var rec = new CallRec(addr, a0, a1, a2, a3, c.V0, c.V1);
        foreach (var s in _stack) s.Calls.Add(rec);
    }

    // ---- reporting ---------------------------------------------------------------------

    static List<(uint Addr, int Len, int Count)> CoalesceCounts(
        Dictionary<uint, int> counts, uint entrySp, Region? only)
    {
        var keys = counts.Keys.Where(a => only is null || Sample.Classify(a, entrySp) == only.Value).ToArray();
        Array.Sort(keys);
        var runs = new List<(uint, int, int)>();
        int i = 0;
        while (i < keys.Length)
        {
            int j = i + 1;
            while (j < keys.Length && keys[j] == keys[j - 1] + 1 && counts[keys[j]] == counts[keys[i]]) j++;
            runs.Add((keys[i], j - i, counts[keys[i]]));
            i = j;
        }
        return runs;
    }

    public static void Dump()
    {
        if (_fns.Count == 0) return;
        Directory.CreateDirectory(DumpDir);
        var report = new StringBuilder();
        report.AppendLine("RecompOne differential-oracle contract report");
        report.AppendLine($"generated {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine();

        foreach (var (addr, st) in _fns.OrderBy(kv => kv.Key))
        {
            // Bucketing the AGGREGATE report needs some entry SP; the newest sample's is
            // representative because the stack depth at a given call site barely moves.
            uint sp = st.Samples.Count > 0 ? st.Samples[^1].EntrySp : 0x801F_FFF0;

            report.AppendLine($"== 0x{addr:X8} {st.Name}");
            report.AppendLine($"   invocations      {st.Invocations}");
            report.AppendLine($"   distinct inputs  {st.Seen.Count}"
                              + (st.Samples.Count < st.Seen.Count ? $" (kept {st.Samples.Count})" : ""));
            if (st.Truncated > 0)
                report.AppendLine($"   NOT HERMETIC     {st.Truncated} invocation(s) did a bulk transfer or raised an IRQ");

            var reads = CoalesceCounts(st.ReadCounts, sp, null)
                        .Where(r => Sample.Classify(r.Addr, sp) != Region.Stack).ToList();
            report.AppendLine($"   reads (non-stack, {reads.Count} runs):");
            foreach (var r in reads.OrderByDescending(r => r.Count).Take(40))
                report.AppendLine($"     0x{r.Addr:X8}+{r.Len,-3} x{r.Count}  [{Sample.Classify(r.Addr, sp)}]");

            foreach (var reg in new[] { Region.Mem, Region.Mmio, Region.Scratch })
            {
                var runs = CoalesceCounts(st.WriteCounts, sp, reg);
                if (runs.Count == 0) continue;
                report.AppendLine($"   writes {reg} ({runs.Count} runs):");
                foreach (var r in runs.OrderByDescending(r => r.Count).Take(40))
                    report.AppendLine($"     0x{r.Addr:X8}+{r.Len,-3} x{r.Count}");
            }

            int stackRuns = CoalesceCounts(st.WriteCounts, sp, Region.Stack).Count;
            if (stackRuns > 0)
                report.AppendLine($"   writes Stack     {stackRuns} runs (frame-local, dead on return)");

            if (st.CallCounts.Count > 0)
            {
                report.AppendLine("   calls out (dispatcher-routed only):");
                foreach (var (t, n) in st.CallCounts.OrderByDescending(kv => kv.Value))
                    report.AppendLine($"     0x{t:X8} x{n}");
            }
            report.AppendLine();

            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"function\": \"{addr:X8}\", \"name\": \"{st.Name}\",");
            sb.AppendLine($"  \"invocations\": {st.Invocations}, \"distinctInputs\": {st.Seen.Count},");
            sb.AppendLine("  \"samples\": [");
            for (int i = 0; i < st.Samples.Count; i++)
            {
                st.Samples[i].WriteJson(sb, "    ");
                sb.AppendLine(i < st.Samples.Count - 1 ? "," : "");
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            File.WriteAllText(Path.Combine(DumpDir, $"verify_{addr:X8}.json"), sb.ToString());
        }

        File.WriteAllText(Path.Combine(DumpDir, "contracts.txt"), report.ToString());
        Console.WriteLine($"[verify] wrote {_fns.Count} contract(s) to {Path.GetFullPath(DumpDir)}");
    }
}
