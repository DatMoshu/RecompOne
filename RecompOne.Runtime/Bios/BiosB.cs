using System.IO;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Bios;

public static class BiosB
{
    struct EvCB { public uint Status, Class, Spec, Mode, Func; }
    const int MaxEvents = 64;
    static readonly EvCB[] _evCBs = new EvCB[MaxEvents];
    struct TCB { public bool Used; }
    const int MaxThreads = 4;
    static readonly TCB[] _tcbs = new TCB[MaxThreads];

    // ---- BIOS threads (OpenTh / ChangeTh / CloseTh) -------------------------
    // OFF BY DEFAULT. With ThreadModel false, ChangeTh stays the historical
    // no-op and OpenTh keeps allocating from slot 0, so every existing port is
    // byte-identical. Only a game that actually runs code in a BIOS thread needs
    // this; as of 2026-09-06 that is Dino Crisis alone (PE1, BFM and PE2 emit no
    // OpenTh/CloseTh/ChangeTh stub at all).
    //
    // WHY REAL THREADS: a statically recompiled MIPS function is a C# method on
    // the real CLR stack, so a thread switch cannot be a jump -- the target's
    // stack has to exist somewhere. .NET has no fibers, so each BIOS thread gets
    // its own CLR thread and we hand a single baton between them. Exactly one
    // thread is ever runnable, which makes these cooperative coroutines rather
    // than concurrency: no other part of the runtime has to become thread-safe.
    //
    // Slot 0 is RESERVED for the original ("main") thread, because that is what
    // the BIOS does -- TCB[0] is the running thread at boot, so a game's own
    // OpenTh handles start at 1 and the well-known handle 0xFF000000 means
    // "switch back to main". Allocating slot 0 to a game thread would alias the
    // two and the switch would become a self-switch no-op.
    public static bool ThreadModel { get; private set; }

    /// <summary>Bounded wait for a handoff. A cooperative switch that never comes back
    /// would otherwise wedge the whole port with no diagnosis, so it fails loudly instead.</summary>
    public static int ThreadSwitchTimeoutMs = 5000;

    sealed class ThreadSlot
    {
        public readonly SemaphoreSlim Gate = new(0, 1);
        public Thread? Native;
        public uint Pc, Sp, Gp;
        public bool Started, Exited;
        /// <summary>CloseTh retired this thread while its CLR thread was still live.
        /// It must unwind at its next park rather than ever being resumed.</summary>
        public bool SelfClosed;
    }

    /// <summary>Thrown inside a BIOS thread that has been closed, to unwind its emulated
    /// call stack. Private control flow, never surfaced to a port.</summary>
    sealed class ThreadClosedSignal : Exception;

    static readonly ThreadSlot[] _slots = CreateSlots();
    static ThreadSlot[] CreateSlots()
    {
        var a = new ThreadSlot[MaxThreads];
        for (int i = 0; i < MaxThreads; i++) a[i] = new ThreadSlot();
        return a;
    }

    const int MainSlot = 0;
    static int _current = MainSlot;
    static int _mainManagedId;
    static volatile bool _shutdown;

    /// <summary>Opt in to real BIOS threads. MUST be called from the thread that will run
    /// the game's main loop (i.e. from Program.cs before Entry.Run), because that thread is
    /// recorded as TCB slot 0 and as the only thread allowed to present frames.</summary>
    public static void EnableThreads()
    {
        ThreadModel = true;
        _mainManagedId = Environment.CurrentManagedThreadId;
        _tcbs[MainSlot] = new TCB { Used = true };
        _slots[MainSlot].Started = true;
        _current = MainSlot;
    }

