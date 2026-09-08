using System.Text.Json;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Play;

namespace RecompOne.Runtime.Host;

/// <summary>One completed segment of a run.</summary>
public sealed class SplitRecord
{
    public int Index { get; set; }
    /// <summary>The tracked value this segment was spent in (PE1: the scene id). Kept
    /// alongside the index because comparing two runs by index alone silently lines up
    /// unrelated segments the moment a route diverges.</summary>
    public long Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Emulated frame the segment ENDED on (cumulative game time).</summary>
    public long EndFrame { get; set; }
    public long SegmentFrames { get; set; }
    /// <summary>Wall clock at the same instant. Recorded but not compared by default -- see
    /// the class comment on Speedrun for why game time is the primary.</summary>
    public double EndRealSeconds { get; set; }
}

public sealed class RunRecord
{
    public string Game { get; set; } = "";
    public string StartedUtc { get; set; } = "";
    public string BuildStamp { get; set; } = "";
    /// <summary>True if anything happened during this run that a leaderboard would not
    /// accept. A run is still saved and still useful for practice; it is just labelled.</summary>
    public bool Assisted { get; set; }
    public string AssistNotes { get; set; } = "";
    public bool Finished { get; set; }
    public long TotalFrames { get; set; }
    public double TotalRealSeconds { get; set; }
    public List<SplitRecord> Splits { get; set; } = [];
}

/// <summary>
/// Split timer. A game host says what counts as a split point (a registered
/// <see cref="Probes"/> value, e.g. PE1's scene id) and this watches it: every time that
/// value changes, the current segment closes and a new one opens. Runs are written to
/// <c>runs/&lt;game&gt;/</c> and can be compared against an earlier run to show ahead/behind
/// per split, the way a stream overlay does.
///
/// TIME IS COUNTED IN EMULATED FRAMES, not wall clock, and that is the whole reason this can
/// live in the engine rather than in a stopwatch beside the screen. One presented frame is
/// one PS1 vblank, so a frame count is the game's own clock: it does not move when the host
/// stutters, when a redraw is skipped, or when the emulation speed control is used. Wall
/// clock is recorded next to it for reference, never compared against.
///
/// Two runs are only comparable while they took the same route, so a comparison matches
/// splits by index AND id and STOPS at the first index where the ids disagree. Showing a
/// confident "-4.2s" against a segment the runner did not actually play is worse than
/// showing nothing at all.
/// </summary>
public static class Speedrun
{
    // ---- configuration, supplied by the game host --------------------------------------
    public static string Game { get; private set; } = "game";
    static string _probe = "";
    static Func<long, string>? _nameFor;
    static Func<IMemory, bool>? _gate;
    static long _finishId;
    static bool _haveFinishId;

    /// <summary>Wire the timer up for a port. <paramref name="splitProbe"/> is the name of a
    /// numeric probe registered with <see cref="Probes"/>; a change in its value is a split.
    /// <paramref name="nameFor"/> turns an id into something a person can read.
    /// <paramref name="gate"/>, if given, suppresses splitting while it reads false (use it to
    /// keep front-end menus out of a run). <paramref name="finishId"/> ends the run
    /// automatically when that id is reached.</summary>
    public static void Configure(string game, string splitProbe, Func<long, string>? nameFor = null,
                                 Func<IMemory, bool>? gate = null, long? finishId = null)
    {
        Game = game;
        _probe = splitProbe;
        _nameFor = nameFor;
        _gate = gate;
        _haveFinishId = finishId.HasValue;
        _finishId = finishId ?? 0;
        Configured = true;
    }

    public static bool Configured { get; private set; }

    // ---- live state ---------------------------------------------------------------------
    public enum State { Idle, Armed, Running, Finished }
    public static State Status { get; private set; } = State.Idle;

    public static RunRecord Current { get; private set; } = new();
    public static RunRecord? Comparison { get; private set; }
    public static string? ComparisonPath { get; private set; }

    /// <summary>Index at which the current run stopped matching the comparison's route, or -1
    /// while they still agree.</summary>
    public static int RouteDivergedAt { get; private set; } = -1;

