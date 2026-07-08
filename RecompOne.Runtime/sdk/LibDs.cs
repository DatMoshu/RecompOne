using System.IO;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Sdk;

// HLE for libds, the queued cd interface. semantics mirror libcd but commands
// complete immediately, so DsSync/DsReadSync report done and callbacks fire inline
public static class LibDs
{
    const int Complete = 0x02;
    const int DataReady = 0x01;
    const byte ModeSize1 = 0x20, ModeSize0 = 0x10;
    const byte StatMotor = 0x02;

    public static bool ProtectGs1;
    // Room-graphics loads that overrun the live gs1 cinematic region are deferred
    // until the cinematic phase clears, then replayed by FlushDeferred (called each
    // frame from Patches.RoomLoadGate once [0x800B0DBA]==0).
    public static bool RoomDeferOn = true;
    static readonly object _deferGate = new();
    static readonly List<(int lba, int sectors, uint buf, int size, byte mode)> _deferred = new();

    // Live state that MUST survive intact while the Day-1 cinematic plays (physical
    // addresses). gs1's stream descriptor/tables, and libpress's (slot0) MDEC quant-
    // table work buffers 0x8010DA0C/0x8010DA90 (132B each, luma+chroma, set by
    // DecDCTReset) + the MMIO-pointer block 0x8010DB1C — clobbering the quant tables
    // makes MDEC decode garbage. (overlay decomp: FUN_8010c1ec DMA's these to MDEC.)
    static bool CinematicProtected(uint p) =>
        (p >= 0x00120D00u && p < 0x00122D00u) ||   // gs1 stream descriptor + tables
        (p >= 0x0010DA0Cu && p < 0x0010DB58u);      // libpress luma/chroma quant tables + MMIO ptrs

    public static void FlushDeferred(IMemory m)
    {
        List<(int lba, int sectors, uint buf, int size, byte mode)> pending;
        lock (_deferGate)
        {
            if (_deferred.Count == 0) return;
            pending = new(_deferred);
            _deferred.Clear();
        }
        foreach (var (lba, sectors, buf, size, _) in pending)
            for (int i = 0; i < sectors; i++)
            {
                Dispatcher.LoadByLba(lba + i);
                byte[] data;
                lock (LibCd.DiscLock) data = Runtime.Cd!.ReadSectorData(lba + i, size);
                for (int j = 0; j < data.Length; j++)
                    m.WriteU8(buf + (uint)(i * size + j), data[j]);
            }
    }

    static byte _status = StatMotor;
    static byte _com;
    static byte _mode;
    static readonly byte[] _lastResult = new byte[8];
    static int _lastIntr = Complete;

    static uint _cbSync;
    static uint _cbReady;
    static uint _cbRead;
    static uint _cbData;

    public static void DsInit(CpuContext c, IMemory m)
    {
        ResetState();
        c.V0 = 1;
    }

    public static void DsReset(CpuContext c, IMemory m)
    {
        ResetState();
        c.V0 = 1;
    }

    public static void DsFlush(CpuContext c, IMemory m) => ResetState();
    public static void DsSetDebug(CpuContext c, IMemory m) { }

    // boot gate: the game spins until DsDiskReady()==1 && DsQueueLen()==0 && DsGetDiskType()==4
    public static void DsDiskReady(CpuContext c, IMemory m) => c.V0 = 1;
    public static void DsGetDiskType(CpuContext c, IMemory m) => c.V0 = 4; // CD001 data disc
    public static void DsReadBreak(CpuContext c, IMemory m) { }
    public static void DsClose(CpuContext c, IMemory m) { }

    public static void DsLastPos(CpuContext c, IMemory m)
    {
        uint p = c.A0;
        if (!ValidPtr(p)) { c.V0 = 0; return; }
        int i = _seekLba + 150;
        m.WriteU8(p, ToBcd(i / 75 / 60));
        m.WriteU8(p + 1, ToBcd(i / 75 % 60));
        m.WriteU8(p + 2, ToBcd(i % 75));
        c.V0 = p;
    }

