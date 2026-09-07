using RecompOne.Runtime.Host;

namespace RecompOne.Runtime.Mods;

/// <summary>Replaces a fixed-camera room's pre-rendered backdrop with a PNG from a folder on
/// disk, live, while the game runs.
///
/// <para>WHERE IT HOOKS, AND WHY THERE. A PS1 fixed-camera backdrop is not geometry — it is put
/// into the framebuffer by an image transfer (GP0 0xA0 / libgpu <c>LoadImage</c> / GPU DMA) or a
/// VRAM-to-VRAM blit (GP0 0x80 / <c>MoveImage</c>). Measured on Parasite Eve, a field room's
/// entire frame is 1124 triangles with ZERO screen-space ones: the plate never reaches the
/// triangle path at all. So this listens on <see cref="Gpu.OnImageUpload"/> /
/// <see cref="Gpu.OnVramCopy"/> and, whenever such a write lands inside a framebuffer, paints the
/// override over exactly that rect. Doing it per-write rather than once per frame is what makes
/// it correct in the awkward cases: a room whose backdrop finishes drawing AFTER the characters
/// have started (PE1 map 2's lit awning does) would otherwise overwrite the override halfway
/// through the frame.</para>
///
/// <para>WHY IT CANNOT PAINT OVER CHARACTERS. The override only ever replaces pixels the game
/// itself just wrote as backdrop. Anything drawn afterwards — characters, HUD, dialogue — draws
/// on top of the new art exactly as it drew on top of the old, because nothing about the draw
/// order changes.</para>
///
/// <para>ALPHA IS MEANINGFUL: a pixel with alpha &lt; 128 is left as the game drew it, so a file
/// can repaint part of a room and leave the rest original. A fully opaque PNG replaces
/// everything.</para>
///
/// <para>This class is the MECHANISM and knows nothing about any game. The game's host switches
/// it on, points it at a folder, and sets <see cref="CurrentKey"/> each frame to whatever names
/// the room; see <c>games/ParasiteEve/ParasiteEve.PC/ArtMod.cs</c>.</para></summary>
public static class ArtOverride
{
    /// <summary>Names the room whose art should be applied right now — e.g. PE1's scene id as
    /// <c>"0xA8000148"</c>. The file looked up is <c>&lt;Folder&gt;/&lt;Prefix&gt;&lt;key&gt;.png</c>.
    /// Set it to null whenever no override should apply (front-end, FMV, loading), which is the
    /// game's call, not the engine's.</summary>
    public static volatile string? CurrentKey;

    /// <summary>Folder watched for art. Set by <see cref="Enable"/>.</summary>
    public static string Folder { get; private set; } = "";

    /// <summary>Filename prefix, so overrides sit next to the plates the exporter writes and use
    /// the same names.</summary>
    public static string Prefix { get; private set; } = "bg_";

    public static bool Active { get; private set; }

    /// <summary>Live reload actually running (a folder that cannot be watched still loads at
    /// startup — see the note logged in <see cref="Enable"/>).</summary>
    public static bool HotReload { get; private set; }

    public static bool Verbose;

    /// <summary>Applied-art counters, for the same reason the plate exporter reports its own:
    /// a mod that silently does nothing is the failure mode worth engineering against.</summary>
    public static long RectsPainted, PixelsPainted, ReloadCount;

    sealed class Art
    {
        public required string Path;
        public DateTime Stamp;
        public int W, H;
        public ushort[] Px = Array.Empty<ushort>();   // RGB555, row-major
        public bool[] Opaque = Array.Empty<bool>();   // alpha >= 128
        public bool AllOpaque;                        // no transparent pixel: enables the no-readback path
        public string? Error;                         // non-null => refused; logged once, then inert
    }

    static readonly Dictionary<string, Art> _cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> _applied = new(StringComparer.OrdinalIgnoreCase);
    static FileSystemWatcher? _watcher;
    static volatile bool _dirty;
    static ushort[] _scratch = Array.Empty<ushort>();

