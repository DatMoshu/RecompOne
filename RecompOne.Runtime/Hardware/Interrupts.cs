using RecompOne.Runtime.Bios;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime;

public static class Interrupts
{
    // Hardware-IRQ model (PSMemory.HwIrqModel): delivery follows the real controller.
    //  * a source is serviced only while I_STAT & I_MASK has its bit — libetc's
    //    SetIntrMask(0) critical sections (libgpu's _exeque DMA queue) rely on it;
    //    a masked source stays latched in I_STAT and fires when the game unmasks it
    //    (PSMemory calls OnMaskChanged on every I_MASK write);
    //  * handlers never nest — a source raised while one is running is deferred
    //    until it unwinds (hardware keeps IRQs disabled in exception mode).
    // Legacy BIOS-HLE games (PE1) keep the original unconditional delivery.
    static bool _inDeliver;
    static uint _deferred;
    /// <summary>True while a hardware-model IRQ handler is executing (host HLE can suspend frame pacing).</summary>
    public static bool InDeliver => _inDeliver;

    // Legacy BIOS-HLE games that nonetheless mask interrupts around a critical section.
    // libgpu's _exeque opens with SetIntrMask(0) and closes by restoring it, because its
    // DMA-queue cursors are not re-entrant; with the mask ignored, the GPU DMA-complete
    // interrupt is delivered INSIDE the drain, its handler calls _exeque again, and both
    // levels advance the consumer for one produced node -- so the consumer passes the
    // producer and the next iteration dispatches a null handler ("unmapped call:
    // 0x00000000" out of libgpu). Dino Crisis dies at frame 75 that way.
    // Off by default: PE1/BFM/PE2 are byte-identical with it off, and it only ever
    // matters for a game that actually writes I_MASK.
    public static bool MaskGatesBiosModel;
    static uint _biosDeferred;
    static bool _inBiosDeliver;

    /// <summary>Re-deliver anything that fell due while the game had it masked. Called from
    /// PSMemory on every I_MASK write in the legacy model, which is the moment a critical
    /// section ends.</summary>
    public static void OnBiosMaskChanged(PSMemory ps, CpuContext? cpu)
    {
        if (!MaskGatesBiosModel || cpu == null || _inBiosDeliver) return;
        uint pend = _biosDeferred & ps.GuestIrqMask;
        if (pend == 0) return;
        _biosDeferred &= ~pend;
        for (int bit = 0; pend != 0 && bit < 11; bit++, pend >>= 1)
            if ((pend & 1u) != 0) Deliver(bit, cpu, ps);
    }

    public static void OnMaskChanged(PSMemory ps, CpuContext? cpu)
    {
        if (cpu == null) return;
        uint pend = ps.IrqStat & ps.IrqMask;
        for (int bit = 0; pend != 0 && bit < 11; bit++, pend >>= 1)
            if ((pend & 1u) != 0) Deliver(bit, cpu, ps);
    }

    // Kernel interrupt-handler chains (SysEnqIntRP). A game that links its own copy of the
    // BIOS pad/card driver registers a {next, handler, verifier} element on one of these and
    // expects the kernel to walk it on every interrupt; nothing in the runtime did, so such a
    // driver never ran and the game saw no controller. Off by default — games served by the
    // libpad/BIOS-pad HLE do not need it.
    public static bool WalkBiosChain;
    /// <summary>Extra chain walks per interrupt while a serial transfer is still in flight.
    /// A linked kernel pad driver moves one byte per entry and expects the port's acknowledge
    /// interrupt to re-enter it for the next byte; a game that never unmasks that interrupt
    /// (Brave Fencer Musashi) needs the re-entry supplied here instead, or its exchange never
    /// gets past the address byte and no controller is ever detected.</summary>
    public static int ChainRepeatWhileSioBusy;
    static bool _inChain;

    static void RunBiosChain(CpuContext cpu, IMemory mem)
    {
        if (!WalkBiosChain || _inChain) return;
        _inChain = true;
        try
        {
            WalkOnce(cpu, mem);
            var sio = (mem as PSMemory)?.Sio;
            for (int i = 0; i < ChainRepeatWhileSioBusy && sio is { TransferActive: true }; i++)
                WalkOnce(cpu, mem);
        }
        finally { _inChain = false; }
    }

