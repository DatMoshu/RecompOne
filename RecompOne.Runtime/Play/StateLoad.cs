using System.IO;
using System.Reflection;
using RecompOne.Runtime.Context;

namespace RecompOne.Runtime.Play;

/// <summary>Which half of a savestate's provenance stopped matching the running build.
///
/// The distinction is the whole point of this type, because the two risks are not equal
/// (see <see cref="BuildStamp.Explain"/>): a game-assembly rebuild is the routine
/// "I rooted another function" case and moves no PS1-side address, while an engine
/// rebuild can change what captured HLE state MEANS. Collapsing both into one word
/// ("stale") throws away the only information that lets someone decide whether to
/// override.</summary>
public enum StampMatch
{
    /// <summary>Same engine build and same game build. Nothing to warn about.</summary>
    Same,
    /// <summary>Only the game host assembly differs (the recompiled port + Patches).</summary>
    GameOnly,
    /// <summary>Only RecompOne.Runtime differs.</summary>
    EngineOnly,
    /// <summary>Both differ.</summary>
    Both,
    /// <summary>The file carries no stamp at all (written before stamping existed, or by
    /// a format this build only partly understands). Not a mismatch — an unknown.</summary>
    Unknown,
}

/// <summary>The build identity a savestate is captured under: engine assembly + game host
/// assembly, each as a last-write timestamp.
///
/// Both halves are needed and neither is sufficient. A savestate's meaning is coupled to
/// the code that produced it, and that code is RecompOne.Runtime.dll AND the game host
/// (Patches, route scripts, and the recompiled output compiled into it). A stamp over the
/// engine alone stays "valid" across a game-only rebuild and loads into code that no longer
/// matches it; a stamp over the game alone misses exactly the changes most likely to make a
/// restore inconsistent. Formerly private to <see cref="Bookmarks"/>; lifted out so plain
/// savestates, bookmarks and the UI all classify provenance the same way.</summary>
public static class BuildStamp
{
    public static long Engine => SafeStamp(typeof(BuildStamp).Assembly);
    public static long Game => SafeStamp(Assembly.GetEntryAssembly());

    /// <summary>The stamp string written into new files. Format is load-bearing: two
    /// fixed-width hex halves joined by '-', so <see cref="TryParse"/> can split a stamp
    /// read back from an older file and say WHICH half moved.</summary>
    public static string Current => Format(Engine, Game);

    public static string Format(long engine, long game) => $"{engine:X16}-{game:X16}";

    public static bool TryParse(string? stamp, out long engine, out long game)
    {
        engine = game = 0;
        if (string.IsNullOrEmpty(stamp)) return false;
        var parts = stamp.Split('-');
        return parts.Length == 2
            && long.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out engine)
            && long.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out game);
    }

    /// <summary>Classify a stamp read off disk against the running build. An empty/absent
    /// stamp is <see cref="StampMatch.Unknown"/>, deliberately distinct from a mismatch:
    /// a file written before stamping existed is not evidence of a different build, just
    /// an absence of evidence, and refusing those would invalidate every state on disk the
    /// day this shipped.</summary>
    public static StampMatch Classify(string? stamp)
    {
        if (string.IsNullOrEmpty(stamp)) return StampMatch.Unknown;
        if (!TryParse(stamp, out long e, out long g)) return StampMatch.Unknown;
        bool engineDiff = e != Engine, gameDiff = g != Game;
        if (!engineDiff && !gameDiff) return StampMatch.Same;
        if (engineDiff && gameDiff) return StampMatch.Both;
        return engineDiff ? StampMatch.EngineOnly : StampMatch.GameOnly;
    }

    /// <summary>One sentence per case, saying what the mismatch actually implies for a
    /// load. This is the text that turns "refused: stale" into a decision someone can
    /// make, so it names the mechanism rather than just the risk level.</summary>
    public static string Explain(StampMatch m) => m switch
    {
        StampMatch.Same => "same engine and game build",
        StampMatch.GameOnly =>
            "the GAME assembly was rebuilt (engine unchanged). Rooting more functions or " +
            "regenerating recompiled code does not move any PS1-side address, so the captured " +
            "RAM and CPU context stay valid — loading across this kind of rebuild is USUALLY SAFE",
        StampMatch.EngineOnly =>
            "the ENGINE (RecompOne.Runtime) was rebuilt. Engine changes can alter what the " +
            "captured HLE state MEANS (CD controller, interrupts, SPU/GPU, BIOS bookkeeping), " +
            "so a state from before the change can restore into an inconsistent machine — " +
            "this is the RISKY kind, like an emulator savestate across a core update",
        StampMatch.Both =>
            "BOTH the engine and the game assembly were rebuilt. The engine half is the risky " +
            "one: it can change what the captured HLE state means",
        _ => "this state carries no build stamp (written before stamping existed), so its " +
             "provenance cannot be checked",
    };

    static long SafeStamp(Assembly? asm)
    {
        try { return asm != null && !string.IsNullOrEmpty(asm.Location) ? File.GetLastWriteTimeUtc(asm.Location).Ticks : 0; }
        catch { return 0; }
    }
}