    public static void DsControl(CpuContext c, IMemory m) => c.V0 = (uint)(Exec(m, (byte)c.A0, c.A1, c.A2) == 0 ? 1 : 0);
    public static void DsControlF(CpuContext c, IMemory m) => c.V0 = (uint)(Exec(m, (byte)c.A0, c.A1, 0) == 0 ? 1 : 0);

    public static void DsControlB(CpuContext c, IMemory m)
    {
        if (Exec(m, (byte)c.A0, c.A1, c.A2) != 0) { c.V0 = 0; return; }
        c.V0 = (uint)(_lastIntr == Complete ? 1 : 0);
    }

    public static void DsCommand(CpuContext c, IMemory m)
    {
        byte cmd = (byte)c.A0;
        Exec(m, cmd, c.A1, 0);
        // Game-start disc-verify (FUN_80069b08) issues command 8, then spins waiting
        // for the shell-open status bit (0x10) to prove the disc was seated/checked.
        // Our disc is always present, so latch the bit; DsStatus clears it read-once.
        if (cmd == 8) _status |= 0x10;
        uint func = c.A2;
        if (func != 0) InvokeCallback(func, Complete);
        c.V0 = 1;
    }

    public static void DsSync(CpuContext c, IMemory m)
    {
        // a0 = command id, a1 = result buffer (0 or -1 means none)
        if (ValidPtr(c.A1)) WriteResult(m, c.A1);
        c.V0 = (uint)_lastIntr;
    }

    public static void DsReady(CpuContext c, IMemory m)
    {
        // while a ReadN is active every poll has a sector waiting for DsGetSector
        if (ValidPtr(c.A1)) WriteResult(m, c.A1);
        c.V0 = _readActive ? (uint)DataReady : (uint)_lastIntr;
    }

    public static void DsQueueLen(CpuContext c, IMemory m) => c.V0 = 0;
    public static void DsStatus(CpuContext c, IMemory m)
    {
        c.V0 = _status;
        _status = (byte)(_status & ~0x10); // shell-open is a read-once event latch
    }
    public static void DsLastCom(CpuContext c, IMemory m) => c.V0 = _com;
    public static void DsMix(CpuContext c, IMemory m) => c.V0 = 1;

    // Square's libds ReadS helper (streaming start): a0 = DslLOC* pos, a1 = mode.
    // installs ring callbacks and issues CdlReadS through the packet core on real
    // hardware; here it hands the stream position to the LibCdStream ring engine
    public static void DsReadS(CpuContext c, IMemory m)
    {
        _seekLba = ReadLoc(m, c.A0);
        Log.Sdk($"DsReadS lba={_seekLba} mode=0x{c.A1:X}");
        LibCdStream.OnReadStream(_seekLba, (c.A1 & 0x80) != 0 ? 150.0 : 75.0);
        c.V0 = 1;
    }

    // Recent DsRead chunk loads, so an unmapped-call crash can name the exact per-scene
    // overlay LBA that needs mapping (each room streams its chunk3 code into 0x8018EFE8
    // etc., and a fn-ptr target linear-sweep missed there crashes as "unmapped call").
    const int RecentN = 64;
    static readonly (int lba, int sectors, uint buf, int size)[] _recentLoads = new (int, int, uint, int)[RecentN];
    static int _recentIdx;
    static void RecordLoad(int lba, int sectors, uint buf, int size)
        => _recentLoads[_recentIdx++ % RecentN] = (lba, sectors, buf & 0x1FFFFFFFu, size);

