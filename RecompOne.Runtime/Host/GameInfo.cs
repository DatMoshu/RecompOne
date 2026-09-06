using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

/// <summary>Generic display registry for the "Game Info" panel: named sections, each
/// either a table of label/value rows or a block of free-form text, polled once per
/// draw. Zero game-specific knowledge lives here — a port that registers nothing gets
/// an empty panel. Callbacks should read live state via <c>RecompOne.Runtime.Play.Probes</c>
/// (the same registry that feeds the capture-data sidecar and the automation driver) or
/// directly via <see cref="IMemory"/>; this class never hardcodes an address itself.</summary>
public static class GameInfo
{
    public sealed class Section
    {
        public required string Title;
        public Func<IMemory, IEnumerable<(string Label, string Value)>>? Rows;
        public Func<IMemory, string>? Text;
    }

    static readonly List<Section> _sections = [];
    public static IReadOnlyList<Section> Sections => _sections;

    public static void RegisterRows(string title, Func<IMemory, IEnumerable<(string, string)>> rows) =>
        _sections.Add(new Section { Title = title, Rows = rows });

    public static void RegisterText(string title, Func<IMemory, string> text) =>
        _sections.Add(new Section { Title = title, Text = text });

    // For tests / re-registering across a hot-reloaded game host in the same process.
    public static void Clear() => _sections.Clear();
}
