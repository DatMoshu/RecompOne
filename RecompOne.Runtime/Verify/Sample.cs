using System.Text;

namespace RecompOne.Runtime.Verify;

/// <summary>Where an address lives, which decides how a diff should treat it.</summary>
public enum Region
{
    /// <summary>Globals and heap. The part of a write set that actually constitutes the contract.</summary>
    Mem,
    /// <summary>Below the entry stack pointer: this invocation's own frame, dead once it returns.</summary>
    Stack,
    /// <summary>Scratchpad. Usually dead, but some games keep persistent state here, so it is
    /// reported separately rather than assumed either way.</summary>
    Scratch,
    /// <summary>Hardware registers. Order of access is semantics, not an implementation detail.</summary>
    Mmio,
}

/// <summary>One call out of the function, for calls that routed through the dispatcher.</summary>
public readonly record struct CallRec(uint Target, uint A0, uint A1, uint A2, uint A3, uint V0, uint V1);

/// <summary>
/// A complete, self-contained specification of one invocation: the registers it started with,
/// every byte it consumed, every byte it produced, and every dispatcher-routed call it made.
///
/// Enough to replay the invocation with no game running -- preload a sparse memory with
/// <see cref="Reads"/>, run a candidate implementation, and diff its writes against
/// <see cref="Writes"/>.
/// </summary>
public sealed class Sample
{
    public uint Addr;
    public long Frame;
    public ulong InputHash;
    public bool Truncated;
    public uint EntrySp;
    public uint[] EntryGpr = new uint[32];
    public uint EntryHi, EntryLo;
    public uint[] ExitGpr = new uint[32];
    public uint ExitHi, ExitLo;
    public Dictionary<uint, byte> Reads = [];
    public Dictionary<uint, byte> Writes = [];
    public List<CallRec> Calls = [];

    public static Region Classify(uint addr, uint entrySp)
    {
        uint phys = addr & 0x1FFFFFFF;
        if (phys is >= 0x1F80_0000 and < 0x1F80_0400) return Region.Scratch;
        if (phys is >= 0x1F80_1000 and < 0x1F80_3000) return Region.Mmio;
        // The frame is [deepestSp, entrySp). 64 KB is far past any real PS1 frame and well
        // clear of the globals, which sit two megabytes lower.
        if (phys < 0x0020_0000 && addr < entrySp && addr >= entrySp - 0x1_0000) return Region.Stack;
        return Region.Mem;
    }

    /// <summary>Coalesce a byte map into contiguous runs, so a 40-word struct prints as one
    /// line instead of 160.</summary>
    public static List<(uint Addr, byte[] Data)> Runs(IReadOnlyDictionary<uint, byte> map)
    {
        var keys = map.Keys.ToArray();
        Array.Sort(keys);
        var runs = new List<(uint, byte[])>();
        int i = 0;
        while (i < keys.Length)
        {
            int j = i + 1;
            while (j < keys.Length && keys[j] == keys[j - 1] + 1) j++;
            var data = new byte[j - i];
            for (int k = i; k < j; k++) data[k - i] = map[keys[k]];
            runs.Add((keys[i], data));
            i = j;
        }
        return runs;
    }

    public static string Hex(byte[] b)
    {
        var sb = new StringBuilder(b.Length * 2);
        foreach (var x in b) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }

    static void WriteRuns(StringBuilder sb, string name, IReadOnlyDictionary<uint, byte> map,
                          uint entrySp, Region? only, string indent)
    {
        var filtered = only is null
            ? map
            : map.Where(kv => Classify(kv.Key, entrySp) == only.Value)
                 .ToDictionary(kv => kv.Key, kv => kv.Value);
        sb.Append(indent).Append('"').Append(name).Append("\": [");
        var runs = Runs(filtered);
        for (int i = 0; i < runs.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append($"[\"{runs[i].Addr:X8}\",\"{Hex(runs[i].Data)}\"]");
        }
        sb.AppendLine("],");
    }

    public void WriteJson(StringBuilder sb, string indent)
    {
        sb.Append(indent).AppendLine("{");
        string i2 = indent + "  ";
        sb.Append(i2).AppendLine($"\"frame\": {Frame}, \"inputHash\": \"{InputHash:X16}\", \"truncated\": {(Truncated ? "true" : "false")},");
        sb.Append(i2).AppendLine($"\"entrySp\": \"{EntrySp:X8}\",");
        sb.Append(i2).Append("\"entryGpr\": [");
        for (int i = 0; i < 32; i++) { if (i > 0) sb.Append(','); sb.Append($"\"{EntryGpr[i]:X8}\""); }
        sb.AppendLine("],");
        sb.Append(i2).Append("\"exitGpr\": [");
        for (int i = 0; i < 32; i++) { if (i > 0) sb.Append(','); sb.Append($"\"{ExitGpr[i]:X8}\""); }
        sb.AppendLine("],");
        sb.Append(i2).AppendLine($"\"entryHiLo\": [\"{EntryHi:X8}\",\"{EntryLo:X8}\"], \"exitHiLo\": [\"{ExitHi:X8}\",\"{ExitLo:X8}\"],");
        WriteRuns(sb, "reads", Reads, EntrySp, null, i2);
        WriteRuns(sb, "writesMem", Writes, EntrySp, Region.Mem, i2);
        WriteRuns(sb, "writesStack", Writes, EntrySp, Region.Stack, i2);
        WriteRuns(sb, "writesScratch", Writes, EntrySp, Region.Scratch, i2);
        WriteRuns(sb, "writesMmio", Writes, EntrySp, Region.Mmio, i2);
        sb.Append(i2).Append("\"calls\": [");
        for (int i = 0; i < Calls.Count; i++)
        {
            var c = Calls[i];
            if (i > 0) sb.Append(", ");
            sb.Append($"{{\"t\":\"{c.Target:X8}\",\"a\":[\"{c.A0:X8}\",\"{c.A1:X8}\",\"{c.A2:X8}\",\"{c.A3:X8}\"],\"v\":[\"{c.V0:X8}\",\"{c.V1:X8}\"]}}");
        }
        sb.AppendLine("]");
        sb.Append(indent).Append('}');
    }
}
