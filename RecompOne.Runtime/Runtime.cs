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
        // Before anything else prints, so the Log panel shows the boot sequence too --
        // overlay loads and CD init are exactly the lines a black-screen report needs.
        Host.LogTail.Install();
        // --speed / --turbo-speed / --max-render-hz / --fast-audio, read from the process
        // command line so every port gets them without wiring anything in its Program.cs.
        FrameClock.ApplyCommandLine();
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
    static double _fpsLogNextMs;

    /// <param name="paced">The caller already waited for this frame's slot (LibEtc's VSync
    /// poll gate), so skip the frame clock — sleeping in both places halves the frame rate.</param>
    public static void PresentFrame(bool paced = false)
    {
        // Presentation touches the GL context, which is thread-affine. Under the BIOS
        // thread model (Bios.BiosB.EnableThreads) emulated code can run on a non-main
        // thread, so verify rather than assume that frames are still presented from
        // main -- an assumption like this holds until the port that breaks it, and the
        // symptom would be inexplicable GL corruption instead of an obvious error.
        // No-op unless the thread model is on.
        Bios.BiosB.AssertMainThread(nameof(PresentFrame));
        FrameCount++;
        _fpsFrames++;
        double nowMs = _fpsClock.Elapsed.TotalMilliseconds;
        if (nowMs - _fpsLastMs >= 500.0)
        {
            Fps = _fpsFrames * 1000.0 / (nowMs - _fpsLastMs);
            _fpsFrames = 0;
            _fpsLastMs = nowMs;
            if (FrameClock.FpsLogSeconds > 0 && nowMs >= _fpsLogNextMs)
            {
                _fpsLogNextMs = nowMs + FrameClock.FpsLogSeconds * 1000.0;
                double sp = FrameClock.Effective;
                Console.WriteLine($"[fps] {Fps:0.0} emulated fps ({Fps / 60.0:0.00}x real time), frame={FrameCount}, " +
                                  $"speed={(sp <= 0 ? "uncapped" : sp.ToString("0.##") + "x")}" +
                                  $"{(Host.Tas.TurboActive ? " TURBO" : "")}" +
                                  $" | stream frames={Sdk.LibCdStream.FramesFed} sectors={Sdk.LibCdStream.SectorsFed} ringFullWaits={Sdk.LibCdStream.FullWaits}");
            }
        }

        // Two different things are called "turbo" and they need opposite treatment.
        // HEADLESS is an unattended driver blasting through frames nobody will look at, so
        // rendering and input are pure cost. VISIBLE is a human holding fast-forward, where
        // dropping the picture and freezing the pad is the whole complaint: a skip that goes
        // black and deaf to the controller reads as a hang, not as a fast-forward. So the
        // visible path keeps presenting -- just not every frame; the redraw is capped at
        // FrameClock.MaxRenderHz while the emulation runs on ahead.
        bool headless = Host.Tas.HeadlessTurbo; // read BEFORE Tas.Tick (which may end replay this frame)
        if (headless) HostWindow.PumpLight();  // keep the window alive; no render, no vsync wait
        else if (FrameClock.ShouldRender()) HostWindow.Present(Gpu);
        // Requesting a capture is not the same as taking one: RequestWindowCapture just marks
        // the next DRAWN frame, so this is correct on a frame whose redraw was skipped, and
        // gating it on `drew` instead silently loses most captures during a fast-forward.
        if (!headless) Host.CaptureManager.Tick(FrameCount); // --snap-window / --dump (engine-level, off by default)
        if (!headless) Audio.Attach(Spu);
        Audio.Suppressed = headless || (Host.Tas.TurboActive && Audio.MuteWhileFast);
        Host.Tas.Tick();          // apply replay input / capture record input for this frame
        Host.AutoState.Tick(FrameCount); // raise a periodic savestate request (the game host decides if it's safe)
        Host.DriveRecorder.Tick();
        // Speed lives in the frame clock, so a visible fast-forward still comes through here
        // and is paced by FrameClock.TurboSpeed rather than running unbounded.
        if (!headless && !paced) FrameClock.Throttle();
        Sdk.LibCd.Tick();
        if (Mem != null) { Bios.BiosB.RefreshPad(Mem); Sdk.LibPad.Refresh(Mem); } //is this correct?
        Hle.GlBackend.ColorHistFrame();
        // Play.Tick (RecompOne.Runtime.Play) MUST run before FrameHook, and both MUST run
        // before this function returns: Controller.State written here is what the
        // recompiled game reads back the next time it samples the pad, which can happen
        // any time between now and the next PresentFrame call. FrameHook (e.g. PE1's
        // Patches.PerFrame) may also want to see/override Controller.State for its own
        // debug drivers (AutoMap/Gizmo), so Play goes first — do not reorder this pump
        // without re-checking that guarantee; a scripted run's WaitForChange movement
        // detection silently returns wrong answers if input lags a frame.
        if (Mem != null) Play.Play.Tick(Mem);
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
                if (Cd.LastStepRaisedIrq) DispatchIrq(2); // CDROM slot; an XA audio sector raises none
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
