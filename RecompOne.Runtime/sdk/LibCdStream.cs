using System.Diagnostics;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Sdk;

public static class LibCdStream
{
    const int HeaderSize = 32;
    const int SlotData = 2016;
    const ushort VideoMagic = 0x0160;

    public static bool InUse { get; private set; }
    public static int MaxFrame;
    public static int EnqMaxFrame;              // highest STR frame# delivered (end detection)
    public static void ForceStop() { _active = false; _reading = false; MaxFrame = 0; _skipToEnd = false; }
    static uint _statusBase;
    static int _slots;
    static uint _dataBase;

    static volatile bool _active;
    static volatile bool _reading;
    static int _pendingLba = -1;
    static int _streamLba = -1;
    static int _streamStartLba;
    static readonly Stopwatch _clock = new();

    static int _writeIdx;
    static bool[] _busy = System.Array.Empty<bool>();
    static readonly Queue<(int start, int n)> _ready = new();
    static int _prevStart = -1, _prevN;

    static Thread? _thread;
    static volatile bool _run;
    static readonly object _lock = new();

    public static void StSetRing(CpuContext c, IMemory m)
    {
        InUse = true;
        lock (_lock)
        {
            _statusBase = c.A0;
            _slots = (int)c.A1;
            _dataBase = _statusBase + (uint)(_slots * HeaderSize);
            ResetRing(m);
        }
        EnsureThread();
        Log.Sdk($"StSetRing base=0x{_statusBase:X8} slots={_slots} data=0x{_dataBase:X8}");
    }

    public static void StClearRing(CpuContext c, IMemory m)
    {
        lock (_lock) ResetRing(m);
        c.V0 = 0;
        Log.Sdk("StClearRing");
    }

    public static void StUnSetRing(CpuContext c, IMemory m)
    {
        _active = false;
        _reading = false;
        Log.Sdk("StUnSetRing");
    }

    public static void StSetStream(CpuContext c, IMemory m)
    {
        lock (_lock)
        {
            _streamLba = -1;
            ResetRing(m);
            XaAudio.Reset();
        }
        _active = true;
        EnsureThread();
        Log.Sdk("StSetStream");
    }

    public static void StSetMask(CpuContext c, IMemory m) { c.V0 = 0; Log.Sdk("StSetMask"); }

    public static bool FrameLog;
    static int _frameLogN;
    static int _getNextLog;
    static int _feedLog;

    // When the linear read runs off the end of the STR video data (many consecutive
    // non-video sectors), loop back to the stream start. This makes the STR frame
    // number wrap low, which is how the game's player detects end-of-cinematic
    // (it treats a frame# that drops below the running max as the last frame).
    static int _skip;
    const int SkipLimit = 200;

    // ---- Skip to the end of the cinematic -------------------------------------------
    // A cutscene skip that fast-forwards has to DECODE every frame it skips, and the
    // measurement in CLAUDE.md says that decode is the bound (~2.7x, the CD producer idle
    // and always ahead). Seeking is the way out: the video frames the player will never
    // see do not have to be delivered at all.
    //
    // This does NOT invent an ending. It moves the read cursor to the LAST video frame in
    // the stream and then lets the existing end-of-stream path run untouched: the producer
    // delivers that final frame, runs into the non-video tail, MaybeLoop wraps the cursor
    // back to the start, the frame number drops below the running max, and the game's own
    // player sees exactly the "last frame" condition it always sees. Same trigger, same
    // code, reached in milliseconds instead of half a minute.
    //
    // The seek is a header-only scan -- it reads sector sub-headers and never decodes -- so
    // the cost is a few thousand cached sector reads.
    //
    // Inert unless something calls SkipToEnd(); nothing in the engine does.
    static volatile bool _skipToEnd;
    public static int LastSeekScanned, LastSeekFrom, LastSeekTo;

