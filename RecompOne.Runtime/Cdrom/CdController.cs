using RecompOne.Runtime.Bios;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Cdrom;

public sealed class CdController
{
    private CueFs _fs;
    private readonly IMemory _m;
    private long _shellOpenUntil;
    private bool _shellLatch;

    private byte _index;
    private readonly Queue<byte> _paramFifo = new();
    private readonly Queue<byte> _responseFifo = new();
    private readonly Queue<(byte irqType, byte[] response)> _pendingIrqs = new();
    private byte _irqFlags;
    private int _seekLba;
    private byte _mode;
    private byte[] _dataBuf = new byte[2048];

    private int _dataFifoPos;
    private bool _dataReady;
    private bool _reading;
    private bool _streamPending;
    private byte _lastIrq;

    public CdController(CueFs fs, IMemory m)
    {
        _fs = fs;
        _m = m;
        BiosA.SetFs(fs);
        BiosA.SetCd(this);
        Runtime.Cd = this;
        DiscManager.InitFromBootDisc(fs.CuePath);
    }

    public void LoadToMemory(string path, uint address, int offset = 0, int length = -1)
    {
        var data = _fs.ReadFile(path);
        int count = length < 0 ? data.Length - offset : length;
        for (int i = 0; i < count; i++)
            _m.WriteU8(address + (uint)i, data[offset + i]);
        RecompOne.Runtime.Log.Cd($"{path} -> 0x{address:X8} | {count} bytes");
        Dispatcher.TryLoad(CdUtils.OverlayName(CdUtils.ExtractFileName(path)));
    }

    public byte Read(uint phys)
    {
        return (phys & 3) switch
        {
            0 => (byte)((_index & 3) | (_paramFifo.Count == 0 ? 0x08 : 0) | 0x10 | (_responseFifo.Count > 0 ? 0x20 : 0) | (_dataReady ? 0x40 : 0)),
            1 => _responseFifo.Count > 0 ? _responseFifo.Dequeue() : (byte)0,
            2 => ReadDataByte(),
            _ => _index == 1 ? _irqFlags : (byte)0,
        };
    }

    public void Write(uint phys, byte val)
    {
        switch (phys & 3)
        {
            case 0:
                _index = (byte)(val & 3);
                break;
            case 1:
                if (_index == 0) ExecuteCommand(val);
                break;
            case 2:
                if (_index == 0) _paramFifo.Enqueue(val);
                else if (_index == 1) _paramFifo.Clear();
                break;
            case 3:
                if (_index == 0)
                {
                    // BFRD is edge-triggered: re-asserting it while the sector window is
                    // already open must not rewind the FIFO (libcd strobes per DMA chunk).
                    if ((val & 0x80) != 0) { if (!_dataReady) { _dataFifoPos = 0; _dataReady = true; } }
                    else _dataReady = false;
                }
                else if (_index == 1)
                {
                    RecompOne.Runtime.Log.Cd($"ack 0x{val:X2}: flags 0x{_irqFlags:X2} -> 0x{_irqFlags & ~val:X2}");
                    _irqFlags &= (byte)~val;
                    if (_irqFlags == 0) AfterAck();
                }
                break;
        }
    }

