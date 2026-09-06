namespace RecompOne.Runtime.Host;

/// <summary>Dump the GPU display area as an RGB PNG — headless-safe, works with the
/// software rasterizer or the GL-backed HLE GPU. Honours the GPU's 15/24-bit display
/// mode exactly like <c>HostWindow.ConvertDisplay</c>: a 24-bit framebuffer holds
/// packed RGB triples, so a row of W pixels occupies W*3 bytes = W*1.5 halfwords, and
/// decoding it as 15-bit produces vertical striping and a colour cast that reads as
/// progressive VRAM corruption when the VRAM is in fact pixel-perfect (this cost a
/// Parasite Eve II session its whole diagnosis -- PE2's MDEC backgrounds are 24-bit).</summary>
public static class Screenshot
{
    public static bool Save(string path)
    {
        var gpu = Runtime.Gpu;
        if (gpu == null) return false;
        int w = gpu.DisplayWidth, h = gpu.DisplayHeight, x0 = gpu.DisplayX, y0 = gpu.DisplayY;
        if (w <= 0 || h <= 0) return false;
        var vram = gpu.Vram;
        if (Hle.GpuHle.Active && Hle.GpuHle.Backend is { } be)
        {   // hardware-accelerated HLE GPU: VRAM lives on the GL side
            vram = new ushort[Gpu.VramWidth * Gpu.VramHeight];
            be.ReadVram(0, 0, Gpu.VramWidth, Gpu.VramHeight, vram);
        }
        var raw = new byte[(w * 3 + 1) * h];
        int o = 0;
        bool rgb24 = gpu.Display24Bit;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0; // filter: none
            if (rgb24)
            {   // packed 24-bit: the row starts at the display origin and runs w*3 bytes
                int lineByte = (((y0 + y) & (Gpu.VramHeight - 1)) * Gpu.VramWidth + x0) * 2;
                for (int x = 0; x < w; x++)
                {
                    int bo = lineByte + x * 3;
                    raw[o++] = VramByte(vram, bo);
                    raw[o++] = VramByte(vram, bo + 1);
                    raw[o++] = VramByte(vram, bo + 2);
                }
                continue;
            }
            for (int x = 0; x < w; x++)
            {
                ushort p = vram[((y0 + y) & (Gpu.VramHeight - 1)) * Gpu.VramWidth + ((x0 + x) & (Gpu.VramWidth - 1))];
                raw[o++] = (byte)((p & 0x1F) << 3); raw[o++] = (byte)(((p >> 5) & 0x1F) << 3); raw[o++] = (byte)(((p >> 10) & 0x1F) << 3);
            }
        }
        PngWriter.WriteRgb(path, w, h, raw);
        return true;
    }

    static byte VramByte(ushort[] vram, int byteOffset)
    {
        int hw = (byteOffset >> 1) & (Gpu.VramWidth * Gpu.VramHeight - 1);
        ushort v = vram[hw];
        return (byte)((byteOffset & 1) == 0 ? v & 0xFF : v >> 8);
    }
}
