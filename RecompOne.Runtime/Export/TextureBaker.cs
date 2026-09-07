using RecompOne.Runtime.Host;

namespace RecompOne.Runtime.Export;

/// <summary>
/// Decodes a PS1 texture page out of VRAM into a plain 256x256 RGBA image, so a captured
/// primitive's material key (<c>tpage</c>/<c>clut</c>/texture-window) can be baked into a real
/// PNG that a glTF consumer understands. Generic: nothing here knows about any particular game.
///
/// <para>THE DECODE IS COPIED FROM THE RENDERER, NOT FROM DOCUMENTATION. The engine already
/// rasterises textured polygons correctly, so the authoritative decode is whatever the GPU does,
/// and matching it bit-for-bit is the whole point — a baker written from a hardware doc will
/// disagree with the shipped renderer in exactly the cases that are hard to notice. The two
/// implementations this mirrors are <c>Gpu.FetchTexel</c> (software raster) and the
/// <c>GlShaders</c> fragment shader (the HLE backend that actually draws the game):</para>
/// <list type="bullet">
///   <item>tpage bits 0-3 = page X in units of 64; bit 4 = page Y in units of 256;
///         bits 5-6 = semi-transparency blend mode; bits 7-8 = colour depth
///         (0 = 4bpp indexed, 1 = 8bpp indexed, 2/3 = 15bpp direct).</item>
///   <item>clut bits 0-5 = palette X in units of 16; bits 6-14 = palette Y (one VRAM row).</item>
///   <item>The texture window is applied to the texel coordinate BEFORE the fetch
///         (<c>u = (u &amp; ~(maskX*8)) | ((offX &amp; maskX)*8)</c>), then wrapped to 0-255.</item>
/// </list>
///
/// <para>TRANSPARENCY. A texel is transparent when the FULL 16-bit value is 0x0000 — not "when it
/// is black". Both renderers implement precisely that (<c>if (texel == 0) continue;</c> and
/// <c>if (texel.rgb == vec3(0.0) &amp;&amp; texel.a &lt; 0.5) discard;</c>), and it holds in every colour
/// mode, including 15bpp direct, not only in the paletted ones. A true black that is meant to be
/// opaque is encoded 0x8000 (black with the STP bit set), which is why getting this rule wrong
/// produces black boxes around sprites rather than a subtle shading error.</para>
///
/// <para>SEMI-TRANSPARENCY. Bit 15 (STP) does not mean "translucent" on its own: it selects, per
/// texel, whether the primitive's blend mode applies, and only when the primitive itself was
/// submitted with the semi-transparent flag. Bit 15 is therefore reported separately
/// (<see cref="PageStats.StpTexels"/>) and baked as a partial alpha only for materials whose
/// primitives really were semi-transparent — see <see cref="Bake"/>'s <c>semiTransparent</c>.</para>
///
/// <para>COLOUR EXPANSION. 5-bit channels are expanded full-range (<c>(c &lt;&lt; 3) | (c &gt;&gt; 2)</c>),
/// which matches the HLE GL backend — it stores VRAM as RGBA5551 and reads it back as
/// <c>c / 31.0</c>, so 31 becomes 255. The legacy software raster's <c>c &lt;&lt; 3</c> tops out at 248
/// and would dull every exported texture by ~3%.</para>
/// </summary>
public static class TextureBaker
{
    public const int PageSize = 256;   // a texture page is 256x256 TEXELS in every colour depth

    /// <summary>Per-page decode counts, so a bake is auditable instead of trusted.</summary>
    public readonly record struct PageStats(int TransparentTexels, int StpTexels, int OpaqueTexels, int Depth);

    /// <summary>Pack a GPU texture window into one key. Mask/offset are 5 bits each (units of 8
    /// texels), so all four fit in 20 bits.</summary>
    public static uint PackTexWindow(int maskX, int maskY, int offX, int offY) =>
        (uint)((maskX & 0x1F) | ((maskY & 0x1F) << 5) | ((offX & 0x1F) << 10) | ((offY & 0x1F) << 15));

    public static (int maskX, int maskY, int offX, int offY) UnpackTexWindow(uint w) =>
        ((int)(w & 0x1F), (int)((w >> 5) & 0x1F), (int)((w >> 10) & 0x1F), (int)((w >> 15) & 0x1F));

    /// <summary>Read VRAM wherever it currently lives. Under the HLE GPU backend VRAM is on the
    /// GL side and the CPU-side shadow is stale, so it must be read back — the same rule
    /// <c>Host.Screenshot</c> and <see cref="BackgroundPlate"/> follow.</summary>
    public static ushort[]? Snapshot()
    {
        var gpu = Runtime.Gpu;
        if (gpu == null) return null;
        if (!Hle.GpuHle.Active || Hle.GpuHle.Backend is not { } be) return gpu.Vram;
        var v = new ushort[Gpu.VramWidth * Gpu.VramHeight];
        be.ReadVram(0, 0, Gpu.VramWidth, Gpu.VramHeight, v);
        return v;
    }

