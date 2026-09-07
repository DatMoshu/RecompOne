using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Mods;

/// <summary>What kind of value a <see cref="ModSetting"/> holds. Everything is stored as a
/// double (one storage type, one JSON shape); the kind only says how to show and round it.</summary>
public enum ModSettingKind { Bool, Int, Float }

/// <summary>One live, typed, user-adjustable knob belonging to a mod.
///
/// Declared by the mod, edited by the Mods panel, persisted into settings.json. The
/// range is not decoration: it is what stops a slider handing a mod a value it was never
/// designed for, which for a game mod usually means a number that walks the player
/// through the scenery rather than a crash.</summary>
public sealed class ModSetting
{
    public required string ModId { get; init; }
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }
    public ModSettingKind Kind { get; init; }
    public double Default { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }

    double _value;
    public double Value
    {
        get => _value;
        set => _value = Kind == ModSettingKind.Bool ? (value != 0 ? 1 : 0)
                      : Math.Clamp(Kind == ModSettingKind.Int ? Math.Round(value) : value, Min, Max);
    }

    /// Set when a command-line flag supplied this value; settings.json is then not allowed
    /// to overwrite it for this launch.
    internal bool CliOverride { get; set; }

    /// Key used in settings.json's ModSettings map and in log lines.
    public string FullKey => $"{ModId}.{Key}";
    public bool IsDefault => Math.Abs(Value - Default) < 1e-9;
}

// The mod switchboard.
//
// settings.json has carried an `ActiveMods` list since the config class was written,
// and nothing read it. This is what reads it. The engine holds ONLY the mechanism --
// which ids are on, what each one's settings are, how a mod refuses -- and never knows
// what any individual mod does; every game-specific address, button and behaviour lives
// in that game's own host project (see games/ParasiteEve/ParasiteEve.PC/Mods.cs).
//
// The safety model is the one this engine already uses for PSMemory.HwIrqModel,
// LibEtc.PollDrivesFrame and Bios.BiosB.EnableThreads: a gated flag that DEFAULTS OFF,
// so a feature one game needs can never perturb another. Here that means: a mod is off
// unless its id is listed in ActiveMods, and with an empty list the port must behave
// exactly as it did before this file existed.
//
// The other rule that matters: a mod that CANNOT apply must decline visibly. Silence is
// the failure mode worth engineering against -- a mod that quietly does nothing (typo'd
// id, address that moved, gate that never opens) is indistinguishable from a mod that is
// working badly, and this project has lost time to exactly that shape of bug more than
// once. So: unknown ids in ActiveMods are reported by name at startup, Decline() names
// the mod and the reason, and every state change is logged once (never per frame).
//
// LOGGING CONVENTION, one for every mod in every port:
//   Note(id, msg) -- a transition worth seeing once ("hook is live", "folder: ...").
//                    De-duplicated for the whole run, so it is safe inside a per-frame path.
//   Log(id, msg)  -- an event that can legitimately recur (a hotkey press, a skip starting
//                    and finishing). NOT de-duplicated, so it must never be reached on an
//                    ordinary frame.
//   Decline(id, why) -- this mod cannot do its job; it is off for the rest of the run
//                    (until the Mods panel re-enables it, which clears the decline and
//                    lets the mod try again).
// Nothing writes "[mods] ..." to the console by hand; the prefix belongs to this class so
// one grep finds every mod's output.
//
// WHO WRITES settings.json. The Mods panel does, through SetActive/SetValue + Persist().
// That matters because HostWindow.OnClosing re-serialises GameConfig from memory on exit,
// so anything that changes a mod's state MUST change the in-memory GameConfig too or the
// exit write silently reverts it. Both setters below do exactly that before anything else.
public static class ModRegistry
{
    sealed class Entry
    {
        public required string Id;
        public required string Summary;
        public string[] Aliases = [];
        public bool Requested;              // listed in ActiveMods (or forced on via CLI)
        public string? DeclineReason;       // non-null => off until re-enabled
    }

