using System.IO;
using RecompOne.Runtime.Cdrom;
using RecompOne.Runtime.Hardware;

namespace RecompOne.Runtime.Memory;

public sealed class PSMemory : IMemory
{
    private readonly byte[] _ram = new byte[Runtime.Mode == RunMode.Devkit ? MemoryMap.DevkitRamSize : MemoryMap.RetailRamSize];
    private readonly byte[] _scratchpad = new byte[MemoryMap.ScratchpadSize];
    private readonly byte[] _hwregs = new byte[MemoryMap.HwRegsSize];
    private readonly byte[] _bios = new byte[MemoryMap.BiosSize];

    private readonly Gpu _gpu = new();
    private readonly Spu _spu = new();
    private readonly Mdec _mdec = new();
    private readonly Timers _timers = new();
    private readonly Dma _dma;
    private CdController? _cd;

    // Hardware I_STAT/I_MASK interrupt registers. Off by default so BIOS-HLE
    // games (e.g. PE1) that never read raw 0x1F801070/74 keep plain-memory
    // behaviour; opted in by games (e.g. BFM) whose interrupt entry reads the
    // real hardware register and dispatches per pending source bit.
    public static bool HwIrqModel;
    private uint _iStat;
    private uint _iMask;

    // Raise a hardware interrupt source (0=VBLANK,1=GPU,2=CDROM,3=DMA,...);
    // the source is only serviced once the game unmasks it in I_MASK.
    public void RaiseIrq(int bit) { if (HwIrqModel) _iStat |= 1u << bit; }
    public uint IrqStat => _iStat;
    public uint IrqMask => _iMask;

    public ReadOnlySpan<byte> Ram => _ram;
    internal byte[] RamBuffer => _ram;

    public PSMemory()
    {
        _dma = new Dma(this, _gpu, _spu, _mdec, OnDmaComplete);
        Runtime.Gpu = _gpu;
        Runtime.Spu = _spu;
        Bios.KromFont.InstallInto(_bios);
    }

    // DMA transfers finish synchronously inside the CHCR write, so a DMA-IRQ handler
    // that kicks the next queued transfer (libgpu's _exeque/_cwc chain) would recurse
    // Run->Complete->DispatchIrq->handler->Run... until the host stack overflows.
    // Real hardware only interrupts after the handler returned. Mirror that: a
    // completion raised while a DMA IRQ is being delivered just latches (DICR flag +
    // I_STAT bit are already set) and is re-dispatched once the outer delivery unwinds.
    bool _inDmaIrq, _dmaIrqPending;
    void OnDmaComplete()
    {
        if (_inDmaIrq) { _dmaIrqPending = true; return; }
        _inDmaIrq = true;
        try
        {
            do { _dmaIrqPending = false; RaiseIrq(3); Runtime.DispatchIrq(3); }
            while (_dmaIrqPending);
        }
        finally { _inDmaIrq = false; }
    }

    // Controller/memory-card serial port. Off by default: games served by the libpad or
    // BIOS-pad HLE never touch these registers, and a game with its own driver may already
    // be handled by a game-specific patch. Opted in per game (BFM drives the port itself).
    public static bool SioEnabled;
    readonly SioPad _sio = new(bit => { if (HwIrqModel) Runtime.Mem?.RaiseIrq(bit); });

    public SioPad Sio => _sio;

    public void SetCd(CdController cd) { _cd = cd; _dma.SetCd(cd); }
    public long[] DmaRuns => _dma.Runs;

    private static bool IsDmaChcr(uint phys) => phys >= 0x1F801080u && phys < 0x1F8010F0u && (phys & 0xFu) == 8u;

    private uint Hw32(uint phys)
    {
        int o = (int)(phys - MemoryMap.HwRegsBase);
        return (uint)(_hwregs[o] | (_hwregs[o + 1] << 8) | (_hwregs[o + 2] << 16) | (_hwregs[o + 3] << 24));
    }

    private void Hw32(uint phys, uint v)
    {
        int o = (int)(phys - MemoryMap.HwRegsBase);
        _hwregs[o] = (byte)v;
        _hwregs[o + 1] = (byte)(v >> 8);
        _hwregs[o + 2] = (byte)(v >> 16);
        _hwregs[o + 3] = (byte)(v >> 24);
    }