/// <summary>The one entry point a game host uses to turn a <c>--loadstate=</c> value into a
/// restored machine. Three jobs, all of which every port was otherwise going to re-derive
/// slightly differently:
///
///  1. RESOLVE. <c>latest</c> means "the newest state this port has", which is the form the
///     crash/fix/resume loop actually wants — nobody wants to paste a timestamped filename
///     between every rebuild. Any other value is a path, taken as given.
///
///  2. DISPATCH. Two on-disk formats exist (a plain <see cref="Savestate"/> and a
///     <see cref="Bookmarks"/> file), and a caller handed "some save file" should not have
///     to know which.
///
///  3. DECIDE. A state captured under a different build may not mean the same thing under
///     this one. Default is refuse-and-explain; <see cref="Force"/> overrides. The refusal
///     and the override both NAME which half of the stamp moved, because that is what says
///     whether the override is routine or genuinely dangerous — and the routine case (a
///     game-only rebuild) is precisely the one this whole workflow creates: the fix is what
///     invalidates the state.</summary>
public static class StateLoad
{
    /// <summary>--loadstate-force (and the Savestates panel's Force Load): apply a state
    /// whose build stamp does not match, with a loud warning naming the mismatch. Default
    /// off — refusing by default is right, but refusing with NO way through would make the
    /// fix-and-resume loop impossible.</summary>
    public static bool Force;

    /// <summary>Directories <c>latest</c> searches, in addition to the auto-state directory
    /// and the exe directory. A host adds its own state library here (PE1 has one).</summary>
    public static readonly List<string> SearchDirs = new();

    /// <summary>The path whose stamp verdict has already been announced, so the launch-time
    /// preflight and the apply-time check do not both report it.</summary>
    static string? _accepted;