    /// <summary>Diagnostic view of the BIOS thread slots. Read-only; no behaviour change.</summary>
    public static string DescribeThreads()
    {
        if (!ThreadModel) return "threads off";
        var sb = new System.Text.StringBuilder($"current=slot{_current}");
        for (int i = 0; i < MaxThreads; i++)
        {
            var s = _slots[i];
            if (!_tcbs[i].Used && !s.Started && s.Pc == 0) continue;
            sb.Append($" [{i}] pc=0x{s.Pc:X8} used={(_tcbs[i].Used ? 1 : 0)} started={(s.Started ? 1 : 0)} " +
                      $"exited={(s.Exited ? 1 : 0)} closed={(s.SelfClosed ? 1 : 0)} " +
                      $"gate={s.Gate.CurrentCount} alive={(s.Native?.IsAlive == true ? 1 : 0)};");
        }
        return sb.ToString();
    }

    /// <summary>Guard for work that is pinned to the main thread -- above all frame
    /// presentation, since the GL context is thread-affine. Cheap no-op unless the thread
    /// model is on. Fails immediately and by name rather than corrupting GL state, which
    /// would surface later as inexplicable rendering garbage.</summary>
    /// <summary>False only when the BIOS thread model is on AND emulated code is currently
    /// running on a game thread rather than the original one. Callers that touch the GL
    /// context or the window must check this; everything else in a frame is thread-agnostic,
    /// because the baton guarantees exactly one thread runs emulated code at a time.</summary>
    public static bool OnMainThread => !ThreadModel || Environment.CurrentManagedThreadId == _mainManagedId;

    public static void AssertMainThread(string what)
    {
        if (OnMainThread) return;   // OnMainThread is true for everything when the model is off
        throw new InvalidOperationException(
            $"BIOS-thread violation: '{what}' ran on BIOS thread slot {CurrentSlotOf(Environment.CurrentManagedThreadId)} " +
            $"(managed id {Environment.CurrentManagedThreadId}), but it is pinned to the main thread " +
            $"(managed id {_mainManagedId}). The GL context is thread-affine, so this must not be allowed to proceed. " +
            "Either the game presents frames from a BIOS thread (this runtime cannot do that yet) " +
            "or a switch left the wrong thread running.");
    }

    static int CurrentSlotOf(int managedId)
    {
        for (int i = 0; i < MaxThreads; i++)
            if (_slots[i].Native != null && _slots[i].Native!.ManagedThreadId == managedId) return i;
        return -1;
    }

    /// <summary>Release every parked thread so a shutting-down process does not wait on them.
    /// The threads are background threads as well, so this is belt-and-braces.</summary>
    public static void ShutdownThreads()
    {
        if (!ThreadModel) return;
        _shutdown = true;
        for (int i = 0; i < MaxThreads; i++)
            try { _slots[i].Gate.Release(); } catch (SemaphoreFullException) { }
    }

    static void ChangeTh(CpuContext c, IMemory m, uint handle)
    {
        if (!ThreadModel) return;                     // historical no-op
        int to = (int)(handle & 0xFFu);
        if ((handle & 0xFF000000u) != 0xFF000000u || to < 0 || to >= MaxThreads)
            throw new InvalidOperationException($"ChangeTh: bad thread handle 0x{handle:X8}");

        int from = _current;
        if (to == from) return;                       // switching to self is a no-op
        var dst = _slots[to];
        if (!_tcbs[to].Used || dst.Exited)
            throw new InvalidOperationException(
                $"ChangeTh: handle 0x{handle:X8} (slot {to}) is {(dst.Exited ? "already exited" : "not open")}; " +
                $"switch attempted from slot {from}.");

        _current = to;
        if (!dst.Started) StartThread(m, to);
        dst.Gate.Release();
        Park(from, to);
        c.V0 = 1u;
    }

