namespace RecompOne.Runtime.Memory;

// On hardware, mdec-out refills of a ping-pong buffer are paced against the gpu
// upload consuming it (games chain DecDCTout via the DMA1 irq while uploads drain
// concurrently). Recompiled, all refills run before any upload, so the buffer only
// holds its last contents by the time it is read. Each refill's data is kept here
// keyed by address, and whoever uploads that address (DMA ch2 block reads or the
// LoadImage HLE) consumes the copies in fifo order.
public static class MdecOutStaging
{
    // stack, not queue: games build their deferred upload list newest-first, so
    // uploads arrive in reverse fill order (observed in PE1's FMV player)
    static readonly Dictionary<uint, Stack<uint[]>> _pending = new();

    public static void Push(uint madr, uint[] data)
    {
        lock (_pending)
        {
            if (!_pending.TryGetValue(madr, out var q)) _pending[madr] = q = new();
            if (q.Count < 64) q.Push(data);
        }
    }

    public static uint[]? Pop(uint madr)
    {
        lock (_pending)
            return _pending.TryGetValue(madr, out var q) && q.Count > 0 ? q.Pop() : null;
    }
}
