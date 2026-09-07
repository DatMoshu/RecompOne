using Silk.NET.Input;
using RecompOne.Runtime.Host;

namespace RecompOne.Runtime.Mods;

// Input helpers for mods.
//
// Two things a mod host cannot do for itself. First, InputManager is `internal` to the
// runtime assembly, so a game's host project (a separate assembly) has no way to ask
// "is F9 down" -- this is the passthrough. Second, edge detection: a mod bound to a
// button needs "pressed this frame", and every mod re-implementing a `_prev` field is
// the kind of duplication that eventually disagrees with itself about what a frame is.
//
// Pad buttons need no passthrough -- Hardware.Controller.State is already public, and is
// what a pad-bound mod should read (it is active-LOW: a bit CLEAR means held).
public static class ModInput
{
    static readonly Dictionary<string, bool> _prevKey = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<ushort, bool> _prevPad = new();

    /// True while the named key is held. Name is a Silk.NET `Key` enum member
    /// ("F9", "Backspace", "ShiftLeft"), matching how settings.json spells key bindings.
    /// An unparseable name is false rather than an exception: a mod reading a misspelled
    /// key should be inert and diagnosable, not fatal.
    public static bool KeyHeld(string name) =>
        Enum.TryParse<Key>(name, ignoreCase: true, out var k) && InputManager.IsKeyDown(k);

    /// True on the frame the key transitions from up to down. Call at most once per
    /// frame per name -- the previous state is stored per name, so two callers polling
    /// the same key in one frame would see the edge only once (by design: an edge is
    /// consumed, not broadcast).
    public static bool KeyPressed(string name)
    {
        bool now = KeyHeld(name);
        _prevKey.TryGetValue(name, out bool was);
        _prevKey[name] = now;
        return now && !was;
    }

    /// True while every bit in `mask` is held on the pad. Controller.State is active-low,
    /// so "held" means the bit is CLEAR -- the polarity that has bitten this project
    /// before (see PE1's L2+R2 debug-room hotkey, which reads `(State & 0x0300) == 0`).
    public static bool PadHeld(ushort mask) =>
        mask != 0 && (Hardware.Controller.State & mask) == 0;

    /// True on the frame the pad combination becomes held.
    public static bool PadPressed(ushort mask)
    {
        bool now = PadHeld(mask);
        _prevPad.TryGetValue(mask, out bool was);
        _prevPad[mask] = now;
        return now && !was;
    }
}
