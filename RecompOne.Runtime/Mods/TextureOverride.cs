using System.Text.RegularExpressions;
using RecompOne.Runtime.Host;

namespace RecompOne.Runtime.Mods;

/// <summary>Replaces a PS1 TEXTURE PAGE — the thing character models are painted with — from a
/// PNG on disk, live, while the game runs. The sibling of <see cref="ArtOverride"/>, which does
/// the same job for a fixed-camera room's pre-rendered backdrop; both read the same folder, are
/// switched on together, and share the "alpha &lt; 128 means keep the game's pixel" rule.
///
/// <para>WHY THIS IS NOT AN ArtOverride VARIANT. A backdrop is pixels in the framebuffer, so it
/// can be repainted by writing over the rectangle the game just wrote. A character texture is
/// never in the framebuffer as a unit: it lives in VRAM as 4bpp/8bpp INDICES plus a CLUT, and it
/// reaches the screen one texel at a time inside the rasteriser, modulated by vertex colour and
/// perspective-warped across a triangle. There is no rectangle to paint. Writing the repaint
/// back into VRAM is also not an option worth having: it would mean re-quantising the artist's
/// colours down to the page's own 16- or 256-entry palette AND overwriting a CLUT that other
/// pages legitimately share. So the substitution happens at the FETCH, in the renderer, keyed by
/// the same (texture page, CLUT, texture window) triple the exporter bakes and names its files
/// with.</para>
///
/// <para>WHAT A FILE MEANS. <c>char_&lt;anything&gt;_tp&lt;XXXX&gt;_cl&lt;XXXX&gt;[_tw&lt;XXXXX&gt;].png</c>,
/// 256x256 RGBA. The <c>&lt;anything&gt;</c> is for humans — <c>char_aya_...</c> — and is ignored
/// when matching; the hex fields ARE the key. <c>_cl</c> may be the literal <c>ALL</c> to match
/// every CLUT on that page (one repaint covering all of a model's palette variants), and an
/// exact CLUT always wins over an <c>ALL</c>. <c>_tw</c> is omitted when the texture window is 0,
/// which is the normal case.</para>
///
/// <para>ALPHA, AND THE ONE THING A REPAINT CANNOT DO. A pixel with alpha &gt;= 128 replaces the
/// game's texel COLOUR; alpha &lt; 128 leaves that texel exactly as the game drew it, so a partial
/// repaint works. What a repaint deliberately cannot change is the SILHOUETTE: a texel the game
/// stores as 0x0000 is a hole, the renderer discards it before the override is consulted, and it
/// stays a hole. That is on purpose — the exported page marks holes with alpha 0, and an artist
/// who flattens the PNG onto an opaque background in an editor would otherwise turn every hole
/// into a solid block and wrap the character in a coloured card. The per-texel semi-transparency
/// (STP) bit is likewise the game's, so blending behaves as before.</para>
///
/// <para>Applies to the HLE GL renderer, which is the one every port here actually draws with
/// (see <c>GlBackend</c>/<c>GlShaders</c>); the legacy software rasteriser ignores it and says
/// so once.</para></summary>
public static class TextureOverride
{
    public const int PageSize = 256;

    /// <summary>Filename prefix. Deliberately different from <see cref="ArtOverride.Prefix"/> so
    /// one folder can hold both kinds and each loader takes only its own.</summary>
    public const string Prefix = "char_";

    /// <summary>Matched against a CLUT-less entry. A real CLUT is 15 bits, so 0xFFFF cannot
    /// collide with one.</summary>
    const int ClutAny = 0xFFFF;

    public static bool Active { get; private set; }
    public static string Folder { get; private set; } = "";
    public static bool Verbose;

    /// <summary>Bumped on every successful (re)scan. The renderer holds its own copy and
    /// re-uploads when the two differ — that is the whole hot-reload handshake, and it means the
    /// GL side never has to know a file changed.</summary>
    public static int Version { get; private set; }

    /// <summary>Vertices that carried an override this run. A mod that silently does nothing is
    /// the failure mode this project keeps paying for, so it is counted, not assumed.</summary>
    public static long VertsMatched;

    static readonly List<byte[]> _pages = new();     // 256*256*4 RGBA, one per file
    static readonly List<string> _pageName = new();
    static readonly Dictionary<long, int> _slot = new();
    static FileSystemWatcher? _watcher;
    static volatile bool _dirty;

    /// <summary>The loaded pages, in slot order. The renderer uploads these as one texture array
    /// and the per-vertex slot index selects between them.</summary>
    public static IReadOnlyList<byte[]> Pages => _pages;
    public static int Count => _pages.Count;

    static long Key(int tpage, int clut, uint texWin) =>
        ((long)(tpage & 0x1FF) << 40) | ((long)(clut & 0xFFFF) << 24) | (texWin & 0xFFFFF);

    /// <summary>Which override page, if any, this primitive should sample. -1 = none, which is
    /// every primitive in a stock run.
    ///
    /// <para>The texture page is masked to its low 9 bits — page X/Y, colour depth and blend mode
    /// — because the higher bits (dither, draw-to-display) change nothing about which texels are
    /// fetched, and letting them into the key would make a file match on some frames and not
    /// others.</para></summary>
    public static int Slot(int tpage, int clut, uint texWin)
    {
        if (_slot.Count == 0) return -1;
        if (_slot.TryGetValue(Key(tpage, clut & 0x7FFF, texWin), out int s)) { VertsMatched++; Hit(s, tpage, clut & 0x7FFF, texWin); return s; }
        if (_slot.TryGetValue(Key(tpage, ClutAny, texWin), out s)) { VertsMatched++; Hit(s, tpage, clut & 0x7FFF, texWin); return s; }
        if (Verbose) Miss(tpage, clut & 0x7FFF, texWin);
        return -1;
    }