    /// <summary>Block the calling BIOS thread until something switches back to it.
    /// Bounded, so a lost baton is reported instead of hanging the port silently.</summary>
    static void Park(int slot, int handedTo)
    {
        var s = _slots[slot];
        // A thread parked while the rest of the port keeps running is NORMAL -- a game
        // task can legitimately sleep for many seconds of frames before its scheduler
        // switches back. So the bound is on the PORT being stuck, not on this thread
        // waiting: keep waiting as long as frames are still being presented, and only
        // fail when nothing at all has progressed for the timeout. (A flat per-wait
        // timeout killed healthy long sleeps -- it fired on Dino Crisis's module thread
        // while main was happily rendering at 60 fps.)
        // A thread that has been closed is DEAD on hardware -- its stack is gone and it
        // can never be resumed -- so unwind out of the emulated call stack rather than
        // queueing for a baton that now belongs to whoever inherited this slot.
        if (s.SelfClosed) throw new ThreadClosedSignal();
        long lastFrame = Runtime.FrameCount;
        int stalledMs = 0;
        while (true)
        {
            if (s.Gate.Wait(250))
            {
                if (_shutdown) ExitThread(slot);
                else if (s.SelfClosed) throw new ThreadClosedSignal();
                return;
            }
            if (_shutdown) return;
            long now = Runtime.FrameCount;
            if (now != lastFrame) { lastFrame = now; stalledMs = 0; continue; }
            stalledMs += 250;
            if (stalledMs >= ThreadSwitchTimeoutMs) break;
        }
        throw new TimeoutException(
            $"BIOS thread handoff wedged: no frame presented for {ThreadSwitchTimeoutMs} ms. " +
            $"Slot {slot} (handle 0xFF0000{slot:X2}, entered at pc 0x{s.Pc:X8}) switched to slot {handedTo} " +
            $"(handle 0xFF0000{handedTo:X2}, pc 0x{_slots[handedTo].Pc:X8}) and is still waiting to be resumed. " +
            "The thread that received control never switched back.");
    }

    static void StartThread(IMemory m, int slot)
    {
        var s = _slots[slot];
        s.Started = true;
        s.Native = new Thread(() =>
        {
            // Park first: the switcher releases this gate immediately after starting us,
            // so only one thread is ever running emulated code.
            if (!s.Gate.Wait(ThreadSwitchTimeoutMs) || _shutdown) return;
            try
            {
                var tc = new CpuContext { SP = s.Sp, GP = s.Gp, RA = 0u };
                Dispatch.Dispatcher.Call(tc, m, s.Pc);
            }
            catch (ThreadClosedSignal)
            {
                // CloseTh retired this thread while it was switching away. That switch
                // already handed the baton on, so retire QUIETLY -- releasing main's gate
                // here would double-release it and corrupt the handoff.
                ExitThread(slot, quiet: true);
                return;
            }
            catch (Exception ex) when (!_shutdown)
            {
                Console.WriteLine($"[bios] thread slot {slot} (pc 0x{s.Pc:X8}) faulted: {ex}");
            }
            ExitThread(slot);
        })
        { IsBackground = true, Name = $"psx-bios-th{slot}" };
        s.Native.Start();
    }

    /// <summary>A BIOS thread entry point returned (or the process is going down). Retire the
    /// slot and hand control back to main so the game does not park forever waiting on a
    /// thread that no longer exists.</summary>
    static void ExitThread(int slot, bool quiet = false)
    {
        var s = _slots[slot];
        s.Exited = true;
        s.SelfClosed = false;
        _tcbs[slot] = default;
        if (_shutdown || quiet) return;
        _current = MainSlot;
        try { _slots[MainSlot].Gate.Release(); } catch (SemaphoreFullException) { }
    }

    static readonly uint[] _intChain = new uint[4];

    public static uint IntrEnvInInterruptAddr = 0u;

    static uint _padBuf;
    static uint _padBufA, _padBufB;
    static bool _padStarted;

    public static void DeliverEvent(uint @class, uint spec)
    {
        for (int i = 0; i < MaxEvents; i++)
            if (_evCBs[i].Status == 2u && _evCBs[i].Class == @class && _evCBs[i].Spec == spec)
                _evCBs[i].Status = 4u;
    }
    
    public static void DeliverEventIntr(CpuContext c, IMemory m, uint @class, uint spec)
    {
        for (int i = 0; i < MaxEvents; i++)
        {
            if (_evCBs[i].Status != 2u || _evCBs[i].Class != @class || _evCBs[i].Spec != spec) continue;
            if ((_evCBs[i].Mode & 0x1000u) != 0 && _evCBs[i].Func != 0u)
            {
                var snap = c.Snapshot();
                RecompOne.Runtime.Dispatch.Dispatcher.Call(c, m, _evCBs[i].Func);
                c.Restore(snap);
            }
            else
            {
                _evCBs[i].Status = 4u;
            }
        }
    }
    