    /// <summary>Turn a --loadstate value into a concrete path, or null if nothing matches
    /// (already reported to the console). "latest" scans; anything else is a literal path,
    /// so an explicit file that does not exist is an error rather than a silent scan.</summary>
    public static string? Resolve(string spec)
    {
        if (!string.Equals(spec, "latest", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(spec)) return spec;
            Console.WriteLine($"[loadstate] no such state file: {spec}");
            return null;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FileInfo? best = null;
        foreach (var dir in CandidateDirs())
        {
            if (!seen.Add(Path.GetFullPath(dir))) continue;
            if (!Directory.Exists(dir)) continue;
            foreach (var f in new DirectoryInfo(dir).GetFiles("*.sav"))
            {
                // Only files that are actually ours. A PS1 memory-card image (carda.sav)
                // conventionally lives next to the exe and must never be picked as "latest".
                if (!IsStateFile(f.FullName)) continue;
                if (best == null || f.LastWriteTimeUtc > best.LastWriteTimeUtc) best = f;
            }
        }

        if (best == null)
        {
            Console.WriteLine("[loadstate] --loadstate=latest: no savestates found in " +
                              string.Join(", ", CandidateDirs().Where(Directory.Exists).DefaultIfEmpty("(no existing directories)")));
            return null;
        }

        var age = DateTime.UtcNow - best.LastWriteTimeUtc;
        Console.WriteLine($"[loadstate] latest -> {best.FullName} ({FormatAge(age)} old)");
        return best.FullName;
    }

    /// <summary>Load either format, checking the build stamp. Returns false WITHOUT touching
    /// the running machine on any refusal, so a declined load leaves a normally-booting game
    /// rather than a half-restored one.</summary>
    public static bool TryLoad(string path, CpuContext c, out string milestone, bool force = false)
    {
        milestone = "";
        if (!File.Exists(path)) { Console.WriteLine($"[loadstate] no such state file: {path}"); return false; }

        if (!Accept(path, force)) return false;

        try
        {
            if (Bookmarks.IsBookmarkFile(path)) return Bookmarks.TryLoad(path, c, out milestone, ignoreStamp: true);
            Savestate.LoadFromFile(path, c);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[loadstate] {path}: load failed ({ex.Message})");
            return false;
        }
    }

    /// <summary>The build-stamp decision, on its own so a host can make it AT LAUNCH instead
    /// of at the safe frame.
    ///
    /// That distinction is worth the extra method. A game's safe point can be a minute of
    /// boot logos and title screens away (BFM's is), so deciding only at the apply point
    /// means someone waits out the whole boot to be told the state was refused — and then
    /// waits it out again with the flag added. Deciding here lets the refusal land before
    /// the window even opens, while <see cref="TryLoad"/> keeps the same check as the
    /// authority (a host that never preflights is still safe, just slower to be told).</summary>
    public static bool Accept(string path, bool force = false)
    {
        // A host that preflighted at launch has already printed the verdict for this exact
        // file; saying it again when the load finally applies reads as if something changed.
        if (string.Equals(_accepted, path, StringComparison.OrdinalIgnoreCase)) return true;
        var match = Classify(path);
        if (match is StampMatch.Same or StampMatch.Unknown || force || Force) _accepted = path;
        if (match == StampMatch.Unknown)
        {
            // An unstamped file (v1/v2, written before stamping existed) is an ABSENCE of
            // evidence, not evidence of a mismatch — refusing it would invalidate every state
            // already on disk the day stamping shipped, including the autostate a crashed
            // session just left behind. Note it and load: the note keeps a later surprise
            // traceable, and refusing made the feature useless on its very first run, which
            // is exactly how this was caught in testing.
            Console.WriteLine($"[loadstate] {Path.GetFileName(path)}: {BuildStamp.Explain(match)}. Loading anyway.");
            return true;
        }
        if (match == StampMatch.Same) return true;

        if (!(force || Force))
        {
            Console.WriteLine(
                $"[loadstate] REFUSED {Path.GetFileName(path)} — {BuildStamp.Explain(match)}.\n" +
                $"[loadstate] Re-run with --loadstate-force to load it anyway" +
                (match == StampMatch.GameOnly ? " (this is the routine case: a game-only rebuild)." : "."));
            return false;
        }
        Console.WriteLine(
            $"[loadstate] FORCED load of {Path.GetFileName(path)} across a build change — {BuildStamp.Explain(match)}. " +
            "If the game misbehaves after this, suspect the state, not the game.");
        return true;
    }

    /// <summary>Read a file's build stamp without restoring anything — safe to call from a
    /// per-frame UI. Works for both formats.</summary>
    public static StampMatch Classify(string path) => BuildStamp.Classify(ReadStamp(path));

    public static string? ReadStamp(string path)
    {
        if (Bookmarks.IsBookmarkFile(path)) return Bookmarks.ReadStamp(path);
        return Savestate.TryReadStamp(path, out var s) ? s : null;
    }

    static bool IsStateFile(string path) =>
        (Savestate.TryReadHeader(path, out var isState, out _) && isState) || Bookmarks.IsBookmarkFile(path);

    static IEnumerable<string> CandidateDirs()
    {
        // Auto-states first: they are what this workflow produces, and they are the states a
        // "resume where I was" almost always means. Resolved against BOTH the exe directory
        // and the process cwd because AutoState.Dir is relative and a launcher .bat can start
        // the exe from either.
        yield return Path.Combine(AppContext.BaseDirectory, Host.AutoState.Dir);
        yield return Host.AutoState.Dir;
        foreach (var d in SearchDirs) yield return d;
        yield return AppContext.BaseDirectory;
        yield return ".";
    }

    static string FormatAge(TimeSpan t) =>
        t.TotalMinutes < 1 ? $"{t.TotalSeconds:F0}s"
        : t.TotalHours < 1 ? $"{t.TotalMinutes:F0}m"
        : t.TotalDays < 1 ? $"{t.TotalHours:F1}h"
        : $"{t.TotalDays:F1}d";
}