    static readonly Dictionary<string, Entry> _mods = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, ModSetting> _settings = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> _aliasOf = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> _cliOn = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> _cliOff = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> _requested = new(StringComparer.OrdinalIgnoreCase);
    static bool _allOff;
    static bool _resolved;

    /// Every mod a game supports declares itself here, at host startup, BEFORE the config
    /// is loaded. Declaring costs nothing and turns nothing on -- it exists so the registry
    /// can list what is available and, more usefully, so an id in ActiveMods that matches
    /// no declaration can be reported as a typo instead of silently ignored.
    ///
    /// `aliases` are ids this mod used to be called. A user's settings.json outlives any
    /// rename, and "your mod vanished" is a worse outcome than carrying two strings.
    public static void Declare(string id, string summary, params string[] aliases)
    {
        if (!_mods.TryGetValue(id, out var e))
        {
            e = new Entry { Id = id, Summary = summary };
            // A mod declared AFTER the list was resolved (a lazily-initialised subsystem,
            // say) must still see whether it was requested — otherwise it would be silently
            // and permanently off, which is the one outcome this class exists to prevent.
            if (_resolved) e.Requested = _requested.Contains(id);
            _mods[id] = e;
        }
        e.Summary = summary;
        if (aliases.Length > 0)
        {
            e.Aliases = aliases;
            foreach (var a in aliases) _aliasOf[a] = id;
        }
    }

    /// Declare one adjustable value belonging to a mod. Safe to call before the config is
    /// loaded (the stored value is applied when settings.json is read) and idempotent, so
    /// a host that declares on every construction does not reset the user's number.
    public static ModSetting DeclareSetting(string modId, string key, string label, string description,
                                            ModSettingKind kind, double def, double min = 0, double max = 1)
    {
        var full = $"{modId}.{key}";
        if (_settings.TryGetValue(full, out var existing)) return existing;
        var s = new ModSetting
        {
            ModId = modId, Key = key, Label = label, Description = description,
            Kind = kind, Default = def,
            Min = kind == ModSettingKind.Bool ? 0 : min,
            Max = kind == ModSettingKind.Bool ? 1 : max,
        };
        s.Value = def;
        _settings[full] = s;
        if (ConfigManager.Loaded) ApplyStored(s);
        return s;
    }

    public static ModSetting DeclareFlag(string modId, string key, string label, string description, bool def) =>
        DeclareSetting(modId, key, label, description, ModSettingKind.Bool, def ? 1 : 0);

    /// Command-line override for one setting (`--walk=3`). Wins over settings.json for this
    /// launch and is not written back, so a one-off measurement run cannot quietly become
    /// the user's saved configuration. Editing the same setting in the Mods panel still
    /// works and still persists -- an override sets the starting value, it does not lock it.
    public static void Override(string modId, string key, double v)
    {
        if (!_settings.TryGetValue($"{modId}.{key}", out var s))
        {
            Console.WriteLine($"[mods] no setting '{key}' on mod '{modId}' - ignoring the override");
            return;
        }
        s.Value = v;
        s.CliOverride = true;
    }

    /// Read a setting. Cheap enough for a per-frame path; resolution against settings.json
    /// has already happened by the first call from inside the game loop.
    public static double Num(string modId, string key)
    {
        Resolve();
        return _settings.TryGetValue($"{modId}.{key}", out var s) ? s.Value : 0;
    }

    public static bool Flag(string modId, string key) => Num(modId, key) != 0;
    public static int Int(string modId, string key) => (int)Math.Round(Num(modId, key));

    /// Change a setting and mirror it into the in-memory GameConfig so the exit rewrite
    /// keeps it. Does NOT touch the disk -- call <see cref="Persist"/> for that, once the
    /// user has finished dragging the slider.
    public static void SetValue(ModSetting s, double v)
    {
        s.Value = v;
        ConfigManager.Game.ModSettings[s.FullKey] = s.Value;
    }