    // PE1's music/SE sequencer runs off Root Counter 2 (event class 0xF2000002),
    // set up in FUN_80085644 as SetRCnt(0xF2000002, 0x44E8, 0x1000) — i.e. 17640
    // ticks at sysclock/8 (4.2336 MHz) = 240 Hz. Nothing generates that timer IRQ
    // under static recompilation, so the sequencer never advances and anything that
    // waits for a song to finish (notably the post-battle victory jingle) spins
    // forever. Pump the registered RCnt2 handler at its native rate each frame.
    // No-op until the game opens+enables the event, so it is safe to always call.
    static bool _rcnt2Logged;
    public static void PumpRootCounter2(CpuContext c, IMemory m, int ticks)
    {
        for (int t = 0; t < ticks; t++)
            for (int i = 0; i < MaxEvents; i++)
            {
                if (_evCBs[i].Status != 2u || _evCBs[i].Class != 0xF2000002u) continue;
                if ((_evCBs[i].Mode & 0x1000u) == 0 || _evCBs[i].Func == 0u) continue;
                if (!_rcnt2Logged) { _rcnt2Logged = true; Console.WriteLine($"[rcnt2] sound sequencer tick firing (handler 0x{_evCBs[i].Func:X8})"); }
                var snap = c.Snapshot();
                RecompOne.Runtime.Dispatch.Dispatcher.Call(c, m, _evCBs[i].Func);
                c.Restore(snap);
            }
    }

    // On hardware a card BIOS call completes asynchronously via one of two event
    // classes; under static recompilation no IRQ fires, so we deliver the matching
    // event INLINE (via the mode-0x1000 handlers the game registered in
    // FUN_800409b4) at the moment the op finishes. PE1's card task FUN_800405a4
    // sequences on those handlers: _card_info/_card_load complete with the HwCARD
    // class 0xF4000001, and _card_read/_card_write/_new_card complete with the
    // SwCARD class 0xF0000011. Spec 0x0004 = success (present, readable card),
    // 0x8000 = no-card/error. The handlers set DAT_800a1820/182c... which drive
    // the state machine to set the card-present bit (FUN_80042770 & 1).
    static uint CardSpec(uint port) => ((port & 0x10u) != 0 ? Runtime.CardB : Runtime.CardA).Enabled ? 0x0004u : 0x8000u;
    static void CardHw(CpuContext c, IMemory m, uint port) => DeliverEventIntr(c, m, 0xF4000001u, CardSpec(port));
    static void CardSw(CpuContext c, IMemory m, uint port) => DeliverEventIntr(c, m, 0xF0000011u, CardSpec(port));

    // _card_info(port) / _card_load(port): A-calls that probe the card and load its
    // directory; both complete with the HwCARD event.
    static uint _cardInfoSig = 0xFFFFFFFF;
    public static void CardInfo(CpuContext c, IMemory m)
    {
        var card = (c.A0 & 0x10u) != 0 ? Runtime.CardB : Runtime.CardA;
        // The card task re-issues _card_info/_card_load every poll; log only on change.
        uint sig = (c.A0 & 0x1Fu) | (card.Enabled ? 0x100u : 0u);
        if (BiosA.CardLog && sig != _cardInfoSig)
        {
            _cardInfoSig = sig;
            Console.WriteLine($"[card] _card_info/load port=0x{c.A0:X} enabled={card.Enabled}");
        }
        CardHw(c, m, c.A0); c.V0 = 1u;
    }

    static void NewCard(CpuContext c, IMemory m)
    {
        // _new_card re-initializes the card driver for a fresh access; the game
        // calls it (via _card_clear) before a write. Complete with the SwCARD event.
        CardSw(c, m, c.A0);
        c.V0 = 1u;
    }

