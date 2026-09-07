using Silk.NET.Input;
using Silk.NET.SDL;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Hardware;

namespace RecompOne.Runtime.Host;

internal static unsafe class InputManager
{
    static IKeyboard?_keyboard;
    static Sdl?_sdl;
    static GameController* _controller;

    const int AxisThreshold = 8000;
    const int StickThreshold = 16000;
    const int LeftTrigger = 100;
    const int RightTrigger = 101;
    const int LeftStickLeft = 102;
    const int LeftStickRight = 103;
    const int LeftStickUp = 104;
    const int LeftStickDown = 105;
    const int RightStickLeft = 106;
    const int RightStickRight = 107;
    const int RightStickUp = 108;
    const int RightStickDown = 109;
    static bool _topBarToggle;
    static bool _fullscreenToggle;

    
    public static bool ConsumeTopBarToggle() { var v = _topBarToggle; _topBarToggle = false; return v; }
    public static bool ConsumeFullscreenToggle(){ var v = _fullscreenToggle; _fullscreenToggle = false; return v; }

    public static void Initialize(IInputContext input)
    {
        if (input.Keyboards.Count > 0)
        {
            _keyboard = input.Keyboards[0];
            _keyboard.KeyDown += OnKeyDown;
        }
        
        
        try
        {
            _sdl = Sdl.GetApi();
            _sdl.SetHint("SDL_JOYSTICK_RAWINPUT", "0");
            // SDL is initialised WITHOUT its video subsystem here (the window is GLFW), so it
            // owns no windows and its focus-based event suppression should not apply. Set the
            // hint anyway: it costs nothing, and it keeps the pad alive if this ever gains an
            // SDL window, which is the shape of bug that is miserable to diagnose later.
            _sdl.SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            _sdl.InitSubSystem(Sdl.InitGamecontroller);
            TryOpenController();
        }
        catch { _sdl = null; }
    }

    public static bool IsConnected => _controller != null;

    public static bool IsKeyDown(Key k) => _keyboard?.IsKeyPressed(k) ?? false;

    public static void Poll()
    {
        PollGamepadEvents();
        // While a TAS is replaying, Tas.Tick drives Controller.State; don't let live
        // keyboard/gamepad input clobber it (the replay hands control back when it ends).
        if (Tas.Playing) return;
        PollKeyboard();
        PollGamepad();
        if (Runtime.FastStart) ApplyFastStart();
    }

    // Debug bypass (--newgame): auto-drive the front-end into New Game loading.
    // The whole front-end (title, press-start, new-game menu, slot select, tutorial
    // menu) reads scene state 0x00000000; only the committed game-start scene reads
    // 0xA80830C8. Since sub-screens are indistinguishable by state, run a repeating
    // press/release cycle of the buttons a verified manual drive used — START (skip
    // intro/FMV, pass press-start), then UP immediately before CROSS (so a menu that
    // defaults to Tutorial moves to Start Game before confirming), plus an extra
    // CROSS for the save-slot prompt — and stop the instant the game commits.
    static int _fsCounter;
    static void ApplyFastStart()
    {
        uint state = Runtime.Mem?.ReadU32(0x8009D280u) ?? 0u;
        if (state == 0xA80830C8u) { Runtime.FastStart = false; return; }  // committed to New Game
        int f = ++_fsCounter;
        ushort s = 0xFFFF;
        void Hold(ushort bit) => s &= (ushort)~bit;
        int g = f % 200;                                 // ~3.3s cycle: press ~0.2s, release ~0.4s between
        if (g < 12) Hold(Controller.Start);              // skip intro/FMV, pass press-start
        else if (g < 60) { }                             // release
        else if (g < 72) Hold(Controller.Up);            // Tutorial -> Start Game (harmless elsewhere)
        else if (g < 108) { }                            // release
        else if (g < 120) Hold(Controller.Cross);        // confirm New Game / Start Game
        else if (g < 156) { }                            // release
        else if (g < 168) Hold(Controller.Cross);        // confirm save-slot prompt
        Controller.State = s;
    }

