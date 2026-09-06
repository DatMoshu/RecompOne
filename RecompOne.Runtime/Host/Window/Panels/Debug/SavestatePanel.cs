using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using RecompOne.Runtime.Play;

namespace RecompOne.Runtime.Host.Window;

/// <summary>Quick-access UI over the existing <see cref="Savestate"/> mailbox
/// (SavePending/LoadPending, applied by the game host at its own safe frame boundary
/// -- see PE1's Patches.PerFrame). This panel never calls Capture/Restore directly;
/// it only sets the mailbox, so it can never apply a state at the wrong call depth
/// itself. Two things it DOES check before allowing a click:
///  - version stamp (<see cref="Savestate.IsCurrentVersion"/>) -- a state saved by a
///    different build is shown as stale and the Load button is disabled;
///  - <see cref="SafeToLoadProbe"/>, an optional <c>Play.Probes</c> flag name a game
///    can register (e.g. PE1's "pe1.bookmarkSafe") so the button visibly declines
///    instead of silently queuing a load that the host will never find a safe frame
///    to apply (or worse, applies at the wrong depth and goes black).</summary>
public sealed class SavestatePanel : IPanel
{
    public string Name => "Savestates";
    public bool IsOpen { get; set; }

    // Directory to scan for *.sav slots. A game host points this wherever its state
    // library lives (default: next to the exe, matching Savestate.SlotPath's default).
    public static string SlotsDir = ".";
    // Optional Play.Probes flag name; null = no gating (always considered safe).
    public static string? SafeToLoadProbe;

    // Best-effort label lookup: if SlotsDir contains a "manifest.json" shaped as
    // [{"file":"x.sav","label":"..."}], use it; otherwise fall back to the filename.
    // This is a guess pending the real schema from the session generating the PE1
    // state library (games/ParasiteEve/saves/merged/) -- update this parser once
    // that manifest exists if the shape differs.
    static Dictionary<string, string>? _labels;
    static DateTime _labelsStamp;

    string _newSlotName = "";

    public void Draw()
    {
        ImGui.SetNextWindowSize(new Vector2(420, 420), ImGuiCond.FirstUseEver);
        bool open = IsOpen;
        if (!ImGui.Begin(Name, ref open)) { IsOpen = open; ImGui.End(); return; }

        var mem = Runtime.Mem;
        bool safe = SafeToLoadProbe == null || (mem != null && Probes.TryFlag(SafeToLoadProbe, mem, out var v) && v);

        ImGui.TextDisabled($"Slots: {SlotsDir}");
        if (!safe)
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), "Not at a safe point to load right now (mid-FMV/menu/loading).");
        }

        ImGui.Separator();
        ImGui.InputText("##newslot", ref _newSlotName, 64);
        ImGui.SameLine();
        if (ImGui.Button("Save New") && _newSlotName.Length > 0)
        {
            Directory.CreateDirectory(SlotsDir);
            Savestate.SlotPath = Path.Combine(SlotsDir, _newSlotName + ".sav");
            Savestate.SavePending = true;
            _newSlotName = "";
        }

        ImGui.Separator();

        if (!Directory.Exists(SlotsDir))
        {
            ImGui.TextDisabled("(directory does not exist yet)");
            IsOpen = open; ImGui.End();
            return;
        }

        var labels = LoadLabels();
        foreach (var path in Directory.EnumerateFiles(SlotsDir, "*.sav").OrderBy(p => p))
        {
            // Skip anything without our magic outright (e.g. carda.sav/cardb.sav, the PS1
            // memory-card images that conventionally live next to the exe too) -- those
            // aren't stale savestates, they're not savestates at all.
            if (!Savestate.TryReadHeader(path, out var isState, out _) || !isState) continue;

            var file = Path.GetFileName(path);
            bool current = Savestate.IsCurrentVersion(path);
            string label = labels?.GetValueOrDefault(file) ?? Path.GetFileNameWithoutExtension(file);

            ImGui.PushID(path);
            ImGui.TextUnformatted(label);
            if (!current) { ImGui.SameLine(); ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "STALE"); }
            ImGui.SameLine();

            bool canLoad = current && safe;
            if (!canLoad) ImGui.BeginDisabled();
            if (ImGui.SmallButton("Load"))
            {
                Savestate.SlotPath = path;
                Savestate.LoadPending = true;
            }
            if (!canLoad) ImGui.EndDisabled();
            if (!current && ImGui.IsItemHovered())
                ImGui.SetTooltip("Saved by a different build (version stamp mismatch) -- refusing to load.");
            else if (!safe && ImGui.IsItemHovered())
                ImGui.SetTooltip("Not at a safe frame to apply a load right now.");

            ImGui.SameLine();
            if (ImGui.SmallButton("Save Here")) { Savestate.SlotPath = path; Savestate.SavePending = true; }
            ImGui.PopID();
        }

        IsOpen = open;
        ImGui.End();
    }

    static Dictionary<string, string>? LoadLabels()
    {
        var manifestPath = Path.Combine(SlotsDir, "manifest.json");
        if (!File.Exists(manifestPath)) return null;
        var stamp = File.GetLastWriteTimeUtc(manifestPath);
        if (_labels != null && stamp == _labelsStamp) return _labels;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var map = new Dictionary<string, string>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.TryGetProperty("file", out var f) && entry.TryGetProperty("label", out var l))
                    map[f.GetString() ?? ""] = l.GetString() ?? "";
            }
            _labels = map;
            _labelsStamp = stamp;
        }
        catch { _labels = null; }
        return _labels;
    }
}