    static void CardRead(CpuContext c, IMemory m)
    {
        var card = (c.A0 & 0x10u) != 0 ? Runtime.CardB : Runtime.CardA;
        if (card.Enabled && c.A2 != 0u)
        {
            Span<byte> f = stackalloc byte[0x80];
            int fr = (int)(c.A1 & 0x3FFu);
            card.FrameRead(fr, f);
            for (uint i = 0; i < 0x80u; i++) m.WriteU8(c.A2 + i, f[(int)i]);
            if (BiosA.CardLog) Console.WriteLine($"[card] _card_read port=0x{c.A0:X} frame={fr} status=0x{f[0]:X2} name={System.Text.Encoding.ASCII.GetString(f.Slice(0x0A, 12).ToArray()).TrimEnd('\0')}");
        }
        else if (BiosA.CardLog) Console.WriteLine($"[card] _card_read port=0x{c.A0:X} frame={c.A1 & 0x3FFu} (skipped: enabled={card.Enabled} buf=0x{c.A2:X8})");
        CardSw(c, m, c.A0);
        c.V0 = 1u;
    }

    static void CardWrite(CpuContext c, IMemory m)
    {
        var card = (c.A0 & 0x10u) != 0 ? Runtime.CardB : Runtime.CardA;
        if (card.Enabled && c.A2 != 0u)
        {
            Span<byte> f = stackalloc byte[0x80];
            for (uint i = 0; i < 0x80u; i++) f[(int)i] = m.ReadU8(c.A2 + i);
            card.FrameWrite((int)(c.A1 & 0x3FFu), f);
        }
        CardSw(c, m, c.A0);
        c.V0 = 1u;
    }

    public static uint GetFreeEvSlot()
    {
        for (int i = 0; i < MaxEvents; i++) if (_evCBs[i].Status == 0u) return (uint)i;
        return 0xFFFFFFFFu;
    }
    
    static void PadRead(IMemory m)
    {
        ushort s = Hardware.Controller.State;
        if (_padBuf != 0)
        {
            ushort swapped = (ushort)((s >> 8) | (s << 8));
            m.WriteU32(_padBuf,     0xFFFF0000u | swapped);
            m.WriteU8(_padBuf + 4, Hardware.Controller.RightX);
            m.WriteU8(_padBuf + 5, Hardware.Controller.RightY);
            m.WriteU8(_padBuf + 6, Hardware.Controller.LeftX);
            m.WriteU8(_padBuf + 7, Hardware.Controller.LeftY);
        }
        // standard InitPad(B12)/StartPad(B13) buffers: raw pad response block,
        // byte0 = 0 connected, byte1 = 0x41 digital pad, bytes 2-3 = buttons (active low)
        if (_padStarted && _padBufA != 0)
        {
            m.WriteU8(_padBufA + 0, 0);
            m.WriteU8(_padBufA + 1, 0x41);
            m.WriteU8(_padBufA + 2, (byte)(s & 0xFF));
            m.WriteU8(_padBufA + 3, (byte)(s >> 8));
        }
        if (_padStarted && _padBufB != 0)
            m.WriteU8(_padBufB + 0, 0xFF); // nothing in port 2
    }

