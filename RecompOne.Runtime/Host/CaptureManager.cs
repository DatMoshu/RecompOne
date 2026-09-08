namespace RecompOne.Runtime.Host;

/// <summary>Periodic full-window + data-sidecar captures, engine-level so every
/// port gets it for free (a game host just sets these two fields from a CLI flag,
/// mirroring the existing per-game `--snap=N` pattern). Independent of
/// <see cref="Screenshot"/> (clean game framebuffer, unchanged) — this is the
/// "screenshot with the debug panels in it" path.</summary>
public static class CaptureManager
{
    public static int SnapWindowEvery; // --snap-window=N, 0 = off

    // --snap-display=N, 0 = off. The CLEAN game framebuffer (no panels), decoded from
    // VRAM on the CPU by Screenshot. Deliberately a separate path from the window
    // capture above: it shares no code with the GL present shader, so when the window
    // shows black the two together say whether the pixels are missing from VRAM or only
    // from what the presenter drew.
    public static int SnapDisplayEvery;
    // Standalone JSON-only cadence (no image), 0 = off. Not wired to a CLI flag here:
    // some ports already have an unrelated `--dump=` flag (e.g. PE2's function-entry
    // trace histogram), so pick a game-specific flag name when wiring this up rather
    // than reusing `--dump=`.
    public static int DumpEvery;
    public static string OutDir = "snaps";

    // --dump-all=<dir>[:N] : one-shot full-machine dump (DumpAll) for black-screen /
    // hang triage. N is an optional frame to wait for; -1 (default) fires on the
    // next Tick. Also reachable live via the Debug menu (calls DumpAll.Write directly,
    // bypassing this queue entirely).
    public static string? DumpAllDir;
    public static long DumpAllAtFrame = -1;
    static bool _dumpAllDone;

    public static void Tick(long frame)
    {
        if (DumpAllDir != null && !_dumpAllDone && frame >= DumpAllAtFrame)
        {
            _dumpAllDone = true;
            DumpAll.Write(DumpAllDir);
        }
        // The window capture's own JSON sidecar (same base name) is written by
        // HostWindow at the moment the image is actually captured, so both
        // describe the identical rendered frame.
        if (SnapWindowEvery > 0 && frame % SnapWindowEvery == 0)
            HostWindow.RequestWindowCapture(Path.Combine(OutDir, $"window_{frame:D6}.png"));

        if (SnapDisplayEvery > 0 && frame % SnapDisplayEvery == 0)
        {
            Directory.CreateDirectory(OutDir);
            Screenshot.Save(Path.Combine(OutDir, $"display_{frame:D6}.png"));
        }

        if (DumpEvery > 0 && frame % DumpEvery == 0)
            CaptureData.Write(Path.Combine(OutDir, $"dump_{frame:D6}.json"));
    }
}