    private void TrackWrite(uint phys, int size)
    {
        if (phys < MemoryMap.RamWindow)
            Runtime.RamLog.RecordWrite(phys % (uint)_ram.Length, size);
    }

    private Span<byte> Resolve(uint address, int size)
    {
        uint phys = MemoryMap.ToPhysical(address);

        if (phys < MemoryMap.RamWindow)
            return _ram.AsSpan((int)(phys % (uint)_ram.Length), size);

        if (phys >= MemoryMap.ScratchpadBase && phys < MemoryMap.ScratchpadBase + MemoryMap.ScratchpadSize)
            return _scratchpad.AsSpan((int)(phys - MemoryMap.ScratchpadBase), size);

        if (phys >= MemoryMap.HwRegsBase && phys < MemoryMap.HwRegsBase + MemoryMap.HwRegsSize)
            return _hwregs.AsSpan((int)(phys - MemoryMap.HwRegsBase), size);

        if (phys >= MemoryMap.BiosBase && phys < MemoryMap.BiosBase + MemoryMap.BiosSize)
            return _bios.AsSpan((int)(phys - MemoryMap.BiosBase), size);

        // Unmapped access. By default this is a hard error (surfaces real bugs).
        // With --lenient it degrades like most PS1 emulators (reads 0, writes
        // discarded) so a single stray pointer doesn't kill the whole port.
        if (!Dispatch.Dispatcher.LenientCalls)
            throw new InvalidOperationException($"unmapped address: 0x{address:X8}");
        if (Log.BiosOn) Log.Bios($"unmapped access 0x{address:X8} size {size}");
        _oob.AsSpan(0, 8).Clear();
        return _oob.AsSpan(0, size);
    }

    private readonly byte[] _oob = new byte[8];

    private static bool IsCd(uint phys) => phys >= 0x1F801800u && phys <= 0x1F801803u;
    private static bool IsSpu(uint phys) => phys >= 0x1F801C00u && phys < 0x1F801E80u;
    private static bool IsIrqStat(uint phys) => phys == 0x1F801070u;
    private static bool IsIrqMask(uint phys) => phys == 0x1F801074u;

    public byte ReadU8(uint address)
    {
        uint phys = MemoryMap.ToPhysical(address);
        if (_cd != null && IsCd(phys)) return _cd.Read(phys);
        if (SioEnabled && SioPad.InRange(phys)) return (byte)_sio.Read(phys, 1);
        return Resolve(address, 1)[0];
    }

    public ushort ReadU16(uint address)
    {
        uint phys = MemoryMap.ToPhysical(address);
        if (_cd != null && IsCd(phys)) return _cd.Read(phys);
        if (SioEnabled && SioPad.InRange(phys)) return (ushort)_sio.Read(phys, 2);
        if (HwIrqModel && IsIrqStat(phys)) return (ushort)_iStat;
        if (HwIrqModel && IsIrqMask(phys)) return (ushort)_iMask;
        if (IsSpu(phys)) return _spu.ReadReg16(phys);
        if (Timers.InRange(phys) && _timers.TryRead(phys, out uint tv)) return (ushort)tv;
        var s = Resolve(address, 2);
        return (ushort)(s[0] | (s[1] << 8));
    }

    public uint ReadU32(uint address)
    {
        uint phys = MemoryMap.ToPhysical(address);
        if (phys == 0x1F801810u) return _gpu.ReadData();
        if (phys == 0x1F801814u) return _gpu.ReadStat();
        if (phys == 0x1F801820u) return _mdec.ReadData();
        if (phys == 0x1F801824u) return _mdec.ReadStatus();
        if (phys == 0x1F8010F4u) return _dma.ReadDicr();
        if (HwIrqModel && IsIrqStat(phys)) return _iStat;
        if (HwIrqModel && IsIrqMask(phys)) return _iMask;
        if (SioEnabled && SioPad.InRange(phys)) return _sio.Read(phys, 4);
        if (_cd != null && IsCd(phys)) return _cd.Read(phys);
        if (IsSpu(phys)) return (uint)(_spu.ReadReg16(phys) | (_spu.ReadReg16(phys + 2) << 16));
        if (Timers.InRange(phys) && _timers.TryRead(phys, out uint tv)) return tv;
        var s = Resolve(address, 4);
        return (uint)(s[0] | (s[1] << 8) | (s[2] << 16) | (s[3] << 24));
    }

