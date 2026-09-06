using System.Text;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

/// <summary>One-shot full-machine dump for black-screen/hang triage ("is there
/// anything in there at all?"). Writes the whole 1024x512 VRAM (decoded BOTH as
/// 15-bit and as 24-bit -- see <see cref="Screenshot"/>'s note on why a wrong guess
/// there reads as corruption when the frame is pixel-perfect), the full 2 MB RAM
/// image plus a rendered RAM-map PNG (reusing the RAM Map panel's own
/// <see cref="RamLogger"/>), the <see cref="CaptureData"/> state JSON, and the
/// active overlay set from <see cref="Dispatcher"/>. Every step is defensive: a
/// missing GPU/memory or a mid-hang machine still yields whatever is available
/// instead of throwing. Default off, only runs on explicit request
/// (--dump-all=&lt;dir&gt; or the Debug menu), so it costs nothing unused.</summary>
public static class DumpAll
{
    public static void Write(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { return; }

        var gpu = Runtime.Gpu;
        var mem = Runtime.Mem;

        Safe(() => WriteVram(dir, gpu));
        Safe(() => WriteRam(dir, mem as PSMemory));
        Safe(() => CaptureData.Write(Path.Combine(dir, "state.json")));
        Safe(() => WriteOverlays(dir));
        Safe(() => WriteIndex(dir, gpu, mem));

        Console.WriteLine($"[dump-all] wrote {Path.GetFullPath(dir)}");
    }

    static void Safe(Action a) { try { a(); } catch (Exception ex) { Console.WriteLine($"[dump-all] step failed: {ex.Message}"); } }

    static ushort[]? FetchVram(Gpu? gpu)
    {
        if (gpu == null) return null;
        var vram = gpu.Vram;
        if (Hle.GpuHle.Active && Hle.GpuHle.Backend is { } be)
        {   // hardware-accelerated HLE GPU: VRAM lives on the GL side
            vram = new ushort[Gpu.VramWidth * Gpu.VramHeight];
            be.ReadVram(0, 0, Gpu.VramWidth, Gpu.VramHeight, vram);
        }
        return vram;
    }

    static void WriteVram(string dir, Gpu? gpu)
    {
        var vram = FetchVram(gpu);
        if (vram == null) return;
        WriteVram15(dir, vram);
        WriteVram24(dir, vram);
    }