    public static void RefreshPad(IMemory m) => PadRead(m);
    public static void Syscall(CpuContext c, IMemory m, uint fn)
    {
        Log.Bios($"B({fn:X2}) {BiosNames.B(fn)}");
        switch (fn)
        {
            case 0x00: c.V0 = 0u; break;
            case 0x01: break;
            case 0x02: c.V0 = 0u; break;
            case 0x03: c.V0 = 0u; break;
            case 0x04: break;
            case 0x05: break;
            case 0x06: break;
            case 0x07: DeliverEvent(c.A0, c.A1); break;
            case 0x08: c.V0 = OpenEvent(c.A0, c.A1, c.A2, c.A3); break;
            case 0x09: CloseEvent(c.A0); c.V0 = 1u; break;
            case 0x0A: c.V0 = WaitEvent(c.A0); break;
            case 0x0B: c.V0 = TestEvent(c.A0); break;
            case 0x0C: EnableEvent(c.A0); c.V0 = 1u; break;
            case 0x0D: DisableEvent(c.A0); c.V0 = 1u; break;
            case 0x0E: c.V0 = OpenTh(c.A0, c.A1, c.A2); break;
            case 0x0F: CloseTh(c.A0); c.V0 = 1u; break;
            case 0x10: ChangeTh(c, m, c.A0); break;
            case 0x11: break;
            case 0x12: _padBufA = c.A0; _padBufB = c.A2; c.V0 = 1u; break; // InitPad(buf1,siz1,buf2,siz2)
            case 0x13: _padStarted = true; c.V0 = 1u; break;              // StartPad
            case 0x14: _padStarted = false; c.V0 = 1u; break;             // StopPad
            case 0x15: _padBuf = c.A1; break;
            case 0x16: PadRead(m); break;
            case 0x17: break;
            case 0x18: IntrEnvInInterruptAddr = 0u; break;
            case 0x19: IntrEnvInInterruptAddr = c.A0 != 0u ? c.A0 - 0x36u : 0u; break;
            case 0x1A: break;
            case 0x1B: break;
            case 0x1C: break;
            case 0x1D: break;
            case 0x1E: break;
            case 0x1F: break;
            case 0x20: UnDeliverEvent(c.A0, c.A1); break;
            case 0x2B: break;
            case 0x2C: break;
            case 0x2D: break;
            case 0x2E: break;
            case 0x2F: c.V0 = 0u; break;
            case 0x30: c.V0 = 0u; break;
            case 0x31: c.V0 = 0u; break;
            case 0x32: BiosA.Syscall(c, m, 0x00); break;
            case 0x33: BiosA.Syscall(c, m, 0x01); break;
            case 0x34: BiosA.Syscall(c, m, 0x02); break;
            case 0x35: BiosA.Syscall(c, m, 0x03); break;
            case 0x36: BiosA.Syscall(c, m, 0x04); break;
            case 0x37: BiosA.Syscall(c, m, 0x05); break;
            case 0x38: BiosA.Syscall(c, m, 0x06); break;
            case 0x39: c.V0 = c.A0 <= 2u ? 2u : 0u; break;
            case 0x3A: c.V0 = 0xFFFFFFFFu; break;
            case 0x3B: Console.Write((char)(c.A0 & 0xFF)); c.V0 = c.A0; break;
            case 0x3C: c.V0 = 0xFFFFFFFFu; break;
            case 0x3D: Console.Write((char)(c.A0 & 0xFF)); c.V0 = c.A0; break;
            case 0x3E: c.V0 = 0u; break;
            case 0x3F: Console.Write(Bios.ReadString(m, c.A0)); c.V0 = c.A0; break;
            case 0x40: c.V0 = 1u; break;
            case 0x41: c.V0 = BiosA.CardFormat(m, c.A0); break;
            case 0x42: c.V0 = BiosA.FirstFile(m, c.A0, c.A1); break;
            case 0x43: c.V0 = BiosA.NextFile(m, c.A0); break;
            case 0x44: c.V0 = 0u; break;
            case 0x45: c.V0 = BiosA.CardDelete(m, c.A0); break;
            case 0x46: c.V0 = 0u; break;
            case 0x47: c.V0 = GetFreeEvSlot(); break;
            case 0x48: c.V0 = 0xFFFFFFFFu; break;
            case 0x49: break;
            case 0x4A: c.V0 = 1u; break;
            case 0x4B: c.V0 = 1u; break;
            case 0x4C: c.V0 = 1u; break;
            case 0x4D: break;
            case 0x4E: CardWrite(c, m); break;
            case 0x4F: CardRead(c, m); break;
            case 0x50: NewCard(c, m); break; // _new_card: reset card driver state, ack complete
            case 0x51: c.V0 = KromFont.Krom2RawAdd(c.A0); break;
            case 0x53: c.V0 = KromFont.Krom2Offset(c.A0); break;
            case 0x54: c.V0 = BiosA.LastErrno; break;
            case 0x55: c.V0 = 0u; break;
            case 0x56: c.V0 = 0u; break;
            case 0x57: c.V0 = 0u; break;
            case 0x58: break;
            case 0x59: c.V0 = BiosA.TestDevice(m, c.A0); break;
            case 0x5B: c.V0 = 0u; break;
            case 0x5C: c.V0 = 0u; break;
            case 0x5D: break;
            default: break;
        }
    }

