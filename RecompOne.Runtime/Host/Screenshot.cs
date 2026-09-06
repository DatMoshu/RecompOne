using System.IO.Compression;
using System.Text;

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
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string type, byte[] data)
        {
            var t = Encoding.ASCII.GetBytes(type);
            var len = BitConverter.GetBytes(data.Length); Array.Reverse(len); ms.Write(len);
            ms.Write(t); ms.Write(data);
            var crc = BitConverter.GetBytes(Crc32(t, data)); Array.Reverse(crc); ms.Write(crc);
        }
        var ihdr = new byte[13];
        BitConverter.GetBytes(w).Reverse().ToArray().CopyTo(ihdr, 0);
        BitConverter.GetBytes(h).Reverse().ToArray().CopyTo(ihdr, 4);
        ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB
        Chunk("IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true)) zs.Write(raw);
            Chunk("IDAT", z.ToArray());
        }
        Chunk("IEND", Array.Empty<byte>());
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, ms.ToArray());
        return true;
    }

    static byte VramByte(ushort[] vram, int byteOffset)
    {
        int hw = (byteOffset >> 1) & (Gpu.VramWidth * Gpu.VramHeight - 1);
        ushort v = vram[hw];
        return (byte)((byteOffset & 1) == 0 ? v & 0xFF : v >> 8);
    }

    static uint Crc32(byte[] a, byte[] b)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var arr in new[] { a, b })
            foreach (var by in arr)
            {
                c ^= by;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
        return ~c;
    }
}