    // Full VRAM decoded as 16 pixels-per-16bit-word (5:5:5), the normal mode.
    static void WriteVram15(string dir, ushort[] vram)
    {
        int w = Gpu.VramWidth, h = Gpu.VramHeight;
        var raw = new byte[(w * 3 + 1) * h];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0;
            for (int x = 0; x < w; x++)
            {
                ushort p = vram[y * w + x];
                raw[o++] = (byte)((p & 0x1F) << 3);
                raw[o++] = (byte)(((p >> 5) & 0x1F) << 3);
                raw[o++] = (byte)(((p >> 10) & 0x1F) << 3);
            }
        }
        PngWriter.WriteRgb(Path.Combine(dir, "vram_full_15bit.png"), w, h, raw);
    }

    // Full VRAM decoded as packed 24-bit RGB triples (3 bytes/pixel across the
    // 16-bit-wide grid), narrower output (~682px) exactly like real 24-bit display.
    static void WriteVram24(string dir, ushort[] vram)
    {
        int wFull = Gpu.VramWidth, h = Gpu.VramHeight;
        int w = wFull * 2 / 3;
        var raw = new byte[(w * 3 + 1) * h];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0;
            int lineByte = y * wFull * 2;
            for (int x = 0; x < w; x++)
            {
                int bo = lineByte + x * 3;
                raw[o++] = VramByte(vram, bo);
                raw[o++] = VramByte(vram, bo + 1);
                raw[o++] = VramByte(vram, bo + 2);
            }
        }
        PngWriter.WriteRgb(Path.Combine(dir, "vram_full_24bit.png"), w, h, raw);
    }

    static byte VramByte(ushort[] vram, int byteOffset)
    {
        int hw = (byteOffset >> 1) & (Gpu.VramWidth * Gpu.VramHeight - 1);
        ushort v = vram[hw];
        return (byte)((byteOffset & 1) == 0 ? v & 0xFF : v >> 8);
    }

    static void WriteRam(string dir, PSMemory? psMem)
    {
        if (psMem == null) return;
        File.WriteAllBytes(Path.Combine(dir, "ram.bin"), psMem.Ram.ToArray());

        // Reuse the RAM Map panel's own renderer so the dump matches what the user
        // already sees live in the panel (same greyscale/write-heat encoding).
        int w = RamLogger.Width, h = RamLogger.Height;
        var rgba = new byte[w * h * 4];
        Runtime.RamLog.BuildTexture(psMem.Ram, rgba);
        var raw = new byte[(w * 3 + 1) * h];
        int o = 0, si = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0;
            for (int x = 0; x < w; x++)
            {
                raw[o++] = rgba[si]; raw[o++] = rgba[si + 1]; raw[o++] = rgba[si + 2];
                si += 4;
            }
        }
        PngWriter.WriteRgb(Path.Combine(dir, "ram_map.png"), w, h, raw);
    }

    static void WriteOverlays(string dir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# active overlays (RecompOne.Runtime.Dispatch.Dispatcher)");
        foreach (var o in Dispatcher.DescribeActive())
            sb.AppendLine($"{o.Name}\tlba={o.Lba}\tfunctions={o.FunctionCount}\trange=0x{o.MinAddr:X8}-0x{o.MaxAddr:X8}");
        File.WriteAllText(Path.Combine(dir, "overlays.txt"), sb.ToString());
    }

    static void WriteIndex(string dir, Gpu? gpu, IMemory? mem)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# dump-all — full machine snapshot");
        sb.AppendLine($"frame {Runtime.FrameCount}, fps {Runtime.Fps:F1}");
        sb.AppendLine();
        sb.AppendLine("## files");
        sb.AppendLine("- vram_full_15bit.png — whole 1024x512 VRAM decoded as 5:5:5 (normal mode)");
        sb.AppendLine("- vram_full_24bit.png — same VRAM decoded as packed 24-bit RGB (narrower, ~682px)");
        sb.AppendLine("- ram.bin — raw 2 MB (or devkit-sized) main RAM image");
        sb.AppendLine("- ram_map.png — rendered RAM Map panel view (greyscale content + recent-write heat)");
        sb.AppendLine("- state.json — CaptureData: frame/fps, GPU regs (display/draw area/offset/range), DMA channels, pad state, registered probes");
        sb.AppendLine("- overlays.txt — active Dispatcher overlays and their function address ranges");
        sb.AppendLine();
        sb.AppendLine("## how to read this");
        sb.AppendLine("- VRAM EMPTY (flat/blank in both PNGs) + black screen => nothing ever drew. Look at draw calls / GPU init / GTE, not the display path.");
        sb.AppendLine("- VRAM FULL (a real frame visible in one of the two decodes) + black screen => the game rendered fine; the DISPLAY PATH failed (wrong displayEnabled/display24Bit/displayX,Y/hRange,vRange in state.json, or the host's present/convert step).");
        sb.AppendLine("- If the 15-bit PNG looks like striped garbage but the 24-bit PNG looks like a real picture (or vice versa), you had the bit depth backwards — check state.json's display24Bit against which PNG looks right.");
        sb.AppendLine("- overlays.txt empty/missing the expected module => the code that should be drawing was never loaded into this address range.");
        if (gpu != null)
            sb.AppendLine($"- gpu: displayEnabled={gpu.DisplayEnabled} display24Bit={gpu.Display24Bit} displayX={gpu.DisplayX} displayY={gpu.DisplayY} size={gpu.DisplayWidth}x{gpu.DisplayHeight}");
        if (mem == null) sb.AppendLine("- NOTE: Runtime.Mem was null — ram.bin/state.json/overlays may be incomplete.");
        File.WriteAllText(Path.Combine(dir, "index.md"), sb.ToString());
    }
}