    /// <summary>
    /// Decode one texture page through one CLUT into a 256x256 RGBA byte array (row-major, top
    /// row first, 4 bytes per texel).
    ///
    /// <para>The texture window is baked IN rather than left for the consumer to reproduce: the
    /// result is defined so that texel (u,v) of the returned image is exactly what the GPU would
    /// fetch for raw UV (u,v). That makes the glTF side trivially correct — no per-primitive UV
    /// arithmetic, no wrap mode to emulate — at the cost of one baked page per distinct window,
    /// which is why the window is part of the material key.</para>
    /// </summary>
    /// <param name="semiTransparent">Whether the primitives using this material were submitted
    /// with the semi-transparency flag. Only then does the per-texel STP bit mean anything, and
    /// only then is it baked down to a partial alpha.</param>
    public static byte[] Bake(ushort[] vram, ushort tpage, ushort clut, uint texWin, bool semiTransparent, out PageStats stats)
    {
        int pageX = (tpage & 0xF) * 64;
        int pageY = ((tpage >> 4) & 1) * 256;
        int depth = (tpage >> 7) & 3;
        int clutX = (clut & 0x3F) * 16;
        int clutY = (clut >> 6) & 0x1FF;
        var (maskX, maskY, offX, offY) = UnpackTexWindow(texWin);

        var rgba = new byte[PageSize * PageSize * 4];
        int transparent = 0, stp = 0, opaque = 0;

        for (int v = 0; v < PageSize; v++)
        {
            // Texture window, exactly as both renderers apply it, then wrap into the page.
            int tv = ((v & ~(maskY * 8)) | ((offY & maskY) * 8)) & 0xFF;
            int row = (pageY + tv) & (Gpu.VramHeight - 1);
            for (int u = 0; u < PageSize; u++)
            {
                int tu = ((u & ~(maskX * 8)) | ((offX & maskX) * 8)) & 0xFF;

                ushort texel;
                if (depth >= 2)
                {
                    texel = vram[row * Gpu.VramWidth + ((pageX + tu) & (Gpu.VramWidth - 1))];
                }
                else if (depth == 0)
                {
                    ushort block = vram[row * Gpu.VramWidth + ((pageX + (tu >> 2)) & (Gpu.VramWidth - 1))];
                    int index = (block >> ((tu & 3) * 4)) & 0xF;
                    texel = vram[(clutY & (Gpu.VramHeight - 1)) * Gpu.VramWidth + ((clutX + index) & (Gpu.VramWidth - 1))];
                }
                else
                {
                    ushort block = vram[row * Gpu.VramWidth + ((pageX + (tu >> 1)) & (Gpu.VramWidth - 1))];
                    int index = (block >> ((tu & 1) * 8)) & 0xFF;
                    texel = vram[(clutY & (Gpu.VramHeight - 1)) * Gpu.VramWidth + ((clutX + index) & (Gpu.VramWidth - 1))];
                }

                int o = (v * PageSize + u) * 4;
                if (texel == 0)
                {
                    // Fully transparent. Write transparent BLACK rather than leaving the colour
                    // channels undefined: a viewer that ignores alpha then shows black instead of
                    // garbage, and bilinear filtering bleeds black rather than a random neighbour.
                    transparent++;
                    rgba[o] = rgba[o + 1] = rgba[o + 2] = rgba[o + 3] = 0;
                    continue;
                }

                bool stpBit = (texel & 0x8000) != 0;
                if (stpBit) stp++; else opaque++;
                rgba[o] = Expand5(texel & 0x1F);
                rgba[o + 1] = Expand5((texel >> 5) & 0x1F);
                rgba[o + 2] = Expand5((texel >> 10) & 0x1F);
                // 0.5 alpha for a translucent texel is the closest a single glTF texture can get
                // to the PS1's four blend equations (average / additive / subtractive / quarter);
                // the exact mode is preserved in the material's `extras` for anyone who needs it.
                rgba[o + 3] = (stpBit && semiTransparent) ? (byte)128 : (byte)255;
            }
        }

        stats = new PageStats(transparent, stp, opaque, depth);
        return rgba;
    }

    /// <summary>PNG-encode a 256x256 RGBA buffer produced by <see cref="Bake"/>.</summary>
    public static byte[] EncodePng(byte[] rgba) => PngWriter.EncodeRgba(PageSize, PageSize, AddFilterBytes(rgba));

    /// <summary>PNG rows are each prefixed with a filter byte; <see cref="Bake"/> returns raw
    /// pixels, so add the 0x00 ("no filter") bytes the encoder expects.</summary>
    static byte[] AddFilterBytes(byte[] rgba)
    {
        var raw = new byte[(PageSize * 4 + 1) * PageSize];
        int o = 0;
        for (int y = 0; y < PageSize; y++)
        {
            raw[o++] = 0;
            Array.Copy(rgba, y * PageSize * 4, raw, o, PageSize * 4);
            o += PageSize * 4;
        }
        return raw;
    }

    static byte Expand5(int c) => (byte)((c << 3) | (c >> 2));
}
