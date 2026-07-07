using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Cdrom;

public static class DiscManager
{
    public static int Current { get; private set; }

    public static IReadOnlyList<string> Discs =>
        ConfigManager.Game.Discs.Count > 0 ? ConfigManager.Game.Discs : [ConfigManager.Game.CdPath];

    public static void InitFromBootDisc(string cuePath)
    {
        Current = 0;
        if (string.IsNullOrWhiteSpace(cuePath)) return;
        var discs = Discs;
        for (int i = 0; i < discs.Count; i++)
            if (!string.IsNullOrWhiteSpace(discs[i]) && string.Equals(Path.GetFullPath(discs[i]), Path.GetFullPath(cuePath), StringComparison.OrdinalIgnoreCase))
            {
                Current = i;
                return;
            }
    }

    public static bool Swap(int index)
    {
        var discs = Discs;
        if (index < 0 || index >= discs.Count || index == Current) return false;
        if (!File.Exists(discs[index])) { Log.Cd($"disc {index + 1} not found: {discs[index]}"); return false; }
        if (Runtime.Cd == null) return false;
        Runtime.Cd.SwapDisc(discs[index]);
        Current = index;
        return true;
    }
}