    /// <summary>Ask the stream to jump to its final video frame. Honoured by the producer
    /// thread at the top of its next iteration (it owns _streamLba; setting that from
    /// another thread would race with a read in flight). Returns false if no stream is
    /// running, so a caller can say "nothing to skip" rather than silently doing nothing.</summary>
    public static bool SkipToEnd()
    {
        if (!InUse || !_active) return false;
        _skipToEnd = true;
        return true;
    }

    // Scan forward for the last sector that STARTS a video frame, mirroring the producer's
    // own accept/reject tests so the two cannot disagree about what a frame is. Stops at the
    // same SkipLimit run of non-video sectors that MaybeLoop uses to recognise the end.
    static void SeekToLastFrame(Cdrom.CdController cd)
    {
        int from = _streamLba, lba = _streamLba, lastStart = -1, miss = 0, scanned = 0;
        int firstFno = -1, lastFno = -1, maxFno = -1;
        const int MaxScan = 400000; // a hard stop: a malformed stream must not spin this thread
        while (scanned < MaxScan)
        {
            byte[] sec;
            try { lock (LibCd.DiscLock) sec = cd.ReadSectorData(lba, 2336); }
            catch { break; } // ran off the disc; whatever we found before it is the end
            scanned++;

            int n = Read16(sec, 14);
            bool frameStart = Read16(sec, 8) == VideoMagic && Read16(sec, 12) == 0 && n > 0 && n <= _slots;
            if (frameStart)
            {
                // Offset 16 is the STR frame number (CollectFrame copies sec[8..40] verbatim
                // into the ring header, and end-of-cinematic reads that header at +8). Within
                // one movie it climbs; the NEXT movie packed after it on disc restarts at 1.
                // That drop is the movie boundary -- the very signal the game ends on -- so
                // stop there. Without this the scan runs straight through into whatever STR
                // follows and seeks to a frame belonging to a different cinematic.
                int fno = Read16(sec, 16);
                if (firstFno < 0) firstFno = fno;
                if (maxFno >= 0 && fno < maxFno) break;
                if (fno > maxFno) maxFno = fno;
                lastFno = fno;
                lastStart = lba;
                miss = 0;
            }
            else if (++miss >= SkipLimit) break;
            lba++;
        }
        LastSeekScanned = scanned; LastSeekFrom = from; LastSeekTo = lastStart;
        if (lastStart < 0)
        {
            Console.WriteLine($"[stream] skip-to-end found no further video frame in {scanned} sectors from {from}; leaving the stream alone");
            return;
        }
        _streamLba = lastStart;
        _skip = 0;
        // Everything queued in the XA decoder is the soundtrack of the part we just jumped
        // over. Left to play out it is heard as a second of garble after the cinematic ends.
        XaAudio.Flush();
        Console.WriteLine($"[stream] skip-to-end: lba {from} -> {lastStart} ({lastStart - from} sectors skipped, " +
                          $"{scanned} scanned, frame {firstFno} -> {lastFno})");
    }
    static void MaybeLoop()
    {
        if (_skip < SkipLimit || _streamStartLba < 0) return;
        _skip = 0;
        _streamLba = _streamStartLba;
        _clock.Restart();
    }
    public static void StGetNext(CpuContext c, IMemory m)
    {
        if (Log.SdkOn && (_getNextLog++ % 200) == 0)
            Log.Sdk($"StGetNext active={_active} reading={_reading} ready={_ready.Count} streamLba={_streamLba} pending={_pendingLba} inUse={InUse} slots={_slots}");
        if (!_active) { c.V0 = 1; return; }

        lock (_lock)
        {
            if (_prevStart >= 0)
            {
                for (int i = 0; i < _prevN; i++) _busy[_prevStart + i] = false;
                _prevStart = -1;
            }

            if (_ready.Count == 0) { c.V0 = 1; return; }

            var (start, n) = _ready.Dequeue();
            uint dataPtr = _dataBase + (uint)(start * SlotData);
            uint hdrPtr = _statusBase + (uint)(start * HeaderSize);
            m.WriteU32(c.A0, dataPtr);
            m.WriteU32(c.A1, hdrPtr);
            int fno = m.ReadU16(hdrPtr + 8);
            if (fno > MaxFrame) MaxFrame = fno;
            if (FrameLog && (_frameLogN++ % 20) == 0)
                Console.WriteLine($"[frame] deliver#{_frameLogN} hdr+8=0x{m.ReadU16(hdrPtr + 8):X4} hdr+10=0x{m.ReadU16(hdrPtr + 0x10):X4} hdr+12=0x{m.ReadU16(hdrPtr + 0x12):X4} desc[0x80122468]=0x{m.ReadU16(0x80122468u):X4} thr[0x801227E8]=0x{m.ReadU16(0x801227E8u):X4}");
            _prevStart = start;
            _prevN = n;
            c.V0 = 0;
        }
    }

