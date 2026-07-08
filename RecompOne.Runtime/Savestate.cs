using System.IO;
using RecompOne.Runtime.Bios;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Sdk;

namespace RecompOne.Runtime;

// Emulator-style whole-machine snapshot. The static recompiler runs the game as
// native C# calls, so there is no CPU loop to freeze — the host must call
// SaveToFile/LoadFromFile at a safe frame boundary (e.g. inside FrameHook, after a
// frame is presented and before the next VSync re-enters game code). This class only
// serializes/deserializes; it does not decide when a snapshot is taken.
public static class Savestate
{
    const uint Magic = 0x52315353; // "R1SS"
    const int Version = 1;

    // Host sets these on a keypress; the host is responsible for calling
    // SaveToFile/LoadFromFile at the safe point (these are just a mailbox).
    public static bool SavePending, LoadPending;
    public static string SlotPath = "state1.sav";

    public static byte[] Capture(CpuContext c)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Magic);
        w.Write(Version);

        c.SaveState(w);
        ((PSMemory)Runtime.Mem!).SaveState(w);
        Runtime.Gpu!.SaveState(w);
        Runtime.Spu!.SaveState(w);

        var overlays = Dispatcher.CaptureActive();
        w.Write(overlays.Count);
        foreach (var name in overlays) w.Write(name);

        LibDs.SaveState(w);
        LibCd.SaveState(w);
        BiosB.SaveState(w);
        LibEtc.SaveState(w);

        w.Flush();
        return ms.ToArray();
    }

    public static void Restore(byte[] data, CpuContext c)
    {
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("savestate: bad magic");
        int ver = r.ReadInt32();
        if (ver != Version) throw new InvalidDataException($"savestate: version mismatch (got {ver}, expected {Version})");

        c.LoadState(r);
        ((PSMemory)Runtime.Mem!).LoadState(r);
        Runtime.Gpu!.LoadState(r);
        Runtime.Spu!.LoadState(r);

        int n = r.ReadInt32();
        var overlays = new List<string>(n);
        for (int i = 0; i < n; i++) overlays.Add(r.ReadString());
        Dispatcher.RestoreActive(overlays);

        LibDs.LoadState(r);
        LibCd.LoadState(r);
        BiosB.LoadState(r);
        LibEtc.LoadState(r);
    }

    public static void SaveToFile(string path, CpuContext c) => File.WriteAllBytes(path, Capture(c));

    public static void LoadFromFile(string path, CpuContext c) => Restore(File.ReadAllBytes(path), c);
}
