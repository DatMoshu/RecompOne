namespace RecompOne.Runtime.Host;

// Generic debug-overlay buffer drawn on top of the presented game image (see
// OutputPanel.Draw). Nothing game-specific: a game's FrameHook fills this each
// frame with HUD text lines and/or primitives, and the host renders them over
// the frame via ImGui's draw list. Single-threaded — the FrameHook writes at the
// end of a frame and OutputPanel draws it at the start of the next (one frame of
// lag, no locking needed). Primitive coords are NORMALIZED [0..1] over the game
// image rect, so they line up regardless of window size / aspect.
public static class DebugGizmos
{
    public static bool Enabled;

    public readonly record struct HudLine(string Text, uint Color);
    public readonly record struct Marker(float Nx, float Ny, float Radius, uint Color, string? Label);
    public readonly record struct Line(float Nx0, float Ny0, float Nx1, float Ny1, uint Color);
    public readonly record struct Box(float Nx0, float Ny0, float Nx1, float Ny1, uint Color, bool Filled);

    public static readonly List<HudLine> Hud = new();
    public static readonly List<Marker> Markers = new();
    public static readonly List<Line> Lines = new();
    public static readonly List<Box> Boxes = new();

    // ImGui packs colors as 0xAABBGGRR.
    public static uint Rgba(byte r, byte g, byte b, byte a = 255)
        => (uint)a << 24 | (uint)b << 16 | (uint)g << 8 | r;

    public static readonly uint White = Rgba(255, 255, 255);
    public static readonly uint Yellow = Rgba(255, 220, 60);
    public static readonly uint Green = Rgba(80, 230, 120);
    public static readonly uint Red = Rgba(255, 90, 90);
    public static readonly uint Cyan = Rgba(90, 210, 240);
    public static readonly uint Dim = Rgba(160, 160, 160);

    // Clear last frame's contents. Call once at the top of the game's per-frame hook.
    public static void BeginFrame()
    {
        Hud.Clear();
        Markers.Clear();
        Lines.Clear();
        Boxes.Clear();
    }

    public static void Text(string text, uint color) => Hud.Add(new HudLine(text, color));
    public static void Text(string text) => Hud.Add(new HudLine(text, White));
    public static void AddMarker(float nx, float ny, float radius, uint color, string? label = null)
        => Markers.Add(new Marker(nx, ny, radius, color, label));
    public static void AddLine(float nx0, float ny0, float nx1, float ny1, uint color)
        => Lines.Add(new Line(nx0, ny0, nx1, ny1, color));
    public static void AddBox(float nx0, float ny0, float nx1, float ny1, uint color, bool filled = false)
        => Boxes.Add(new Box(nx0, ny0, nx1, ny1, color, filled));
}
