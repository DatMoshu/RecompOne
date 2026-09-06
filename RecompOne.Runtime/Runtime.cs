using RecompOne.Runtime.Context;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime;

public enum RunMode { Retail, Devkit }

public static class Runtime
{
    public static CpuContext? Cpu { get; private set; }
    public static IMemory? Mem { get; private set; }
    public static Gpu? Gpu;
    public static Spu? Spu;
    public static Cdrom.CdController? Cd;

    public static (int W, int H)? WindowSize; // host override for the initial window size (--window=WxH)
    public static bool FastStart; // debug: auto-drive the front-end into New Game
    public static bool SoundTimerOn = true; // pump PE1's RCnt2 sound-sequencer IRQ each frame
    public static Action<IMemory>? FrameHook; // invoked once per presented frame (host-registered)

    public static RunMode Mode { get; private set; } = RunMode.Retail;
    public static void SetMode(RunMode mode) => Mode = mode; //devkit vs retail, devkits reads from sim and has more ram
    public static string CdPath => Config.ConfigManager.Game.CdPath;
    
    public static Config.ViewConfig View => Config.ConfigManager.View;
    public static void SaveView() => Config.ConfigManager.SaveView(Host.Window.PanelManager.Panels);
    
    public static Hardware.MemoryCard CardA = new("carda.sav") { Enabled = true };
    public static Hardware.MemoryCard CardB = new("cardb.sav") { Enabled = true };
    public static readonly Memory.RamLogger RamLog = new();
    public static readonly Dispatch.OverlayEventLog OverlayLog = new();

    public static void Initialize(string title)
    {
        HostWindow.Initialize(title);
        Audio.Initialize();
        Audio.SetMasterVolume(Config.ConfigManager.Game.Muted ? 0f : Config.ConfigManager.Game.MasterVolume);
    }

    public static void WaitForValidDisc() => HostWindow.WaitForValidDisc();

    public static void SetContext(CpuContext c, IMemory m)
    {
        Cpu = c;
        Mem = m;
    }

    /// <summary>Presented frames per second, averaged over the last second.</summary>
    public static double Fps { get; private set; }
    /// <summary>Emulated vblanks per second the game actually advanced through.</summary>
    public static long FrameCount { get; private set; }
    static readonly System.Diagnostics.Stopwatch _fpsClock = System.Diagnostics.Stopwatch.StartNew();
    static long _fpsFrames;
    static double _fpsLastMs;

    /// <param name="paced">The caller already waited for this frame's slot (LibEtc's VSync
    /// poll gate), so skip the frame clock — sleeping in both places halves the frame rate.</param>
    public static void PresentFrame(bool paced = false)
    {
        FrameCount++;
        _fpsFrames++;
        double nowMs = _fpsClock.Elapsed.TotalMilliseconds;
        if (nowMs - _fpsLastMs >= 500.0)
        {
            Fps = _fpsFrames * 1000.0 / (nowMs - _fpsLastMs);
            _fpsFrames = 0;
            _fpsLastMs = nowMs;
        }

        bool turbo = Host.Tas.TurboActive; // read BEFORE Tas.Tick (which may end replay this frame)
        if (turbo) HostWindow.PumpLight();  // keep the window alive; no render, no vsync wait
        else HostWindow.Present(Gpu);
        if (!turbo) Audio.Attach(Spu);      // don't drive audio at fast-forward speed
        Host.Tas.Tick();          // apply replay input / capture record input for this frame
        Host.DriveRecorder.Tick();
        if (!turbo && !paced) FrameClock.Throttle();  // uncapped while fast-forwarding or already paced
        Sdk.LibCd.Tick();
        if (Mem != null) { Bios.BiosB.RefreshPad(Mem); Sdk.LibPad.Refresh(Mem); } //is this correct?
        Hle.GlBackend.ColorHistFrame();
        if (Mem != null) FrameHook?.Invoke(Mem); // per-frame game hook (registered by the host, e.g. PE1 patches)
        if (SoundTimerOn && Cpu != null && Mem != null)
            Bios.BiosB.PumpRootCounter2(Cpu, Mem, 4); // 240Hz sequencer tick (4 per 60fps frame)
        // Hardware-IRQ games dispatch per-bit from I_STAT inside their single interrupt
        // entry; without bit 0 their own VBLANK handler chain never runs (BFM's advances
        // its software vblank counter and pumps the boot/CD state machine). Gated: no-op for PE1.
        Mem?.RaiseIrq(0);
        DispatchIrq(0); //using this to dispatch irqs too if necessary, probably not needed after the rest of stuff is reimplemented
        // MMIO ReadN streaming (BFM): keep feeding sector-ready INT1s and let the
        // game's CDROM-IRQ handler consume each one. Gated to HwIrqModel so PE1
        // (libds path, never sets _reading via MMIO) is unaffected.
        if (Memory.PSMemory.HwIrqModel && Cd != null)
            for (int i = 0, n = Cd.SectorsThisFrame(); i < n && Cd.AdvanceStreaming(); i++)
                DispatchIrq(2); // CDROM slot; AdvanceStreaming raised INT1
    }

    public static void DispatchIrq(int irq)
    {
        if (Cpu != null && Mem != null)
            Interrupts.Deliver(irq, Cpu, Mem);
    }

    public static void Shutdown()
    {
        Audio.Shutdown();
        HostWindow.Shutdown();
    }
}