    public static long CurrentId { get; private set; }
    public static string CurrentName { get; private set; } = "";
    /// <summary>Frames elapsed in the run so far (cumulative game time).</summary>
    public static long RunFrames { get; private set; }
    /// <summary>Frames elapsed in the segment in progress.</summary>
    public static long SegmentFrames { get; private set; }

    static readonly System.Diagnostics.Stopwatch _real = new();
    static long _lastId;
    static bool _haveLastId;
    static long _stateLoadsAtStart;

    /// <summary>Folder this run is being written into, fixed at Start.</summary>
    public static string? RunDir { get; private set; }

    static Dictionary<long, long> _golds = [];
    /// <summary>Best segment ever recorded for each split id, across every saved run for this
    /// game ("golds"). Frames.</summary>
    public static IReadOnlyDictionary<long, long> Golds => _golds;

    // ---- control ------------------------------------------------------------------------

    /// <summary>Wait for the game to move, then start. Used by --speedrun so the timer does
    /// not bill menu time to the runner.</summary>
    public static void Arm()
    {
        Reset();
        Status = State.Armed;
        Console.WriteLine("[speedrun] armed - the timer starts at the next split point");
    }

    public static void Start()
    {
        Reset();
        Current = new RunRecord
        {
            Game = Game,
            StartedUtc = DateTime.UtcNow.ToString("o"),
            BuildStamp = SafeStamp(),
        };
        _stateLoadsAtStart = Savestate.LoadCount;
        _real.Restart();
        Status = State.Running;
        // Claim the folder NOW rather than at the end. A run is written incrementally from
        // here on, so a crash, a kill or a closed window three hours in still leaves every
        // split that was reached on disk -- losing the whole run to the last minute of it
        // is exactly the failure a runner cannot afford.
        try
        {
            RunDir = Path.Combine(RunsDir, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(RunDir);
        }
        catch (Exception e) { RunDir = null; Console.WriteLine($"[speedrun] cannot create the run folder: {e.Message}"); }
        Flush();  // so the folder is never an empty directory waiting on the first split
        Console.WriteLine($"[speedrun] started ({Game}) -> {RunDir ?? "(not saving)"}");
    }

    public static void Reset()
    {
        Current = new RunRecord { Game = Game };
        RunFrames = 0;
        SegmentFrames = 0;
        _haveLastId = false;
        RouteDivergedAt = -1;
        RunDir = null;
        _real.Reset();
        Status = State.Idle;
    }

    /// <summary>Close the run and write it out. Returns the folder it was written to.</summary>
    public static string? Stop(bool finished = false)
    {
        if (Status != State.Running) return null;
        CloseSegment();                 // the segment in progress still counts
        Current.Finished = finished;
        Current.TotalFrames = RunFrames;
        Current.TotalRealSeconds = _real.Elapsed.TotalSeconds;
        Status = State.Finished;
        _real.Stop();
        var dir = Save();
        var tail = Current.Assisted ? ", ASSISTED" : "";
        Console.WriteLine($"[speedrun] {(finished ? "finished" : "stopped")} at {Format(RunFrames)} " +
                          $"({Current.Splits.Count} splits{tail}) -> {dir}");
        return dir;
    }

    // ---- per-frame ----------------------------------------------------------------------

    /// <summary>Once per presented frame from Runtime.PresentFrame. Cheap: one probe read and
    /// a comparison unless a split actually fires.</summary>
    public static void Tick(IMemory m)
    {
        if (!Configured || Status is State.Idle or State.Finished) return;
        if (!Probes.TryNum(_probe, m, out long id)) return;
        bool live = _gate == null || _gate(m);

        if (Status == State.Armed)
        {
            // Arm-then-start: the first CHANGE begins the run, so a launch that sits on a
            // title screen for a minute does not bill that minute to the runner.
            if (!live) return;
            if (!_haveLastId) { _lastId = id; _haveLastId = true; return; }
            if (id == _lastId) return;
            Start();
            _lastId = id;
            _haveLastId = true;
            CurrentId = id;
            CurrentName = NameOf(id);
            return;
        }

        // Time accrues on EVERY frame once the run has started, gate or no gate. A
        // cutscene, a load and a menu are all part of a run and a runner is judged on
        // them; stopping the clock there would report a time nobody could compare
        // against. The gate's job is narrower -- it decides whether a change in the
        // probe is a real split, so a scene id that dips to 0 mid-transition (or the
        // front end's own scenes) cannot manufacture segments.
        RunFrames++;
        SegmentFrames++;
        NoteAssists();
        if (!live) return;

        if (!_haveLastId)
        {
            _lastId = id;
            _haveLastId = true;
            CurrentId = id;
            CurrentName = NameOf(id);
            return;
        }
        if (id == _lastId) return;

        CloseSegment();
        _lastId = id;
        CurrentId = id;
        CurrentName = NameOf(id);

        if (_haveFinishId && id == _finishId) Stop(finished: true);
    }

    static void CloseSegment()
    {
        if (SegmentFrames <= 0) return;
        var rec = new SplitRecord
        {
            Index = Current.Splits.Count,
            Id = _lastId,
            Name = NameOf(_lastId),
            EndFrame = RunFrames,
            SegmentFrames = SegmentFrames,
            EndRealSeconds = _real.Elapsed.TotalSeconds,
        };
        Current.Splits.Add(rec);
        if (!_golds.TryGetValue(rec.Id, out var best) || rec.SegmentFrames < best)
            _golds[rec.Id] = rec.SegmentFrames;
        SegmentFrames = 0;
        UpdateDivergence();
        if (Status == State.Running) Flush();   // survive a crash with the splits so far
    }

    static void UpdateDivergence()
    {
        if (Comparison == null) { RouteDivergedAt = -1; return; }
        for (int i = 0; i < Current.Splits.Count; i++)
        {
            if (i >= Comparison.Splits.Count) { RouteDivergedAt = i; return; }
            if (Current.Splits[i].Id != Comparison.Splits[i].Id) { RouteDivergedAt = i; return; }
        }
        RouteDivergedAt = -1;
    }

    static void NoteAssists()
    {
        if (FrameClock.Effective is not 1.0 || Tas.TurboActive)
            Assist("emulation speed was changed or fast-forward was used");
        if (Savestate.LoadCount != _stateLoadsAtStart)
            Assist("a savestate was loaded during the run");
    }

    static void Assist(string why)
    {
        if (Current.AssistNotes.Contains(why, StringComparison.Ordinal)) return;
        Current.Assisted = true;
        Current.AssistNotes = Current.AssistNotes.Length == 0 ? why : Current.AssistNotes + "; " + why;
        Console.WriteLine($"[speedrun] run marked ASSISTED: {why}");
    }

    static string NameOf(long id) => _nameFor?.Invoke(id) is { Length: > 0 } n ? n : $"0x{id:X8}";

    // ---- comparison + persistence -------------------------------------------------------

    public static string RunsDir => Path.Combine(AppContext.BaseDirectory, "runs", Game);
    public static string PbPath => Path.Combine(RunsDir, "pb.json");
    static string GoldsPath => Path.Combine(RunsDir, "golds.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Write the run so far into its folder as JSON plus a CSV (so it opens in a
    /// spreadsheet with no tooling). Called on every split, not only at the end.</summary>
    static void Flush()
    {
        if (RunDir == null) return;
        try
        {
            Current.TotalFrames = RunFrames;
            Current.TotalRealSeconds = _real.Elapsed.TotalSeconds;
            File.WriteAllText(Path.Combine(RunDir, "run.json"), JsonSerializer.Serialize(Current, Json));

            var csv = new System.Text.StringBuilder();
            csv.AppendLine("index,id,name,segment_frames,segment_time,end_frame,end_time,real_seconds");
            foreach (var s in Current.Splits)
            {
                var safeName = s.Name.Replace('"', '\'');
                csv.Append($"{s.Index},0x{s.Id:X8},\"{safeName}\",{s.SegmentFrames},");
                csv.AppendLine($"{Format(s.SegmentFrames)},{s.EndFrame},{Format(s.EndFrame)},{s.EndRealSeconds:0.000}");
            }
            File.WriteAllText(Path.Combine(RunDir, "splits.csv"), csv.ToString());
        }
        catch (Exception e) { Console.WriteLine($"[speedrun] could not write the run: {e.Message}"); }
    }

    /// <summary>Finalise: flush, persist golds, and promote to pb.json if this earned it.</summary>
    static string Save()
    {
        Flush();
        SaveGolds();
        // A run only becomes the PB if it was clean AND faster. An assisted run is kept for
        // reference but must never become the thing later runs are measured against.
        try
        {
            if (!Current.Assisted && Current.Splits.Count > 0)
            {
                var pb = LoadRun(PbPath);
                if (pb == null || Current.TotalFrames < pb.TotalFrames)
                {
                    Directory.CreateDirectory(RunsDir);
                    File.WriteAllText(PbPath, JsonSerializer.Serialize(Current, Json));
                    Console.WriteLine($"[speedrun] new personal best: {Format(Current.TotalFrames)}");
                }
            }
        }
        catch (Exception e) { Console.WriteLine($"[speedrun] could not update the personal best: {e.Message}"); }
        return RunDir ?? "";
    }

    public static RunRecord? LoadRun(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(path)) : null; }
        catch { return null; }
    }

