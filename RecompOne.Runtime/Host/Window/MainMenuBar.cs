using ImGuiNET;
using RecompOne.Runtime.Cdrom;
using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Host.Window;

internal static class MainMenuBar
{
    public static void Draw()
    {
     
        ConfigMenu();
        DebugMenu();
        HelpMenu();
        ImGui.EndMainMenuBar();
    }

    static void ConfigMenu()
    {
        if (!ImGui.BeginMainMenuBar()) return;

        if (ImGui.BeginMenu("Settings"))
        {
            if (ImGui.MenuItem("Settings..."))
                if (PanelManager.Get<SettingsPopup>() is { } popup) popup.IsOpen = true;

            if (DiscManager.Discs.Count > 1 && ImGui.BeginMenu("Swap Disc"))
            {
                for (int i = 0; i < DiscManager.Discs.Count; i++)
                {
                    var label = $"Disc {i + 1}: {Path.GetFileNameWithoutExtension(DiscManager.Discs[i])}";
                    if (ImGui.MenuItem(label, null, DiscManager.Current == i) && DiscManager.Current != i)
                        DiscManager.Swap(i);
                }
                ImGui.EndMenu();
            }

            ImGui.Separator();

            bool showBar = !ConfigManager.View.HideTopBar;
            if (ImGui.MenuItem("Show Menu Bar", "F1", showBar))
            {
                ConfigManager.View.HideTopBar = showBar;
                ConfigManager.SaveView(PanelManager.Panels);
            }

            bool fs = ConfigManager.View.Fullscreen;
            if (ImGui.MenuItem("Fullscreen", "F11", fs))
            {
                ConfigManager.View.Fullscreen = !fs;
                HostWindow.SetFullscreen(!fs);
                ConfigManager.SaveView(PanelManager.Panels);
            }

            ImGui.EndMenu();
        }
    }
    static void DebugMenu()
    {
        if (!ImGui.BeginMenu("Debug")) return;

        if (ImGui.BeginMenu("GPU"))
        {
            Toggle<OutputPanel>("Output");
            Toggle<VramViewerPanel>("VRAM Viewer");
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("CPU"))
        {
            Toggle<CpuStatePanel>("CPU State");
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Memory"))
        {
            Toggle<RamMapPanel>("RAM Map");
            Toggle<RamHexPanel>("RAM Hex");
            ImGui.Separator();
            Toggle<DmaViewerPanel>("DMA Viewer");
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("System"))
        {
            Toggle<OverlayEventsPanel>("Overlay Events");
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Input"))
        {
            Toggle<ControllerPanel>("Controller");
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Info"))
        {
            Toggle<GameInfoPanel>("Game Info");
            Toggle<SavestatePanel>("Savestates");
            Toggle<ModsPanel>("Mods");
            Toggle<AssetViewerPanel>("Asset Viewer");
            ImGui.EndMenu();
        }

        ImGui.Separator();

        SpeedMenu();

        if (ImGui.MenuItem("Dump All (VRAM + RAM + state)..."))
            Host.DumpAll.Write(Path.Combine("dumps", $"dump_{Runtime.FrameCount:D6}"));

        ImGui.Separator();

        if (ImGui.MenuItem("Reset Layout"))
        {
            ConfigManager.ResetView(PanelManager.Panels);
            HostWindow.RequestLayoutReset(); // rebuild the docked default next frame
        }

        if (ImGui.MenuItem("Restart Game (relaunch process)"))
            ProcessRestart.Request();

        ImGui.EndMenu();
    }

    // Emulation speed. One presented frame is one PS1 vblank, so a multiplier here is a
    // multiplier on everything the game does. Turbo is the same control with its own rate,
    // toggled by a key -- not a separate mode -- so the picture and the pad keep working
    // through it (RecompOne.Runtime.Host.FrameClock).
    static readonly (string Label, double Value)[] SpeedChoices =
    [
        ("0.25x (slow motion)", 0.25), ("0.5x", 0.5), ("1x (normal)", 1.0),
        ("1.5x", 1.5), ("2x", 2.0), ("3x", 3.0), ("4x", 4.0), ("8x", 8.0),
        ("Uncapped", 0.0),
    ];

    static void SpeedMenu()
    {
        if (ImGui.MenuItem("Turbo (fast-forward)", "Tab", Host.Tas.UserTurbo))
            Host.Tas.UserTurbo = !Host.Tas.UserTurbo;

        if (ImGui.BeginMenu("Speed"))
        {
            ImGui.TextDisabled("Normal speed");
            foreach (var (label, value) in SpeedChoices)
                if (ImGui.MenuItem(label, "", Math.Abs(Host.FrameClock.Speed - value) < 1e-9))
                    Host.FrameClock.Speed = value;

            ImGui.Separator();
            ImGui.TextDisabled("Turbo speed (Tab)");
            foreach (var (label, value) in SpeedChoices)
            {
                if (value != 0 && value < 1) continue; // a turbo slower than real time is not a turbo
                if (ImGui.MenuItem(label + "##turbo", "", Math.Abs(Host.FrameClock.TurboSpeed - value) < 1e-9))
                    Host.FrameClock.TurboSpeed = value;
            }

            ImGui.Separator();
            bool mute = Host.Audio.MuteWhileFast;
            if (ImGui.MenuItem("Mute while fast-forwarding", "", mute))
                Host.Audio.MuteWhileFast = !mute;
            ImGui.EndMenu();
        }
    }

    static void HelpMenu()
    {
        if (!ImGui.BeginMenu("Help")) return;
        if (ImGui.MenuItem("About"))
            if (PanelManager.Get<AboutPopup>() is { } about) about.IsOpen = true;

        ImGui.EndMenu();
    }

    static void Toggle<T>(string label) where T : class, IPanel
    {
        var panel = PanelManager.Get<T>();
        if (panel == null) return;
        bool open = panel.IsOpen;
        if (ImGui.MenuItem(label, null, open)) panel.IsOpen = !open;
    }
}
