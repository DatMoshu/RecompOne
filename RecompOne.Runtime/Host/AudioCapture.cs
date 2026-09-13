using System;
using System.IO;

namespace RecompOne.Runtime.Host;

/// <summary>
/// Deterministic audio capture to a WAV file, for comparing a port's sound against a reference
/// (an emulator, or an earlier build of this one).
///
/// IT DOES NOT TAP THE LIVE MIXER, AND THAT IS THE WHOLE POINT. Host.Audio pulls the SPU from its
/// own thread at wall-clock rate, so a recording taken there is paced by how fast this machine
/// happened to run: it drifts under load, stretches under a fast-forward, and its length depends
/// on the host rather than the game. Two such recordings cannot be sample-aligned, which makes a
/// difference measurement meaningless — the same class of mistake as timing a savestate hash
/// against a wall-clock-paced subsystem.
///
/// Instead this pulls a FIXED number of samples per presented frame, from the frame pump. One
/// presented frame is one PS1 vblank, so the capture is locked to EMULATED time: the same input
/// script produces a byte-identical-length recording every run, at any host speed, and lines up
/// against an emulator's dump, which is emulated-time-locked for the same reason.
///
/// Because it is the SPU's only consumer while armed, the live mixer is detached for the duration
/// (Mix advances voice and ADPCM decode state, so two consumers would double-advance it and the
/// capture would be missing every other buffer). A capture run is therefore silent on the
/// speakers: it is a measurement, not a playback.
///
/// 44100 Hz stereo 16-bit. 44100 / 60 is not an integer, so the per-frame sample count carries the
/// remainder forward rather than rounding — otherwise the capture runs 0.03% fast and a minute-long
/// recording ends ~18 ms adrift of the reference, which looks exactly like a real timing bug.
/// </summary>
public static class AudioCapture
{
    public const int SampleRate = 44100;

    public static bool Active => _writer != null;

    /// <summary>Frames captured so far (one per presented frame).</summary>
    public static long Frames { get; private set; }

    /// <summary>Peak absolute sample seen, so a run can report "this was not silence" without
    /// anyone opening the file.</summary>
    public static int Peak { get; private set; }

    static BinaryWriter? _writer;
    static string? _path;
    static Spu? _spu;
    static long _samplesWritten;
    static long _accum;        // fixed-point remainder of SampleRate/60 per frame
    static short[] _buf = new short[4096];

    static bool _exitHooked;
    static string? _pending;
    static bool _pendingChecked;

    /// <summary>--audio-capture=&lt;file.wav&gt;, read the same way FrameClock reads its flags so any
    /// port including future ones gets it with no wiring. Arms as soon as the SPU exists; the
    /// file is finished by Stop (Runtime shutdown, or --exit-at).</summary>
    public static void ArmIfRequested(Spu? spu)
    {
        if (_writer != null || spu == null) return;
        if (!_pendingChecked)
        {
            _pendingChecked = true;
            foreach (var a in Environment.GetCommandLineArgs())
                if (a.StartsWith("--audio-capture=", StringComparison.Ordinal))
                    _pending = a["--audio-capture=".Length..];
        }
        if (_pending == null) return;
        string path = _pending;
        _pending = null;
        Start(path, spu);
    }

    /// <summary>Arm capture. Call before the game starts; Tick does the rest.</summary>
    public static void Start(string path, Spu spu)
    {
        Stop();
        _path = path;
        _spu = spu;
        Frames = 0; Peak = 0; _samplesWritten = 0; _accum = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        _writer = new BinaryWriter(File.Create(path));
        WriteHeaderPlaceholder(_writer);
        // A WAV whose RIFF sizes were never patched is unreadable, and most scripted runs end via
        // Environment.Exit (--exit-at) rather than by returning, so registering here is what makes
        // the file usable at all. Idempotent: Stop() returns immediately if already closed.
        if (!_exitHooked)
        {
            _exitHooked = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
        }
        // The live mixer must not also pull the SPU; see the class note.
        Audio.Attach(null);
        Console.WriteLine($"[audio] capturing to {path} (deterministic, {SampleRate} Hz stereo, " +
                          "frame-locked; speakers are silent while capturing)");
    }

    /// <summary>Call once per presented frame, from the frame pump.</summary>
    public static void Tick()
    {
        if (_writer == null || _spu == null) return;
        // 44100 samples per 60 frames = 735 exactly at 60 Hz, but keep the remainder machinery so
        // a port running at another refresh does not accumulate error.
        _accum += SampleRate;
        int want = (int)(_accum / 60);
        _accum -= (long)want * 60;
        if (want <= 0) return;
        if (_buf.Length < want * 2) _buf = new short[want * 2];
        _spu.Mix(_buf, want);
        for (int i = 0; i < want * 2; i++)
        {
            short s = _buf[i];
            int a = s < 0 ? -s : s;
            if (a > Peak) Peak = a;
            _writer.Write(s);
        }
        _samplesWritten += want;
        Frames++;
    }

    /// <summary>Finish the file (patches the RIFF sizes) and report what was captured.</summary>
    public static void Stop()
    {
        if (_writer == null) return;
        var w = _writer;
        _writer = null;
        try
        {
            long dataBytes = _samplesWritten * 4;         // stereo, 16-bit
            w.Seek(4, SeekOrigin.Begin); w.Write((uint)(36 + dataBytes));
            w.Seek(40, SeekOrigin.Begin); w.Write((uint)dataBytes);
            w.Flush();
            Console.WriteLine($"[audio] captured {_samplesWritten} samples " +
                              $"({_samplesWritten / (double)SampleRate:0.00}s, {Frames} frames) -> {_path}  " +
                              $"peak={Peak}{(Peak == 0 ? "  *** DIGITAL SILENCE ***" : "")}");
        }
        catch (Exception ex) { Console.WriteLine($"[audio] capture close failed: {ex.Message}"); }
        finally { w.Dispose(); }
    }

    static void WriteHeaderPlaceholder(BinaryWriter w)
    {
        w.Write("RIFF"u8);  w.Write(0u);              // size patched in Stop
        w.Write("WAVE"u8);
        w.Write("fmt "u8);  w.Write(16u);
        w.Write((ushort)1); w.Write((ushort)2);        // PCM, stereo
        w.Write((uint)SampleRate);
        w.Write((uint)(SampleRate * 4));               // byte rate
        w.Write((ushort)4); w.Write((ushort)16);       // block align, bits
        w.Write("data"u8); w.Write(0u);                // size patched in Stop
    }
}