    public static bool SetComparison(string? path)
    {
        if (path == null) { Comparison = null; ComparisonPath = null; RouteDivergedAt = -1; return true; }
        var run = LoadRun(path);
        if (run == null) return false;
        Comparison = run;
        ComparisonPath = path;
        UpdateDivergence();
        return true;
    }

    /// <summary>Every saved run for this game, newest first (the folder name is a timestamp),
    /// with pb.json at the top when one exists.</summary>
    public static List<string> ListRuns()
    {
        try
        {
            if (!Directory.Exists(RunsDir)) return [];
            var list = Directory.GetDirectories(RunsDir)
                .Select(d => Path.Combine(d, "run.json"))
                .Where(File.Exists).ToList();
            list.Sort(StringComparer.Ordinal);
            list.Reverse();
            if (File.Exists(PbPath)) list.Insert(0, PbPath);
            return list;
        }
        catch { return []; }
    }

    public static void LoadGolds()
    {
        try
        {
            if (!File.Exists(GoldsPath)) return;
            _golds = JsonSerializer.Deserialize<Dictionary<long, long>>(File.ReadAllText(GoldsPath)) ?? [];
        }
        catch { _golds = []; }
    }

    static void SaveGolds()
    {
        try
        {
            Directory.CreateDirectory(RunsDir);
            File.WriteAllText(GoldsPath, JsonSerializer.Serialize(_golds, Json));
        }
        catch { }
    }