    /// <summary>Switch on. Returns false (and explains on the console) if the folder does not
    /// exist — the caller should <c>ModRegistry.Decline</c> on that, because an art mod pointed
    /// at nothing is exactly the silent no-op this project keeps losing time to.</summary>
    public static bool Enable(string folder, string prefix = "bg_", bool verbose = false)
    {
        Folder = Path.GetFullPath(folder);
        Prefix = prefix;
        Verbose = verbose;
        if (!Directory.Exists(Folder)) return false;

        Gpu.OnImageUpload += OnTransfer;
        Gpu.OnVramCopy += OnTransfer;
        Gpu.OnRectDrawn += OnSpriteDrawn;
        Gpu.OnTriangleDrawn += OnTriangle;
        Active = true;

        // Inventory up front, so "I dropped a file in and nothing happened" is answerable
        // without a debugger: either the name is not in this list, or the room never loaded.
        var files = Directory.GetFiles(Folder, "*.png").Where(f => Path.GetFileName(f).StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).StartsWith(FgPrefix, StringComparison.OrdinalIgnoreCase)).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Console.WriteLine(files.Length == 0
            ? $"[art] watching {Folder} — no {Prefix}*.png backgrounds yet; drop one in and it loads without a restart"
            : $"[art] watching {Folder} — {files.Length} background override(s): {string.Join(", ", files)}");

