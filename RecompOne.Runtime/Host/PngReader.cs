using System.IO.Compression;
using System.Text;

namespace RecompOne.Runtime.Host;

/// <summary>Minimal PNG decoder, the counterpart to <see cref="PngWriter"/>. Exists because
/// the art-override mod has to read images a human made in an ordinary paint program, and the
/// runtime deliberately has no imaging dependency.
///
/// <para>SCOPE, stated rather than discovered later: 8-bit non-interlaced truecolour, with or
/// without alpha (PNG colour types 2 and 6), which is what every editor writes by default.
/// Anything else — 16-bit channels, palette, greyscale, Adam7 interlace — is REFUSED by name
/// rather than half-decoded into a wrong-looking image. A refusal the caller can print is far
/// more useful than a picture that is subtly incorrect.</para></summary>
public static class PngReader
{
    /// <summary>Decode to tightly packed RGBA8 (w*h*4, top row first). Returns false and sets
    /// <paramref name="error"/> on anything unsupported or malformed; never throws for bad
    /// input, so a caller can treat an unreadable art file as "carry on with the original".</summary>
    public static bool TryDecodeRgba(string path, out int w, out int h, out byte[] rgba, out string error)
    {
        w = h = 0; rgba = Array.Empty<byte>(); error = "";
        try
        {
            var f = File.ReadAllBytes(path);
            return TryDecodeRgba(f, out w, out h, out rgba, out error);
        }
        catch (Exception e) { error = e.Message; return false; }
    }

    public static bool TryDecodeRgba(byte[] f, out int w, out int h, out byte[] rgba, out string error)
    {
        w = h = 0; rgba = Array.Empty<byte>(); error = "";
        try
        {
            if (f.Length < 8 || f[0] != 0x89 || f[1] != 'P' || f[2] != 'N' || f[3] != 'G')
            { error = "not a PNG (bad signature)"; return false; }

            int p = 8;
            byte bitDepth = 0, colorType = 0, interlace = 0;
            bool haveHdr = false;
            using var idat = new MemoryStream();
            while (p + 8 <= f.Length)
            {
                int len = BE32(f, p); p += 4;
                if (len < 0 || p + 4 + len + 4 > f.Length) { error = "truncated chunk"; return false; }
                string type = Encoding.ASCII.GetString(f, p, 4); p += 4;
                if (type == "IHDR")
                {
                    if (len < 13) { error = "short IHDR"; return false; }
                    w = BE32(f, p); h = BE32(f, p + 4);
                    bitDepth = f[p + 8]; colorType = f[p + 9]; interlace = f[p + 12];
                    haveHdr = true;
                }
                else if (type == "IDAT") idat.Write(f, p, len);
                else if (type == "IEND") { p += len + 4; break; }
                p += len + 4; // payload + CRC (not verified: a corrupt file fails at inflate anyway)
            }

            if (!haveHdr) { error = "no IHDR"; return false; }
            if (w <= 0 || h <= 0 || (long)w * h > 64L * 1024 * 1024) { error = $"implausible size {w}x{h}"; return false; }
            if (bitDepth != 8) { error = $"{bitDepth}-bit channels unsupported (need 8-bit; re-save as 8 bits per channel)"; return false; }
            if (colorType != 2 && colorType != 6)
            {
                error = colorType switch
                {
                    0 => "greyscale unsupported (re-save as RGB or RGBA)",
                    3 => "palettised/indexed unsupported (re-save as RGB or RGBA)",
                    4 => "greyscale+alpha unsupported (re-save as RGB or RGBA)",
                    _ => $"colour type {colorType} unsupported (need RGB or RGBA)",
                };
                return false;
            }
            if (interlace != 0) { error = "interlaced (Adam7) unsupported — re-save without interlacing"; return false; }

            int bpp = colorType == 6 ? 4 : 3;
            int stride = w * bpp;
            var raw = new byte[(long)(stride + 1) * h <= int.MaxValue ? (stride + 1) * h : 0];
            if (raw.Length == 0) { error = "image too large"; return false; }

            idat.Position = 0;
            using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            {
                int got = 0;
                while (got < raw.Length)
                {
                    int n = z.Read(raw, got, raw.Length - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got < raw.Length) { error = "image data ended early (truncated or corrupt PNG)"; return false; }
            }

            // Un-filter in place, row by row (PNG filter types 0..4).
            var cur = new byte[stride];
            var prev = new byte[stride];
            rgba = new byte[w * h * 4];
            int src = 0;
            for (int y = 0; y < h; y++)
            {
                byte ft = raw[src++];
                Buffer.BlockCopy(raw, src, cur, 0, stride); src += stride;
                for (int i = 0; i < stride; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0;
                    int b = prev[i];
                    int c = i >= bpp ? prev[i - bpp] : 0;
                    cur[i] = ft switch
                    {
                        0 => cur[i],
                        1 => (byte)(cur[i] + a),
                        2 => (byte)(cur[i] + b),
                        3 => (byte)(cur[i] + ((a + b) >> 1)),
                        4 => (byte)(cur[i] + Paeth(a, b, c)),
                        _ => cur[i],
                    };
                }
                if (ft > 4) { error = $"unknown row filter {ft}"; return false; }
                int dst = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    rgba[dst + x * 4 + 0] = cur[x * bpp + 0];
                    rgba[dst + x * 4 + 1] = cur[x * bpp + 1];
                    rgba[dst + x * 4 + 2] = cur[x * bpp + 2];
                    rgba[dst + x * 4 + 3] = bpp == 4 ? cur[x * bpp + 3] : (byte)255;
                }
                (prev, cur) = (cur, prev);
            }
            return true;
        }
        catch (Exception e) { error = e.Message; return false; }
    }

    static int Paeth(int a, int b, int c)
    {
        int pp = a + b - c, pa = Math.Abs(pp - a), pb = Math.Abs(pp - b), pc = Math.Abs(pp - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    static int BE32(byte[] f, int i) => (f[i] << 24) | (f[i + 1] << 16) | (f[i + 2] << 8) | f[i + 3];
}
