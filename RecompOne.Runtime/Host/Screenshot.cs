using System.IO.Compression;
using System.Text;

namespace RecompOne.Runtime.Host;

/// <summary>Dump the GPU display area (15-bit VRAM) as an RGB PNG — headless-safe,
/// works with the software rasterizer or the GL-backed HLE GPU.</summary>
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
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0; // filter: none
            for (int x = 0; x < w; x++)
            {
                ushort p = vram[((y0 + y) & 511) * Gpu.VramWidth + ((x0 + x) & 1023)];
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