    static void WalkOnce(CpuContext cpu, IMemory mem)
    {
        {
            for (int prio = 0; prio < 4; prio++)
            {
                uint elem = BiosB.IntChainHead(prio);
                for (int guard = 0; elem != 0 && guard < 32; guard++)
                {
                    uint handler = mem.ReadU32(elem + 4u);
                    uint verifier = mem.ReadU32(elem + 8u);
                    uint next = mem.ReadU32(elem);
                    // The verifier reports whether this device raised the interrupt; only then
                    // does its handler run. Each call gets its own register snapshot, like a
                    // real exception entry.
                    bool fire = true;
                    if (verifier != 0)
                    {
                        var vs = cpu.Snapshot();
                        Dispatcher.Call(cpu, mem, verifier);
                        fire = cpu.V0 != 0;
                        cpu.Restore(vs);
                    }
                    if (fire && handler != 0)
                    {
                        var hs = cpu.Snapshot();
                        Dispatcher.Call(cpu, mem, handler);
                        cpu.Restore(hs);
                    }
                    elem = next;
                }
            }
        }
    }

    public static void Deliver(int irq, CpuContext cpu, IMemory mem)
    {
        if (PSMemory.HwIrqModel && mem is PSMemory ps)
        {
            if ((ps.IrqStat & ps.IrqMask & (1u << irq)) == 0) return; // masked: stays latched in I_STAT
            if (_inDeliver) { _deferred |= 1u << irq; return; }
            _inDeliver = true;
            try
            {
                // Before the game's own entry handler: it acknowledges I_STAT bits as it
                // dispatches, and the chain verifiers test those same bits to decide whether
                // their device fired.
                RunBiosChain(cpu, mem);
                Invoke(irq, cpu, mem);
                for (int guard = 0; _deferred != 0 && guard < 32; guard++)
                {
                    uint pend = _deferred & ps.IrqStat & ps.IrqMask;
                    _deferred = 0;
                    for (int bit = 0; pend != 0 && bit < 11; bit++, pend >>= 1)
                        if ((pend & 1u) != 0) Invoke(bit, cpu, mem);
                }
            }
            finally { _inDeliver = false; }
            return;
        }
        if (MaskGatesBiosModel && mem is PSMemory bp && bp.GuestMaskWritten)
        {
            // Latch rather than drop, exactly as the hardware model does: a source that
            // came due inside a critical section must still be serviced when it ends.
            if ((bp.GuestIrqMask & (1u << irq)) == 0 || _inBiosDeliver)
            {
                _biosDeferred |= 1u << irq;
                return;
            }
            _inBiosDeliver = true;
            try
            {
                Invoke(irq, cpu, mem);
                for (int guard = 0; _biosDeferred != 0 && guard < 32; guard++)
                {
                    uint pend = _biosDeferred & bp.GuestIrqMask;
                    _biosDeferred &= ~pend;
                    if (pend == 0) break;
                    for (int bit = 0; pend != 0 && bit < 11; bit++, pend >>= 1)
                        if ((pend & 1u) != 0) Invoke(bit, cpu, mem);
                }
            }
            finally { _inBiosDeliver = false; }
            return;
        }
        Invoke(irq, cpu, mem);
    }

    // Savestate: only the deferred-bit set is real state. _inDeliver is false at any point
    // safe enough to take a state at (a handler is by definition not running there), and
    // _inChain likewise; persisting either would let a stale "handler running" flag survive
    // a load and suppress every subsequent interrupt.
    public static void SaveState(BinaryWriter w) => w.Write(_deferred);
    public static void LoadState(BinaryReader r) { _deferred = r.ReadUInt32(); _inDeliver = false; _inChain = false; _inBiosDeliver = false; }

    static void Invoke(int irq, CpuContext cpu, IMemory mem)
    {
        uint intrEnv = BiosB.IntrEnvInInterruptAddr;
        if (intrEnv == 0) { if (irq != 0) Log.Dma($"irq{irq} dropped: no intrEnv"); return; }

        uint handler = mem.ReadU32(intrEnv + 2u + (uint)irq * 4u);
        if (handler == 0) { if (irq != 0) Log.Dma($"irq{irq} dropped: no handler"); return; }
        if (irq != 0) Log.Dma($"irq{irq} -> handler 0x{handler:X8}");

        //takes a snap, apparently interrupt callbacks dont operate at the same context? could be wrong in mips3000, need to check furter TODO, seens to be accurate
        var snap = cpu.Snapshot();
        mem.WriteU16(intrEnv, 1);
        Dispatcher.Call(cpu, mem, handler);
        mem.WriteU16(intrEnv, 0);
        cpu.Restore(snap);
    }
}
