namespace RecompOne.Runtime.Host;

/// <summary>
/// Periodic, disposable savestates taken while the game runs, so a session that ends in a
/// crash can be resumed from a minute earlier instead of replayed from the start.
///
/// This began life tied to TAS recording, and is deliberately NOT any more: crashes do not
/// wait for you to be recording, so it is an iteration feature first. It runs during normal
/// play as well, on every port that has a defensible safe point.
///
/// Division of labour, which matters: this class only decides WHEN to ask. It raises a
/// request (<see cref="Savestate.AutoSavePath"/>) and nothing else — it never touches the
/// CPU context and never blocks a frame. The game host decides whether it is safe to honour
/// the request right now, because only the game knows what a consistent call depth looks
/// like, and a state captured at the wrong depth is worse than no state at all.
///
/// Policy is newest-wins-never-blocks: if a request is still parked because the gate has
/// been false, a later interval OVERWRITES it rather than queueing behind it. The freshest
/// recoverable point is the useful one, and a backlog of stale requests helps nobody.
/// </summary>
public static class AutoState
{
    /// <summary>Off unless a port turns it on. Not a default-off feature so much as a
    /// per-port one: a game with no measured safe point must not be capturing states at an
    /// arbitrary call depth, so each host opts in (see its Program.cs) rather than
    /// inheriting a global default it cannot honour.</summary>
    public static bool Enabled;

    public static long IntervalFrames = 3600; // ~60 s at 60 fps; <= 0 disables
    /// <summary>How many of the newest to keep. A state is ~3.7 MB, so one a minute across an
    /// hour is 60 files and 200+ MB — and this now runs during every session of every port,
    /// not only recorded ones. Retention is what keeps that from quietly filling a disk.
    /// Raised from 8 after a real loss: short diagnostic runs kept evicting the one deep
    /// session that mattered (see <see cref="Prune"/>, which now also pins the furthest).</summary>
    public static int Keep = 30;

    /// <summary>Their own directory, separate from the user's manual states and the room-index
    /// library in games/*/saves/states/, so nothing here is ever mistaken for something a
    /// person made and chose to keep. Relative to the running exe, hence per-game already.</summary>
    public static string Dir = "autostates";
    /// <summary>Prefix on top of the directory: even copied elsewhere, the name says throwaway.</summary>
    public static string Prefix = "auto_";

    static long _nextDue = -1;
    static bool _skipLogged;
    static readonly string _session = DateTime.Now.ToString("yyyyMMdd_HHmmss");

    static string PathFor(long frame) =>
        System.IO.Path.Combine(Dir, $"{Prefix}{_session}_f{frame:D6}.sav");

    /// <summary>Once per presented frame, from Runtime.PresentFrame. Cheap: two comparisons
    /// in the common case.</summary>
    public static void Tick(long frame)
    {
        if (!Enabled || IntervalFrames <= 0) return;
        if (_nextDue < 0) { _nextDue = frame + IntervalFrames; return; } // first interval starts now
        if (frame < _nextDue) return;
        _nextDue = frame + IntervalFrames;
        Savestate.AutoSavePath = PathFor(frame); // newest-wins; never queues
    }

    /// <summary>The game host calls this once the file is actually on disk. Retention is driven
    /// from real files rather than from requests, so a request the safety gate declined can
    /// never evict a state that exists.</summary>
    public static void NoteWritten(string path)
    {
        _skipLogged = false; // a success re-arms the "first skip in a stretch" log
        Prune();
    }

    /// <summary>The game host calls this when the gate refused. Deliberately near-silent: during
    /// normal play the gate is false for long stretches (cutscenes, menus, loading), and a line
    /// per frame — or even per attempt — would bury everything else. One line when a stretch of
    /// refusals begins, then nothing until a capture succeeds again.</summary>
    public static void NoteSkipped(string why)
    {
        if (_skipLogged) return;
        _skipLogged = true;
        Console.WriteLine($"[autostate] not safe to capture yet ({why}); will retry quietly");
    }

    /// <summary>True for a path this class produced — used by the savestates UI to label these
    /// as disposable rather than as something the user saved.</summary>
    public static bool IsAutoState(string path) =>
        System.IO.Path.GetFileName(path).StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Keep the newest <see cref="Keep"/> files, ALWAYS keep the furthest-progress
    /// one, and delete the rest.
    ///
    /// Ordered by last-write time and pruned from the OLD end, so the newest can never be the
    /// one deleted. Pruning scans the directory rather than tracking an in-process list, which
    /// is what stops states accumulating across runs — otherwise every restart would begin a
    /// fresh list and the previous session's files would live forever.
    ///
    /// **Recency alone is the wrong policy, and it cost a real session.** A long play session
    /// that reached frame 14401 was evicted by a handful of short diagnostic runs that never
    /// got past frame 6000, because every one of those was newer. The deep state is precisely
    /// the expensive one to recreate — it is worth minutes of play, while the shallow ones are
    /// worth seconds. So the highest frame number (parsed from the filename this class writes)
    /// is pinned in addition to the newest N, and cannot be evicted by anything shallower.
    ///
    /// Only files carrying <see cref="Prefix"/> are ever considered, so a manual F5 state is
    /// untouchable here by construction rather than by policy: it is not written by this class,
    /// does not carry the prefix, and normally does not even live in this directory.</summary>
    static void Prune()
    {
        try
        {
            if (!System.IO.Directory.Exists(Dir)) return;
            var files = new System.IO.DirectoryInfo(Dir)
                .GetFiles($"{Prefix}*.sav")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            var furthest = files.OrderByDescending(FrameOf).FirstOrDefault();
            for (int i = Math.Max(1, Keep); i < files.Count; i++)
            {
                if (ReferenceEquals(files[i], furthest)) continue; // deepest progress is pinned
                try { files[i].Delete(); } catch { }
            }
        }
        catch { } // retention is best-effort; never let housekeeping break a run
    }

    /// <summary>Frame number out of a name this class wrote (`..._f014401.sav`); -1 if absent,
    /// so a foreign or renamed file simply never wins the "furthest" pin.</summary>
    static long FrameOf(System.IO.FileInfo f)
    {
        var m = System.Text.RegularExpressions.Regex.Match(f.Name, @"_f(\d+)\.sav$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success && long.TryParse(m.Groups[1].Value, out var n) ? n : -1;
    }

    /// <summary>Convenience for a host: write the state and record it for retention, returning
    /// false (without throwing) if it could not be written. Keeps the same three lines from
    /// being re-derived slightly differently in every port.</summary>
    public static bool Capture(string path, Context.CpuContext cpu)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            Savestate.SaveToFile(path, cpu);
            NoteWritten(path);
            Console.WriteLine($"[autostate] captured -> {path}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[autostate] capture failed: {ex.Message}");
            return false;
        }
    }
}
