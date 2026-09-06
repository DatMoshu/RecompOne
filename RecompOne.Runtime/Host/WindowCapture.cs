using Silk.NET.OpenGL;

namespace RecompOne.Runtime.Host;

/// <summary>Dump the WHOLE window backbuffer (game view + docked debug panels),
/// as opposed to <see cref="Screenshot"/> which reads the clean GPU display area
/// straight out of VRAM. Used for self-documenting diagnostic captures — a
/// screenshot that shows CPU/GPU/DMA/pad state alongside the frame it belongs to.
/// Must run at the tail of <c>HostWindow.OnRender</c>, after ImGui has drawn and
/// before the backbuffer is swapped (see <c>HostWindow.RequestWindowCapture</c>).</summary>
internal static class WindowCapture
{
    public static unsafe void Save(GL gl, int w, int h, string path)
    {
        if (w <= 0 || h <= 0) return;
        var pixels = new byte[w * h * 3];
        gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgb, PixelType.UnsignedByte, pixels.AsSpan());

        // glReadPixels rows are bottom-to-top; PNG rows are top-to-bottom.
        var raw = new byte[(w * 3 + 1) * h];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0; // filter: none
            int srcRow = h - 1 - y;
            Array.Copy(pixels, srcRow * w * 3, raw, o, w * 3);
            o += w * 3;
        }
        PngWriter.WriteRgb(path, w, h, raw);
    }
}
