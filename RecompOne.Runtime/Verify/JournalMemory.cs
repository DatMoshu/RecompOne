using RecompOne.Runtime.Cdrom;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Verify;

/// <summary>
/// An <see cref="IMemory"/> decorator that records the exact set of bytes a function
/// consumed and produced. One instance wraps one invocation.
///
/// Byte granularity is deliberate. A game writes a state byte at 0x800B0DC5 and reads the
/// containing word at 0x800B0DC4; at word granularity those alias and the read set silently
/// absorbs the function's own output. At byte granularity overlap is exact and free.
///
/// The read set is the function's INPUT working set, so a byte this function has already
/// written is never added to it -- reading back your own output is not an input, and
/// preloading it into a replay would hand the candidate an answer the original had to
/// compute. That single rule is what makes a recorded sample hermetic.
/// </summary>
public sealed class JournalMemory(IMemory inner, uint entrySp) : IMemory
{
    readonly IMemory _inner = inner;

    /// <summary>First value observed at each byte the function read and had not itself written.</summary>
    public readonly Dictionary<uint, byte> Reads = [];

    /// <summary>Final value at each byte the function wrote.</summary>
    public readonly Dictionary<uint, byte> Writes = [];

    /// <summary>Stack pointer on entry. Writes below it are this function's own frame.</summary>
    public readonly uint EntrySp = entrySp;

    /// <summary>Set when a bulk transfer exceeded <see cref="BulkCap"/> bytes, so the journal
    /// is a summary rather than a complete record and the sample cannot be replayed hermetically.</summary>
    public bool Truncated;

    /// <summary>Bytes of a single LoadBytes/ZeroRange recorded before giving up on exactness.</summary>
    public const int BulkCap = 4096;

    static bool IsRam(uint a) => (a & 0x1FFFFFFF) < 0x0020_0000;

    void NoteRead(uint addr, int len, uint value)
    {
        for (int i = 0; i < len; i++)
        {
            uint a = addr + (uint)i;
            if (Writes.ContainsKey(a) || Reads.ContainsKey(a)) continue;
            Reads[a] = (byte)(value >> (8 * i));
        }
    }

    void NoteWrite(uint addr, int len, uint value)
    {
        for (int i = 0; i < len; i++) Writes[addr + (uint)i] = (byte)(value >> (8 * i));
    }

    public byte ReadU8(uint address)
    {
        byte v = _inner.ReadU8(address);
        NoteRead(address, 1, v);
        return v;
    }

    public ushort ReadU16(uint address)
    {
        ushort v = _inner.ReadU16(address);
        NoteRead(address, 2, v);
        return v;
    }

    public uint ReadU32(uint address)
    {
        uint v = _inner.ReadU32(address);
        NoteRead(address, 4, v);
        return v;
    }

    public void WriteU8(uint address, byte value)
    {
        _inner.WriteU8(address, value);
        NoteWrite(address, 1, value);
    }

    public void WriteU16(uint address, ushort value)
    {
        _inner.WriteU16(address, value);
        NoteWrite(address, 2, value);
    }

    public void WriteU32(uint address, uint value)
    {
        _inner.WriteU32(address, value);
        NoteWrite(address, 4, value);
    }

    // lwl/lwr/swl/swr. The byte subset each touches depends on address alignment, so rather
    // than reimplement that here (and risk disagreeing with the real memory), record the whole
    // containing word: for reads, the word as the inner memory holds it; for writes, the word
    // read back AFTER the store. Read-back is only safe in RAM -- an MMIO port can change state
    // on read -- and these ops only ever target RAM in practice, so a non-RAM target is flagged.
    public uint ReadWordLeft(uint current, uint address)
    {
        uint v = _inner.ReadWordLeft(current, address);
        uint word = address & ~3u;
        if (IsRam(word)) NoteRead(word, 4, _inner.ReadU32(word)); else Truncated = true;
        return v;
    }

    public uint ReadWordRight(uint current, uint address)
    {
        uint v = _inner.ReadWordRight(current, address);
        uint word = address & ~3u;
        if (IsRam(word)) NoteRead(word, 4, _inner.ReadU32(word)); else Truncated = true;
        return v;
    }

    public void WriteWordLeft(uint address, uint value)
    {
        uint word = address & ~3u;
        if (IsRam(word)) NoteRead(word, 4, _inner.ReadU32(word)); else Truncated = true;
        _inner.WriteWordLeft(address, value);
        if (IsRam(word)) NoteWrite(word, 4, _inner.ReadU32(word));
    }

    public void WriteWordRight(uint address, uint value)
    {
        uint word = address & ~3u;
        if (IsRam(word)) NoteRead(word, 4, _inner.ReadU32(word)); else Truncated = true;
        _inner.WriteWordRight(address, value);
        if (IsRam(word)) NoteWrite(word, 4, _inner.ReadU32(word));
    }

    public void LoadBytes(uint address, byte[] data)
    {
        _inner.LoadBytes(address, data);
        int n = Math.Min(data.Length, BulkCap);
        for (int i = 0; i < n; i++) Writes[address + (uint)i] = data[i];
        if (data.Length > BulkCap) Truncated = true;
    }

    public void ZeroRange(uint address, uint length)
    {
        _inner.ZeroRange(address, length);
        uint n = Math.Min(length, BulkCap);
        for (uint i = 0; i < n; i++) Writes[address + i] = 0;
        if (length > BulkCap) Truncated = true;
    }

    public void SetCd(CdController cd) => _inner.SetCd(cd);

    // A raised interrupt is an effect the journal cannot express as memory, so a sample that
    // raises one is not self-contained. Flag rather than silently record an incomplete spec.
    public void RaiseIrq(int bit)
    {
        Truncated = true;
        _inner.RaiseIrq(bit);
    }
}
