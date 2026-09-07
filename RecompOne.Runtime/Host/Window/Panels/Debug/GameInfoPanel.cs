using System.Numerics;
using ImGuiNET;

namespace RecompOne.Runtime.Host.Window;

/// <summary>Renders whatever the game host registered in <see cref="GameInfo"/>. Purely
/// a view: no addresses, no game logic. Closed by default (not part of the shipped
/// dock layout) — toggle from Debug > Info.</summary>
internal sealed class GameInfoPanel : IPanel
{
    public string Name => "Game Info";
    public bool IsOpen { get; set; }

    public void Draw()
    {
        ImGui.SetNextWindowSize(new Vector2(420, 560), ImGuiCond.FirstUseEver);
        bool open = IsOpen;
        if (!ImGui.Begin(Name, ref open)) { IsOpen = open; ImGui.End(); return; }

        var mem = Runtime.Mem;
        if (mem == null)
        {
            ImGui.TextDisabled("No memory");
            IsOpen = open; ImGui.End();
            return;
        }

        if (GameInfo.Sections.Count == 0)
            ImGui.TextDisabled("This port hasn't registered any Game Info sections yet.");

        DrawNamesFooter();

        foreach (var s in GameInfo.Sections)
        {
            if (!ImGui.CollapsingHeader(s.Title, ImGuiTreeNodeFlags.DefaultOpen)) continue;

            if (s.Rows != null)
            {
                if (ImGui.BeginTable($"##{s.Title}", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
                {
                    foreach (var (label, value, tooltip) in s.Rows(mem))
                    {
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        ImGui.TextDisabled(label);
                        ImGui.TableSetColumnIndex(1);
                        ImGui.TextUnformatted(value);
                        if (tooltip != null && ImGui.IsItemHovered())
                        {
                            ImGui.BeginTooltip();
                            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 24f);
                            ImGui.TextUnformatted(tooltip);
                            ImGui.PopTextWrapPos();
                            ImGui.EndTooltip();
                        }
                    }
                    ImGui.EndTable();
                }
            }

            if (s.Text != null)
                ImGui.TextWrapped(s.Text(mem));
        }

        IsOpen = open;
        ImGui.End();
    }

    /// <summary>States where the names shown in this panel came from, because a name with no
    /// provenance is indistinguishable from a guess. <c>game_names.json</c> is optional — most
    /// ports have none, and then this just says the panel is showing raw ids.</summary>
    static void DrawNamesFooter()
    {
        GameNames.EnsureLoaded();
        if (!GameNames.Loaded)
        {
            ImGui.TextDisabled("Names: none loaded (raw ids)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("No game_names.json next to the exe, so ids are shown unresolved.\n" +
                                 GameNames.Status + "\n\nGenerate one with tools/gamedb/export_names.py.");
            return;
        }

        ImGui.TextDisabled("Names: " + GameNames.Status);
        if (!ImGui.IsItemHovered()) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 26f);
        foreach (var (name, t) in GameNames.Tables)
        {
            ImGui.TextUnformatted($"{name}: {t.Entries.Count} entries, {t.Confidence}");
            if (t.Source != null) ImGui.TextDisabled("  " + t.Source);
        }
        ImGui.TextUnformatted("");
        ImGui.TextDisabled("datamined = read out of the game and checked against it. " +
                           "derived = secondary research, unverified.");
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
}
