using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Host.Window;

namespace RecompOne.Runtime.Host;

internal static class HostWindow
{
    static IWindow? _window;
    static GL? _gl;
    static ImGuiController? _imgui;
    static bool _headless;
    static Gpu? _gpu;

    static uint _displayTex;
    static uint _vramTex;
    static uint _ramTex;
    static Hle.GlBackend? _glBackend;

    static byte[] _rgbDisplay = [];
    static byte[] _rgbVram = [];
    static byte[] _ramFront = new byte[Memory.RamLogger.Width * Memory.RamLogger.Height * 4];
    static byte[] _ramBack = new byte[Memory.RamLogger.Width * Memory.RamLogger.Height * 4];
    static Task? _ramTask;
    static volatile bool _ramReady;
    static int _ramFrame;

    static bool _closed;
    static DiscPickerPopup? _discPicker;

    // Only true when there is no saved layout to restore (first run, or "Reset Layout").
    // Never set when a user's interface.ini already has dock data -- see ConfigureImGui.
    static bool _layoutPending;

    public static void Initialize(string title)
    {
        ConfigManager.Load();

        try
        {
            var (defW, defH) = DefaultWindowSize();
            var options = WindowOptions.Default with
            {
                Size = Runtime.WindowSize is { } ws ? new Vector2D<int>(ws.W, ws.H)
                    : new Vector2D<int>(ConfigManager.View.GetInt("WindowWidth", defW), ConfigManager.View.GetInt("WindowHeight", defH)),
                Title = title,
                VSync = false,
                UpdatesPerSecond = 0,
                FramesPerSecond = 0,
                WindowState = ConfigManager.View.Fullscreen ? WindowState.Fullscreen : WindowState.Normal,
                API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(4, 5)),
            };
            _window = Silk.NET.Windowing.Window.Create(options);
            _window.Load += OnLoad;
            _window.Render += OnRender;
            _window.Closing += OnClosing;
            _window.Initialize();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Host] window unavailable {e.Message}");
            _headless = true;
        }
    }

    // Target a window wide enough to comfortably fit the shipped default layout
    // (left column + centered game view + right column of debug panels), but never
    // wider/taller than the primary monitor's work area (90%, floored) so it doesn't
    // overflow a laptop screen. Overridable by --window and by whatever size the
    // user last resized to (persisted via ViewConfig).
    const int PreferredWindowW = 1730, PreferredWindowH = 1015;
    static (int W, int H) DefaultWindowSize()
    {
        int mw = 1920, mh = 1080;
        try
        {
            var res = Silk.NET.Windowing.Monitor.GetMainMonitor(null)?.VideoMode.Resolution;
            if (res is { } r) (mw, mh) = (r.X, r.Y);
        }
        catch { }
        int maxW = (int)(mw * 0.9f), maxH = (int)(mh * 0.9f);
        int w = Math.Max(Math.Min(PreferredWindowW, maxW), 1024);
        int h = Math.Max(Math.Min(PreferredWindowH, maxH), 600);
        return (w, h);
    }

    public static void Present(Gpu? gpu)
    {
        _gpu = gpu;
        if (_headless || _window == null) return;
        try { _window.DoEvents(); }
        catch (Exception e) {
            Console.WriteLine(e.Message);
        }
        if (_window.IsClosing) { Runtime.Shutdown(); Environment.Exit(0); }
        UpdateTitleFps();
        InputManager.Poll();
        if (InputManager.ConsumeTopBarToggle())
        {
            ConfigManager.View.HideTopBar = !ConfigManager.View.HideTopBar;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        if (InputManager.ConsumeFullscreenToggle())
        {
            ConfigManager.View.Fullscreen = !ConfigManager.View.Fullscreen;
            SetFullscreen(ConfigManager.View.Fullscreen);
            ConfigManager.SaveView(PanelManager.Panels);
        }
        _window.DoRender();
    }

    // Turbo fast-forward pump: process OS events occasionally (so the window stays
    // responsive and a close request is honored) but skip rendering entirely. Called
    // in place of Present() while replay is fast-forwarding.
    static int _pumpCounter;
    public static void PumpLight()
    {
        if (_headless || _window == null) return;
        if ((_pumpCounter++ & 63) != 0) return; // ~once per 64 fast-forwarded frames
        try { _window.DoEvents(); } catch { }
        if (_window.IsClosing) { Runtime.Shutdown(); Environment.Exit(0); }
    }

    // Frame rate in the title bar: visible without opening a panel and cheap enough to leave on.
    static string _baseTitle = "";
    static readonly System.Diagnostics.Stopwatch _titleClock = System.Diagnostics.Stopwatch.StartNew();
    static double _titleNextMs;
    static string? _runLabel;

    // Flags that mean the run is DRIVING ITSELF. Used to fail safe: an automated run whose
    // author forgot --run-label must never claim to be the human's window.
    static readonly string[] AutomationFlags =
    [
        "--replay", "--automap", "--newgame", "--exit-at", "--padtest", "--btnsweep",
        "--autostart", "--autocross", "--savetest", "--snap", "--driveto", "--selftest",
        "--export-room", "--export-bg", "--export-accum", "--probe", "--warp",
    ];

    /// <summary>
    /// Says in the title bar whether this window belongs to the human or to an agent:
    /// "Agent Run: &lt;goal&gt;" from <c>--run-label=&lt;text&gt;</c>, or "HUMAN PLAY TEST".
    ///
    /// Read straight from the process command line rather than plumbed through each port's
    /// Program.cs, so every game -- including any added later -- gets it with no wiring and
    /// no chance of one port silently lacking it.
    ///
    /// Why it matters: several ports and several automated runs are often open at once, and
    /// a screenshot of one is otherwise indistinguishable from a screenshot of another. This
    /// session has already wasted time on exactly that -- a screenshot of an automated replay
    /// was read as the user's own session, and a report of "no gamepad" was really two
    /// instances fighting over one pad.
    ///
    /// FAILS SAFE: with no label but any self-driving flag present, it says "Agent Run
    /// (unlabelled)". Wrongly telling the user to keep off a window is a small cost;
    /// wrongly inviting them into an automated run corrupts the run and confuses both of us.
    /// </summary>
    static string RunLabel()
    {
        if (_runLabel != null) return _runLabel;
        _runLabel = "HUMAN PLAY TEST";
        try
        {
            var args = Environment.GetCommandLineArgs();
            // Explicit human launch wins over the heuristic below: the admin launcher's
            // Snaps button passes --snap-window, which the flag scan would otherwise read
            // as automation and mislabel a window the user opened themselves.
            foreach (var a in args)
                if (a == "--human") return _runLabel;
            foreach (var a in args)
                if (a.StartsWith("--run-label=", StringComparison.Ordinal))
                {
                    var text = a["--run-label=".Length..].Trim().Trim('"');
                    if (text.Length > 0) { _runLabel = "Agent Run: " + text; return _runLabel; }
                }
            foreach (var a in args)
                foreach (var f in AutomationFlags)
                    if (a.StartsWith(f, StringComparison.Ordinal))
                    { _runLabel = "Agent Run (unlabelled)"; return _runLabel; }
        }
        catch { /* title decoration must never be the thing that stops a port booting */ }
        return _runLabel;
    }

    static void UpdateTitleFps()
    {
        if (_window == null) return;
        double now = _titleClock.Elapsed.TotalMilliseconds;
        if (now < _titleNextMs) return;
        _titleNextMs = now + 500.0;
        if (_baseTitle.Length == 0) _baseTitle = _window.Title;
        // Turbo goes in the title so it is visible in a screenshot -- a fast-forwarding
        // run otherwise just looks like a port behaving strangely.
        var turbo = Tas.TurboActive ? "  -  TURBO" : "";
        _window.Title = $"{_baseTitle}  -  {RunLabel()}  -  {Runtime.Fps:F1} fps{turbo}";
    }

    public static void Shutdown()
    {
        if (!_headless && _window != null && !_window.IsClosing)
            _window.Close();
        InputManager.Shutdown();
    }

    public static void SetFullscreen(bool on)
    {
        if (_window == null) return;
        _window.WindowState = on ? WindowState.Fullscreen : WindowState.Normal;
    }

    public static bool IsKeyDown(Key k) => InputManager.IsKeyDown(k);

    public static void RequestDiscPath() => _discPicker?.Show();

    public static void WaitForValidDisc() // wait for disc path to be valid before running it!!
    {
        if (_headless || _window == null) return;
        while (true)
        {
            var path = ConfigManager.Game.CdPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return;

            try { _window.DoEvents(); } catch { }
            if (_window.IsClosing) { Runtime.Shutdown(); Environment.Exit(0); }
            InputManager.Poll();
            _window.DoRender();
        }
    }

    static void OnLoad()
    {
        var input = _window!.CreateInput();
        InputManager.Initialize(input);

        _gl = GL.GetApi(_window);
        _gl.ClearColor(0.08f, 0.08f, 0.08f, 1f);

        var fb = _window!.FramebufferSize;
        _gl.Viewport(0, 0, (uint)fb.X, (uint)fb.Y);
        _window.FramebufferResize += size => _gl?.Viewport(0, 0, (uint)size.X, (uint)size.Y);
        _displayTex = CreateTexture(_gl);
        _vramTex= CreateTexture(_gl);
        _ramTex = CreateTexture(_gl);

        _glBackend = new Hle.GlBackend(_gl);
        _glBackend.InitGl();
        Hle.GpuHle.Active = _glBackend.Ready;
        Hle.GpuHle.Backend = _glBackend;

        _imgui = new ImGuiController(_gl, _window, input, null, ConfigureImGui);

        PanelManager.Register(new OutputPanel());
        PanelManager.Register(new VramViewerPanel());
        PanelManager.Register(new CpuStatePanel());
        PanelManager.Register(new RamMapPanel());
        PanelManager.Register(new RamHexPanel());
        PanelManager.Register(new DmaViewerPanel());
        PanelManager.Register(new OverlayEventsPanel());
        PanelManager.Register(new ControllerPanel());
        PanelManager.Register(new GameInfoPanel());
        PanelManager.Register(new SavestatePanel());
        PanelManager.Register(new ModsPanel());
        PanelManager.Register(new LogPanel());
        PanelManager.Register(new SettingsPopup());
        PanelManager.Register(new AboutPopup());

        SettingsRegistry.Register(new InputSettingsSection());
        SettingsRegistry.Register(new DisplaySettingsSection());
        SettingsRegistry.Register(new AudioSettingsSection());

        _discPicker = new DiscPickerPopup();
        PanelManager.Register(_discPicker);

        ConfigManager.ApplyViewToPanels(PanelManager.Panels);

        var cdPath = ConfigManager.Game.CdPath;
        if (string.IsNullOrWhiteSpace(cdPath) || !File.Exists(cdPath))
            _discPicker.Show();
    }

    static void ConfigureImGui()
    {
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        io.ConfigWindowsMoveFromTitleBarOnly = true;
        unsafe { io.NativePtr->IniFilename = null; }

        // No saved layout to restore (first run) -> build the shipped default next
        // frame (see DrawDockspace / DockBuilder.SetupColumnsLayout).
        //
        // A saved layout is otherwise honoured -- EXCEPT when it predates the current
        // default. A saved interface.ini has dock nodes only for the panels that existed
        // when it was written, so adding a panel leaves the new one floating loose and
        // can leave the rest mis-sized: the user sees a broken window and has to know to
        // run Reset Layout. That happened the first time the Mods panel shipped.
        // Bumping LayoutVersion below rebuilds the default once, automatically.
        bool stale = Config.ConfigManager.View.GetInt("LayoutVersion", 0) != LayoutVersion;
        if (!Config.ConfigManager.ApplyImGuiLayout() || stale)
        {
            if (stale)
                Console.WriteLine($"[host] default layout changed (v{Config.ConfigManager.View.GetInt("LayoutVersion", 0)}"
                                  + $" -> v{LayoutVersion}); rebuilding it once.");
            _layoutPending = true;
        }
    }

    /// <summary>
    /// Bump whenever the SHIPPED DEFAULT layout changes -- a panel added to or removed from
    /// <see cref="ApplyDefaultLayout"/>, or the columns rearranged. Saved layouts from an
    /// older version are rebuilt once instead of being restored with the new panel missing.
    /// v2: added the Log panel under the viewport and a far-right column (Game Info,
    ///     Savestates, Mods).
    /// </summary>
    public const int LayoutVersion = 2;

    static void OnRender(double dt)
    {
        var gl = _gl!;
        _imgui!.Update((float)dt);
    
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        var fbDef = _window!.FramebufferSize;
        gl.Viewport(0, 0, (uint)fbDef.X, (uint)fbDef.Y);
        gl.ClearColor(0.08f, 0.08f, 0.08f, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit);

        Runtime.RamLog.Tick();

        var gpu = _gpu;
        if (gpu != null)
        {

            if (Hle.GpuHle.Active && _glBackend is { Ready: true } && gpu.DisplayEnabled)
            {
                var wf = _window!.FramebufferSize;
                var (tex, tw, th, aspect) = _glBackend.PresentDisplay(
                    gpu.DisplayX, gpu.DisplayY,
                    gpu.DisplayWidth, gpu.DisplayHeight,
                    gpu.Display24Bit,
                    outW: wf.X, outH: wf.Y);
                if (tex != 0) OutputPanel.SetTexture(tex, tw, th, aspect);
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.Viewport(0, 0, (uint)wf.X, (uint)wf.Y);
            }
            else
            {
                UploadDisplayTexture(gl, gpu);
            }

            if (PanelManager.Get<VramViewerPanel>()?.IsOpen == true)
                UploadVramTexture(gl, gpu);
        }

        if (PanelManager.Get<RamMapPanel>()?.IsOpen == true)
        {
            QueueRamConvert();
            if (_ramReady) FlushRamTexture(gl);
        }

        if (!ConfigManager.View.HideTopBar)
            MainMenuBar.Draw();

        DrawDockspace();
        PanelManager.DrawPanels();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.Viewport(0, 0, (uint)fbDef.X, (uint)fbDef.Y);
        _imgui.Render();

        if (_pendingWindowCapture != null)
        {
            var path = _pendingWindowCapture;
            _pendingWindowCapture = null;
            try
            {
                WindowCapture.Save(gl, fbDef.X, fbDef.Y, path);
                // Data sidecar written at the exact same instant as the image, so the
                // two always describe the identical rendered frame.
                CaptureData.Write(Path.ChangeExtension(path, ".json"));
            }
            catch (Exception e) { Console.Error.WriteLine($"[Host] window capture failed: {e.Message}"); }
        }
    }

    // Full-window capture (game view + docked debug panels) plus its JSON sidecar,
    // queued here and taken at the tail of this frame's render — after ImGui draws,
    // before the backbuffer swaps. One frame of latency vs. the request, same as
    // any other deferred GL readback; irrelevant for periodic diagnostic snaps.
    static string? _pendingWindowCapture;
    public static void RequestWindowCapture(string pngPath) => _pendingWindowCapture = pngPath;

    static void DrawDockspace()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);
        ImGui.SetNextWindowViewport(viewport.ID);

        const ImGuiWindowFlags hostFlags = ImGuiWindowFlags.NoDocking | 
                                           ImGuiWindowFlags.NoTitleBar |
                                           ImGuiWindowFlags.NoCollapse |
                                           ImGuiWindowFlags.NoResize |
                                           ImGuiWindowFlags.NoMove |
                                           ImGuiWindowFlags.NoBringToFrontOnFocus |
                                           ImGuiWindowFlags.NoBackground;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.Begin("##DockHost", hostFlags);
        ImGui.PopStyleVar(3);
        uint dockId = ImGui.GetID("##MainDock");
        int openCount = PanelManager.Panels.Count(p => p.IsOpen && p is not AboutPopup);
        var dockFlags = openCount <= 1 ? (ImGuiDockNodeFlags)4096 : ImGuiDockNodeFlags.None;
        ImGui.DockSpace(dockId, Vector2.Zero, dockFlags);

        if (_layoutPending)
        {
            _layoutPending = false;
            ApplyDefaultLayout(dockId, viewport.WorkSize);
        }

        ImGui.End();
    }

    // The shipped default, identical for every game and every launch: CPU State +
    // Controller in a left column; the viewport centered with the Log beneath it; the
    // machine-state panels (RAM Map / VRAM Viewer / Overlay Events / DMA Viewer) in one
    // right column; and the game-state panels (Game Info / Savestates / Mods) in a second.
    // Each panel gets its own row rather than being tabbed, so a single screenshot of a
    // run carries everything needed to diagnose it.
    static readonly string[] DefaultRight = ["RAM Map", "VRAM Viewer", "Overlay Events", "DMA Viewer"];
    static readonly string[] DefaultFarRight = ["Game Info", "Savestates", "Mods"];

    static void ApplyDefaultLayout(uint dockId, Vector2 size)
    {
        // Docking a CLOSED window positions it but never draws it, which looks exactly
        // like the layout failing to apply. So open everything the layout places.
        foreach (var p in PanelManager.Panels)
            if (p.Name is "Output" or "CPU State" or "Controller" or "Log"
                or "RAM Map" or "VRAM Viewer" or "Overlay Events" or "DMA Viewer"
                or "Game Info" or "Savestates" or "Mods")
                p.IsOpen = true;

        DockBuilder.SetupColumnsLayout(dockId, size, "Output",
            ("CPU State", "Controller"), DefaultRight, DefaultFarRight, centerBottom: "Log");
    }

    // Rebuilds the shipped default layout next frame. Only invoked explicitly (menu
    // bar "Reset Layout"), never on a normal boot with a saved interface.ini.
    public static void RequestLayoutReset() => _layoutPending = true;

    static void OnClosing()
    {
        if (_closed) return;
        _closed = true;
        if (_window != null && _window.WindowState == WindowState.Normal)
        {
            ConfigManager.View.WindowWidth = _window.Size.X;
            ConfigManager.View.WindowHeight = _window.Size.Y;
        }
        ConfigManager.SaveView(PanelManager.Panels);
        ConfigManager.SaveGame();
        PanelManager.Shutdown();
        _glBackend?.Dispose();
        _imgui?.Dispose();
        _gl?.DeleteTexture(_displayTex);
        _gl?.DeleteTexture(_vramTex);
        _gl?.DeleteTexture(_ramTex);
    }

    static uint CreateTexture(GL gl)
    {
        var tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        return tex;
    }

    static void UploadDisplayTexture(GL gl, Gpu gpu)
    {
        int w = gpu.DisplayWidth, h = gpu.DisplayHeight;
        if (!gpu.DisplayEnabled || w <= 0 || h <= 0) return;
        int needed = w * h * 3;
        if (_rgbDisplay.Length < needed) _rgbDisplay = new byte[needed];
        ConvertDisplay(gpu, w, h);
        gl.BindTexture(TextureTarget.Texture2D, _displayTex);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgb, (uint)w, (uint)h, 0,
            PixelFormat.Rgb, PixelType.UnsignedByte, _rgbDisplay.AsSpan(0, needed));
        OutputPanel.SetTexture(_displayTex, w, h);
    }

    static ushort[] _vramView = new ushort[Gpu.VramWidth * Gpu.VramHeight];
    static void UploadVramTexture(GL gl, Gpu gpu)
    {
        const int sz = Gpu.VramWidth * Gpu.VramHeight * 3;
        if (_rgbVram.Length < sz) _rgbVram = new byte[sz];
        ushort[] src;
        if (Hle.GpuHle.Active && _glBackend is { Ready: true })
        {
            _glBackend.ReadVram(0, 0, Gpu.VramWidth, Gpu.VramHeight, _vramView);
            src = _vramView;
        }
        else src = gpu.Vram;
        ConvertVramToBuffer(src, _rgbVram);
        gl.BindTexture(TextureTarget.Texture2D, _vramTex);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgb, Gpu.VramWidth, Gpu.VramHeight, 0, PixelFormat.Rgb, PixelType.UnsignedByte, _rgbVram.AsSpan(0, sz));
        VramViewerPanel.SetTexture(_vramTex, Gpu.VramWidth, Gpu.VramHeight);
    }

    static void QueueRamConvert()
    {
        if (_ramTask is { IsCompleted: false }) return;
        if (++_ramFrame < 6) return;
        _ramFrame = 0;
        var psMem = Runtime.Mem as Memory.PSMemory;
        if (psMem == null) return;
        var ram = psMem.RamBuffer;
        var back = _ramBack;
        _ramTask = Task.Run(() => Runtime.RamLog.BuildTexture(ram, back))
            .ContinueWith(_ =>
            {
                (_ramFront, _ramBack) = (_ramBack, _ramFront);
                _ramReady = true;
            }, TaskContinuationOptions.ExecuteSynchronously);
    }

    static void FlushRamTexture(GL gl)
    {
        _ramReady = false;
        gl.BindTexture(TextureTarget.Texture2D, _ramTex);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba,
            Memory.RamLogger.Width, Memory.RamLogger.Height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, _ramFront);
        RamMapPanel.SetTexture(_ramTex);
    }

    static void ConvertDisplay(Gpu gpu, int w, int h)
    {
        var vram = gpu.Vram;
        int dx = gpu.DisplayX, dy = gpu.DisplayY;
        int o = 0;
        if (gpu.Display24Bit)
        {
            for (int y = 0; y < h; y++)
            {
                int lineByte = ((dy + y) * Gpu.VramWidth + dx) * 2;
                for (int x = 0; x < w; x++)
                {
                    int bo = lineByte + x * 3;
                    _rgbDisplay[o++] = VramByte(vram, bo);
                    _rgbDisplay[o++] = VramByte(vram, bo + 1);
                    _rgbDisplay[o++] = VramByte(vram, bo + 2);
                }
            }
        }
        else
        {
            for (int y = 0; y < h; y++)
            {
                int line = ((dy + y) & (Gpu.VramHeight - 1)) * Gpu.VramWidth;
                for (int x = 0; x < w; x++)
                {
                    ushort px = vram[line + ((dx + x) & (Gpu.VramWidth - 1))];
                    _rgbDisplay[o++] = (byte)((px & 0x1F) << 3);
                    _rgbDisplay[o++] = (byte)(((px >> 5) & 0x1F) << 3);
                    _rgbDisplay[o++] = (byte)(((px >> 10) & 0x1F) << 3);
                }
            }
        }
    }

    static void ConvertVramToBuffer(ushort[] vram, byte[] output)
    {
        int o = 0;
        for (int y = 0; y < Gpu.VramHeight; y++)
        for (int x = 0; x < Gpu.VramWidth; x++)
        {
            ushort px = vram[y * Gpu.VramWidth + x];
            output[o++] = (byte)((px & 0x1F) << 3);
            output[o++] = (byte)(((px >> 5) & 0x1F) << 3);
            output[o++] = (byte)(((px >> 10) & 0x1F) << 3);
        }
    }

    static byte VramByte(ushort[] vram, int byteOffset)
    {
        int hw = (byteOffset >> 1) & (Gpu.VramWidth * Gpu.VramHeight - 1);
        ushort v = vram[hw];
        return (byte)((byteOffset & 1) == 0 ? v & 0xFF : v >> 8);
    }
}
