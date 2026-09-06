using System.IO;
using RecompOne.Runtime.Bios;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Sdk;

namespace RecompOne.Runtime;

// Emulator-style whole-machine snapshot. The static recompiler runs the game as
// native C# calls, so there is no CPU loop to freeze — the host must call
// SaveToFile/LoadFromFile at a safe frame boundary (e.g. inside FrameHook, after a
// frame is presented and before the next VSync re-enters game code). This class only
// serializes/deserializes; it does not decide when a snapshot is taken.
public static class Savestate
{
    const uint Magic = 0x52315353; // "R1SS"
    const int Version = 1;

    // Host sets these on a keypress; the host is responsible for calling
    // SaveToFile/LoadFromFile at the safe point (these are just a mailbox).
    public static bool SavePending, LoadPending;
    public static string SlotPath = "state1.sav";

    public static byte[] Capture(CpuContext c)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Magic);
        w.Write(Version);

        c.SaveState(w);
        ((PSMemory)Runtime.Mem!).SaveState(w);
        // Gpu.SaveState serializes the CPU-side VRAM shadow (Gpu.Vram), but the GL
        // hardware-accelerated backend renders the live scene into its own VRAM texture
        // and per-region render targets, and does not keep that CPU shadow in sync as
        // it draws — only explicit VRAM writes (e.g. CD texture uploads) touch it. Left
        // alone, a save could capture a shadow that never reflected what was actually on
        // screen. Force a full readback (which internally writes back any dirty render
        // targets first) into the shadow immediately before serializing it, mirroring
        // the forced upload done on the load side below.
        Hle.GpuHle.Backend?.ReadVram(0, 0, Gpu.VramWidth, Gpu.VramHeight, Runtime.Gpu!.Vram);
        Runtime.Gpu!.SaveState(w);
        Runtime.Spu!.SaveState(w);

        var overlays = Dispatcher.CaptureActive();
        w.Write(overlays.Count);
        foreach (var name in overlays) w.Write(name);

        LibDs.SaveState(w);
        LibCd.SaveState(w);
        BiosB.SaveState(w);
        LibEtc.SaveState(w);

        w.Flush();
        return ms.ToArray();
    }

    public static void Restore(byte[] data, CpuContext c)
    {
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("savestate: bad magic");
        int ver = r.ReadInt32();
        if (ver != Version) throw new InvalidDataException($"savestate: version mismatch (got {ver}, expected {Version})");

        c.LoadState(r);
        ((PSMemory)Runtime.Mem!).LoadState(r);
        Runtime.Gpu!.LoadState(r);
        // The GL backend caches VRAM (and render targets derived from it) in GPU
        // textures that LoadState above never touches — it only overwrites the CPU-side
        // shadow (Gpu.Vram). Left alone, the display keeps showing whatever was in
        // those GL textures before the load until something else writes to VRAM (e.g.
        // the next room's texture uploads), which is exactly the reported symptom:
        // wrong art immediately after a load, self-correcting on the next room change.
        // Force a full re-upload + render-target resync here so the fix applies the
        // instant a state loads, not on the next incidental VRAM write.
        Hle.GpuHle.Backend?.WriteVram(0, 0, Gpu.VramWidth, Gpu.VramHeight, Runtime.Gpu!.Vram);
        Runtime.Spu!.LoadState(r);

        int n = r.ReadInt32();
        var overlays = new List<string>(n);
        for (int i = 0; i < n; i++) overlays.Add(r.ReadString());
        Dispatcher.RestoreActive(overlays);

        LibDs.LoadState(r);
        LibCd.LoadState(r);
        BiosB.LoadState(r);
        LibEtc.LoadState(r);
    }

    public static void SaveToFile(string path, CpuContext c) => File.WriteAllBytes(path, Capture(c));

    public static void LoadFromFile(string path, CpuContext c) => Restore(File.ReadAllBytes(path), c);

    // Header-only peek so a UI (Host/Window/Panels/Debug/SavestatePanel.cs) can label a
    // state as stale and refuse to load it, instead of throwing mid-load. Deliberately
    // does not touch CpuContext/PSMemory -- this must be safe to call every frame just
    // to populate a list. IsSavestate is false for anything without our magic (e.g. a
    // PS1 memory-card image such as carda.sav sitting in the same directory by
    // convention) -- those aren't ours at all, distinct from a same-format-but-stale
    // version mismatch.
    public static bool TryReadHeader(string path, out bool isSavestate, out int version)
    {
        isSavestate = false;
        version = 0;
        try
        {
            using var fs = File.OpenRead(path);
            using var r = new BinaryReader(fs);
            if (r.ReadUInt32() != Magic) return true;
            isSavestate = true;
            version = r.ReadInt32();
            return true;
        }
        catch { return false; }
    }

    public static bool IsCurrentVersion(string path) =>
        TryReadHeader(path, out var isState, out var v) && isState && v == Version;
}