    private void ExecuteCommand(byte cmd)
    {
        RecompOne.Runtime.Log.Cd($"cmd 0x{cmd:X2} t={Environment.TickCount64 % 100000} count={_m.ReadU32(0x8006CFA0u)} remain={(int)_m.ReadU32(0x8006CFB4u)} lba={_seekLba}");
        var prms = new List<byte>();
        while (_paramFifo.Count > 0) prms.Add(_paramFifo.Dequeue());

        switch (cmd)
        {
            case 0x01:
                QueueIrq(3, [DriveStatus()]);
                break;
            case 0x02: //Setloc
                if (prms.Count >= 3)
                    _seekLba = BcdToLba(prms[0], prms[1], prms[2]);
                QueueIrq(3, [DriveStatus()]);
                break;
            case 0x06: // ReadN
                StartRead();
                break;
            case 0x08: //Stop
                _reading = false;
                _streamPending = false;
                QueueIrq(3, [DriveStatus()]);
                QueueIrq(2, [DriveStatus()]);
                break;
            case 0x09: // Pause
                _reading = false;
                _streamPending = false;
                QueueIrq(3, [DriveStatus()]);
                QueueIrq(2, [DriveStatus()]);
                break;
            case 0x0A:
                QueueIrq(3, [DriveStatus()]);
                QueueIrq(2, [DriveStatus()]);
                break;
            case 0x0B: // mute
                QueueIrq(3, [DriveStatus()]);
                break;
            case 0x0C: // demute
                QueueIrq(3, [DriveStatus()]);
                break;
            case 0x0E: // set mode
                if (prms.Count >= 1) _mode = prms[0];
                QueueIrq(3, [DriveStatus()]);
                break;
            case 0x13: // GetTN
                QueueIrq(3, [DriveStatus(), ToBcd(_fs.FirstTrack), ToBcd(_fs.LastTrack)]);
                break;
            case 0x14: // GetTD (track start MSF; track 0 = lead-out)
            {
                int track = prms.Count > 0 ? BcdToInt(prms[0]) : 0;
                var (mm, ss, _) = _fs.TrackStartMsf(track);
                QueueIrq(3, [DriveStatus(), ToBcd(mm), ToBcd(ss)]);
                break;
            }
            case 0x15: // seek L
            case 0x16: //seek P
                QueueIrq(3, [DriveStatus()]);
                QueueIrq(2, [DriveStatus()]);
                break;
            case 0x1B: // ReadS (streaming): fresh XA audio pipeline per stream
                if (PSMemory.HwIrqModel) XaAudio.Reset();
                StartRead();
                break;
            default:
                Console.WriteLine($"[CD] command 0x{cmd:X2} is unknow");
                QueueIrq(5, [DriveStatus(), 0x40]);
                break;
        }
    }

    // ReadN/ReadS: the INT3 ack is immediate, but the first sector-ready INT1 must
    // NOT land inside the same command write. Real hardware delivers INT1 only after
    // seek+read latency; libcd's CdRead arms its state (remaining = count) AFTER
    // CdControlF(ReadN) returns, and its ready callback treats remaining==0 as
    // "done" -> Pause. A synchronous INT1 therefore completed the read before it
    // was armed and left CdReadSync stalling/retrying forever (BFM boot loop).
    // With the hardware-IRQ model the per-frame streaming pump delivers the first
    // INT1 next frame instead; the legacy path keeps the immediate delivery since
    // nothing else pumps it.
    private void StartRead()
    {
        _reading = true;
        if (PSMemory.HwIrqModel)
        {
            Dispatcher.LoadByLba(_seekLba); // MMIO-driven games: a code overlay keyed on this read's LBA
            _streamPending = true;
            QueueIrq(3, [DriveStatus()]);
            return;
        }
        ReadNextSector();
        QueueIrq(3, [DriveStatus()]);
        QueueIrq(1, [DriveStatus()]);
    }

    private void QueueIrq(byte irqType, byte[] response)
    {
        if (_irqFlags == 0 && _pendingIrqs.Count == 0)
            DeliverImmediate(irqType, response);
        else
            _pendingIrqs.Enqueue((irqType, response));
    }

    private void AfterAck()
    {
        if (_pendingIrqs.Count > 0) { DeliverNext(); return; }
        if (_reading && _lastIrq == 1) _streamPending = true;
        ClearInInterrupt();
    }

    // Real drive pacing for MMIO-driven reads: 75 sectors/s at 1x, 150 at 2x (mode 0x80),
    // i.e. 1.25 / 2.5 sectors per 60 Hz frame. Delivering sectors as fast as the game acks
    // them (up to 16/frame) overran libcdstream's STR ring and fed the VLC decoder garbage.
    private double _sectorBudget;
    public int SectorsThisFrame()
    {
        _sectorBudget += (_mode & 0x80) != 0 ? 2.5 : 1.25;
        int n = (int)_sectorBudget;
        _sectorBudget -= n;
        return n;
    }

    public bool AdvanceStreaming()
    {
        if (!_reading || !_streamPending) return false;
        if (_irqFlags != 0 || _pendingIrqs.Count > 0) return false;
        _streamPending = false;
        ReadNextSector();
        DeliverImmediate(1, [DriveStatus()]);
        return true;
    }