    // Which recently-loaded chunk covers a (physical) address — annotates unmapped-call
    // crashes with the overlay config line to add.
    public static string DescribeLoadFor(uint addr)
    {
        addr &= 0x1FFFFFFFu;
        for (int k = 1; k <= RecentN; k++)
        {
            var (lba, sectors, buf, size) = _recentLoads[((_recentIdx - k) % RecentN + RecentN) % RecentN];
            if (sectors == 0) continue;
            if (addr >= buf && addr < buf + (uint)(sectors * size))
                return $"in chunk: DsRead lba={lba} sectors={sectors} size={sectors * size} base=0x{buf | 0x80000000u:X8} — add this as an overlay";
        }
        return "no recent chunk load covers this address";
    }

    // After a savestate load, re-run overlay activation for the chunks that were resident
    // when the state was saved (captured in _recentLoads), oldest->newest so the most
    // recent resident overlay wins _funcMap. This makes an overlay configured AFTER the
    // state was saved (e.g. a newly-mapped room chunk3) become resident even though it
    // wasn't in the saved active-overlay set — so boot-into-savestate picks up new fixes.
    // Most recent DsRead LBA into the per-scene chunk3 region (0x8018EFE8), so the map log
    // can record EVERY room's chunk3 LBA (not just the ones that crash) for batch mapping.
    public static int LastChunk3Lba()
    {
        for (int k = 1; k <= RecentN; k++)
        {
            var (lba, sectors, buf, _) = _recentLoads[((_recentIdx - k) % RecentN + RecentN) % RecentN];
            if (sectors != 0 && buf == 0x0018EFE8u) return lba;
        }
        return -1;
    }

    public static void ReactivateRecentOverlays()
    {
        for (int k = 0; k < RecentN; k++)
        {
            var (lba, sectors, _, _) = _recentLoads[(_recentIdx + k) % RecentN];
            if (sectors != 0) Dispatcher.LoadByLba(lba);
        }
    }

    public static void DsRead(CpuContext c, IMemory m)
    {
        int lba = ReadLoc(m, c.A0);
        int sectors = (int)c.A1;
        uint buf = c.A2;
        _mode = (byte)c.A3;
        int size = SectorSize(_mode);
        RecordLoad(lba, sectors, buf, size);
        Log.Sdk($"DsRead lba={lba} sectors={sectors} buf=0x{buf:X8} mode=0x{_mode:X2} size={size}");

        // Day-1 cinematic sequencing: while the STR cinematic phase [0x800B0DBA] is
        // active, the gs1 overlay is live at 0x80120D00. A room-graphics load whose
        // destination overruns into that region would clobber the running cinematic
        // (crash). On hardware the room load stays deferred until the cinematic ends.
        // Defer the whole read here and replay it once phase clears (FlushDeferred).
        // While the cinematic is active, protect gs1's live data region: write the
        // read normally but hold back any bytes landing in [0x80120D00,0x80122D00),
        // and record the read to replay in full once phase clears (FlushDeferred).
        // (gs1's *code* is C#, not in PSMemory, so only its data must be protected;
        // func_8006B4F8 reads the room header from the slot0 part, which we do write.)
        uint bp = buf & 0x1FFFFFFFu, blen = (uint)(sectors * size);
        bool defer = RoomDeferOn && m.ReadU8(0x800B0DBAu) != 0
            && bp < 0x00122D00u && bp + blen > 0x0010DA0Cu; // overlaps any live cinematic-decode state
        if (defer) lock (_deferGate) _deferred.Add((lba, sectors, buf, size, _mode));

        for (int i = 0; i < sectors; i++)
        {
            Dispatcher.LoadByLba(lba + i);
            byte[] data;
            lock (LibCd.DiscLock) data = Runtime.Cd!.ReadSectorData(lba + i, size);
            for (int j = 0; j < data.Length; j++)
            {
                uint dst = buf + (uint)(i * size + j);
                if (defer && CinematicProtected(dst & 0x1FFFFFFFu))
                    continue; // hold back live decode state until the cinematic ends
                m.WriteU8(dst, data[j]);
            }
        }
        _lastIntr = Complete;
        // Drive the real libds engine's completion state so any game code that polls
        // it directly (not via our DsReadSync HLE) sees the transfer as finished:
        // sectors-remaining [0x8009B6B4]=0, busy [0x8009B6CC]=0. The PE.IMG game-start
        // asset loader (func_8006ad40) spins on this. (analyst a7d66bd.)
        if (_cbRead != 0) InvokeCallback(_cbRead, Complete);
        c.V0 = 1;
    }

