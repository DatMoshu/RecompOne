using RecompOne.Runtime.Hardware;

namespace RecompOne.Runtime.Host;

// Debug (--recorddrive): logs the user's physical button presses alongside the
// PE1 main-scene state each frame, but only when either changes. Lets us see the
// exact input sequence that reaches New Game vs. what lands on Tutorial.
public static class DriveRecorder
{
    public static bool Enabled;
    static int _frame;
    static ushort _lastButtons = 0xFFFF;
    static uint _lastState = 0xFFFFFFFF;
    static readonly System.Text.StringBuilder _sb = new();

    static readonly (ushort Bit, string Name)[] _names =
    [
        (Controller.Up, "Up"), (Controller.Down, "Down"), (Controller.Left, "Left"), (Controller.Right, "Right"),
        (Controller.Cross, "Cross"), (Controller.Circle, "Circle"), (Controller.Square, "Square"), (Controller.Triangle, "Triangle"),
        (Controller.Start, "Start"), (Controller.Select, "Select"),
        (Controller.L1, "L1"), (Controller.R1, "R1"), (Controller.L2, "L2"), (Controller.R2, "R2"),
    ];

    public static void Tick()
    {
        if (!Enabled) return;
        _frame++;
        ushort btn = Controller.State;
        uint state = Runtime.Mem?.ReadU32(0x8009D280u) ?? 0u;
        if (btn == _lastButtons && state == _lastState) return;

        var pressed = "";
        foreach (var (bit, name) in _names)
            if ((btn & bit) == 0) pressed += name + " ";
        if (pressed == "") pressed = "(none)";

        _sb.AppendLine($"f={_frame,-6} state=0x{state:X8}  buttons: {pressed}");
        _lastButtons = btn;
        _lastState = state;
        // Flush frequently so a crash still leaves the trail on disk.
        System.IO.File.WriteAllText("drivelog.txt", _sb.ToString());
    }
}