    public static void StFreeRing(CpuContext c, IMemory m) { c.V0 = 0; Log.Sdk("StFreeRing"); }

    public static void StGetBackloc(CpuContext c, IMemory m)
    {
        // report the current stream position so a stalled stream restarts where it
        // left off instead of from the beginning
        int lba = _streamLba >= 0 ? _streamLba : _streamStartLba;
        if (c.A0 != 0 && c.A0 != 0xFFFFFFFF && lba >= 0)
        {
            int i = lba + 150;
            m.WriteU8(c.A0, ToBcd(i / 75 / 60));
            m.WriteU8(c.A0 + 1, ToBcd(i / 75 % 60));
            m.WriteU8(c.A0 + 2, ToBcd(i % 75));
            c.V0 = c.A0;
        }
        else c.V0 = 0xFFFFFFFFu;
        Log.Sdk($"StGetBackloc lba={lba}");
    }

    static byte ToBcd(int n) => (byte)(((n / 10) << 4) + (n % 10));

    static volatile float _sectorsPerSecond = 75f;

    internal static void OnReadStream(int lba) => OnReadStream(lba, LibCd.SectorsPerSecond);

    internal static void OnReadStream(int lba, double sectorsPerSecond)
    {
        if (!InUse) return;
        _sectorsPerSecond = (float)sectorsPerSecond;
        _pendingLba = lba;
        _reading = true;
        EnsureThread();
    }

    internal static void OnStopStream()
    {
        _reading = false;
    }

    static void ResetRing(IMemory m)
    {
        _writeIdx = 0;
        _prevStart = -1;
        _prevN = 0;
        _ready.Clear();
        _busy = _slots > 0 ? new bool[_slots] : System.Array.Empty<bool>();
        for (int i = 0; i < _slots; i++)
            m.WriteU16(_statusBase + (uint)(i * HeaderSize), 0);
    }

    static void EnsureThread()
    {
        if (_thread is { IsAlive: true }) return;
        _run = true;
        _thread = new Thread(StreamLoop) { IsBackground = true, Name = "CdStream" };
        _thread.Start();
    }

