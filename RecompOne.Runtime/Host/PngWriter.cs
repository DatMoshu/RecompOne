using System.IO.Compression;
using System.Text;

namespace RecompOne.Runtime.Host;

/// <summary>Minimal PNG encoder shared by <see cref="Screenshot"/> (clean game
/// framebuffer) and <see cref="WindowCapture"/> (full window incl. debug panels).
/// Takes a pre-filtered raw buffer: each row is a 0x00 "no filter" byte followed
/// by w*3 RGB bytes, top row first.</summary>
internal static class PngWriter
{
    /// <summary>Same encoder, 8-bit RGBA. Rows are a 0x00 filter byte + w*4 RGBA bytes.
    /// Used by the background-plate exporter, which needs real transparency where the
    /// characters were masked out.</summary>
    public static void WriteRgba(string path, int w, int h, byte[] rawFiltered) =>
        Write(path, w, h, rawFiltered, colorType: 6);

    public static void WriteRgb(string path, int w, int h, byte[] rawFiltered) =>
        Write(path, w, h, rawFiltered, colorType: 2);

    /// <summary>Same 8-bit RGBA encoder, but returns the PNG bytes instead of writing a file.
    /// Needed by <see cref="Export.GltfWriter"/>, which embeds baked textures directly in the
    /// .glb's binary chunk so the exported room is one self-contained file.</summary>
    public static byte[] EncodeRgba(int w, int h, byte[] rawFiltered) =>
        Encode(w, h, rawFiltered, colorType: 6);

    static void Write(string path, int w, int h, byte[] rawFiltered, byte colorType)
    {
        var png = Encode(w, h, rawFiltered, colorType);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, png);
    }

    static byte[] Encode(int w, int h, byte[] rawFiltered, byte colorType)
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
        ihdr[8] = 8; ihdr[9] = colorType; // 8 bits/channel; 2 = RGB, 6 = RGBA
        Chunk("IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true)) zs.Write(rawFiltered);
            Chunk("IDAT", z.ToArray());
        }
        Chunk("IEND", []);
        return ms.ToArray();
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
