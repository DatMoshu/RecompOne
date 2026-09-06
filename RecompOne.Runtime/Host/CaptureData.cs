using System.Text;
using System.Text.Json;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

/// <summary>Machine-readable sidecar for a capture: frame number, FPS, GPU/DMA/pad
/// state, and every value in <c>Play.Probes</c> (the game-agnostic probe registry —
/// this file never hardcodes a game address, it just dumps whatever the game host
/// has registered). Pair with a screenshot by giving it the same base name.</summary>
internal static class CaptureData
{
    public static void Write(string path)
    {
        var mem = Runtime.Mem;
        var gpu = Runtime.Gpu;

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("frame", Runtime.FrameCount);
            w.WriteNumber("fps", Runtime.Fps);

            if (gpu != null)
            {
                w.WriteStartObject("gpu");
                w.WriteNumber("gp0Words", gpu.Gp0Words);
                w.WriteNumber("gp1Words", gpu.Gp1Words);
                w.WriteBoolean("displayEnabled", gpu.DisplayEnabled);
                w.WriteBoolean("display24Bit", gpu.Display24Bit);
                w.WriteNumber("displayX", gpu.DisplayX);
                w.WriteNumber("displayY", gpu.DisplayY);
                w.WriteNumber("displayWidth", gpu.DisplayWidth);
                w.WriteNumber("displayHeight", gpu.DisplayHeight);
                w.WriteStartObject("drawArea");
                w.WriteNumber("left", gpu.DrawAreaLeft);
                w.WriteNumber("top", gpu.DrawAreaTop);
                w.WriteNumber("right", gpu.DrawAreaRight);
                w.WriteNumber("bottom", gpu.DrawAreaBottom);
                w.WriteEndObject();
                w.WriteStartObject("drawOffset");
                w.WriteNumber("x", gpu.DrawOffsetX);
                w.WriteNumber("y", gpu.DrawOffsetY);
                w.WriteEndObject();
                w.WriteStartObject("hRange");
                w.WriteNumber("1", gpu.HRange1);
                w.WriteNumber("2", gpu.HRange2);
                w.WriteEndObject();
                w.WriteStartObject("vRange");
                w.WriteNumber("1", gpu.VRange1);
                w.WriteNumber("2", gpu.VRange2);
                w.WriteEndObject();
                w.WriteEndObject();
            }

            if (mem != null)
            {
                w.WriteStartObject("dma");
                w.WriteString("dicr", $"{SafeRead(mem, 0x1F8010F4u):X8}");
                w.WriteStartArray("channels");
                string[] names = ["MDECin", "MDECout", "GPU", "CDROM", "SPU", "PIO", "OTC"];
                for (int ch = 0; ch < 7; ch++)
                {
                    uint baseAddr = 0x1F801080u + (uint)(ch * 0x10);
                    uint chcr = SafeRead(mem, baseAddr + 8);
                    w.WriteStartObject();
                    w.WriteString("name", names[ch]);
                    w.WriteString("madr", $"{SafeRead(mem, baseAddr):X8}");
                    w.WriteString("bcr", $"{SafeRead(mem, baseAddr + 4):X8}");
                    w.WriteString("chcr", $"{chcr:X8}");
                    w.WriteBoolean("active", (chcr & 0x01000000u) != 0);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteStartObject("pad");
            w.WriteString("state", $"{Controller.State:X4}");
            w.WriteNumber("leftX", Controller.LeftX);
            w.WriteNumber("leftY", Controller.LeftY);
            w.WriteNumber("rightX", Controller.RightX);
            w.WriteNumber("rightY", Controller.RightY);
            w.WriteEndObject();

            // Game-agnostic probe registry (RecompOne.Runtime.Play.Probes): each game
            // host registers named live-state readers; we just enumerate whatever is
            // there. No game-specific knowledge lives in this file.
            w.WriteStartObject("probes");
            if (mem != null)
            {
                foreach (var name in Play.Probes.NumNames)
                    if (Play.Probes.TryNum(name, mem, out var v)) w.WriteNumber(name, v);
                foreach (var name in Play.Probes.FlagNames)
                    if (Play.Probes.TryFlag(name, mem, out var b)) w.WriteBoolean(name, b);
            }
            w.WriteEndObject();

            w.WriteEndObject();
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, ms.ToArray());
    }

    static uint SafeRead(IMemory mem, uint addr)
    {
        try { return mem.ReadU32(addr); }
        catch { return 0; }
    }
}