    static uint OpenEvent(uint @class, uint spec, uint mode, uint func)
    {
        Log.Bios($"OpenEvent class=0x{@class:X8} spec=0x{spec:X4} mode=0x{mode:X4} func=0x{func:X8}");
        for (int i = 0; i < MaxEvents; i++)
        {
            if (_evCBs[i].Status == 0u)
            {
                _evCBs[i] = new EvCB { Status = 1u, Class = @class, Spec = spec, Mode = mode, Func = func };
                return 0xF0000000u | (uint)i;
            }
        }
        return 0xFFFFFFFFu;
    }

    static int EvSlot(uint ev)
    {
        int i = (int)(ev & 0xFFu);
        return i < MaxEvents ? i : -1;
    }

    static void CloseEvent(uint ev)
    {
        int s = EvSlot(ev);
        if (s >= 0) _evCBs[s] = default;
    }

    static uint WaitEvent(uint ev)
    {
        int s = EvSlot(ev);
        if (s >= 0 && _evCBs[s].Status == 4u) _evCBs[s].Status = 2u;
        return 1u;
    }

    static uint TestEvent(uint ev)
    {
        int s = EvSlot(ev);
        if (s >= 0 && _evCBs[s].Status == 4u) { _evCBs[s].Status = 2u; return 1u; }
        return 0u;
    }

    static void EnableEvent(uint ev)
    {
        int s = EvSlot(ev);
        if (s >= 0) _evCBs[s].Status = 2u;
    }

    static void DisableEvent(uint ev)
    {
        int s = EvSlot(ev);
        if (s >= 0 && _evCBs[s].Status != 0u) _evCBs[s].Status = 1u;
    }

    static void UnDeliverEvent(uint @class, uint spec)
    {
        for (int i = 0; i < MaxEvents; i++)
            if (_evCBs[i].Status == 4u && _evCBs[i].Class == @class && _evCBs[i].Spec == spec)
                _evCBs[i].Status = 2u;
    }
    static uint OpenTh(uint pc, uint spFp, uint gp)
    {
        // Slot 0 belongs to the main thread once the thread model is on (see above),
        // so game threads start at 1. With the model off this is the historical
        // behaviour exactly: allocate from 0 and discard pc/sp/gp.
        int first = ThreadModel ? 1 : 0;
        for (int i = first; i < MaxThreads; i++)
        {
            if (_tcbs[i].Used) continue;
            // A slot whose CLR thread has not actually unwound yet is NOT free, even
            // once its TCB is retired: handing it out would put two threads on one
            // gate, and the stale one -- queued first -- would steal the next baton.
            if (ThreadModel && !_slots[i].Exited && _slots[i].Native is { IsAlive: true }) continue;
            _tcbs[i] = new TCB { Used = true };
            if (ThreadModel)
            {
                var s = _slots[i];
                s.Pc = pc; s.Sp = spFp; s.Gp = gp;
                s.Started = false; s.Exited = false; s.SelfClosed = false; s.Native = null;
                while (s.Gate.CurrentCount > 0) s.Gate.Wait(0);   // reuse a recycled slot cleanly
                Log.Bios($"OpenTh -> slot {i} pc=0x{pc:X8} sp=0x{spFp:X8} gp=0x{gp:X8}");
            }
            return 0xFF000000u | (uint)i;
        }
        return 0xFFFFFFFFu;
    }
    static void CloseTh(uint handle)
    {
        int i = (int)(handle & 0xFFu);
        if (i >= MaxThreads) return;
        if (ThreadModel && i != MainSlot)
        {
            var s = _slots[i];
            if (s.Started && !s.Exited)
            {
                // The thread is LIVE -- normally it is closing ITSELF, the convention a
                // one-shot task uses to hand its scheduler slot to a successor and switch
                // away (Dino Crisis's module loader at 0x80020868 does exactly this). Its
                // CLR thread still owns an emulated call stack, so the slot must NOT be
                // recycled here: OpenTh would hand the same slot -- and the same gate --
                // to the successor, and this thread, queued first, would take the next
                // baton and run the code after its own switch, which is unreachable on
                // real hardware. Mark it instead and let it unwind at its next park;
                // ExitThread frees the TCB once the CLR thread is genuinely gone.
                s.SelfClosed = true;
                if (i != _current)
                    try { s.Gate.Release(); } catch (SemaphoreFullException) { }
                return;
            }
            s.Exited = true;
            if (!s.Started) s.Native = null;
        }
        _tcbs[i] = default;
    }