    public static void DsReadSync(CpuContext c, IMemory m)
    {
        if (ValidPtr(c.A0)) WriteResult(m, c.A0);
        c.V0 = 0; // no sectors remaining, read completed inline
    }

    // NOTE: 0x80080AC4 is MISNAMED "DsGetSector" in the map — it is actually
    // DsMix/CdMix (CD-audio -> SPU volume apply). A0 is a 4-byte CdlATV *input*
    // {LtoL, LtoR, RtoR, RtoL}; it is never an output buffer. The real function
    // only writes the CD controller's index-banked volume MMIO regs and returns 1.
    // (The prior HLE copied a 2048-byte sector into A0, smashing the caller's
    // stack frame.) The actual sector/DMA fetch primitives live at 0x80080AE4 /
    // 0x80080B04. Here we read the 4 volume bytes and return 1; we do not write A0.
    public static void DsGetSector(CpuContext c, IMemory m)
    {
        if (ValidPtr(c.A0))
            Log.Sdk($"DsMix (mislabelled DsGetSector) atv[0..3]={m.ReadU8(c.A0)},{m.ReadU8(c.A0 + 1)},{m.ReadU8(c.A0 + 2)},{m.ReadU8(c.A0 + 3)}");
        c.V0 = 1; // CD-audio volume apply is a no-op here; must NOT write to A0
    }
    public static void DsDataSync(CpuContext c, IMemory m) => c.V0 = 0;
    public static void DsSearchFile(CpuContext c, IMemory m) => LibCd.CdSearchFile(c, m);

    public static void DsPosToInt(CpuContext c, IMemory m)
    {
        uint p = c.A0;
        c.V0 = (uint)((Bcd(m.ReadU8(p)) * 60 + Bcd(m.ReadU8(p + 1))) * 75 + Bcd(m.ReadU8(p + 2)) - 150);
    }

    public static void DsIntToPos(CpuContext c, IMemory m)
    {
        int i = (int)c.A0 + 150;
        uint p = c.A1;
        m.WriteU8(p, ToBcd(i / 75 / 60));
        m.WriteU8(p + 1, ToBcd(i / 75 % 60));
        m.WriteU8(p + 2, ToBcd(i % 75));
        c.V0 = p;
    }

    public static void DsSyncCallback(CpuContext c, IMemory m) { c.V0 = _cbSync; _cbSync = c.A0; }
    public static void DsReadyCallback(CpuContext c, IMemory m) { c.V0 = _cbReady; _cbReady = c.A0; }
    public static void DsReadCallback(CpuContext c, IMemory m) { c.V0 = _cbRead; _cbRead = c.A0; }
    public static void DsDataCallback(CpuContext c, IMemory m) { c.V0 = _cbData; _cbData = c.A0; }

    static int Exec(IMemory m, byte com, uint param, uint result)
    {
        _com = com;
        _lastIntr = Complete;
        Log.Sdk($"Ds cmd 0x{com:X2} param=0x{param:X8}");

        switch (com)
        {
            case 0x02: // Setloc
                if (ValidPtr(param)) _seekLba = ReadLoc(m, param);
                break;
            case 0x0E: // Setmode
                if (ValidPtr(param)) _mode = m.ReadU8(param);
                break;
            case 0x06: // ReadN
                _readActive = true;
                Dispatcher.LoadByLba(_seekLba);
                break;
            case 0x1B: // ReadS
                _readActive = true;
                Dispatcher.LoadByLba(_seekLba);
                LibCdStream.OnReadStream(_seekLba, (_mode & 0x80) != 0 ? 150.0 : 75.0);
                break;
            case 0x08: // Stop
            case 0x09: // Pause
            case 0x0A: // Init
                _readActive = false;
                LibCdStream.OnStopStream();
                break;
        }

        _lastResult[0] = _status;
        for (int i = 1; i < _lastResult.Length; i++) _lastResult[i] = 0;
        if (ValidPtr(result)) WriteResult(m, result);
        return 0;
    }