    public static int? GetFirstPressedPadButton()
    {
        if (_sdl == null || _controller == null) return null;
        for (int b = 0; b < (int)GameControllerButton.Max; b++)
            if (_sdl.GameControllerGetButton(_controller, (GameControllerButton)b) != 0)
                return b;
        if (_sdl.GameControllerGetAxis(_controller, GameControllerAxis.Triggerleft)  > AxisThreshold) return LeftTrigger;
        if (_sdl.GameControllerGetAxis(_controller, GameControllerAxis.Triggerright) > AxisThreshold) return RightTrigger;
        for (int b = LeftStickLeft; b <= RightStickDown; b++)
        {
            var (axis, positive) = AxisBinding(b);
            short v = _sdl.GameControllerGetAxis(_controller, axis);
            if (positive ? v > StickThreshold : v < -StickThreshold) return b;
        }
        return null;
    }

    static bool IsStickBinding(int b) => b is >= LeftStickLeft and <= RightStickDown;

    static (GameControllerAxis Axis, bool Positive) AxisBinding(int b) => b switch
    {
        LeftStickLeft   => (GameControllerAxis.Leftx,  false),
        LeftStickRight  => (GameControllerAxis.Leftx,  true),
        LeftStickUp     => (GameControllerAxis.Lefty,  false),
        LeftStickDown   => (GameControllerAxis.Lefty,  true),
        RightStickLeft  => (GameControllerAxis.Rightx, false),
        RightStickRight => (GameControllerAxis.Rightx, true),
        RightStickUp    => (GameControllerAxis.Righty, false),
        _               => (GameControllerAxis.Righty, true),
    };

    public static void Shutdown()
    {
        if (_controller != null) { _sdl?.GameControllerClose(_controller); _controller = null; _instanceId = -1; }
        _sdl?.QuitSubSystem(Sdl.InitGamecontroller);
        _sdl?.Dispose();
        _sdl = null;
    }

    // Instance id of the pad we currently hold open (-1 = none). SDL reuses device INDEXES
    // as devices come and go, but an instance id is unique for the lifetime of a connection,
    // so it is the only safe way to tell "our pad was unplugged" from "some other device was".
    static int _instanceId = -1;
    static int _rescanCountdown;
    const int RescanFrames = 120; // ~2 s at 60 fps

    static void PollGamepadEvents()
    {
        if (_sdl == null) return;
        Event ev;
        while (_sdl.PollEvent(&ev) != 0)
        {
            if (ev.Type == (uint)EventType.Controllerdeviceadded)
            {
                // CONTROLLERDEVICEADDED carries a device INDEX, not an instance id.
                LastPadEvent = $"device added (index {ev.Cdevice.Which})";
                // Pick it up immediately, so unplugging and replugging a pad just works
                // instead of needing the Rescan button.
                if (_controller == null) TryOpenController();
            }
            if (ev.Type == (uint)EventType.Controllerdeviceremoved)
            {
                // CONTROLLERDEVICEREMOVED carries the INSTANCE id of the device that left.
                //
                // This used to close _controller unconditionally, without ever comparing that
                // id against the pad actually open -- so ANY other device disappearing (a
                // second pad, a wireless dongle re-enumerating, an XInput slot flapping, which
                // the SDL_JOYSTICK_RAWINPUT=0 hint below makes more likely) closed the working
                // controller. Nothing then reopened it unless some device happened to be added
                // later, leaving the pad dead for the rest of the session while the keyboard
                // kept working -- an intermittent "the gamepad just stops" that looked like a
                // focus bug because it depended on unrelated devices coming and going.
                bool ours = _controller != null && ev.Cdevice.Which == _instanceId;
                LastPadEvent = $"device removed (instance {ev.Cdevice.Which}){(ours ? " - was ours" : " - not ours, ignored")}";
                if (ours)
                {
                    _sdl.GameControllerClose(_controller);
                    _controller = null;
                    _instanceId = -1;
                    TryOpenController(); // another pad may still be attached
                }
            }
        }

        // Self-healing rescan. Whatever the cause of a dropout -- a removal we never saw an
        // add for, a pad claimed by another process at start-up, a device that came back
        // without an event -- retry periodically rather than staying dead until someone
        // opens the debug panel and presses Rescan.
        if (_controller == null && --_rescanCountdown <= 0)
        {
            _rescanCountdown = RescanFrames;
            _sdl.JoystickUpdate();
            TryOpenController();
        }
    }

