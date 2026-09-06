using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Hardware;
using Silk.NET.SDL;

namespace RecompOne.Runtime.Host.Window;

// Live controller diagnostics. A pad can fail to reach the game at three different
// points, and this panel separates them so the failure is obvious at a glance:
//   1. SDL never sees the device            -> "Devices" list is empty
//   2. SDL sees it but has no mapping       -> device is listed as unusable
//   3. It is open and the raw buttons move  -> the fault is in the bindings or the game
// The bottom section shows the decoded PS1 pad word the game actually reads, so a
// binding that never fires is visible next to the raw input that should have set it.
internal sealed class ControllerPanel : IPanel
{
    public string Name => "Controller";
    public bool IsOpen { get; set; } = true;   // debug build: on by default, docked right

    static readonly (string Label, ushort Bit)[] Ps1Buttons =
    [
        ("Up", Controller.Up), ("Down", Controller.Down), ("Left", Controller.Left), ("Right", Controller.Right),
        ("Cross", Controller.Cross), ("Circle", Controller.Circle), ("Square", Controller.Square), ("Triangle", Controller.Triangle),
        ("L1", Controller.L1), ("R1", Controller.R1), ("L2", Controller.L2), ("R2", Controller.R2),
        ("L3", Controller.L3), ("R3", Controller.R3), ("Start", Controller.Start), ("Select", Controller.Select),
    ];

    static readonly Vector4 Green = new(0.35f, 0.95f, 0.4f, 1f);
    static readonly Vector4 Red = new(0.95f, 0.4f, 0.35f, 1f);
    static readonly Vector4 Dim = new(0.6f, 0.6f, 0.6f, 1f);

    public void Draw()
    {
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(new Vector2(vp.WorkPos.X + vp.WorkSize.X - 370, vp.WorkPos.Y + 20), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(355, 640), ImGuiCond.FirstUseEver);

        bool open = IsOpen;
        if (!ImGui.Begin(Name, ref open)) { IsOpen = open; ImGui.End(); return; }
        IsOpen = open;

        // --- 1. Is SDL up and has it opened a pad? --------------------------------
        bool connected = InputManager.IsConnected;
        ImGui.TextColored(connected ? Green : Red, connected ? "PAD CONNECTED" : "NO PAD OPEN");
        ImGui.SameLine();
        if (ImGui.Button("Rescan")) InputManager.Rescan();

        ImGui.TextColored(Dim, $"SDL ready: {InputManager.SdlReady}");
        ImGui.TextColored(Dim, $"open result: {InputManager.OpenResult}");
        ImGui.TextColored(Dim, $"last event: {InputManager.LastPadEvent}");
        ImGui.TextWrapped($"name: {InputManager.ControllerName}");

        ImGui.Separator();
        ImGui.Text($"Devices seen by SDL: {InputManager.DeviceCount}");
        foreach (var d in InputManager.DeviceList()) ImGui.TextWrapped("  " + d);

        // --- 2. Raw SDL input ------------------------------------------------------
        ImGui.Separator();
        if (ImGui.CollapsingHeader("Raw SDL input", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (!connected) ImGui.TextColored(Dim, "(no pad open)");
            else
            {
                var pressed = new List<string>();
                for (int b = 0; b < (int)GameControllerButton.Max; b++)
                    if (InputManager.RawButton(b)) pressed.Add(((GameControllerButton)b).ToString());
                ImGui.TextColored(pressed.Count > 0 ? Green : Dim,
                    pressed.Count > 0 ? "buttons: " + string.Join(", ", pressed) : "buttons: (none held)");

                for (int a = 0; a <= (int)GameControllerAxis.Triggerright; a++)
                {
                    short v = InputManager.RawAxis(a);
                    ImGui.Text($"{(GameControllerAxis)a,-14} {v,7}");
                    ImGui.SameLine();
                    ImGui.ProgressBar((v + 32768) / 65535f, new Vector2(110, 0), "");
                }
            }
        }

        // --- 3. What the game actually reads --------------------------------------
        ImGui.Separator();
        if (ImGui.CollapsingHeader("PS1 pad word (what the game reads)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ushort state = Controller.State;   // active low: 0 = pressed
            ImGui.Text($"state = 0x{state:X4}   sticks L({Controller.LeftX},{Controller.LeftY}) R({Controller.RightX},{Controller.RightY})");
            int col = 0;
            foreach (var (label, bit) in Ps1Buttons)
            {
                bool down = (state & bit) == 0;
                ImGui.TextColored(down ? Green : Dim, down ? $"[{label}]" : $" {label} ");
                if (++col % 4 != 0) ImGui.SameLine();
            }
            if (col % 4 != 0) ImGui.NewLine();
        }

        // --- 4. Bindings, so a wrong mapping is visible next to the raw input ------
        ImGui.Separator();
        if (ImGui.CollapsingHeader("Pad bindings (settings.json)"))
        {
            var p = ConfigManager.Game.Pad;
            ImGui.TextColored(Dim, "SDL button index per PS1 button; 100/101 = triggers, 102+ = stick directions");
            ImGui.Text($"Cross {p.Cross}   Circle {p.Circle}   Square {p.Square}   Triangle {p.Triangle}");
            ImGui.Text($"L1 {p.L1}   R1 {p.R1}   L2 {p.L2}   R2 {p.R2}   L3 {p.L3}   R3 {p.R3}");
            ImGui.Text($"Start {p.Start}   Select {p.Select}");
            ImGui.Text($"Up {p.Up}   Down {p.Down}   Left {p.Left}   Right {p.Right}");
        }

        ImGui.End();
    }
}