    /// Turn a mod on or off at runtime, exactly as if its id had been in ActiveMods.
    /// Enabling clears a previous decline, so the mod gets a fresh attempt (the reason it
    /// declined -- a missing file, an unreachable folder -- is usually the thing the user
    /// just fixed).
    public static void SetActive(string id, bool on)
    {
        Resolve();
        if (!_mods.TryGetValue(id, out var e)) return;

        e.Requested = on;
        if (on) { _requested.Add(id); e.DeclineReason = null; _cliOff.Remove(id); }
        else { _requested.Remove(id); _cliOn.Remove(id); }

        // The in-memory config is what the exit rewrite serialises, so it has to agree
        // now, not at save time. Aliases are dropped on the way through: whatever the user
        // had, the file ends up spelling the current id.
        var list = ConfigManager.Game.ActiveMods;
        list.RemoveAll(x => string.Equals(x.Trim(), id, StringComparison.OrdinalIgnoreCase)
                         || (_aliasOf.TryGetValue(x.Trim(), out var canon) && canon == id));
        if (on) list.Add(id);

        Console.WriteLine($"[mods] {id} {(on ? "enabled" : "disabled")}");
    }

    /// Write settings.json now. Separate from the setters because an ImGui slider fires on
    /// every mouse-move and this is a whole-file write.
    public static void Persist()
    {
        foreach (var s in _settings.Values) ConfigManager.Game.ModSettings[s.FullKey] = s.Value;
        ConfigManager.SaveGame();
    }

    /// Command-line overrides, applied whether or not the id was declared yet.
    /// `--mods=a,b` forces those on; `--no-mods` forces everything off (the escape hatch
    /// for "is this bug mine or the mod's?", which is the question a mod framework has to
    /// be able to answer in one launch).
    public static void ForceOn(string id) { _cliOn.Add(id); _resolved = false; }
    public static void ForceOff(string id) { _cliOff.Add(id); _resolved = false; }
    public static void ForceAllOff() { _allOff = true; _resolved = false; }

    /// True when this mod should be running. Safe to call every frame from anywhere:
    /// resolution against settings.json happens once, lazily, on the first query --
    /// which matters because a game's host declares its mods in Program.cs, before
    /// Runtime.Run has loaded settings.json.
    public static bool Active(string id)
    {
        Resolve();
        return _mods.TryGetValue(id, out var e) && e.Requested && e.DeclineReason == null;
    }

    /// True when the user asked for this mod but it refused. Distinguishes "off" from
    /// "broken", which is the whole point of declining out loud.
    public static bool Declined(string id) =>
        _mods.TryGetValue(id, out var e) && e.Requested && e.DeclineReason != null;

    /// Turn a mod off for the rest of the run and say why, once. Call this the moment a
    /// mod discovers it cannot do its job correctly -- a missing address, a gate that
    /// proves false, a game build it does not match. Declining is always better than
    /// applying half of an effect: half-applied state is the thing that produces a bug
    /// report nobody can reproduce.
    public static void Decline(string id, string reason)
    {
        Resolve();
        if (!_mods.TryGetValue(id, out var e)) { _mods[id] = e = new Entry { Id = id, Summary = "(undeclared)" }; }
        if (e.DeclineReason != null) return;                 // already declined; stay quiet
        e.DeclineReason = reason;
        if (e.Requested) Console.WriteLine($"[mods] {id} DECLINED: {reason}");
    }

    /// One-shot log helper for mods: report a state change exactly once per distinct
    /// message. Mods run inside per-frame hooks, so the useful discipline is "log the
    /// transition, never the state".
    public static void Note(string id, string message) => ModLog.Once("mods", $"{id}: {message}");

    /// Log an event that can legitimately happen more than once (a hotkey press, a
    /// fast-forward starting and stopping). Same prefix as everything else here, so mods
    /// never hand-roll a "[mods]" string of their own -- but unlike Note it repeats, so it
    /// must not be reached on an ordinary frame.
    public static void Log(string id, string message) => Console.WriteLine($"[mods] {id}: {message}");

