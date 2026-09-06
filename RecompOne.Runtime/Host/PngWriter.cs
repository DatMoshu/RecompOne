using System.IO.Compression;
using System.Text;

namespace RecompOne.Runtime.Host;

/// <summary>Minimal PNG encoder shared by <see cref="Screenshot"/> (clean game
/// framebuffer) and <see cref="WindowCapture"/> (full window incl. debug panels).
/// Takes a pre-filtered raw buffer: each row is a 0x00 "no filter" byte followed
/// by w*3 RGB bytes, top row first.</summary>
internal static class PngWriter
{
    public static void WriteRgb(string path, int w, int h, byte[] rawFiltered)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
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
            using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true)) zs.Write(rawFiltered);
            Chunk("IDAT", z.ToArray());
        }
        Chunk("IEND", []);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, ms.ToArray());
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
