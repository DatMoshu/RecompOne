using RecompOne.Runtime.Hle;

namespace RecompOne.Runtime;

/// <summary>Observation and direct access points for the CPU→VRAM transfer paths.
///
/// <para>WHY THESE EXIST. A fixed-camera PS1 game does not draw its pre-rendered backdrop
/// with polygons — it puts it in the framebuffer with an <b>image transfer</b> (GP0 0xA0,
/// i.e. libgpu <c>LoadImage</c> / a GPU DMA block) or a <b>VRAM-to-VRAM blit</b> (GP0 0x80,
/// <c>MoveImage</c>). Measured on Parasite Eve: a correct single-frame capture of a field
/// room is 1124 triangles, <b>all</b> of them GTE-resolved 3D and <b>zero</b> screen-space —
/// the plate never passes through <c>HleTri</c> at all. So anything that wants to observe
/// or replace the backdrop has to hook the transfer, not the draw.</para>
///
/// <para>Both hooks are multicast <c>Action</c> fields, and callers must attach with
/// <c>+=</c> / detach with <c>-=</c>: the plate exporter and the art-override mod both
/// listen, and a plain assignment by one would silently unhook the other.</para>
///
/// <para>Cost when nobody is listening: one null check per completed transfer — not per
/// pixel — so a port that never attaches is unchanged.</para></summary>
public sealed partial class Gpu
{
    /// <summary>Fired once per COMPLETED CPU→VRAM image transfer, with the destination rect
    /// in absolute VRAM pixels. Fires after the pixels have landed (in the GL backend when
    /// the HLE GPU is active, otherwise in <see cref="Vram"/>), so a listener that reads the
    /// rect back sees exactly what was uploaded.</summary>
    public static Action<int, int, int, int>? OnImageUpload;

    /// <summary>Fired once per completed VRAM→VRAM blit, with the DESTINATION rect.
    /// Same contract as <see cref="OnImageUpload"/>.</summary>
    public static Action<int, int, int, int>? OnVramCopy;

    /// <summary>Fired after each rectangle/sprite primitive is rasterised: rect in absolute VRAM
    /// pixels, then whether it was textured, its texture page and CLUT.
    ///
    /// <para>Sprites are the third way a backdrop reaches the screen, and the one that is easiest
    /// to miss: they carry no GTE transform, so a GTE-correlating capture never sees them, and
    /// they are not image transfers either. PE1 draws the foreground pieces of a room this way —
    /// the theatre awning in map 2 is sprites, submitted after the characters so it occludes
    /// them. The texture page travels with the rect because it is the only thing that
    /// distinguishes "a piece of the room" from "a glyph of the dialogue box" at this level.</para></summary>
    public static Action<int, int, int, int, bool, ushort, ushort>? OnRectDrawn;

    /// <summary>Fired after each triangle is rasterised. Exists to give a listener the one
    /// boundary that separates a fixed-camera game's BACKGROUND layer from its FOREGROUND layer:
    /// the models are drawn between them, so everything painted before the frame's first
    /// triangle is behind the characters and everything painted after it is in front. PE1 relies
    /// on exactly that — the limo's open door and the theatre awning are tile sprites submitted
    /// after the characters so they occlude them.</summary>
    public static Action? OnTriangleDrawn;

    /// <summary>Read a rect of VRAM into <paramref name="dst"/> (row-major, w*h halfwords),
    /// from whichever store is authoritative for this backend. Coordinates wrap like the
    /// real GPU's addressing.</summary>
    public void ReadVramRect(int x, int y, int w, int h, ushort[] dst)
    {
        if (w <= 0 || h <= 0 || dst.Length < w * h) return;
        if (HleOn) { GpuHle.Backend!.ReadVram(x, y, w, h, dst); return; }
        var v = Vram;
        for (int row = 0; row < h; row++)
        {
            int sy = (y + row) & (VramHeight - 1);
            for (int col = 0; col < w; col++)
                dst[row * w + col] = v[sy * VramWidth + ((x + col) & (VramWidth - 1))];
        }
    }

    /// <summary>Write a rect of VRAM from <paramref name="src"/> (row-major, w*h halfwords).
    /// Mirrors what an image transfer does — including the fact that in HLE mode the
    /// authoritative copy is the backend's, not the shadow array.</summary>
    public void WriteVramRect(int x, int y, int w, int h, ReadOnlySpan<ushort> src)
    {
        if (w <= 0 || h <= 0 || src.Length < w * h) return;
        if (HleOn) { GpuHle.Backend!.WriteVram(x, y, w, h, src); return; }
        var v = Vram;
        for (int row = 0; row < h; row++)
        {
            int dy = (y + row) & (VramHeight - 1);
            for (int col = 0; col < w; col++)
                v[dy * VramWidth + ((x + col) & (VramWidth - 1))] = src[row * w + col];
        }
    }
}
