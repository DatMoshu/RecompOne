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
    /// <summary>Where the drive is positioned. Exposed read-only so a port can attribute a
    /// transfer to a disc location -- BFM needs it to work out which sectors a raw CD-&gt;RAM DMA
    /// is delivering, since that path bypasses the PAC/overlay model entirely.</summary>
    public int CurrentLba => _seekLba;
    private byte _mode;
    // CdlSetfilter state. Mode bit 0x08 (CdlModeSF) makes the drive play ONLY the
    // Form2 audio sectors whose subheader file/channel match these; without it every
    // audio sector on the track is fed to the SPU. A voice line and its background
    // music are interleaved on the same track and told apart only by this pair.
    private byte _filterFile, _filterChannel;
    private byte[] _dataBuf = new byte[2048];

    private int _dataFifoPos;
    private bool _dataReady;
    private bool _reading;
    /// <summary>CD-DA playback requested by CdlPlay(0x03). Only that command sets it, so
    /// every port that never plays redbook audio sees the previous status byte exactly.</summary>
    private bool _playing;
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
                _playing = false;
                _streamPending = false;
                QueueIrq(3, [DriveStatus()]);
                QueueIrq(2, [DriveStatus()]);
                break;
            case 0x09: // Pause
                _reading = false;
                _playing = false;
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
            case 0x0D: // Setfilter(file, channel) — which XA-ADPCM stream to listen to
                // Unimplemented, this answered INT5 (error). libcd's CdControl then returns
                // 0, and a caller that retries — BFM's streamed-voice command does, once per
                // frame forever — never advances. That is what made BFM's "shout" dialogue
                // balloon open and then sit there blank: the balloon's message object parks
                // in its wait state until the voice command completes, so a CD command the
                // controller refused presented as a text bug several layers away.
                if (prms.Count >= 2) { _filterFile = prms[0]; _filterChannel = prms[1]; }
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
            case 0x03: // Play — start CD-DA playback of a redbook audio track.
                // Unimplemented, this hit the default INT5 branch and Dino Crisis retried it
                // forever (it wants track 2 for its title music), the third instance of the
                // retry trap documented on 0x0D. An optional BCD track parameter selects the
                // track; with none, playback resumes from the last Setloc.
                // HONEST LIMIT: this makes the drive REPORT playback so the caller proceeds.
                // No audio is produced — the redbook track is not fed to the mixer, which is
                // a separate piece of work (CdController has no CD-DA sample path at all).
                if (prms.Count >= 1 && BcdToInt(prms[0]) > 0)
                {
                    var (pm, ps_, pf) = _fs.TrackStartMsf(BcdToInt(prms[0]));
                    _seekLba = (pm * 60 + ps_) * 75 + pf - 150;
                }
                _reading = false;
                _playing = true;
                QueueIrq(3, [DriveStatus()]);
                break;
            case 0x19: // Test — sub-function selected by the first parameter.
                // Another instance of the retry trap: unhandled, it answered INT5 and Dino
                // Crisis reissued it once per frame forever. 0x20 is the one sub-function
                // games actually depend on (drive firmware date/version); everything else
                // gets a plain status ack rather than an error, because an unknown SUB-command
                // is not an invalid command and must not restart the retry loop.
                if (prms.Count >= 1 && prms[0] == 0x20)
                    QueueIrq(3, [0x94, 0x09, 0x19, 0xC0]);
                else
                    QueueIrq(3, [DriveStatus()]);
                break;
            case 0x1A: // GetID — disc identity and region licence, ack then result.
                // Same INT5 trap as 0x0D/0x1E: unimplemented, a retrying caller spins.
                // Byte 1 = 0 (licensed, not denied), byte 2 = 0x20 (data disc), then the
                // four licence characters. NOTE: hardcoded SCEA (NTSC-U) — the runtime has
                // no region field today and no currently mapped game but Dino Crisis (USA)
                // sends this. A PAL (SCEE) or NTSC-J (SCEI) port must make this a property
                // of the mounted disc rather than a constant.
                QueueIrq(3, [DriveStatus()]);
                QueueIrq(2, [DriveStatus(), 0x00, 0x20, 0x00,
                             (byte)'S', (byte)'C', (byte)'E', (byte)'A']);
                break;
            case 0x1E: // ReadTOC — re-read the table of contents from the lead-in.
                // Unimplemented, this fell through to the default INT5 (error) branch, which
                // is the same trap documented on 0x0D above: libcd's CdControl returns 0 on
                // INT5, so a caller that retries once per frame never advances and the stall
                // surfaces far away from the CD. Dino Crisis issues this at boot and retries
                // it indefinitely. Our TOC comes from the cue at mount time and cannot go
                // stale, so there is genuinely nothing to re-read: ack, then complete, which
                // is what the drive does. Unreachable for a game that never sends 0x1E.
                QueueIrq(3, [DriveStatus()]);
                QueueIrq(2, [DriveStatus()]);
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
        // DO NOT deliver the next sector from here. It was tried (2026-09-06) and it breaks
        // the boot: this ack runs inside the game's CD ready callback, so delivering an INT1
        // re-enters libcd's cb_read while the previous call is still unwinding, and its
        // `remaining` counter goes NEGATIVE (observed remain=-2). BFM then re-issues
        // SetLoc/SetMode/ReadN/Pause once per frame at lba 16 forever -- 2397 of each over
        // 2400 frames -- and never reaches the movie (XA sectors 188 -> 0). This is the same
        // re-entrancy the deferred-first-INT1 fix in StartRead and the "VSync(-1) must not
        // present while InDeliver" fix in LibEtc each exist to prevent. The sector release
        // point has to be OUTSIDE the callback unwind, which is what the per-frame pump in
        // Runtime.PresentFrame already is.
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

    /// <summary>True when the sector this step consumed was a data sector and an INT1 was
    /// raised for it. False after an XA audio sector, which the drive swallows silently.</summary>
    public bool LastStepRaisedIrq { get; private set; }

    /// <summary>Consume ONE sector of budget. Returns true if a sector was consumed at all.
    ///
    /// <para>An XA audio sector costs a slot exactly like a data sector, because on real
    /// hardware the drive reads the disc at a fixed rate and cannot skip past the audio it
    /// is not playing. This used to drain a whole run of consecutive audio sectors (up to
    /// 64) inside one step, which is invisible on an interleaved STR — video data breaks the
    /// run after a few sectors — but catastrophic on a pure-XA stream such as BFM's dialogue
    /// voice lines, where nothing breaks the run: a 1848-sector voice was consumed in 24
    /// steps, about 10 frames, so the line started and cut out almost immediately. It also
    /// meant the 65th sector, still audio, was handed to the data FIFO as if it were
    /// data.</para></summary>
    public bool AdvanceStreaming()
    {
        LastStepRaisedIrq = false;
        if (!_reading || !_streamPending) return false;
        if (_irqFlags != 0 || _pendingIrqs.Count > 0) return false;
        _streamPending = false;
        if (!ReadNextSector())
        {
            // Audio: consumed, no INT1. Stay armed so the next budget slot keeps the
            // stream moving rather than waiting for an ack that will never come.
            _streamPending = true;
            return true;
        }
        LastStepRaisedIrq = true;
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

    /// <summary>Consume the sector at <c>_seekLba</c>. Returns false if it was an XA audio
    /// sector (decoded or filtered out, but never delivered as data), true if it is now in
    /// the data buffer ready for an INT1.</summary>
    private bool ReadNextSector()
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
                var sub = _fs.ReadSectorData(_seekLba, 2336); // from the subheader
                if ((sub[2] & 0x04) != 0)
                {
                    // Mode bit 0x08 (CdlModeSF): only the filtered file/channel is audible.
                    // The others are still consumed by the drive rather than delivered as
                    // data — they are audio sectors either way.
                    bool match = (_mode & 0x08) == 0
                              || (sub[0] == _filterFile && sub[1] == _filterChannel);
                    if (match) XaAudio.DecodeSector(sub, 8, sub[3]);
                    RecompOne.Runtime.Log.Cd($"xa lba={_seekLba} file={sub[0]} chan={sub[1]} "
                        + $"coding=0x{sub[3]:X2} filt={_filterFile}/{_filterChannel} {(match ? "play" : "skip")}");
                    _seekLba++;
                    return false;   // one sector of budget spent; no data, so no INT1
                }
            }
            // Mode bit 0x20 = whole-sector reads: the data FIFO exposes 0x924 bytes
            // from the sector header (header+subheader+data+EDC/ECC); libcd validates
            // the 12-byte header/subheader before fetching the 2048 data bytes.
            _dataBuf = (_mode & 0x20) != 0 ? _fs.ReadSectorData(_seekLba, 2340) : _fs.ReadSector(_seekLba);
            RecompOne.Runtime.Log.Cd($"sector lba={_seekLba} mode=0x{_mode:X2} hdr={Convert.ToHexString(_dataBuf.AsSpan(0, Math.Min(16, _dataBuf.Length)))}");
            _seekLba++;
            return true;
        }
        catch
        {
            Array.Clear(_dataBuf);
            return true; // deliver the empty sector rather than stalling the stream
        }
    }

    bool _shellSeen = true;   // nothing to report until a swap happens

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
        _shellSeen = false;
        RecompOne.Runtime.Log.Cd($"disc swapped -> {Path.GetFileName(cuePath)}");
    }

    // shell-open flag (0x10) that games poll via GetStat to detect a disc change,
    // stays set while the virtual lid is open, then is reported once more after close
    public byte ShellStatusBits()
    {
        bool open = Environment.TickCount64 < _shellOpenUntil;
        if (!open && !_shellLatch) return 0;
        // "We swapped the disc" and "the game noticed" are different claims, and only the second
        // one means multi-disc works. Say once, per swap, that the game actually read the bit --
        // silence here after a swap means its disc-verify machine is polling something else (or
        // nothing), which is the failure this bit exists to prevent.
        if (!_shellSeen)
        {
            _shellSeen = true;
            // NOT Log.Cd: that is off unless --trace-cd, and this is the one line that
            // distinguishes "we swapped the disc" from "the game noticed", once per swap.
            System.Console.WriteLine("[disc] shell-open bit read by the game — the disc change was detected");
        }
        if (open) return 0x10;
        _shellLatch = false;
        return 0x10;
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
    private byte DriveStatus() => (byte)(0x02 | (_reading && PSMemory.HwIrqModel ? 0x20 : 0)
                                             | (_playing ? 0x80 : 0) | ShellStatusBits());

    private static byte ToBcd(int v) => (byte)(((v / 10) << 4) | (v % 10));
    private static int BcdToInt(byte v) => (v >> 4) * 10 + (v & 0xF);

    private static int BcdToLba(byte mm, byte ss, byte ff)
    {
        int m = (mm >> 4) * 10 + (mm & 0xF);
        int s = (ss >> 4) * 10 + (ss & 0xF);
        int f = (ff >> 4) * 10 + (ff & 0xF);
        return (m * 60 + s) * 75 + f - 150;
    }

    // ---- Savestate ------------------------------------------------------------------
    // Games driven through the BIOS/libds HLE (Parasite Eve) never touch these fields, so
    // for them this block round-trips zeros and changes nothing. It exists for the MMIO
    // path (Brave Fencer Musashi), where the drive IS this state: a load that restored RAM
    // but left _reading/_streamPending/_irqFlags as they were would resume with a drive
    // either stalled or streaming sectors the restored game is no longer expecting.
    //
    // Deliberately NOT captured: _fs (the mounted disc is host-owned and outlives a state;
    // restoring it would fight the disc picker), _m, and _dispatching (transient — it is
    // false at any point safe enough to take a state at all). Also not captured: the
    // Setfilter file/channel — the game re-issues Setfilter immediately before every
    // stream it starts, so the pair is re-established within one command of any restore,
    // and adding it mid-block would shift the byte layout and silently misparse every
    // state written by an earlier build (the version floor would have to move, throwing
    // away states that are otherwise perfectly good).
    public void SaveState(BinaryWriter w)
    {
        w.Write(_shellOpenUntil);
        w.Write(_shellLatch);
        w.Write(_index);
        w.Write(_irqFlags);
        w.Write(_lastIrq);
        w.Write(_seekLba);
        w.Write(_mode);
        w.Write(_dataFifoPos);
        w.Write(_dataReady);
        w.Write(_reading);
        w.Write(_streamPending);
        w.Write(_sectorBudget);
        w.Write(_dataBuf.Length);
        w.Write(_dataBuf);
        WriteBytes(w, _paramFifo);
        WriteBytes(w, _responseFifo);
        w.Write(_pendingIrqs.Count);
        foreach (var (irqType, response) in _pendingIrqs)
        {
            w.Write(irqType);
            w.Write(response.Length);
            w.Write(response);
        }
    }

    public void LoadState(BinaryReader r)
    {
        _shellOpenUntil = r.ReadInt64();
        _shellLatch = r.ReadBoolean();
        _index = r.ReadByte();
        _irqFlags = r.ReadByte();
        _lastIrq = r.ReadByte();
        _seekLba = r.ReadInt32();
        _mode = r.ReadByte();
        _dataFifoPos = r.ReadInt32();
        _dataReady = r.ReadBoolean();
        _reading = r.ReadBoolean();
        _streamPending = r.ReadBoolean();
        _sectorBudget = r.ReadDouble();
        _dataBuf = r.ReadBytes(r.ReadInt32());
        ReadBytes(r, _paramFifo);
        ReadBytes(r, _responseFifo);
        _pendingIrqs.Clear();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            byte irqType = r.ReadByte();
            _pendingIrqs.Enqueue((irqType, r.ReadBytes(r.ReadInt32())));
        }
    }

    static void WriteBytes(BinaryWriter w, Queue<byte> q)
    {
        w.Write(q.Count);
        foreach (var b in q) w.Write(b);
    }

    static void ReadBytes(BinaryReader r, Queue<byte> q)
    {
        q.Clear();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++) q.Enqueue(r.ReadByte());
    }
}