    static void PollKeyboard()
    {
        var kb = _keyboard;
        var cfg = ConfigManager.Game.Keys;
        if (kb == null) return;

        ushort s = 0xFFFF;
        void B(string keyName, ushort bit)
        {
            if (Enum.TryParse<Key>(keyName, out var k) && kb.IsKeyPressed(k))
                s &= (ushort)~bit;
        }

        B(cfg.Cross,    Controller.Cross);
        B(cfg.Circle,   Controller.Circle);
        B(cfg.Square,   Controller.Square);
        B(cfg.Triangle, Controller.Triangle);
        B(cfg.L1,       Controller.L1);
        B(cfg.R1,       Controller.R1);
        B(cfg.L2,       Controller.L2);
        B(cfg.R2,       Controller.R2);
        B(cfg.L3,       Controller.L3);
        B(cfg.R3,       Controller.R3);
        B(cfg.Start,    Controller.Start);
        B(cfg.Select,   Controller.Select);
        B(cfg.Up,       Controller.Up);
        B(cfg.Down,     Controller.Down);
        B(cfg.Left,     Controller.Left);
        B(cfg.Right,    Controller.Right);

        // Make Enter (Start) also act as menu-confirm (Cross): PE1 menus confirm
        // on Cross, so plain Enter otherwise can't pick New Game/Tutorial.
        if (Enum.TryParse<Key>(cfg.Start, out var startKey) && kb.IsKeyPressed(startKey))
            s &= unchecked((ushort)~Controller.Cross);

        Controller.State = s;
    }

    static void PollGamepad()
    {
        if (_sdl == null || _controller == null) return;
        var pad = ConfigManager.Game.Pad;
        ushort s = Controller.State;

        void B(int binding, ushort bit)
        {
            if (binding == LeftTrigger)
            {
                if (_sdl.GameControllerGetAxis(_controller, GameControllerAxis.Triggerleft) > AxisThreshold)
                    s &= (ushort)~bit;
            }
            else if (binding == RightTrigger)
            {
                if (_sdl.GameControllerGetAxis(_controller, GameControllerAxis.Triggerright) > AxisThreshold)
                    s &= (ushort)~bit;
            }
            else if (IsStickBinding(binding))
            {
                var (axis, positive) = AxisBinding(binding);
                short v = _sdl.GameControllerGetAxis(_controller, axis);
                if (positive ? v > StickThreshold : v < -StickThreshold)
                    s &= (ushort)~bit;
            }
            else if (_sdl.GameControllerGetButton(_controller, (GameControllerButton)binding) != 0)
                s &= (ushort)~bit;
        }

        B(pad.Cross,    Controller.Cross);
        B(pad.Circle,   Controller.Circle);
        B(pad.Square,   Controller.Square);
        B(pad.Triangle, Controller.Triangle);
        B(pad.L1,       Controller.L1);
        B(pad.R1,       Controller.R1);
        B(pad.L2,       Controller.L2);
        B(pad.R2,       Controller.R2);
        B(pad.L3,       Controller.L3);
        B(pad.R3,       Controller.R3);
        B(pad.Start,    Controller.Start);
        B(pad.Select,   Controller.Select);
        B(pad.Up,       Controller.Up);
        B(pad.Down,     Controller.Down);
        B(pad.Left,     Controller.Left);
        B(pad.Right,    Controller.Right);

        Controller.State = s;
        Controller.LeftX = AxisToByte(_sdl.GameControllerGetAxis(_controller, GameControllerAxis.Leftx));
        Controller.LeftY = AxisToByte(_sdl.GameControllerGetAxis(_controller, GameControllerAxis.Lefty));
        Controller.RightX = AxisToByte(_sdl.GameControllerGetAxis(_controller, GameControllerAxis.Rightx));
        Controller.RightY = AxisToByte(_sdl.GameControllerGetAxis(_controller, GameControllerAxis.Righty));
    }

    static byte AxisToByte(short axis)
    {
        float f = Math.Clamp(axis * 1.3f / 32768.0f, -1.0f, 1.0f);
        return (byte)Math.Clamp((int)MathF.Round((f + 1.0f) * 127.5f), 0, 255);
    }

    public static void SetRumble(byte large, byte small)
    {
        if (_sdl == null || _controller == null) return;
        ushort lo = (ushort)(large * 257);
        ushort hi = small != 0 ? (ushort)65535 : (ushort)0;
        uint duration = large == 0 && small == 0 ? 0u : 500u;
        _sdl.GameControllerRumble(_controller, lo, hi, duration);
    }

