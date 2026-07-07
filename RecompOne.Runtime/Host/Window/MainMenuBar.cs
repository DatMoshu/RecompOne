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

        ImGui.Separator();

        if (ImGui.MenuItem("Reset View")) ConfigManager.ResetView(PanelManager.Panels);
        
        ImGui.EndMenu();
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
