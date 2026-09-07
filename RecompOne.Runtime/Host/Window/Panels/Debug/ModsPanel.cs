using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Mods;

namespace RecompOne.Runtime.Host.Window;

/// <summary>Live switchboard for whatever mods the running port declared
/// (<see cref="ModRegistry"/>): what exists, what each one does, whether it is on, why one
/// refused, and every adjustable value it exposes.
///
/// Entirely game-agnostic — it renders declarations. No addresses, no game logic, nothing
/// that knows PE1 from Brave Fencer Musashi; a port gets this panel for free the moment it
/// calls <c>ModRegistry.Declare</c>.
///
/// THIS PANEL IS THE WRITER for settings.json's mod state, and that is a deliberate fix
/// rather than a convenience. HostWindow.OnClosing re-serialises GameConfig from memory on
/// exit, so an external edit made while the game is running is overwritten on quit — which
/// is exactly how a hand-edited ActiveMods was silently lost once already. Every change
/// here goes through ModRegistry.SetActive / SetValue, which update the in-memory
/// GameConfig first, and are then flushed with ModRegistry.Persist(). So a toggle survives
/// both the exit rewrite and the next launch.
///
/// Writes are flushed on mouse-release (ImGui.IsItemDeactivatedAfterEdit), not on every
/// frame of a slider drag: Persist() rewrites the whole file.</summary>
internal sealed class ModsPanel : IPanel
{
    public string Name => "Mods";
    public bool IsOpen { get; set; }

    public void Draw()
    {
        ImGui.SetNextWindowSize(new Vector2(440, 460), ImGuiCond.FirstUseEver);
        bool open = IsOpen;
        if (!ImGui.Begin(Name, ref open)) { IsOpen = open; ImGui.End(); return; }

        var mods = ModRegistry.All().ToList();
        if (mods.Count == 0)
        {
            ImGui.TextDisabled("This port hasn't declared any mods.");
            ImGui.TextWrapped("A game host declares them at startup with ModRegistry.Declare(id, summary); " +
                              "see games/ParasiteEve/ParasiteEve.PC/Mods.cs.");
            IsOpen = open; ImGui.End();
            return;
        }

        int onCount = mods.Count(x => x.Active);
        ImGui.TextDisabled($"{onCount} of {mods.Count} active - saved to settings.json as you change them.");
        ImGui.Separator();

        foreach (var (id, summary, active, declined) in mods)
        {
            ImGui.PushID(id);

            bool on = active || declined != null;   // "requested" — a declined mod is still ticked
            if (ImGui.Checkbox(id, ref on))
            {
                ModRegistry.SetActive(id, on);
                ModRegistry.Persist();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(on
                    ? "Turn this mod off. Written to settings.json immediately."
                    : "Turn this mod on. Written to settings.json immediately.\n" +
                      "Some mods only take effect at the next room/scene; a few need a restart.");

            ImGui.Indent();
            ImGui.PushTextWrapPos(0f);
            ImGui.TextDisabled(summary);
            ImGui.PopTextWrapPos();

            if (declined != null)
            {
                // A decline is the interesting state: the user asked for the mod and it
                // refused. Say so in colour, say why, and say what clears it — silently
                // showing an unticked box here would recreate the exact ambiguity the
                // decline mechanism exists to remove.
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.72f, 0.25f, 1f));
                ImGui.TextWrapped($"DECLINED: {declined}");
                ImGui.PopStyleColor();
                if (ImGui.SmallButton("Retry"))
                {
                    ModRegistry.SetActive(id, true);   // clears the decline and re-arms the mod
                    ModRegistry.Persist();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Clear the refusal and let the mod try again - use after fixing\n" +
                                     "whatever it complained about (a missing file, a bad folder).");
            }

            DrawSettings(id);

            ImGui.Unindent();
            ImGui.Separator();
            ImGui.PopID();
        }

        IsOpen = open;
        ImGui.End();
    }

    static void DrawSettings(string modId)
    {
        var settings = ModRegistry.SettingsOf(modId).ToList();
        if (settings.Count == 0) return;

        ImGui.PushItemWidth(150f);
        foreach (var s in settings)
        {
            ImGui.PushID(s.Key);
            switch (s.Kind)
            {
                case ModSettingKind.Bool:
                {
                    bool b = s.Value != 0;
                    if (ImGui.Checkbox(s.Label, ref b)) { ModRegistry.SetValue(s, b ? 1 : 0); ModRegistry.Persist(); }
                    break;
                }
                case ModSettingKind.Int:
                {
                    int i = (int)s.Value;
                    if (ImGui.SliderInt(s.Label, ref i, (int)s.Min, (int)s.Max)) ModRegistry.SetValue(s, i);
                    if (ImGui.IsItemDeactivatedAfterEdit()) ModRegistry.Persist();
                    break;
                }
                default:
                {
                    float f = (float)s.Value;
                    if (ImGui.SliderFloat(s.Label, ref f, (float)s.Min, (float)s.Max, "%.2f")) ModRegistry.SetValue(s, f);
                    if (ImGui.IsItemDeactivatedAfterEdit()) ModRegistry.Persist();
                    break;
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{s.Description}\n\ndefault {Format(s, s.Default)}" +
                                 (s.Kind == ModSettingKind.Bool ? "" : $", range {Format(s, s.Min)}-{Format(s, s.Max)}"));

            if (!s.IsDefault)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Reset")) { ModRegistry.SetValue(s, s.Default); ModRegistry.Persist(); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Back to the default, {Format(s, s.Default)}.");
            }
            ImGui.PopID();
        }
        ImGui.PopItemWidth();
    }

    static string Format(ModSetting s, double v) => s.Kind switch
    {
        ModSettingKind.Bool => v != 0 ? "on" : "off",
        ModSettingKind.Int => ((int)v).ToString(),
        _ => v.ToString("0.##"),
    };
}