    private void DeliverImmediate(byte irqType, byte[] response)
    {
        _responseFifo.Clear();
        foreach (var b in response) _responseFifo.Enqueue(b);
        _irqFlags = irqType;
        _lastIrq = irqType;
        SetInInterrupt(1);
        // libcd's ISR consumes every INT type (it reads the flags register, stores
        // the response, and sets the Sync/Ready flags its poll path checks), so the
        // CDROM hardware IRQ must fire for command acks too, not just sector-ready.
        _m.RaiseIrq(2); // no-op unless HwIrqModel
        DispatchSync();
    }

    private void DeliverNext()
    {
        var (irqType, response) = _pendingIrqs.Dequeue();
        _responseFifo.Clear();
        foreach (var b in response) _responseFifo.Enqueue(b);
        _irqFlags = irqType;
        _lastIrq = irqType;
        SetInInterrupt(1);
        _m.RaiseIrq(2);
        DispatchSync();
    }

    private bool _dispatching;

    // CD_cw/CD_sync busy-spin on ISR-set flags without ever yielding to the
    // per-frame pump, so a raised CDROM IRQ must be delivered right where the
    // hardware would preempt: synchronously at the raising MMIO write. Guarded
    // so an ack inside the ISR (AfterAck -> DeliverNext) can't recurse; the
    // pending INT then lands on the next frame dispatch instead.
    private void DispatchSync()
    {
        if (!PSMemory.HwIrqModel || _dispatching) return;
        _dispatching = true;
        try
        {
            // Loop: an INT queued from inside the handler (ack -> AfterAck -> DeliverNext,
            // e.g. the completion Pause libcd issues from its ready callback) skips the
            // nested dispatch and must be delivered once this outer dispatch unwinds.
            for (int i = 0; i < 8 && _irqFlags != 0; i++)
                Runtime.DispatchIrq(2); // CDROM slot of the game's handler table
        }
        finally { _dispatching = false; }
    }

    private byte ReadDataByte()
    {
        if (!_dataReady || _dataFifoPos >= _dataBuf.Length) { _dataReady = false; return 0; }
        byte b = _dataBuf[_dataFifoPos++];
        if (_dataFifoPos >= _dataBuf.Length) _dataReady = false;
        return b;
    }

    public void DmaReadData(IMemory m, uint addr, uint byteCount)
    {
        for (uint i = 0; i < byteCount; i++)
            m.WriteU8(addr + i, _dataFifoPos < _dataBuf.Length ? _dataBuf[_dataFifoPos++] : (byte)0);
        if (_dataFifoPos >= _dataBuf.Length) _dataReady = false;
    }

    public void LoadSectorToFifo(byte[] data)
    {
        _dataBuf = (byte[])data.Clone();
        _dataFifoPos = 0;
        _dataReady = true;
    }

    private void SetInInterrupt(ushort val)
    {
        if (BiosB.IntrEnvInInterruptAddr != 0)
            _m.WriteU16(BiosB.IntrEnvInInterruptAddr, val);
    }

    private void ClearInInterrupt()
    {
        if (BiosB.IntrEnvInInterruptAddr != 0)
            _m.WriteU16(BiosB.IntrEnvInInterruptAddr, 0);
    }

    private void ReadNextSector()
    {
        try
        {
            // Mode 0x40 (XA-ADPCM on): the drive consumes Form2 audio sectors itself —
            // decodes them to the SPU CD-audio input and raises NO INT1 for them — so an
            // STR stream's interleaved audio sectors never reach the data FIFO. Delivering
            // them as data made libcdstream see a non-0x0160 header every 8th sector and
            // reset its ring (BFM LOGOB.STR hang / VLC-decoder garbage). Mirror hardware.
            if (PSMemory.HwIrqModel && (_mode & 0x40) != 0)
            {
                for (int guard = 0; guard < 64; guard++)
                {
                    var sub = _fs.ReadSectorData(_seekLba, 2336); // from the subheader
                    if ((sub[2] & 0x04) == 0) break;
                    XaAudio.DecodeSector(sub, 8, sub[3]);
                    RecompOne.Runtime.Log.Cd($"xa lba={_seekLba} coding=0x{sub[3]:X2}");
                    _seekLba++;
                }
            }
            // Mode bit 0x20 = whole-sector reads: the data FIFO exposes 0x924 bytes
            // from the sector header (header+subheader+data+EDC/ECC); libcd validates
            // the 12-byte header/subheader before fetching the 2048 data bytes.
            _dataBuf = (_mode & 0x20) != 0 ? _fs.ReadSectorData(_seekLba, 2340) : _fs.ReadSector(_seekLba);
            RecompOne.Runtime.Log.Cd($"sector lba={_seekLba} mode=0x{_mode:X2} hdr={Convert.ToHexString(_dataBuf.AsSpan(0, Math.Min(16, _dataBuf.Length)))}");
            _seekLba++;
        }
        catch
        {
            Array.Clear(_dataBuf);
        }
    }

