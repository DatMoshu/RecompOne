using System.IO;
using RecompOne.Runtime.Context;

namespace RecompOne.Runtime.Play;

// Auto-bookmarking: a named savestate a script can create in passing and resume from
// on a later run, so iterating on "the bit after the opening cinematic" means driving
// from boot exactly once instead of every run. Wraps the existing Savestate.
//
// VERSIONING (required, not a nicety): CLAUDE.md already documents a hand-taken F5/F7
// save hanging when loaded after an HLE change — a savestate's meaning is coupled to
// the code that produced it, and that code is not just RecompOne.Runtime.dll but also
// the game host assembly (Patches/RouteScript/config-driven recompiled output all
// shape what a state means). A bookmark stamped with only the engine's build time
// would stay valid across a game-only rebuild and load into code that no longer
// matches it. So the stamp covers both assemblies, and any mismatch is a silent,
// logged fallback to driving from boot — never a load into a mismatched build.
public static class Bookmarks
{
    const string Magic = "R1BM";

    // The stamp itself now lives in BuildStamp, shared with plain savestates and the UI, so
    // there is exactly one definition of "which build produced this" across all formats.
    static string ComputeStamp() => BuildStamp.Current;

    /// <summary>Peek a bookmark's stamp without restoring it (null if unreadable), so a UI
    /// or --loadstate can classify provenance before deciding to load.</summary>
    public static string? ReadStamp(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var r = new BinaryReader(fs);
            if (r.ReadString() != Magic) return null;
            return r.ReadString();
        }
        catch { return null; }
    }

    public static void Save(string path, CpuContext c, string milestone)
    {
        var data = Savestate.Capture(c);
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write(Magic);
        w.Write(ComputeStamp());
        w.Write(milestone);
        w.Write(data.Length);
        w.Write(data);
        Console.WriteLine($"[bookmark] saved '{milestone}' -> {path}");
    }

    // True if `path` is a Bookmarks-format file (magic "R1BM", length-prefixed string)
    // rather than a plain Savestate file (magic 0x52315353, a raw little-endian uint —
    // so byte 0 there is 0x53, never 0x04). Lets a loader that only knows it was handed
    // "some save file" (e.g. --loadstate) dispatch to the right reader instead of
    // guessing from the file extension or failing with a magic mismatch.
    public static bool IsBookmarkFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 5) return false;
            Span<byte> head = stackalloc byte[5];
            if (fs.Read(head) != 5) return false;
            return head[0] == Magic.Length && head[1] == (byte)'R' && head[2] == (byte)'1' && head[3] == (byte)'B' && head[4] == (byte)'M';
        }
        catch { return false; }
    }

    // Loads either format, so a single call site (e.g. --loadstate) doesn't need to
    // know in advance which kind of file it was handed. `milestone` is "" for a plain
    // Savestate file (it doesn't carry one).
    public static bool TryLoadAny(string path, CpuContext c, out string milestone, bool ignoreStamp = false)
    {
        if (IsBookmarkFile(path)) return TryLoad(path, c, out milestone, ignoreStamp);
        milestone = "";
        try { Savestate.LoadFromFile(path, c); return true; }
        catch (Exception ex) { Console.WriteLine($"[bookmark] {path}: plain-savestate load failed ({ex.Message})"); return false; }
    }

    // Returns false (and never touches the running machine state) on any mismatch —
    // missing file, bad magic, or a stamp from a different build. `milestone` is the
    // name the bookmark was saved under, for the caller to resume a script at.
    // `ignoreStamp` is the caller having ALREADY made the accept/refuse decision itself
    // (Play.StateLoad does, with --loadstate-force and a message naming which half of the
    // stamp moved). Without it, a forced --loadstate would be refused a second time here,
    // by a check with less information than the one that already let it through.
    public static bool TryLoad(string path, CpuContext c, out string milestone, bool ignoreStamp = false)
    {
        milestone = "";
        if (!File.Exists(path)) return false;
        try
        {
            using var fs = File.OpenRead(path);
            using var r = new BinaryReader(fs);
            if (r.ReadString() != Magic) { Console.WriteLine($"[bookmark] {path}: not a bookmark file, ignoring"); return false; }
            string stamp = r.ReadString();
            string want = ComputeStamp();
            if (stamp != want && !ignoreStamp)
            {
                Console.WriteLine($"[bookmark] {path}: stale — {BuildStamp.Explain(BuildStamp.Classify(stamp))} " +
                                  $"(saved by build {stamp}, running build {want}) — falling back to driving from boot");
                return false;
            }
            string name = r.ReadString();
            int len = r.ReadInt32();
            var data = r.ReadBytes(len);
            Savestate.Restore(data, c);
            milestone = name;
            Console.WriteLine($"[bookmark] loaded '{milestone}' <- {path}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bookmark] {path}: failed to load ({ex.Message}) — falling back to driving from boot");
            return false;
        }
    }
}