    // Announce the first primitive each file actually claims. This is the line that answers "is
    // it loaded" vs "is it being used" -- a loaded-but-never-matched file is the whole failure
    // mode, and it is invisible without this.
    static readonly HashSet<long> _hitSeen = new();
    static void Hit(int slot, int tpage, int clut, uint texWin)
    {
        if (_hitSeen.Count >= 64 || !_hitSeen.Add(Key(tpage, clut, texWin))) return;
        Console.WriteLine($"[tex] {_pageName[slot]} -> tp{tpage:X4} cl{clut:X4} tw{texWin:X5}");
    }

    // "I dropped the file in and nothing happened" has exactly two causes -- the file's key does
    // not match anything the game draws, or the game never draws that page in this scene -- and
    // they are indistinguishable from the outside. --art-trace prints the keys the renderer is
    // actually asking for, so the answer is one glance instead of a debugging session.
    static readonly HashSet<long> _missSeen = new();
    static void Miss(int tpage, int clut, uint texWin)
    {
        if (_missSeen.Count >= 200) return;
        if (!_missSeen.Add(Key(tpage, clut, texWin))) return;
        Console.WriteLine($"[tex] no override for tp{tpage:X4} cl{clut:X4} tw{texWin:X5} " +
                          $"(file would be {Prefix}<name>_tp{tpage:X4}_cl{clut:X4}" +
                          (texWin != 0 ? $"_tw{texWin:X5}" : "") + ".png)");
    }

    public static bool Enable(string folder, bool verbose = false)
    {
        Folder = Path.GetFullPath(folder);
        Verbose = verbose;
        if (!Directory.Exists(Folder)) return false;
        Active = true;
        Rescan();
        try
        {
            _watcher = new FileSystemWatcher(Folder, "*.png")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => _dirty = true;
            _watcher.Created += (_, _) => _dirty = true;
            _watcher.Deleted += (_, _) => _dirty = true;
            _watcher.Renamed += (_, _) => _dirty = true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[tex] live reload unavailable ({e.Message}); {Prefix}*.png load once, at startup");
        }
        return true;
    }

    public static void Disable()
    {
        if (!Active) return;
        _watcher?.Dispose(); _watcher = null;
        _pages.Clear(); _pageName.Clear(); _slot.Clear();
        Active = false;
        Version++;   // so the renderer drops what it uploaded
    }

    /// <summary>Call once per frame from the game host. Cheap when nothing changed.</summary>
    public static void Poll()
    {
        if (!Active || !_dirty) return;
        _dirty = false;
        Rescan();
    }

    // char_<label>_tp<hex>_cl<hex|ALL>[_tw<hex>]  -- the label is free-form and ignored.
    static readonly Regex _name = new(
        @"_tp([0-9A-Fa-f]{1,4})_cl(ALL|[0-9A-Fa-f]{1,4})(?:_tw([0-9A-Fa-f]{1,5}))?$",
        RegexOptions.Compiled);

    static void Rescan()
    {
        _pages.Clear(); _pageName.Clear(); _slot.Clear();
        string[] files;
        try { files = Directory.GetFiles(Folder, Prefix + "*.png"); }
        catch (Exception e) { Console.WriteLine($"[tex] cannot list {Folder}: {e.Message}"); return; }

        int bad = 0;
        foreach (var f in files.OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            // The exporter writes its UV templates beside the paintable pages. They are reference
            // images, not overrides -- loading one would paint the wireframe onto the character.
            if (stem.EndsWith("_uv", StringComparison.OrdinalIgnoreCase) ||
                stem.EndsWith("_uvguide", StringComparison.OrdinalIgnoreCase)) continue;

            var m = _name.Match(stem);
            if (!m.Success)
            {
                bad++;
                NoteOnce($"{Path.GetFileName(f)}: name does not end in _tp<hex>_cl<hex|ALL>[_tw<hex>] — ignored");
                continue;
            }
            int tpage = Convert.ToInt32(m.Groups[1].Value, 16) & 0x1FF;
            int clut = m.Groups[2].Value.Equals("ALL", StringComparison.OrdinalIgnoreCase)
                ? ClutAny : Convert.ToInt32(m.Groups[2].Value, 16) & 0x7FFF;
            uint tw = m.Groups[3].Success ? Convert.ToUInt32(m.Groups[3].Value, 16) & 0xFFFFF : 0u;

            if (!PngReader.TryDecodeRgba(f, out int w, out int h, out var rgba, out var err))
            {
                // A file mid-save reads as corrupt; leaving it out of this scan is enough, the
                // watcher will fire again when the editor finishes.
                NoteOnce($"{Path.GetFileName(f)}: {err}");
                continue;
            }
            if (w != PageSize || h != PageSize)
            {
                NoteOnce($"{Path.GetFileName(f)} is {w}x{h}; a PS1 texture page is {PageSize}x{PageSize} — ignored");
                continue;
            }

            long key = Key(tpage, clut, tw);
            if (_slot.ContainsKey(key))
            {
                NoteOnce($"{Path.GetFileName(f)} duplicates tp{tpage:X4}/cl{(clut == ClutAny ? "ALL" : clut.ToString("X4"))} — keeping the first");
                continue;
            }
            _slot[key] = _pages.Count;
            _pages.Add(rgba);
            _pageName.Add(Path.GetFileName(f));
        }

        Version++;
        Console.WriteLine(_pages.Count == 0
            ? $"[tex] watching {Folder} — no usable {Prefix}*.png yet{(bad > 0 ? $" ({bad} misnamed)" : "")}"
            : $"[tex] {_pages.Count} texture-page override(s): {string.Join(", ", _pageName)}");
    }

    static void NoteOnce(string msg) => ModLog.Once("tex", msg);
}
