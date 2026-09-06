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
    public static void Dispatch(CpuContext c, IMemory m, uint fn)
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
            case 0x10: break;
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
            case 0x32: BiosA.Dispatch(c, m, 0x00); break;
            case 0x33: BiosA.Dispatch(c, m, 0x01); break;
            case 0x34: BiosA.Dispatch(c, m, 0x02); break;
            case 0x35: BiosA.Dispatch(c, m, 0x03); break;
            case 0x36: BiosA.Dispatch(c, m, 0x04); break;
            case 0x37: BiosA.Dispatch(c, m, 0x05); break;
            case 0x38: BiosA.Dispatch(c, m, 0x06); break;
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
        for (int i = 0; i < MaxThreads; i++)
            if (!_tcbs[i].Used) { _tcbs[i] = new TCB { Used = true }; return 0xFF000000u | (uint)i; }
        return 0xFFFFFFFFu;
    }
    static void CloseTh(uint handle)
    {
        int i = (int)(handle & 0xFFu);
        if (i < MaxThreads) _tcbs[i] = default;
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