    public void WriteU8(uint address, byte value)
    {
        uint phys = MemoryMap.ToPhysical(address);
        Watch(phys, value, 1);
        TrackWrite(phys, 1);
        if (_cd != null && IsCd(phys)) { _cd.Write(phys, value); return; }
        if (SioEnabled && SioPad.InRange(phys)) { _sio.Write(phys, value, 1); return; }
        Resolve(address, 1)[0] = value;
    }

    public void WriteU16(uint address, ushort value)
    {
        uint phys = MemoryMap.ToPhysical(address);
        Watch(phys, value, 2);
        TrackWrite(phys, 2);
        if (_cd != null && IsCd(phys)) { _cd.Write(phys, (byte)value); return; }
        if (SioEnabled && SioPad.InRange(phys)) { _sio.Write(phys, value, 2); return; }
        if (HwIrqModel && IsIrqStat(phys)) { _iStat &= value; return; } // ack: write clears 0-bits
        if (HwIrqModel && IsIrqMask(phys)) { _iMask = value; Interrupts.OnMaskChanged(this, Runtime.Cpu); return; }
        if (IsSpu(phys)) { _spu.WriteReg16(phys, value); return; }
        if (_timers.TryWrite(phys, value)) return;
        var s = Resolve(address, 2);
        s[0] = (byte)value;
        s[1] = (byte)(value >> 8);
    }

    public static bool WatchOn;
    // Generic host watchpoints: physical addresses (word-aligned base) whose writes are
    // reported to WatchHook(phys, value, size). Empty set = zero cost beyond the count check.
    public static readonly HashSet<uint> WatchAddrs = [];
    public static Action<uint, uint, int>? WatchHook;

    private void Watch(uint phys, uint value, int size)
    {
        if (WatchAddrs.Count != 0 && WatchAddrs.Contains(phys & 0x1FFFFFFCu)) WatchHook?.Invoke(phys, value, size);
        if (WatchOn && (phys & 0x1FFFFFFFu) == 0x000B0CD8u && (value & 0x100u) != 0
            && (_ram[0x000B0CD9] & 0x01) == 0) // bit 0x100 newly set
            Console.WriteLine($"[watch] cd8 |= 0x100  val=0x{value:X8} scene=0x{(uint)(_ram[0x0009D280]|(_ram[0x0009D281]<<8)|(_ram[0x0009D282]<<16)|(_ram[0x0009D283]<<24)):X8}\n{new System.Diagnostics.StackTrace(2, false)}");
    }

    // PE1 Day-1 cinematic fix: the slot1 movie controller mis-fires at the seam
    // between the two STR segments and stores 0 into the scene selector [0x8009D280],
    // which collapses the scene and mass-tears-down the actor list (incl. the Day-1
    // controller that would advance to the dressing room), freezing the game. While
    // the movie phase [0x800B0DBA] is active, reject a 0-write to the scene selector
    // so the scene stays on the map-431 controller until both segments truly finish.
    public static bool SceneZeroGuard;