    static void StreamLoop()
    {
        while (_run)
        {
            var cd = Runtime.Cd;
            var m = Runtime.Mem;
            if (cd == null || m == null || !_active || !_reading || _slots <= 0)
            {
                Thread.Sleep(2);
                continue;
            }

            if (_streamLba < 0)
            {
                _streamLba = _pendingLba >= 0 ? _pendingLba : LibCd.CurrentLba;
                _streamStartLba = _streamLba;
                _clock.Restart();
            }

            // Honoured here, on the thread that owns _streamLba, and only once the cursor is
            // valid -- so a skip requested before the stream has picked a start still lands.
            if (_skipToEnd) { _skipToEnd = false; SeekToLastFrame(cd); }

            byte[] sec;
            try { lock (LibCd.DiscLock) sec = cd.ReadSectorData(_streamLba, 2336); }
            catch { Thread.Sleep(2); continue; }

            if (Log.SdkOn && (_feedLog++ % 40) == 0)
                Log.Sdk($"feed lba={_streamLba} sub2=0x{sec[2]:X2} m8=0x{Read16(sec,8):X4} w12=0x{Read16(sec,12):X4} n14={Read16(sec,14)}");

            if ((sec[2] & 0x04) != 0) { XaAudio.DecodeSector(sec, 8, sec[3]); _streamLba++; _skip++; MaybeLoop(); continue; }
            if (Read16(sec, 8) != VideoMagic || Read16(sec, 12) != 0) { _streamLba++; _skip++; MaybeLoop(); continue; }

            int n = Read16(sec, 14);
            if (n <= 0 || n > _slots) { _streamLba++; _skip++; MaybeLoop(); continue; }
            _skip = 0;

            // Real-CD-speed pacing, so normal playback looks/sounds right. Under turbo
            // (Host.Tas.TurboActive), drop ONLY this wall-clock gate, not the ring-full
            // backpressure check below — the earlier turbo/FMV stall was this clock
            // capping delivery to real time regardless of how fast the consumer could
            // drain the ring, not a ring-capacity problem. Removing backpressure too
            // would let delivery outrun the consumer and corrupt the stream (the exact
            // failure BFM hit with XA Form2 sectors resetting the ring every 8th sector).
            if (!Host.Tas.TurboActive)
            {
                double delivered = _clock.Elapsed.TotalSeconds * _sectorsPerSecond;
                if ((_streamLba - _streamStartLba) + n > delivered) { Thread.Sleep(1); continue; }
            }

            int start;
            lock (_lock)
            {
                if (_writeIdx + n > _slots) _writeIdx = 0;
                start = _writeIdx;
                bool free = true;
                for (int i = 0; i < n; i++) if (_busy[start + i]) { free = false; break; }
                if (!free) { FullWaits++; Thread.Sleep(1); continue; }
            }

            if (!CollectFrame(cd, m, start, n)) continue;

            int efno = m.ReadU16(_statusBase + (uint)(start * HeaderSize) + 8);
            if (efno > EnqMaxFrame) EnqMaxFrame = efno;
            if (FrameLog && (efno >= 1010 || efno <= 3))
                Console.WriteLine($"[enq] fno={efno} lba={_streamLba} enqMax={EnqMaxFrame}");
            lock (_lock)
            {
                for (int i = 0; i < n; i++) _busy[start + i] = true;
                _ready.Enqueue((start, n));
                _writeIdx = start + n;
            }
            FramesFed++; SectorsFed += n;
        }
    }

    static bool CollectFrame(Cdrom.CdController cd, IMemory m, int start, int n)
    {
        int collected = 0;
        int lba = _streamLba;
        while (collected < n)
        {
            byte[] sec;
            try { lock (LibCd.DiscLock) sec = cd.ReadSectorData(lba, 2336); }
            catch { return false; }
            lba++;

            if ((sec[2] & 0x04) != 0) { XaAudio.DecodeSector(sec, 8, sec[3]); continue; }
            if (Read16(sec, 8) != VideoMagic) continue;

            uint hdr = _statusBase + (uint)((start + collected) * HeaderSize);
            uint dat = _dataBase + (uint)((start + collected) * SlotData);
            for (int j = 0; j < HeaderSize; j++) m.WriteU8(hdr + (uint)j, sec[8 + j]);
            for (int j = 0; j < SlotData; j++) m.WriteU8(dat + (uint)j, sec[8 + HeaderSize + j]);
            collected++;
        }
        _streamLba = lba;
        Thread.MemoryBarrier();
        return true;
    }

    static ushort Read16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));

    // Feed-rate counters, for answering "is a slow FMV the producer or the consumer?"
    // with a number instead of a hypothesis. FullWaits counts iterations that found the
    // ring full (producer AHEAD -- the game is the slow half); Frames/Sectors are what
    // actually reached the game. Read by the --fps-log line; free when it is off.
    public static long FullWaits;
    public static long FramesFed;
    public static long SectorsFed;
}
