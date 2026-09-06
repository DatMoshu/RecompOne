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

        foreach (var s in GameInfo.Sections)
        {
            if (!ImGui.CollapsingHeader(s.Title, ImGuiTreeNodeFlags.DefaultOpen)) continue;

            if (s.Rows != null)
            {
                if (ImGui.BeginTable($"##{s.Title}", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
                {
                    foreach (var (label, value) in s.Rows(mem))
                    {
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        ImGui.TextDisabled(label);
                        ImGui.TableSetColumnIndex(1);
                        ImGui.TextUnformatted(value);
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
}
