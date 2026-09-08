using System.Numerics;
using ImGuiNET;

namespace RecompOne.Runtime.Host.Window;

/// <summary>
/// Splits overlay, the way a runner's stream shows one: the current time, the segment
/// list, and per-split delta against a comparison run coloured ahead/behind. Purely a
/// view over <see cref="Speedrun"/> — it holds no timing state of its own, so the timer
/// runs correctly whether or not this window is open.
///
/// Closed by default; toggle from Debug &gt; Speedrun.
/// </summary>
internal sealed class SpeedrunPanel : IPanel
{
    public string Name => "Speedrun";
    public bool IsOpen { get; set; }

    static readonly Vector4 Ahead = new(0.35f, 0.85f, 0.40f, 1f);
    static readonly Vector4 Behind = new(0.95f, 0.40f, 0.35f, 1f);
    static readonly Vector4 Gold = new(1.00f, 0.82f, 0.30f, 1f);
    static readonly Vector4 Dim = new(0.60f, 0.60f, 0.60f, 1f);
    static readonly Vector4 Warn = new(1.00f, 0.65f, 0.20f, 1f);

    int _pick;                 // index into the run list in the comparison picker
    string[] _runs = [];
    bool _listed;

    public void Draw()
    {
        ImGui.SetNextWindowSize(new Vector2(460, 520), ImGuiCond.FirstUseEver);
        bool open = IsOpen;
        if (!ImGui.Begin(Name, ref open)) { IsOpen = open; ImGui.End(); return; }

        if (!Speedrun.Configured)
        {
            ImGui.TextColored(Dim, "This port hasn't registered a split source yet.");
            ImGui.TextWrapped("A game host calls Speedrun.Configure(game, probe, ...) with the name of " +
                              "a numeric probe whose value changes when the player moves on — in Parasite " +
                              "Eve that's the scene id. Every change closes a segment.");
            IsOpen = open; ImGui.End();
            return;
        }

        DrawHeader();
        ImGui.Separator();
        DrawControls();
        ImGui.Separator();
        DrawSplits();
        ImGui.Separator();
        DrawFooter();

        IsOpen = open;
        ImGui.End();
    }