    /// For a list-and-exit CLI path (`--mods-list`) that runs before Runtime.Run has
    /// loaded anything: read settings.json now, so the listing shows the user's real
    /// ActiveMods instead of defaults. Only safe because such a path prints and exits.
    public static void LoadConfigForListing() => ConfigManager.Load();

    public static IEnumerable<(string Id, string Summary, bool Active, string? Declined)> All()
    {
        Resolve();
        foreach (var e in _mods.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
            yield return (e.Id, e.Summary, e.Requested && e.DeclineReason == null, e.DeclineReason);
    }

    /// The settings belonging to one mod, in declaration order.
    public static IEnumerable<ModSetting> SettingsOf(string modId)
    {
        Resolve();
        foreach (var s in _settings.Values)
            if (string.Equals(s.ModId, modId, StringComparison.OrdinalIgnoreCase)) yield return s;
    }

    static void ApplyStored(ModSetting s)
    {
        if (s.CliOverride) return;      // this launch's flag beats the saved file
        if (ConfigManager.Game.ModSettings.TryGetValue(s.FullKey, out var v)) s.Value = v;
    }

    static void Resolve()
    {
        if (_resolved) return;

        // Do NOT cache an answer taken from an unloaded config. A game's host parses its
        // whole command line -- declaring mods, and possibly querying them -- before
        // Runtime.Run gets as far as ConfigManager.Load(). Resolving in that window would
        // read an empty default GameConfig, latch "nothing is active", and leave
        // settings.json's ActiveMods exactly as dead as it was before this class existed.
        // Answer from the CLI overrides alone until the real file has been read.
        bool haveConfig = ConfigManager.Loaded;
        if (haveConfig) _resolved = true;

        var requested = _requested;
        requested.Clear();
        var renamed = new List<(string from, string to)>();
        if (!_allOff)
        {
            if (haveConfig)
                foreach (var raw in ConfigManager.Game.ActiveMods)
                {
                    var id = raw.Trim();
                    if (_aliasOf.TryGetValue(id, out var canon)) { renamed.Add((id, canon)); id = canon; }
                    requested.Add(id);
                }
            foreach (var id in _cliOn) requested.Add(_aliasOf.TryGetValue(id, out var c) ? c : id);
            foreach (var id in _cliOff) requested.Remove(_aliasOf.TryGetValue(id, out var c) ? c : id);
        }
        requested.Remove("");

        foreach (var e in _mods.Values) e.Requested = requested.Contains(e.Id);

        // Everything below is the one-time announcement, and it belongs to the resolved
        // answer -- not to the provisional CLI-only one we may compute several times
        // before settings.json is read.
        if (!haveConfig) return;

        foreach (var s in _settings.Values) ApplyStored(s);

        foreach (var (from, to) in renamed)
            Console.WriteLine($"[mods] '{from}' in ActiveMods is the old name for '{to}' - honouring it; " +
                              "the Mods panel will write the new name next time you change anything.");

        // An id nobody declared is almost always a typo in settings.json, and a typo that
        // fails silently is a support question. Name it.
        foreach (var id in requested)
            if (!_mods.ContainsKey(id))
                Console.WriteLine($"[mods] '{id}' is in ActiveMods but no mod with that id exists in this build " +
                                  $"(known: {(_mods.Count == 0 ? "none" : string.Join(", ", _mods.Keys.OrderBy(x => x, StringComparer.Ordinal)))})");

        var on = _mods.Values.Where(e => e.Requested).Select(e => e.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Console.WriteLine(on.Count == 0
            ? $"[mods] none active ({(_allOff ? "--no-mods" : "settings.json ActiveMods is empty")}) - stock behaviour"
            : $"[mods] active: {string.Join(", ", on)}");
    }
}