    /// <summary>Heads of the four kernel interrupt-handler chains built by SysEnqIntRP.</summary>
    public static uint IntChainHead(int priority) => _intChain[priority & 3];

    public static void SysEnqIntRP(CpuContext c, IMemory m)
    {
        uint priority = c.A0 & 3u;
        uint struc = c.A1;
        c.V0 = _intChain[priority];
        m.WriteU32(struc, _intChain[priority]);
        _intChain[priority] = struc;
    }
    public static void SysDeqIntRP(CpuContext c, IMemory m)
    {
        uint priority = c.A0 & 3u;
        uint struc = c.A1;
        if (_intChain[priority] == struc)
        {
            _intChain[priority] = m.ReadU32(struc);
            c.V0 = 1u;
            return;
        }
        uint cur = _intChain[priority];
        while (cur != 0u)
        {
            uint next = m.ReadU32(cur);
            if (next == struc) { m.WriteU32(cur, m.ReadU32(struc)); c.V0 = 1u; return; }
            cur = next;
        }
        c.V0 = 0u;
    }

    public static void SaveState(BinaryWriter w)
    {
        // KNOWN INCOMPATIBILITY: a savestate captures ONE CpuContext, but under the
        // thread model there is a live CLR stack per BIOS thread and a state taken
        // mid-switch would restore incoherently. Refuse rather than silently produce
        // a state that fails much later and looks like a savestate bug. Capturing
        // these properly needs per-thread stack capture, which this runtime cannot do.
        if (ThreadModel)
            throw new NotSupportedException(
                "Savestates are not supported while BIOS threads are enabled (BiosB.EnableThreads). " +
                "A savestate holds a single CpuContext, but each BIOS thread owns a live CLR call stack " +
                "that cannot be serialised. See the Dino Crisis config header.");
        foreach (var e in _evCBs) { w.Write(e.Status); w.Write(e.Class); w.Write(e.Spec); w.Write(e.Mode); w.Write(e.Func); }
        foreach (var t in _tcbs) w.Write(t.Used);
        foreach (var ic in _intChain) w.Write(ic);
        w.Write(IntrEnvInInterruptAddr);
        w.Write(_padBuf); w.Write(_padBufA); w.Write(_padBufB); w.Write(_padStarted);
    }

    public static void LoadState(BinaryReader r)
    {
        for (int i = 0; i < MaxEvents; i++)
            _evCBs[i] = new EvCB { Status = r.ReadUInt32(), Class = r.ReadUInt32(), Spec = r.ReadUInt32(), Mode = r.ReadUInt32(), Func = r.ReadUInt32() };
        for (int i = 0; i < MaxThreads; i++) _tcbs[i] = new TCB { Used = r.ReadBoolean() };
        for (int i = 0; i < _intChain.Length; i++) _intChain[i] = r.ReadUInt32();
        IntrEnvInInterruptAddr = r.ReadUInt32();
        _padBuf = r.ReadUInt32(); _padBufA = r.ReadUInt32(); _padBufB = r.ReadUInt32(); _padStarted = r.ReadBoolean();
    }
}