    public CueFs Fs => _fs;
    public byte DriveStatusByte() => DriveStatus();

    public void SwapDisc(string cuePath)
    {
        var newFs = CueFs.Open(cuePath);
        lock (Sdk.LibCd.DiscLock)
        {
            _reading = false;
            _streamPending = false;
            _dataReady = false;
            _dataFifoPos = 0;
            var old = _fs;
            _fs = newFs;
            BiosA.SetFs(newFs);
            old.Dispose();
        }
        _shellOpenUntil = Environment.TickCount64 + 1000;
        _shellLatch = true;
        RecompOne.Runtime.Log.Cd($"disc swapped -> {Path.GetFileName(cuePath)}");
    }

    // shell-open flag (0x10) that games poll via GetStat to detect a disc change,
    // stays set while the virtual lid is open, then is reported once more after close
    public byte ShellStatusBits()
    {
        if (Environment.TickCount64 < _shellOpenUntil) return 0x10;
        if (_shellLatch) { _shellLatch = false; return 0x10; }
        return 0;
    }

    public byte[] ReadSectorData(int lba)
    {
        _seekLba = lba;
        ReadNextSector();
        return (byte[])_dataBuf.Clone();
    }

    public byte[] ReadSectorData(int lba, int size) => _fs.ReadSectorData(lba, size);

    public void QueueAsyncSeekL(byte mm, byte ss, byte ff)
    {
        _seekLba = BcdToLba(mm, ss, ff);
        QueueIrq(3, [DriveStatus()]);
        QueueIrq(2, [DriveStatus()]);
    }

    public void QueueAsyncGetStatus()
    {
        QueueIrq(3, [DriveStatus()]);
    }

    public void QueueAsyncSetMode(byte mode)
    {
        QueueIrq(3, [DriveStatus()]);
    }

    public void QueueAsyncReadSector(uint count, uint dstAddr, uint mode)
    {
        for (uint i = 0; i < count; i++)
        {
            ReadNextSector();
            int sectorSize = (mode & 0x30) == 0 ? 2048 : 2048; //fix
            for (int j = 0; j < Math.Min(_dataBuf.Length, sectorSize); j++)
                _m.WriteU8(dstAddr + i * (uint)sectorSize + (uint)j, _dataBuf[j]);
            _seekLba++;
        }
        QueueIrq(3, [DriveStatus()]);
        QueueIrq(1, [DriveStatus()]);
        QueueIrq(2, [DriveStatus()]);
    }

    // Bit 0x20 = reading (libcd's CdRead pauses a busy drive before re-issuing);
    // only reported under the hardware-IRQ model so legacy HLE paths stay identical.
    private byte DriveStatus() => (byte)(0x02 | (_reading && PSMemory.HwIrqModel ? 0x20 : 0) | ShellStatusBits());

    private static byte ToBcd(int v) => (byte)(((v / 10) << 4) | (v % 10));
    private static int BcdToInt(byte v) => (v >> 4) * 10 + (v & 0xF);

    private static int BcdToLba(byte mm, byte ss, byte ff)
    {
        int m = (mm >> 4) * 10 + (mm & 0xF);
        int s = (ss >> 4) * 10 + (ss & 0xF);
        int f = (ff >> 4) * 10 + (ff & 0xF);
        return (m * 60 + s) * 75 + f - 150;
    }
}