    static void OnKeyDown(IKeyboard kb, Key key, int _)
    {
        if (key == Key.F1)  _topBarToggle = true;
        if (key == Key.F11) _fullscreenToggle = true;
        // Savestates: F5 save / F7 load (applied at the next safe frame boundary by the
        // host's scene-handler hook). F6/F8 switch to a second slot.
        if (key == Key.F5) Savestate.SavePending = true;
        if (key == Key.F7) Savestate.LoadPending = true;
        if (key == Key.F6) Savestate.SlotPath = "state2.sav";
        if (key == Key.F8) Savestate.SlotPath = "state1.sav";
        // Tab toggles fast-forward. A toggle rather than a hold, because the thing worth
        // skipping is usually a 40-second FMV or a boot sequence, and holding a key for
        // that long while also playing is worse than pressing it twice.
        if (key == Key.Tab) Tas.UserTurbo = !Tas.UserTurbo;
    }

    // --- Controller diagnostics (Host/Window/Panels/Debug/ControllerPanel.cs) ---------
    // The panel needs to answer one question: is the pad missing because SDL never saw
    // it, because it has no gamecontroller mapping, or because the game ignores it?
    public static string LastPadEvent = "(none yet)";
    public static string OpenResult = "(not attempted)";
    public static bool SdlReady => _sdl != null;
    public static int DeviceCount => _sdl?.NumJoysticks() ?? 0;

    public static string ControllerName
    {
        get
        {
            if (_sdl == null) return "(SDL not initialised)";
            if (_controller == null) return "(none open)";
            var p = _sdl.GameControllerName(_controller);
            return p == null ? "(unnamed)" : System.Runtime.InteropServices.Marshal.PtrToStringAnsi((nint)p) ?? "(unnamed)";
        }
    }

    public static string[] DeviceList()
    {
        if (_sdl == null) return ["SDL not initialised"];
        int n = _sdl.NumJoysticks();
        if (n == 0) return ["no joysticks seen by SDL"];
        var list = new string[n];
        for (int i = 0; i < n; i++)
        {
            bool gc = _sdl.IsGameController(i) == SdlBool.True;
            var np = gc ? _sdl.GameControllerNameForIndex(i) : _sdl.JoystickNameForIndex(i);
            string nm = np == null ? "?" : System.Runtime.InteropServices.Marshal.PtrToStringAnsi((nint)np) ?? "?";
            list[i] = gc ? $"{i}: {nm}" : $"{i}: {nm}   [NO gamecontroller mapping - unusable]";
        }
        return list;
    }

    public static bool RawButton(int b)
        => _sdl != null && _controller != null && _sdl.GameControllerGetButton(_controller, (GameControllerButton)b) != 0;

    public static short RawAxis(int a)
        => _sdl != null && _controller != null ? _sdl.GameControllerGetAxis(_controller, (GameControllerAxis)a) : (short)0;

    // Manual re-scan: closes any open pad and re-opens the first usable one. Covers the
    // case where the pad was claimed by another process (an emulator) at start-up.
    public static void Rescan()
    {
        if (_sdl == null) { OpenResult = "SDL not initialised"; return; }
        if (_controller != null) { _sdl.GameControllerClose(_controller); _controller = null; _instanceId = -1; }
        _sdl.JoystickUpdate();
        TryOpenController();
    }

    static void TryOpenController()
    {
        if (_sdl == null) { OpenResult = "SDL not initialised"; return; }
        int n = _sdl.NumJoysticks();
        if (n == 0) { OpenResult = "no joysticks present"; return; }
        int mapped = 0;
        for (int i = 0; i < n; i++)
        {
            if (_sdl.IsGameController(i) != SdlBool.True) continue;
            mapped++;
            _controller = _sdl.GameControllerOpen(i);
            if (_controller != null)
            {
                // Record the instance id so a later CONTROLLERDEVICEREMOVED can be matched
                // against THIS pad instead of closing it on any device's removal.
                var joy = _sdl.GameControllerGetJoystick(_controller);
                _instanceId = joy != null ? _sdl.JoystickInstanceID(joy) : -1;
                OpenResult = $"opened device {i} (instance {_instanceId})";
                return;
            }
        }
        OpenResult = mapped == 0
            ? $"{n} joystick(s) present but none has a gamecontroller mapping"
            : $"{mapped} mapped device(s) present but GameControllerOpen failed";
    }
}