    static bool ValidPtr(uint p) => p != 0 && p != 0xFFFFFFFF;

    static int _seekLba;
    static bool _readActive;

    static void InvokeCallback(uint func, int intr)
    {
        var c = Runtime.Cpu;
        var m = Runtime.Mem;
        if (c == null || m == null) return;
        var snap = c.Snapshot();
        c.A0 = (uint)intr;
        c.A1 = 0;
        Dispatcher.Call(c, m, func);
        c.Restore(snap);
    }

    static int ReadLoc(IMemory m, uint p) =>
        (Bcd(m.ReadU8(p)) * 60 + Bcd(m.ReadU8(p + 1))) * 75 + Bcd(m.ReadU8(p + 2)) - 150;

    static void ResetState()
    {
        _status = StatMotor;
        _com = 0;
        _mode = 0;
        _lastIntr = Complete;
        _readActive = false;
        _cbSync = _cbReady = _cbRead = _cbData = 0;
        Array.Clear(_lastResult);
    }

    static void WriteResult(IMemory m, uint addr)
    {
        for (int i = 0; i < _lastResult.Length; i++)
            m.WriteU8(addr + (uint)i, _lastResult[i]);
    }

    static int SectorSize(byte mode)
    {
        if ((mode & ModeSize1) != 0) return 2340;
        if ((mode & ModeSize0) != 0) return 2328;
        return 2048;
    }

    static int Bcd(byte b) => (b >> 4) * 10 + (b & 0xF);
    static byte ToBcd(int n) => (byte)(((n / 10) << 4) + (n % 10));

    public static void SaveState(BinaryWriter w)
    {
        w.Write(_status); w.Write(_com); w.Write(_mode);
        w.Write(_lastIntr); w.Write(_seekLba); w.Write(_readActive);
        w.Write(_cbSync); w.Write(_cbReady); w.Write(_cbRead); w.Write(_cbData);
        w.Write(_lastResult);
        w.Write(_recentIdx);
        foreach (var (lba, sectors, buf, size) in _recentLoads)
        { w.Write(lba); w.Write(sectors); w.Write(buf); w.Write(size); }
        lock (_deferGate)
        {
            w.Write(_deferred.Count);
            foreach (var (lba, sectors, buf, size, mode) in _deferred)
            { w.Write(lba); w.Write(sectors); w.Write(buf); w.Write(size); w.Write(mode); }
        }
    }

    public static void LoadState(BinaryReader r)
    {
        _status = r.ReadByte(); _com = r.ReadByte(); _mode = r.ReadByte();
        _lastIntr = r.ReadInt32(); _seekLba = r.ReadInt32(); _readActive = r.ReadBoolean();
        _cbSync = r.ReadUInt32(); _cbReady = r.ReadUInt32(); _cbRead = r.ReadUInt32(); _cbData = r.ReadUInt32();
        for (int i = 0; i < _lastResult.Length; i++) _lastResult[i] = r.ReadByte();
        _recentIdx = r.ReadInt32();
        for (int i = 0; i < RecentN; i++)
            _recentLoads[i] = (r.ReadInt32(), r.ReadInt32(), r.ReadUInt32(), r.ReadInt32());
        lock (_deferGate)
        {
            _deferred.Clear();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
                _deferred.Add((r.ReadInt32(), r.ReadInt32(), r.ReadUInt32(), r.ReadInt32(), r.ReadByte()));
        }
    }
}