    /// <summary>Sum of the best segment ever seen for each split of the comparison route --
    /// the "if everything went perfectly" number a stream overlay shows.</summary>
    public static long SumOfBest()
    {
        var route = Comparison ?? Current;
        long total = 0;
        foreach (var s in route.Splits)
            total += _golds.TryGetValue(s.Id, out var g) ? g : s.SegmentFrames;
        return total;
    }

    // ---- formatting ---------------------------------------------------------------------

    /// <summary>Frames -> h:mm:ss.mmm at 60 Hz.</summary>
    public static string Format(long frames)
    {
        var t = TimeSpan.FromSeconds(frames / 60.0);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}"
            : $"{t.Minutes}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    /// <summary>Signed delta in seconds, the way a splits overlay writes it.</summary>
    public static string FormatDelta(long frames)
    {
        double s = frames / 60.0;
        return (s >= 0 ? "+" : "-") + Math.Abs(s).ToString("0.00");
    }

    static string SafeStamp()
    {
        try { return Play.BuildStamp.Current; } catch { return ""; }
    }

    // ---- engine-level command line -------------------------------------------------------

    /// <summary>--speedrun (arm at launch) and --speedrun-compare=&lt;path|pb&gt;. Read from the
    /// process command line like the other engine-level flags, so every port gets them.</summary>
    public static void ApplyCommandLine()
    {
        try
        {
            LoadGolds();
            foreach (var a in Environment.GetCommandLineArgs())
            {
                if (a == "--speedrun") Arm();
                else if (a.StartsWith("--speedrun-compare=", StringComparison.Ordinal))
                {
                    var p = a["--speedrun-compare=".Length..].Trim('"');
                    if (p is "pb" or "best") p = PbPath;
                    if (!SetComparison(p)) Console.WriteLine($"[speedrun] no run to compare against at {p}");
                }
            }
        }
        catch { /* a bad speedrun flag must never stop a port booting */ }
    }
}
