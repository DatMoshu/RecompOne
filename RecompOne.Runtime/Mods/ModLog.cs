namespace RecompOne.Runtime.Mods;

/// <summary>Say a thing once, ever.
///
/// Mods and the subsystems they drive live inside per-frame code, so the only logging
/// discipline that stays readable is "report the transition, never the state". Every mod
/// file had grown its own copy of that idea under a different name (<c>Note</c>,
/// <c>NoteOnce</c>, an inline HashSet), each with its own de-duplication set. One
/// implementation, one shared set, one prefix format.
///
/// <c>tag</c> is the bracketed prefix the message appears under: mods use their registry
/// id via <see cref="ModRegistry.Note"/>, engine-side subsystems use their own short tag
/// (<c>art</c>, <c>tex</c>) so a grep for one subsystem's output still works.</summary>
public static class ModLog
{
    static readonly HashSet<string> _seen = [];

    public static void Once(string tag, string message)
    {
        if (_seen.Add($"{tag} {message}")) Console.WriteLine($"[{tag}] {message}");
    }
}