    void DrawHeader()
    {
        long live = Speedrun.RunFrames;
        ImGui.PushFont(ImGui.GetFont());
        ImGui.Text(Speedrun.Format(live));
        ImGui.PopFont();
        ImGui.SameLine();
        ImGui.TextColored(Dim, Speedrun.Status switch
        {
            Speedrun.State.Idle => "  idle",
            Speedrun.State.Armed => "  armed — starts at the next split",
            Speedrun.State.Running => "  running",
            _ => "  finished",
        });

        // The live delta is the run's total against the comparison AT THE SAME SPLIT
        // COUNT — comparing a run in progress against a finished total would read as
        // hours behind for the whole run and tell the runner nothing.
        var cmp = Speedrun.Comparison;
        int done = Speedrun.Current.Splits.Count;
        if (cmp != null && done > 0 && done <= cmp.Splits.Count && Speedrun.RouteDivergedAt < 0)
        {
            long delta = Speedrun.Current.Splits[done - 1].EndFrame - cmp.Splits[done - 1].EndFrame;
            ImGui.SameLine();
            ImGui.TextColored(delta <= 0 ? Ahead : Behind, $"   {Speedrun.FormatDelta(delta)}");
        }

        ImGui.TextColored(Dim, $"now: {Speedrun.CurrentName}   segment {Speedrun.Format(Speedrun.SegmentFrames)}");

        if (Speedrun.Current.Assisted)
        {
            ImGui.TextColored(Warn, "ASSISTED");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Speedrun.Current.AssistNotes);
        }
    }

    void DrawControls()
    {
        bool running = Speedrun.Status == Speedrun.State.Running;
        if (ImGui.Button(running ? "Stop" : "Start"))
        {
            if (running) Speedrun.Stop();
            else Speedrun.Start();
        }
        ImGui.SameLine();
        if (ImGui.Button("Arm")) Speedrun.Arm();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Wait, then start the clock at the next split point — so time spent\n" +
                             "sitting in a menu before the run isn't billed to it.");
        ImGui.SameLine();
        if (ImGui.Button("Reset")) Speedrun.Reset();

        // Comparison picker. Listed lazily and on demand: a saved run is a file read,
        // and doing it every frame would hit the disk 60 times a second for nothing.
        if (!_listed) { _runs = [.. Speedrun.ListRuns()]; _listed = true; }
        ImGui.SetNextItemWidth(260);
        var labels = new string[_runs.Length + 1];
        labels[0] = "(no comparison)";
        for (int i = 0; i < _runs.Length; i++) labels[i + 1] = Label(_runs[i]);
        int sel = _pick;
        if (ImGui.Combo("compare", ref sel, labels, labels.Length))
        {
            _pick = sel;
            Speedrun.SetComparison(sel == 0 ? null : _runs[sel - 1]);
        }
        ImGui.SameLine();
        if (ImGui.Button("Refresh")) { _listed = false; }
    }

    static string Label(string path)
    {
        if (path.EndsWith("pb.json", StringComparison.OrdinalIgnoreCase)) return "Personal best";
        var dir = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        return dir.Length == 0 ? path : dir;
    }

    void DrawSplits()
    {
        var cmp = Speedrun.Comparison;
        int cols = cmp == null ? 3 : 4;
        if (!ImGui.BeginTable("##splits", cols,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, 260)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("split", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("segment", ImGuiTableColumnFlags.WidthFixed, 90);
        if (cmp != null) ImGui.TableSetupColumn("+/-", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("time", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableHeadersRow();

        var splits = Speedrun.Current.Splits;
        for (int i = 0; i < splits.Count; i++)
        {
            var s = splits[i];
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            bool diverged = Speedrun.RouteDivergedAt >= 0 && i >= Speedrun.RouteDivergedAt;
            if (diverged) ImGui.TextColored(Warn, s.Name);
            else ImGui.TextUnformatted(s.Name);
            if (diverged && ImGui.IsItemHovered())
                ImGui.SetTooltip("Off the comparison's route from here on, so there is nothing\n" +
                                 "meaningful to compare these segments against.");

            ImGui.TableSetColumnIndex(1);
            bool gold = Speedrun.Golds.TryGetValue(s.Id, out var g) && s.SegmentFrames <= g;
            if (gold) ImGui.TextColored(Gold, Speedrun.Format(s.SegmentFrames));
            else ImGui.TextUnformatted(Speedrun.Format(s.SegmentFrames));

            int c = 2;
            if (cmp != null)
            {
                ImGui.TableSetColumnIndex(c++);
                if (!diverged && i < cmp.Splits.Count)
                {
                    long d = s.EndFrame - cmp.Splits[i].EndFrame;
                    ImGui.TextColored(d <= 0 ? Ahead : Behind, Speedrun.FormatDelta(d));
                }
                else ImGui.TextColored(Dim, "-");
            }

            ImGui.TableSetColumnIndex(c);
            ImGui.TextUnformatted(Speedrun.Format(s.EndFrame));
        }

        // Keep the newest split in view while a run is going, the way a splits window does.
        if (Speedrun.Status == Speedrun.State.Running && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 1)
            ImGui.SetScrollHereY(1.0f);

        ImGui.EndTable();
    }

    void DrawFooter()
    {
        if (Speedrun.Golds.Count > 0)
            ImGui.TextColored(Dim, $"sum of best {Speedrun.Format(Speedrun.SumOfBest())}   " +
                                   $"({Speedrun.Golds.Count} golds)");
        ImGui.TextColored(Dim, "runs: " + (Speedrun.RunDir ?? Speedrun.RunsDir));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Each run writes run.json + splits.csv here. A clean run faster than\n" +
                             "the current one is promoted to pb.json; an assisted run never is.");
        ImGui.TextColored(Dim, "Times are counted in emulated frames, so they don't move when the host stutters.");
    }
}