    public void WriteU32(uint address, uint value)
    {
        uint phys = MemoryMap.ToPhysical(address);
        if (SceneZeroGuard && (phys & 0x1FFFFFFFu) == 0x0009D280u && value == 0 && _ram[0x000B0DBA] != 0)
            return;
        Watch(phys, value, 4);
        TrackWrite(phys, 4);
        if (phys == 0x1F801810u) { _gpu.WriteGp0(value); return; }
        if (phys == 0x1F801814u) { _gpu.WriteGp1(value); return; }
        if (phys == 0x1F801820u) { _mdec.Write0(value); return; }
        if (phys == 0x1F801824u) { _mdec.WriteControl(value); return; }
        if (phys == 0x1F8010F4u) { _dma.WriteDicr(value); return; }
        if (IsDmaChcr(phys) && (value & 0x01000000u) != 0)
        {
            Hw32(phys, value & ~0x01000000u);
            _dma.Run((int)((phys - 0x1F801080u) / 0x10u), Hw32(phys - 8u), Hw32(phys - 4u), value);
            return;
        }
        if (_cd != null && IsCd(phys)) { _cd.Write(phys, (byte)value); return; }
        if (SioEnabled && SioPad.InRange(phys)) { _sio.Write(phys, value, 2); return; }
        if (HwIrqModel && IsIrqStat(phys)) { _iStat &= value; return; } // ack: write clears 0-bits
        if (HwIrqModel && IsIrqMask(phys)) { _iMask = value; Interrupts.OnMaskChanged(this, Runtime.Cpu); return; }
        if (IsSpu(phys)) { _spu.WriteReg16(phys, (ushort)value); _spu.WriteReg16(phys + 2, (ushort)(value >> 16)); return; }
        if (_timers.TryWrite(phys, value)) return;
        var s = Resolve(address, 4);
        s[0] = (byte)value;
        s[1] = (byte)(value >> 8);
        s[2] = (byte)(value >> 16);
        s[3] = (byte)(value >> 24);
    }

    public uint ReadWordLeft(uint current, uint address)
    {
        int shift = (int)((address & 3) * 8);
        uint word = ReadU32(address & ~3u);
        return (current & (0x00FFFFFFu >> shift)) | (word << (24 - shift));
    }

    public uint ReadWordRight(uint current, uint address)
    {
        int shift = (int)((address & 3) * 8);
        uint word = ReadU32(address & ~3u);
        return (current & (0xFFFFFF00u << (24 - shift))) | (word >> shift);
    }

    public void WriteWordLeft(uint address, uint value)
    {
        uint aligned = address & ~3u;
        int shift = (int)((address & 3) * 8);
        uint mem = ReadU32(aligned);
        WriteU32(aligned, (mem & (0xFFFFFF00u << shift)) | (value >> (24 - shift)));
    }

    public void WriteWordRight(uint address, uint value)
    {
        uint aligned = address & ~3u;
        int shift = (int)((address & 3) * 8);
        uint mem = ReadU32(aligned);
        WriteU32(aligned, (mem & (0x00FFFFFFu >> (24 - shift))) | (value << shift));
    }

    public void LoadBytes(uint address, byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
            WriteU8(address + (uint)i, data[i]);
    }

    public void ZeroRange(uint address, uint length)
    {
        for (uint i = 0; i < length; i++)
            WriteU8(address + i, 0);
    }

    // Savestate: raw RAM windows only (_bios is static ROM, skipped).
    public void SaveState(BinaryWriter w)
    {
        w.Write(_ram.Length); w.Write(_ram);
        w.Write(_scratchpad.Length); w.Write(_scratchpad);
        w.Write(_hwregs.Length); w.Write(_hwregs);
    }

    public void LoadState(BinaryReader r)
    {
        ReadInto(r, _ram);
        ReadInto(r, _scratchpad);
        ReadInto(r, _hwregs);
    }

    // The interrupt registers are NOT part of the _hwregs array above -- they are handled
    // as special cases in Read/Write and live in their own fields, so a state that saved
    // only the raw windows lost them. Zero for any game on the legacy BIOS-HLE path
    // (HwIrqModel off never writes _iStat), so this round-trips harmlessly there.
    public void SaveIrqState(BinaryWriter w) { w.Write(_iStat); w.Write(_iMask); }
    public void LoadIrqState(BinaryReader r) { _iStat = r.ReadUInt32(); _iMask = r.ReadUInt32(); }

    static void ReadInto(BinaryReader r, byte[] dst)
    {
        int len = r.ReadInt32();
        int n = Math.Min(len, dst.Length);
        int read = 0;
        while (read < n) read += r.Read(dst, read, n - read);
        if (len > n) r.BaseStream.Seek(len - n, SeekOrigin.Current);
    }
}
