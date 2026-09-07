using System.Text.Json;

namespace RecompOne.Runtime.Host;

/// <summary>Read-only id → name/description tables loaded from a <c>game_names.json</c>
/// sitting next to the exe. Game-agnostic: this class knows nothing about what a table
/// is called or what an id means, it just answers lookups the host asks for.
///
/// <para>The file is a projection of <c>tools/gamedb/games.db</c> emitted by
/// <c>tools/gamedb/export_names.py</c>. The database stays the authority; the port never
/// opens it (no SQLite dependency in a game host, by design).</para>
///
/// <para>PROVENANCE IS CARRIED, NOT DROPPED. Every table states the confidence recorded
/// in the database — <c>datamined</c> (read out of the game and checked against it) or
/// <c>derived</c> (secondary research, unverified) — and callers can surface it. A name
/// shown without that distinction is how a guess becomes indistinguishable from a fact.</para>
///
/// <para>Absent or malformed file = every lookup returns null and <see cref="Status"/>
/// says so. Most ports have no such file and must behave exactly as before.</para></summary>
public static class GameNames
{
    public sealed record Entry(string Name, string? Category, string? Desc, string? Confidence);

    public sealed class Table
    {
        public required string Confidence;
        public string? Source;
        public required IReadOnlyDictionary<int, Entry> Entries;
    }

    static readonly Dictionary<string, Table> _tables = new(StringComparer.OrdinalIgnoreCase);
    static bool _tried;

    /// <summary>Human-readable one-liner for a panel: what got loaded, or why nothing did.</summary>
    public static string Status { get; private set; } = "not loaded";

    public static bool Loaded => _tables.Count > 0;
    public static IReadOnlyDictionary<string, Table> Tables { get { EnsureLoaded(); return _tables; } }

    /// <summary>Load explicitly from a path. Safe to call more than once; the last successful
    /// load wins. Never throws — a bad file just leaves the tables empty.</summary>
    public static bool Load(string path)
    {
        _tried = true;
        if (!File.Exists(path)) { Status = $"no names file at {path}"; return false; }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("tables", out var tables) ||
                tables.ValueKind != JsonValueKind.Object)
            {
                Status = $"{Path.GetFileName(path)}: no \"tables\" object";
                return false;
            }

            _tables.Clear();
            foreach (var t in tables.EnumerateObject())
            {
                string conf = Str(t.Value, "confidence") ?? "unattributed";
                var entries = new Dictionary<int, Entry>();
                if (t.Value.TryGetProperty("entries", out var es) &&
                    es.ValueKind == JsonValueKind.Object)
                {
                    foreach (var e in es.EnumerateObject())
                    {
                        if (!int.TryParse(e.Name, out int id)) continue;
                        string? name = Str(e.Value, "name");
                        if (name == null) continue;
                        // Per-entry confidence only appears when a table's rows disagree;
                        // otherwise the entry inherits the table's.
                        entries[id] = new Entry(name, Str(e.Value, "category"),
                                                Str(e.Value, "desc"), Str(e.Value, "confidence") ?? conf);
                    }
                }
                _tables[t.Name] = new Table
                {
                    Confidence = conf,
                    Source = Str(t.Value, "source"),
                    Entries = entries,
                };
            }

            string game = root.TryGetProperty("game", out var g) ? Str(g, "code") ?? "?" : "?";
            Status = _tables.Count == 0
                ? $"{Path.GetFileName(path)}: no tables"
                : game + ": " + string.Join(", ",
                    _tables.Select(kv => $"{kv.Value.Entries.Count} {kv.Key} names [{kv.Value.Confidence}]"));
            return _tables.Count > 0;
        }
        catch (Exception ex)
        {
            _tables.Clear();
            Status = $"{Path.GetFileName(path)}: {ex.GetType().Name}";
            return false;
        }
    }

    /// <summary>Default location: next to the exe. Called lazily so a port that never calls
    /// <see cref="Load(string)"/> still picks the file up if its build copies one.</summary>
    public static void EnsureLoaded()
    {
        if (_tried) return;
        Load(Path.Combine(AppContext.BaseDirectory, "game_names.json"));
    }

    public static Entry? Get(string table, int id)
    {
        EnsureLoaded();
        return _tables.TryGetValue(table, out var t) && t.Entries.TryGetValue(id, out var e) ? e : null;
    }

    /// <summary>The name for an id, or null if there is no names file / no such entry.
    /// Callers must have a raw-id fallback: absent is the normal case for most ports.</summary>
    public static string? Name(string table, int id) => Get(table, id)?.Name;

    public static string? Description(string table, int id) => Get(table, id)?.Desc;

    /// <summary>Confidence for one table, e.g. "datamined". Null if the table is absent.</summary>
    public static string? Confidence(string table)
    {
        EnsureLoaded();
        return _tables.TryGetValue(table, out var t) ? t.Confidence : null;
    }

    static string? Str(JsonElement o, string prop) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(prop, out var v) &&
        v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // For tests / a host that wants to re-point at a different file.
    public static void Reset() { _tables.Clear(); _tried = false; Status = "not loaded"; }
}
