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
        Exec(m, (byte)c.A0, c.A1, 0);
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
    public static void DsStatus(CpuContext c, IMemory m) => c.V0 = _status;
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

    public static void DsRead(CpuContext c, IMemory m)
    {
        int lba = ReadLoc(m, c.A0);
        int sectors = (int)c.A1;
        uint buf = c.A2;
        _mode = (byte)c.A3;
        int size = SectorSize(_mode);
        Log.Sdk($"DsRead lba={lba} sectors={sectors} buf=0x{buf:X8} mode=0x{_mode:X2} size={size}");

        for (int i = 0; i < sectors; i++)
        {
            Dispatcher.LoadByLba(lba + i);
            byte[] data;
            lock (LibCd.DiscLock) data = Runtime.Cd!.ReadSectorData(lba + i, size);
            for (int j = 0; j < data.Length; j++)
                m.WriteU8(buf + (uint)(i * size + j), data[j]);
        }
        _lastIntr = Complete;
        if (_cbRead != 0) InvokeCallback(_cbRead, Complete);
        c.V0 = 1;
    }

    public static void DsReadSync(CpuContext c, IMemory m)
    {
        if (ValidPtr(c.A0)) WriteResult(m, c.A0);
        c.V0 = 0; // no sectors remaining, read completed inline
    }

    public static void DsGetSector(CpuContext c, IMemory m)
    {
        // a0 = dest, a1 = word count; delivers the sector at the current read
        // position and advances it (the ReadN + DsReady + DsGetSector pump)
        uint madr = c.A0;
        int words = (int)c.A1;
        Dispatcher.LoadByLba(_seekLba);
        byte[] data;
        lock (LibCd.DiscLock) data = Runtime.Cd!.ReadSectorData(_seekLba, 2048);
        int bytes = Math.Min(data.Length, words * 4);
        for (int j = 0; j < bytes; j++)
            m.WriteU8(madr + (uint)j, data[j]);
        Log.Sdk($"DsGetSector lba={_seekLba} dst=0x{madr:X8} words={words}");
        _seekLba++;
        c.V0 = 1;
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
}
