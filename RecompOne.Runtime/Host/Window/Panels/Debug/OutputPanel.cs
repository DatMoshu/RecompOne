using System.Numerics;
using ImGuiNET;

namespace RecompOne.Runtime.Host.Window;

internal sealed class OutputPanel : IPanel
{
    public string Name => "Output";
    public bool IsOpen { get; set; } = true;

    static uint _texId;
    static int _texW, _texH;
    static float _aspect = 4f / 3f;

    public static void SetTexture(uint id, int w, int h, float aspect = 0f)
        => (_texId, _texW, _texH, _aspect) = (id, w, h, aspect > 0f ? aspect : 4f / 3f);

    public void Draw()
    {
        ImGui.SetNextWindowSize(new Vector2(640, 480), ImGuiCond.FirstUseEver);

        bool open = IsOpen;
        if (!ImGui.Begin(Name, ref open))
        {
            IsOpen = open;
            ImGui.End();
            return;
        }

        if (_texId != 0 && _texW > 0 && _texH > 0)
        {
            var avail = ImGui.GetContentRegionAvail();
            var imageSize = FitAspect(new Vector2(_aspect, 1f), avail);
            var offset = (avail - imageSize) * 0.5f;
            ImGui.SetCursorPos(ImGui.GetCursorPos() + offset);
            ImGui.Image((nint)_texId, imageSize);

            if (Host.DebugGizmos.Enabled)
                DrawGizmos(ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        }

        IsOpen = open;
        ImGui.End();
    }

    // Overlay the game image with the current DebugGizmos buffer. Primitive coords
    // are normalized [0..1] over the image rect [min..max].
    static void DrawGizmos(Vector2 min, Vector2 max)
    {
        var dl = ImGui.GetWindowDrawList();
        var size = max - min;
        Vector2 P(float nx, float ny) => new(min.X + nx * size.X, min.Y + ny * size.Y);

        foreach (var b in Host.DebugGizmos.Boxes)
        {
            var a = P(b.Nx0, b.Ny0);
            var c = P(b.Nx1, b.Ny1);
            if (b.Filled) dl.AddRectFilled(a, c, b.Color);
            else dl.AddRect(a, c, b.Color);
        }
        foreach (var l in Host.DebugGizmos.Lines)
            dl.AddLine(P(l.Nx0, l.Ny0), P(l.Nx1, l.Ny1), l.Color, 1.5f);
        foreach (var mk in Host.DebugGizmos.Markers)
        {
            var p = P(mk.Nx, mk.Ny);
            float r = MathF.Max(2f, mk.Radius);
            dl.AddCircleFilled(p, r, mk.Color);
            if (!string.IsNullOrEmpty(mk.Label))
                dl.AddText(p + new Vector2(r + 2f, -6f), mk.Color, mk.Label);
        }

        // HUD text block, top-left of the image, over a dark panel for legibility.
        var hud = Host.DebugGizmos.Hud;
        if (hud.Count > 0)
        {
            float lh = ImGui.GetTextLineHeightWithSpacing();
            var pad = new Vector2(6f, 4f);
            var origin = min + new Vector2(6f, 6f);
            float w = 0f;
            foreach (var h in hud) w = MathF.Max(w, ImGui.CalcTextSize(h.Text).X);
            var bgMin = origin - pad;
            var bgMax = origin + new Vector2(w, hud.Count * lh) + pad;
            dl.AddRectFilled(bgMin, bgMax, Host.DebugGizmos.Rgba(0, 0, 0, 150), 3f);
            float y = origin.Y;
            foreach (var h in hud)
            {
                dl.AddText(new Vector2(origin.X, y), h.Color, h.Text);
                y += lh;
            }
        }
    }

    static Vector2 FitAspect(Vector2 src, Vector2 dst)
    {
        float scale = MathF.Min(dst.X / src.X, dst.Y / src.Y);
        return src * scale;
    }
}