        // The other half of "replace this game's art": a room's BACKDROP is framebuffer pixels
        // (above), a CHARACTER's skin is a VRAM texture page and has to be substituted inside the
        // renderer instead. Same folder, same switch, same alpha rule -- see TextureOverride.
        TextureOverride.Enable(Folder, verbose);

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
            HotReload = true;
        }
        catch (Exception e)
        {
            // Not fatal: without a watcher the art still loads, just not live. Say which it is —
            // "it did not update" and "it never loaded" are different problems.
            HotReload = false;
            Console.WriteLine($"[art] live reload unavailable ({e.Message}); art loads once, on first use of each room");
        }
        return true;
    }

    public static void Disable()
    {
        if (!Active) return;
        Gpu.OnImageUpload -= OnTransfer;
        Gpu.OnVramCopy -= OnTransfer;
        Gpu.OnRectDrawn -= OnSpriteDrawn;
        Gpu.OnTriangleDrawn -= OnTriangle;
        _watcher?.Dispose(); _watcher = null;
        Active = false; HotReload = false;
        _cache.Clear(); _applied.Clear();
        TextureOverride.Disable();
    }

    /// <summary>Call once per frame alongside <see cref="BeginFrame"/>. Only the texture-page
    /// half needs it: a backdrop override is re-read lazily when the room next draws, but a
    /// texture page has to be handed to the renderer, so a saved file is picked up here.</summary>
    public static void Poll() => TextureOverride.Poll();

    // ---- the hook ---------------------------------------------------------------------

    // Delegate-shaped entry point for the transfer/blit hooks (an optional parameter does not
    // satisfy Action&lt;int,int,int,int&gt;).
    static void OnTransfer(int x, int y, int w, int h) => OnFramebufferWrite(BgPrefixKey(), x, y, w, h, false);

    // ---- layers ------------------------------------------------------------------------
    //
    // A fixed-camera room is not one image. PE1 draws the backdrop, then the character models,
    // then a FOREGROUND layer over the top so it occludes them -- the limo's open door in map 10
    // and the theatre awning in map 2 are both that. The models are the divider: everything
    // painted before the frame's first triangle is behind them, everything after is in front.
    //
    // That distinction has to be honoured or the override destroys something the user cannot
    // see. Repainting the foreground from a full-frame bg_*.png replaces the door with whatever
    // the artist painted in that rectangle -- which, on a plate that never showed the door, is
    // the car body behind it. The door vanishes and nothing in the file they were given hints
    // that it existed. So by DEFAULT the foreground layer is left exactly as the game draws it,
    // and a user who wants to repaint it supplies fg_<key>.png (whose transparent pixels keep
    // the original, so a partial repaint works).
    static volatile bool _seenTriangle;

    /// <summary>Call once per frame, before the game draws anything, to reopen the background
    /// layer. Without it every write after the first frame counts as foreground.</summary>
    public static void BeginFrame() => _seenTriangle = false;

    static void OnTriangle() => _seenTriangle = true;

    /// <summary>Filename prefix for the foreground layer.</summary>
    public static string FgPrefix { get; private set; } = "fg_";

    static string BgPrefixKey() => Prefix;

    static void OnFramebufferWrite(string prefix, int x, int y, int w, int h, bool allowClip)
    {
        if (!Active) return;
        var key = CurrentKey;
        if (key == null) return;
        if (Runtime.Gpu is not { } gpu) return;

        int fw = gpu.DisplayWidth, fh = gpu.DisplayHeight;
        if (fw <= 0 || fh <= 0) return;

        // Which framebuffer did this land in? A double-buffered game alternates, so try the one
        // being drawn into and the one being displayed.
        //
        // For an image transfer or a blit the rect must fall ENTIRELY inside one of them: a
        // partially-overlapping transfer is texture/CLUT/staging traffic, and clipping that into
        // the frame is how an art mod corrupts textures. A SPRITE is different — it is by
        // definition a draw into the current draw area, and a backdrop tile at the edge of the
        // screen is legitimately clipped by it — so those are allowed to overhang and are
        // trimmed, otherwise every border tile of a tiled backdrop keeps its original art.
        if (!InFrame(x, y, w, h, gpu.DrawOffsetX, gpu.DrawOffsetY, fw, fh, out int ox, out int oy) &&
            !InFrame(x, y, w, h, gpu.DisplayX, gpu.DisplayY, fw, fh, out ox, out oy))
        {
            if (!allowClip) return;
            if (!Overlaps(x, y, w, h, gpu.DrawOffsetX, gpu.DrawOffsetY, fw, fh, out ox, out oy) &&
                !Overlaps(x, y, w, h, gpu.DisplayX, gpu.DisplayY, fw, fh, out ox, out oy))
                return;
            int nx = Math.Max(x, ox), ny = Math.Max(y, oy);
            w = Math.Min(x + w, ox + fw) - nx; h = Math.Min(y + h, oy + fh) - ny;
            x = nx; y = ny;
            if (w <= 0 || h <= 0) return;
        }

        var art = Get(prefix, key, fw, fh);
        if (art == null || art.Error != null) return;

        int lx = x - ox, ly = y - oy;
        int n = w * h;
        if (_scratch.Length < n) _scratch = new ushort[n];

        // FAST PATH, and it is not an optimisation nicety — it is what makes this usable at all.
        // A room that redraws its backdrop as tile sprites issues a few hundred of these per
        // frame, and the readback below is a GL pipeline stall each time: with the readback in
        // the loop, PE1's limo street went from 60 fps to not advancing. A fully opaque override
        // replaces every pixel anyway, so nothing needs reading; the only thing lost is the
        // per-pixel mask/STP bit, which for a wholesale replacement is the new art's to define.
        if (art.AllOpaque && lx >= 0 && ly >= 0 && lx + w <= art.W && ly + h <= art.H)
        {
            for (int row = 0; row < h; row++)
                Array.Copy(art.Px, (ly + row) * art.W + lx, _scratch, row * w, w);
            gpu.WriteVramRect(x, y, w, h, _scratch);
            RectsPainted++; PixelsPainted += n;
            if (_applied.Add(prefix + key)) Console.WriteLine($"[art] {prefix}{key}.png applied ({art.W}x{art.H})");
            if (Verbose) Console.WriteLine($"[art]   painted {n}px over {w}x{h} @({x},{y}) frame origin=({ox},{oy})");
            return;
        }

        // Slow path: a partially transparent override is a PARTIAL repaint, so what the game just
        // wrote has to be read back and kept wherever the art is transparent.
        gpu.ReadVramRect(x, y, w, h, _scratch);
        int painted = 0;
        for (int row = 0; row < h; row++)
        {
            int sy = ly + row;
            if ((uint)sy >= (uint)art.H) continue;
            for (int col = 0; col < w; col++)
            {
                int sx = lx + col;
                if ((uint)sx >= (uint)art.W) continue;
                int si = sy * art.W + sx;
                if (!art.Opaque[si]) continue;
                // Preserve the mask/semi-transparency bit the game set on this pixel: it is a
                // GPU flag, not colour, and clearing it changes how later primitives blend.
                _scratch[row * w + col] = (ushort)(art.Px[si] | (_scratch[row * w + col] & 0x8000));
                painted++;
            }
        }
        if (painted == 0) return;
        gpu.WriteVramRect(x, y, w, h, _scratch);
        RectsPainted++; PixelsPainted += painted;
        if (_applied.Add(prefix + key))
            Console.WriteLine($"[art] {prefix}{key}.png applied ({art.W}x{art.H})");
        if (Verbose) Console.WriteLine($"[art]   painted {painted}px over {w}x{h} @({x},{y}) frame origin=({ox},{oy})");
    }

    /// <summary>Decides whether a SPRITE is part of the room's backdrop. Same contract, and same
    /// reason for having no default, as the plate exporter's rule of the same name — see
    /// <see cref="Export.BackgroundPlate.SpriteIsBackdrop"/>. Null = sprites are never repainted.</summary>
    public static Func<int, int, int, int, bool, ushort, ushort, bool>? SpriteIsBackdrop;

    /// <summary>The third way a backdrop reaches the screen, and the one that cost a debugging
    /// cycle to find. Some PE1 rooms put the backdrop in the framebuffer once with an image
    /// transfer and leave it there — for those, painting at the transfer is enough, and the
    /// override survives (map 2, 0xA8000148, behaves this way). Others redraw the whole backdrop
    /// as a lattice of 16x16 tile sprites EVERY frame (0xA8001048, the limo street, does), and
    /// there the transfer-time paint is correct but is overwritten a few milliseconds later by
    /// the game's own tiles. Measured: both rooms took exactly 186 transfer-time paints and then
    /// none, yet only one of them kept the new art on screen. So the sprite path has to be
    /// painted too — and, as with the exporter, only for sprites the game says are backdrop,
    /// because the dialogue box and its glyphs come down the identical path.</summary>
    static void OnSpriteDrawn(int x, int y, int w, int h, bool textured, ushort tpage, ushort clut)
    {
        if (!Active || SpriteIsBackdrop == null) return;
        if (!SpriteIsBackdrop(x, y, w, h, textured, tpage, clut)) return;
        // Before the first triangle this sprite is backdrop; after it, it is the foreground
        // layer and only an explicit fg_<key>.png may touch it.
        OnFramebufferWrite(_seenTriangle ? FgPrefix : Prefix, x, y, w, h, allowClip: true);
    }

    static bool InFrame(int x, int y, int w, int h, int ox, int oy, int fw, int fh, out int rox, out int roy)
    {
        rox = ox; roy = oy;
        return x >= ox && y >= oy && x + w <= ox + fw && y + h <= oy + fh;
    }

    static bool Overlaps(int x, int y, int w, int h, int ox, int oy, int fw, int fh, out int rox, out int roy)
    {
        rox = ox; roy = oy;
        return x < ox + fw && x + w > ox && y < oy + fh && y + h > oy;
    }

    // ---- loading ----------------------------------------------------------------------

    static Art? Get(string prefix, string key, int fw, int fh)
    {
        string ck = prefix + key;
        if (_dirty) { _dirty = false; _cache.Clear(); ReloadCount++; }
        if (_cache.TryGetValue(ck, out var a))
        {
            // Cheap staleness check for the no-watcher case, and a second line of defence when
            // the watcher fires before the editor has finished writing the file.
            try { if (File.GetLastWriteTimeUtc(a.Path) == a.Stamp) return a; }
            catch { return a; }
            _cache.Remove(ck);
        }

        var path = Path.Combine(Folder, prefix + key + ".png");
        var art = new Art { Path = path };
        _cache[ck] = art;
        if (!File.Exists(path)) { art.Error = ""; return null; }   // no override for this room: normal, silent

        try { art.Stamp = File.GetLastWriteTimeUtc(path); } catch { }
        if (!PngReader.TryDecodeRgba(path, out int w, out int h, out var rgba, out var err))
        {
            // A file that is being written right now reads as corrupt. Do not latch that as a
            // permanent refusal — leave it out of the cache so the next frame tries again.
            _cache.Remove(ck);
            NoteOnce($"{prefix}{key}.png could not be read: {err}");
            return null;
        }
        if (w != fw || h != fh)
        {
            art.Error = "size";
            NoteOnce($"{prefix}{key}.png is {w}x{h} but this room's framebuffer is {fw}x{fh} — ignored, " +
                     "original art kept (export the plate to get the exact size)");
            return null;
        }

        art.W = w; art.H = h;
        art.Px = new ushort[w * h];
        art.Opaque = new bool[w * h];
        art.AllOpaque = true;
        for (int i = 0; i < w * h; i++)
        {
            byte r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2], al = rgba[i * 4 + 3];
            art.Px[i] = (ushort)((r >> 3) | ((g >> 3) << 5) | ((b >> 3) << 10));
            art.Opaque[i] = al >= 128;
            if (!art.Opaque[i]) art.AllOpaque = false;
        }
        _applied.Remove(ck);    // so a reload announces itself once, per version
        Console.WriteLine($"[art] loaded {prefix}{key}.png ({w}x{h})");
        return art;
    }

    static void NoteOnce(string msg) => ModLog.Once("art", msg);
}
