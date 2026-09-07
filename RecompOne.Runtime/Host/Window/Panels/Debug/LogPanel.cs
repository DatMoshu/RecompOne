using System.Numerics;
using ImGuiNET;

namespace RecompOne.Runtime.Host.Window;

/// <summary>
/// Live tail of everything the port writes to the console (see Host/LogTail).
///
/// This exists so a screenshot of a running port carries its own evidence. Diagnosing a
/// report of "it went black" or "the text is missing" otherwise means asking for a log that
/// the session has usually already thrown away, and a second run rarely lands on the same
/// frame. With the tail on screen the picture is the bug report.
/// </summary>
internal sealed class LogPanel : IPanel
{
    const int MaxDrawn = 600;

    public string Name => "Log";
    public bool IsOpen { get; set; }

    string _filter = string.Empty;
    bool _autoScroll = true;
    long _lastTotal = -1;

    public void Draw()
    {
        ImGui.SetNextWindowSize(new Vector2(560, 220), ImGuiCond.FirstUseEver);
        bool open = IsOpen;
        if (!ImGui.Begin(Name, ref open)) { IsOpen = open; ImGui.End(); return; }

        if (ImGui.Button("Clear")) LogTail.Clear();
        ImGui.SameLine();
        if (ImGui.Button("Copy all")) ImGui.SetClipboardText(string.Join("\n", LogTail.Snapshot(_filter)));
        ImGui.SameLine();
        ImGui.Checkbox("Auto-scroll", ref _autoScroll);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##logfilter", "filter (e.g. unmapped, overlay, card)", ref _filter, 128);

        // Ask for only what is drawn. This is a TAIL panel -- recent lines are the point --
        // and pulling the whole ring every frame made the panel itself the bottleneck.
        // "Copy all" still takes the full retained buffer, so nothing is lost.
        var lines = LogTail.Snapshot(_filter, MaxDrawn);
        ImGui.TextDisabled($"last {lines.Count} of {LogTail.Count} retained / {LogTail.Total} total");
        ImGui.Separator();

        if (ImGui.BeginChild("##logscroll", Vector2.Zero, ImGuiChildFlags.None,
                ImGuiWindowFlags.HorizontalScrollbar))
        {
            for (int i = 0; i < lines.Count; i++)
                ImGui.TextUnformatted(lines[i]);

            // Stick to the bottom only while already there, so scrolling back to read
            // something is not yanked away by the next frame of output.
            if (_autoScroll && LogTail.Total != _lastTotal &&
                ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 40f)
                ImGui.SetScrollHereY(1f);
            _lastTotal = LogTail.Total;
        }
        ImGui.EndChild();

        ImGui.End();
        IsOpen = open;
    }
}
