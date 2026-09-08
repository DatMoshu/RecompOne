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

    // v2 appended a TRAILING block (interrupt registers, Interrupts deferred bits, CD
    // controller) needed by ports that drive the drive at raw MMIO under the hardware-IRQ
    // model. It is trailing, and the reader still accepts v1, deliberately: v1 files
    // legitimately have no such state -- a game on the legacy BIOS-HLE path never writes
    // any of it -- so refusing them would invalidate every existing state for no benefit.
    // MinVersion is the real compatibility floor; bump it only when an older file would
    // actually restore WRONG rather than merely restore less.
    // v3 added a LEADING build stamp (engine assembly + game host assembly; see
    // Play.BuildStamp) immediately after the version word. Leading rather than trailing
    // because the whole point is to read it WITHOUT deserializing the state -- a UI, and
    // --loadstate's refusal path, must be able to say "this came from a different build"
    // from the first few bytes of the file. v1/v2 files simply have no stamp, which
    // Play.BuildStamp.Classify reports as Unknown (an absence of evidence, not a
    // mismatch) rather than refusing every state that predates stamping.
    const int Version = 3;
    const int MinVersion = 1;

    // Host sets these on a keypress; the host is responsible for calling
    // SaveToFile/LoadFromFile at the safe point (these are just a mailbox).
    public static bool SavePending, LoadPending;
    public static string SlotPath = "state1.sav";

    // Where a --loadstate boot load reads from, when that is NOT the manual slot.
    //
    // This exists because of a real, silent data loss. --loadstate used to be implemented by
    // pointing SlotPath at the file it had resolved, which quietly made that file the F5
    // TARGET as well as the load source. Boot from an autostate -- the normal way to resume
    // the crash/fix/resume loop -- and every subsequent F5 wrote back into a file inside
    // autostates/ carrying the auto_ prefix. AutoState.Prune then deleted it as an ordinary
    // disposable capture, and because the "furthest progress" pin reads the frame number out
    // of the FILENAME, a file whose name was frozen at f018001 while its contents advanced
    // past frame 40739 looked like the shallowest state present and was evicted FIRST. Nine
    // manual saves across an hour of play went that way with no error and no warning.
    //
    // So a load source and a save destination are now separate things. F5 always writes the
    // user's own slot; only the boot load reads this, and only once.
    public static string? BootLoadPath;

    /// <summary>What a pending load should read: the boot-load file if one is outstanding,
    /// otherwise the manual slot. Never returns the autostate directory unless the user
    /// pointed the manual slot there themselves.</summary>
    public static string LoadPathPending => BootLoadPath ?? SlotPath;

    // Debug escape hatch, added at the user's explicit request (Savestates panel "Force
    // Load"): apply a load on the very next frame regardless of the safety gate a game
    // host enforces around LoadPending (see PE1's Patches.PerFrame `gateOk`). This is
    // how the "load during New Game went black" bug was originally found, and the user
    // wants to keep poking at load-at-arbitrary-points deliberately. PROVISIONAL: remove
    // this once save/load-at-arbitrary-points is properly supported (or decide it never
    // will be and delete the affordance). A game host that never sets this behaves
    // exactly as before.
    public static bool ForceLoadPending;

    // Periodic automatic capture (Host/AutoState.cs raises this; a game host consumes it at
    // its own safe point, exactly like SavePending). Kept separate from SlotPath/SavePending
    // so an automatic capture can never overwrite the slot the user is manually saving to
    // with F5. See Host/AutoState.cs for the policy.
    public static string? AutoSavePath;

    public static byte[] Capture(CpuContext c)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Magic);
        w.Write(Version);
        w.Write(Play.BuildStamp.Current);

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

        // --- v2 trailing block -------------------------------------------------------
        ((PSMemory)Runtime.Mem!).SaveIrqState(w);
        Interrupts.SaveState(w);
        // Length-prefixed so a reader that has no CD controller (or a future block appended
        // after this one) can skip it without desynchronising the stream.
        if (Runtime.Cd == null) w.Write(0);
        else
        {
            using var cdMs = new MemoryStream();
            using (var cdW = new BinaryWriter(cdMs, System.Text.Encoding.UTF8, leaveOpen: true))
                Runtime.Cd.SaveState(cdW);
            var cdBytes = cdMs.ToArray();
            w.Write(cdBytes.Length);
            w.Write(cdBytes);
        }

        w.Flush();
        return ms.ToArray();
    }

    public static void Restore(byte[] data, CpuContext c)
    {
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("savestate: bad magic");
        int ver = r.ReadInt32();
        if (ver < MinVersion || ver > Version)
            throw new InvalidDataException($"savestate: version mismatch (got {ver}, this build reads {MinVersion}..{Version})");
        // Read past the v3 build stamp. Whether a mismatch is fatal is NOT decided here:
        // Restore's job is to deserialize, and the accept/refuse/force policy lives in
        // Play.StateLoad where the user's --loadstate-force can reach it.
        if (ver >= 3) r.ReadString();

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

        // --- v2 trailing block -------------------------------------------------------
        // Absent from a v1 file, which is correct rather than lossy: v1 could only have
        // been written by a build/game that never populated any of it.
        if (ver < 2) return;
        ((PSMemory)Runtime.Mem!).LoadIrqState(r);
        Interrupts.LoadState(r);
        int cdLen = r.ReadInt32();
        if (cdLen <= 0) return;
        var cdBytes = r.ReadBytes(cdLen);
        if (Runtime.Cd == null) return; // nothing to restore into; block already consumed
        using var cdR = new BinaryReader(new MemoryStream(cdBytes));
        Runtime.Cd.LoadState(cdR);
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

    // "Can this build deserialize the file at all", NOT "was it written by this build".
    // Those were the same question while the format version was the only provenance signal;
    // now that a real build stamp exists (v3, see Play.BuildStamp) they are different
    // questions, and conflating them would mark every still-perfectly-readable v1/v2 state
    // unloadable the day stamping shipped. Build provenance is Play.StateLoad.Classify.
    public static bool IsCurrentVersion(string path) =>
        TryReadHeader(path, out var isState, out var v) && isState && v >= MinVersion && v <= Version;

    // Build stamp without deserializing the state (null for a v1/v2 file, which has none).
    // Must stay cheap: a UI calls this once per listed file per frame.
    public static bool TryReadStamp(string path, out string? stamp)
    {
        stamp = null;
        try
        {
            using var fs = File.OpenRead(path);
            using var r = new BinaryReader(fs);
            if (r.ReadUInt32() != Magic) return false;
            int ver = r.ReadInt32();
            if (ver < 3) return true; // ours, simply unstamped
            stamp = r.ReadString();
            return true;
        }
        catch { return false; }
    }
}
