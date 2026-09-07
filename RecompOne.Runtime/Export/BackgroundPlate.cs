using System.Text.Json;
using RecompOne.Runtime.Host;

namespace RecompOne.Runtime.Export;

/// <summary>Writes the pre-rendered background plate of a fixed-camera room as an RGBA PNG
/// with the CHARACTERS MASKED OUT, using the same classification <see cref="GltfWriter"/>
/// already tags geometry with.
///
/// <para>WHY A MASK AND NOT A TIMING WINDOW. The obvious approach — screenshot the room
/// before the actors spawn — was tried on PE1 and does not work: in the one room measured
/// (opera exterior, 0xA8001048) frames 30 and 120 after the warp are still on the loading
/// overlay, and by the time the background is reliably decoded the characters are already
/// posed in it. There may be no characters-free frame at all in a given room, so the plate
/// has to be separated by WHAT DREW EACH PIXEL rather than by when it was drawn.</para>
///
/// <para>HOW THE SEPARATION WORKS. <see cref="GteCapture"/> correlates every triangle the
/// GPU receives with the GTE transform that produced its vertices. A triangle whose vertices
/// came out of the GTE is 3D geometry — a character, a prop. A triangle with no GTE match at
/// any vertex was submitted in screen space: that is the pre-rendered plate (and the 2D HUD),
/// and it is exactly the geometry <c>GltfWriter</c> emits as the <c>background_screenspace</c>
/// primitive. So: keep pixels the screen-space geometry covered, drop pixels the 3D geometry
/// covered.</para>
///
/// <para>COORDINATES. Captured vertex X/Y are ABSOLUTE VRAM coordinates (the draw offset is
/// already applied by <c>GpuRaster</c>), so pixels are read from the buffer the capture was
/// drawn into — <see cref="GteCapture.DrawOriginX"/>/<c>Y</c> — NOT from the display origin,
/// which is the other buffer while a double-buffered game is mid-frame.</para>
///
/// <para>CAPTURE EXACTLY ONE FRAME. A multi-frame capture window mixes both buffers of a
/// double-buffered game and both poses of a moving character; the mask is only meaningful for
/// the single frame whose pixels are being read.</para>
///
/// <para>Nothing here is game-specific.</para></summary>
public static class BackgroundPlate
{
    /// <summary>Counts from one export, so the result is auditable rather than trusted. All
    /// pixel counts are within the WxH written.</summary>
    public readonly record struct Stats(
        int Width, int Height, int OriginX, int OriginY,
        int BackgroundTris, int ForegroundTris, int AmbiguousTris,
        int BackgroundPixels, int MaskedPixels, int KeptPixels, string Rule, string PixelSource,
        int LatePatches, int LatePatchPixels, int LatePatchesDropped,
        int DriftPixels, int DriftX0, int DriftY0, int DriftX1, int DriftY1);

    // ---- pre-geometry snapshot, then late-plate patching ------------------------------
    // A fixed-camera game lays the pre-rendered plate down FIRST and then draws characters over
    // it, so the framebuffer is briefly the plate and nothing else. Masking alone can only cut
    // the characters OUT — it can never recover the plate underneath them, because those pixels
    // were overwritten. Snapshotting at the first triangle of the frame recovers them.
    //
    // BUT "the plate is finished before the first 3D triangle" is NOT true in every room, and
    // the failure is silent. Measured on PE1 map 2 (0xA8000148): the lit awning across the top
    // of the theatre frontage is put into the framebuffer AFTER the first characters, so the
    // pre-geometry snapshot caught the plate mid-draw and the awning exported BLACK — while the
    // export still reported "71680/71680 pixels kept", because a pixel counter cannot see a
    // plate that was never finished. Do not trust that number; see DriftPixels below.
    //
    // So the snapshot is a BASE, not the answer. Every framebuffer write that is not 3D geometry
    // and that happens after the base snapshot is read back and patched into it:
    //   * image transfers   (GP0 0xA0 / libgpu LoadImage / GPU DMA)  -> Gpu.OnImageUpload
    //   * VRAM->VRAM blits  (GP0 0x80 / MoveImage)                   -> Gpu.OnVramCopy
    //   * screen-space triangles drawn after 3D began                -> GteCapture.OnLatePlateTriangle
    // Reading the rect back straight after it lands is what makes this correct: the patch holds
    // exactly what that operation painted, so a character drawn into the same rect earlier in
    // the frame is overwritten by it (as it is on screen) and one drawn later never enters it.
    static ushort[]? _snapVram;
    static int _snapOx, _snapOy;
    static bool _snapArmed;
    static ushort[] _rectBuf = Array.Empty<ushort>();
    static readonly List<(int X, int Y, int W, int H, string Kind)> _latePatches = new();
    static int _lateDropped;

    /// <summary>Ceiling on patched regions, so a game that streams hundreds of small transfers
    /// per frame cannot turn one export into hundreds of VRAM readbacks. Overflow is counted and
    /// reported rather than hidden.</summary>
    public const int MaxLatePatches = 512;

    /// <summary>Log every framebuffer write seen during the capture, with its rect and whether it
    /// landed before or after the base snapshot. The diagnostic for "the plate looks wrong and I
    /// need to know what drew the missing part".</summary>
    public static bool Verbose;

    /// <summary>Arm a VRAM snapshot taken the instant before the frame's first triangle, plus
    /// the late-plate patching described above. Call after arming <see cref="GteCapture"/> for
    /// the frame you intend to export; both are consumed by the next <see cref="Save"/>.</summary>
    public static void ArmPreGeometrySnapshot()
    {
        _snapVram = null;
        _snapArmed = true;
        _latePatches.Clear();
        _lateDropped = 0;
        _lateSprites = 0;
        _spriteGroups.Clear();
        _fgRects.Clear();
        _bgOnlyVram = null;
        GteCapture.BeforeFirstTriangle = () =>
        {
            if (!_snapArmed || Runtime.Gpu is not { } g) return;
            _snapArmed = false;
            _snapOx = g.DrawOffsetX; _snapOy = g.DrawOffsetY;
            _snapVram = ReadVram(g);
            _bgOnlyVram = (ushort[])_snapVram.Clone();
            if (Verbose) Console.WriteLine($"[bg] base snapshot at first triangle, draw origin=({_snapOx},{_snapOy})");
        };
        Gpu.OnImageUpload += OnUpload;
        Gpu.OnVramCopy += OnCopy;
        Gpu.OnRectDrawn += OnRect;
        GteCapture.OnLatePlateTriangle = OnLateTri;
    }

    static void OnUpload(int x, int y, int w, int h) => Patch(x, y, w, h, "upload");
    static void OnCopy(int x, int y, int w, int h) => Patch(x, y, w, h, "vramCopy");
    static void OnLateTri(int x, int y, int w, int h) => Patch(x, y, w, h, "screenSpaceTri");

    /// <summary>Sprites. This is where PE1's late backdrop pieces actually come from — and where
    /// the game's 2D HUD comes from too, which is the whole difficulty. Both are untransformed
    /// rectangles submitted after the characters; nothing about the primitive says which is
    /// which. What separates them is the TEXTURE PAGE: a foreground piece of the room samples
    /// the room's own background pages, the dialogue box and its glyphs sample the font/window
    /// pages. <see cref="SpriteTPages"/> lists the pages a caller wants treated as backdrop, and
    /// when it is empty NO sprite is folded in — so the default behaviour of this exporter is
    /// unchanged and a caller has to opt in with knowledge it actually has.</summary>
    static void OnRect(int x, int y, int w, int h, bool textured, ushort tpage, ushort clut)
    {
        if (_snapVram == null) return;                       // pre-snapshot: already in the base
        _lateSprites++;
        var key = (tpage, w, h, textured);
        if (_spriteGroups.TryGetValue(key, out var g))
            _spriteGroups[key] = (g.N + 1, Math.Min(g.X0, x), Math.Min(g.Y0, y),
                                  Math.Max(g.X1, x + w - 1), Math.Max(g.Y1, y + h - 1), g.Folded);
        else
            _spriteGroups[key] = (1, x, y, x + w - 1, y + h - 1, false);
        if (Verbose) Console.WriteLine($"[bg]   sprite {w}x{h} @({x},{y}) tex={textured} tpage=0x{tpage:X4} clut=0x{clut:X4}");

        if (SpriteIsBackdrop == null || !SpriteIsBackdrop(x, y, w, h, textured, tpage, clut)) return;
        var f = _spriteGroups[key]; _spriteGroups[key] = (f.N, f.X0, f.Y0, f.X1, f.Y1, true);
        Patch(x, y, w, h, $"sprite tpage=0x{tpage:X4}", isSprite: true);
    }

    /// <summary>Decides whether a sprite drawn after the pre-geometry snapshot is part of the
    /// room's backdrop (fold it into the plate) or of the game's 2D overlay (leave it out).
    /// Arguments: x, y, w, h in absolute VRAM pixels, then textured, texture page, CLUT.
    ///
    /// <para>The engine deliberately has no default here. Nothing about a sprite primitive says
    /// which it is — PE1's theatre awning and PE1's dialogue glyphs are both untransformed
    /// textured rectangles submitted after the characters — and a wrong guess bakes a dialogue
    /// box into a room's art without any counter noticing. So: null (the default) folds in NO
    /// sprites, which is exactly the behaviour this exporter had before sprites were hooked, and
    /// a game host opts in with knowledge it actually has. Run with <c>--bg-trace</c> to get the
    /// per-texture-page sprite census the decision should be based on.</para></summary>
    public static Func<int, int, int, int, bool, ushort, ushort, bool>? SpriteIsBackdrop;

    static int _lateSprites;
    static readonly Dictionary<(ushort TPage, int W, int H, bool Tex), (int N, int X0, int Y0, int X1, int Y1, bool Folded)> _spriteGroups = new();

    static void Unhook()
    {
        Gpu.OnImageUpload -= OnUpload;
        Gpu.OnVramCopy -= OnCopy;
        Gpu.OnRectDrawn -= OnRect;
        GteCapture.OnLatePlateTriangle = null;
        GteCapture.BeforeFirstTriangle = null;
    }

    /// <summary>Fold one late framebuffer write into the base snapshot.
    ///
    /// <para>A write from a SPRITE lands in the flattened plate only. Sprites arriving after the
    /// snapshot are, by construction, drawn after the frame's first triangle — i.e. after the
    /// characters — so they are the room's FOREGROUND layer, and keeping a copy of the plate
    /// without them is what lets the two be exported separately. Transfers and blits go into
    /// both: those are background, and a game that finishes uploading its backdrop late has not
    /// thereby created a foreground.</para></summary>
    static void Patch(int x, int y, int w, int h, string kind, bool isSprite = false)
    {
        // Before the base snapshot exists this write is already part of it — nothing to do.
        if (_snapVram is not { } snap) { if (Verbose) Console.WriteLine($"[bg]   early {kind} {w}x{h} @({x},{y})"); return; }
        if (Runtime.Gpu is not { } gpu) return;
        if (w <= 0 || h <= 0 || w > Gpu.VramWidth || h > Gpu.VramHeight) return;
        if (_latePatches.Count >= MaxLatePatches) { _lateDropped++; return; }

        int n = w * h;
        if (_rectBuf.Length < n) _rectBuf = new ushort[n];
        gpu.ReadVramRect(x, y, w, h, _rectBuf);
        var bgOnly = isSprite ? null : _bgOnlyVram;
        for (int row = 0; row < h; row++)
        {
            int dy = (y + row) & (Gpu.VramHeight - 1);
            for (int col = 0; col < w; col++)
            {
                int di = dy * Gpu.VramWidth + ((x + col) & (Gpu.VramWidth - 1));
                snap[di] = _rectBuf[row * w + col];
                if (bgOnly != null) bgOnly[di] = _rectBuf[row * w + col];
            }
        }
        if (isSprite) _fgRects.Add((x, y, w, h));
        _latePatches.Add((x, y, w, h, kind));
        if (Verbose) Console.WriteLine($"[bg]   LATE {kind} {w}x{h} @({x},{y}) patched into plate");
    }

    /// <summary>The plate as it stood before any foreground sprite was folded in — the background
    /// layer, complete, because the foreground is drawn over it later and the snapshot predates
    /// that.</summary>
    static ushort[]? _bgOnlyVram;

    /// <summary>Framebuffer rects the foreground layer covers, in absolute VRAM pixels.</summary>
    static readonly List<(int X, int Y, int W, int H)> _fgRects = new();

    /// <summary>A triangle is treated as 3D (foreground) when at least this many of its three
    /// vertices resolved to a GTE transform. 2 rather than 1 because the GTE↔GPU correlation
    /// is a screen-position match: a single background vertex that happens to land on the same
    /// rounded pixel as a model vertex would otherwise condemn a whole background quad. On the
    /// room measured so far every captured triangle was 3/3 or 0/3, so this only matters as a
    /// guard.</summary>
    public const int ForegroundVertexThreshold = 2;

    /// <summary>Grow the character mask by this many pixels. The GPU's rasterisation fill rule
    /// and the integer vertex coordinates leave a thin rim of character pixels just outside the
    /// triangle the exporter reconstructs; without the dilation those survive as a halo.</summary>
    public const int MaskDilation = 1;

    /// <summary>Write the plate. Returns false (and writes nothing) if there is no GPU, no
    /// display, or no captured geometry to classify — never a half-written file.</summary>
    public static bool Save(string pngPath, out Stats stats, string? sidecarJsonPath = null)
    {
        stats = default;
        var gpu = Runtime.Gpu;
        if (gpu == null) return false;
        int w = gpu.DisplayWidth, h = gpu.DisplayHeight;
        if (w <= 0 || h <= 0) return false;

        var tris = GteCapture.Triangles;
        if (tris.Count == 0) return false;

        int ox = GteCapture.HasDrawOrigin ? GteCapture.DrawOriginX : gpu.DisplayX;
        int oy = GteCapture.HasDrawOrigin ? GteCapture.DrawOriginY : gpu.DisplayY;

        var bg = new bool[w * h];
        var fg = new bool[w * h];
        int bgTris = 0, fgTris = 0, ambTris = 0;

        foreach (var t in tris)
        {
            int hits = (t.A.Has3D ? 1 : 0) + (t.B.Has3D ? 1 : 0) + (t.C.Has3D ? 1 : 0);
            if (hits >= ForegroundVertexThreshold)
            {
                fgTris++;
                Fill(fg, w, h, t, ox, oy);
            }
            else if (hits == 0)
            {
                bgTris++;
                Fill(bg, w, h, t, ox, oy);
            }
            else
            {
                // One resolved vertex out of three: not confidently either. Counted and
                // reported, deliberately not painted into either mask.
                ambTris++;
            }
        }

        if (MaskDilation > 0) Dilate(fg, w, h, MaskDilation);

        int bgPixels = Count(bg);
        // If the game's plate really is screen-space geometry, intersecting with it also drops
        // letterboxing and anything the game never drew this frame. If it is NOT — a game that
        // DMAs its background straight into the framebuffer submits no background triangles at
        // all — that intersection would delete the whole image, so fall back to "everything the
        // 3D geometry did not cover" and SAY which rule produced the file.
        bool useBgMask = bgPixels * 20 >= w * h;   // >= 5% of the frame
        string rule = useBgMask
            ? "kept = covered by screen-space geometry AND not covered by GTE 3D geometry"
            : "kept = not covered by GTE 3D geometry (screen-space geometry covered only " +
              $"{bgPixels} px, too little to mask by — this game may not draw its plate as GPU primitives)";

        // If a pre-geometry snapshot was taken, its pixels ARE the plate — the characters had
        // not been drawn yet — so every pixel is kept and the mask is only reported, not applied.
        // Without one, all that can be done is cut the characters out and leave holes.
        bool preGeom = _snapVram != null;
        var vram = preGeom ? _snapVram! : ReadVram(gpu);
        if (preGeom) { ox = _snapOx; oy = _snapOy; }
        int latePx = 0;
        foreach (var p in _latePatches) latePx += p.W * p.H;
        string pixelSource = preGeom
            ? $"framebuffer snapshot taken before the frame's first triangle, with {_latePatches.Count} " +
              "late framebuffer write(s) read back and patched in (plate complete, no holes)"
            : "end-of-frame framebuffer (characters already composited; masked pixels are holes)";

        // THE CHECK THAT CAN ACTUALLY SEE A BROKEN PLATE. "kept pixels" cannot: it counts what
        // was written, not whether what was written was finished. This does: compare the plate
        // against the FINAL frame everywhere the 3D geometry did NOT draw. Those pixels should
        // agree — if they do not, something painted the framebuffer after the snapshot that the
        // patching above did not fold in. Some disagreement is expected and legitimate (a HUD or
        // dialogue box is drawn late and is correctly absent from the plate), so this is reported
        // with its bounding box for a human to look at, not asserted on.
        int drift = 0, dx0 = int.MaxValue, dy0 = int.MaxValue, dx1 = -1, dy1 = -1;
        if (preGeom)
        {
            var final = ReadVram(gpu);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (fg[y * w + x]) continue;
                    int vy = (oy + y) & (Gpu.VramHeight - 1), vx = (ox + x) & (Gpu.VramWidth - 1);
                    if (vram[vy * Gpu.VramWidth + vx] == final[vy * Gpu.VramWidth + vx]) continue;
                    drift++;
                    if (x < dx0) dx0 = x; if (y < dy0) dy0 = y;
                    if (x > dx1) dx1 = x; if (y > dy1) dy1 = y;
                }
            if (drift == 0) { dx0 = dy0 = 0; }
        }
        else { dx0 = dy0 = 0; }

        var raw = new byte[(w * 4 + 1) * h];
        int o = 0, kept = 0, masked = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0; // filter: none
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                bool keep = preGeom || (!fg[i] && (!useBgMask || bg[i]));
                if (keep) kept++; else masked++;
                var (r, g, b) = Sample(gpu, vram, ox, oy, x, y);
                raw[o++] = r; raw[o++] = g; raw[o++] = b;
                raw[o++] = keep ? (byte)255 : (byte)0;
            }
        }

        PngWriter.WriteRgba(pngPath, w, h, raw);

        // LAYERS. A flat plate is an incomplete description of a PE1 room: part of the backdrop
        // is drawn AFTER the characters so that it occludes them (the limo's open door in map 10,
        // the theatre awning in map 2). Flattening those into one image loses the fact that they
        // are in front, and anyone repainting the flat image cannot tell which pixels those are.
        // So write the same frame as two editable layers beside it:
        //   <name>_bg.png  everything behind the characters   (opaque)
        //   <name>_fg.png  everything in front of them        (opaque only where it covers)
        // The background layer is complete under the foreground — the base snapshot predates the
        // foreground sprites, so nothing had to be reconstructed.
        int fgPixels = 0;
        if (preGeom && _fgRects.Count > 0 && _bgOnlyVram != null)
        {
            var fgMask = new bool[w * h];
            foreach (var (rx, ry, rw, rh) in _fgRects)
                for (int yy = 0; yy < rh; yy++)
                {
                    int fy = ry + yy - oy;
                    if ((uint)fy >= (uint)h) continue;
                    for (int xx = 0; xx < rw; xx++)
                    {
                        int fx = rx + xx - ox;
                        if ((uint)fx < (uint)w) fgMask[fy * w + fx] = true;
                    }
                }

            var bgRaw = new byte[(w * 4 + 1) * h];
            var fgRaw = new byte[(w * 4 + 1) * h];
            int bo = 0, fo = 0;
            for (int y = 0; y < h; y++)
            {
                bgRaw[bo++] = 0; fgRaw[fo++] = 0;
                for (int x = 0; x < w; x++)
                {
                    var (br, bg2, bb) = Sample(gpu, _bgOnlyVram, ox, oy, x, y);
                    bgRaw[bo++] = br; bgRaw[bo++] = bg2; bgRaw[bo++] = bb; bgRaw[bo++] = 255;
                    bool inFg = fgMask[y * w + x];
                    if (inFg) fgPixels++;
                    var (fr, fg2, fb) = Sample(gpu, vram, ox, oy, x, y);
                    fgRaw[fo++] = inFg ? fr : (byte)0;
                    fgRaw[fo++] = inFg ? fg2 : (byte)0;
                    fgRaw[fo++] = inFg ? fb : (byte)0;
                    fgRaw[fo++] = inFg ? (byte)255 : (byte)0;
                }
            }
            var stem = pngPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? pngPath[..^4] : pngPath;
            PngWriter.WriteRgba(stem + "_bg.png", w, h, bgRaw);
            PngWriter.WriteRgba(stem + "_fg.png", w, h, fgRaw);
        }

        stats = new Stats(w, h, ox, oy, bgTris, fgTris, ambTris, bgPixels, masked, kept, rule, pixelSource,
            _latePatches.Count, latePx, _lateDropped, drift, dx0, dy0, dx1, dy1);
        var patches = _latePatches.Select(p => new { kind = p.Kind, x = p.X, y = p.Y, w = p.W, h = p.H }).ToArray();
        // The sprite census. This is the evidence a SpriteIsBackdrop rule should be written
        // from, and the record of what that rule then did — so a plate that folded in the wrong
        // thing (or folded in nothing) can be diagnosed from the sidecar alone.
        var sprites = _spriteGroups
            .OrderByDescending(kv => kv.Value.N)
            .Select(kv => new
            {
                tpage = $"0x{kv.Key.TPage:X4}", w = kv.Key.W, h = kv.Key.H, textured = kv.Key.Tex,
                count = kv.Value.N,
                box = new { x0 = kv.Value.X0 - ox, y0 = kv.Value.Y0 - oy, x1 = kv.Value.X1 - ox, y1 = kv.Value.Y1 - oy },
                foldedIntoPlate = kv.Value.Folded,
            }).ToArray();
        if (Verbose)
            foreach (var s in sprites)
                Console.WriteLine($"[bg] sprites tpage={s.tpage} {s.w}x{s.h} tex={s.textured} n={s.count} " +
                                  $"frameBox=({s.box.x0},{s.box.y0})-({s.box.x1},{s.box.y1}) folded={s.foldedIntoPlate}");
        _snapVram = null; _snapArmed = false;
        Unhook();

        if (sidecarJsonPath != null)
        {
            File.WriteAllText(sidecarJsonPath, JsonSerializer.Serialize(new
            {
                what = "background plate, characters masked out by GteCapture classification",
                rule,
                pixelSource,
                foregroundVertexThreshold = ForegroundVertexThreshold,
                maskDilationPx = MaskDilation,
                width = w,
                height = h,
                vramOrigin = new { x = ox, y = oy, fromDrawOffset = GteCapture.HasDrawOrigin },
                triangles = new
                {
                    total = tris.Count,
                    screenSpaceBackground = bgTris,
                    gte3dForeground = fgTris,
                    ambiguousOneVertexResolved = ambTris,
                },
                pixels = new { backgroundCovered = bgPixels, kept, masked },
                latePlateWrites = new
                {
                    note = "framebuffer writes that landed AFTER the pre-geometry snapshot and were " +
                           "read back and patched into the plate. A room with a backdrop that finishes " +
                           "drawing after the first character has entries here; a room whose plate is " +
                           "complete up front has none.",
                    count = patches.Length,
                    pixelsCovered = latePx,
                    droppedOverCap = _lateDropped,
                    rects = patches,
                    spritesAfterSnapshot = _lateSprites,
                    spriteCensus = sprites,
                },
                layers = new
                {
                    note = "PE1 draws part of a room's backdrop AFTER the characters so that it " +
                           "occludes them (map 10's open limo door, map 2's awning). The flat plate " +
                           "shows those pixels but not the fact that they are in front. _bg.png is " +
                           "everything behind the models (complete — the snapshot predates the " +
                           "foreground, so nothing is reconstructed); _fg.png is everything in front, " +
                           "transparent where it does not cover.",
                    foregroundPixels = fgPixels,
                    foregroundRects = _fgRects.Count,
                    written = fgPixels > 0,
                },
                plateVsFinalFrame = new
                {
                    note = "pixels where the exported plate disagrees with the end-of-frame framebuffer " +
                           "OUTSIDE the 3D geometry mask. Expected to be non-zero only where the game " +
                           "legitimately drew 2D over the plate late (HUD, dialogue box). A large or " +
                           "structural region here means a late plate write was missed — this is the " +
                           "counter to look at, NOT 'kept', which cannot see an unfinished plate.",
                    differingPixels = drift,
                    boundingBox = drift > 0 ? new { x0 = dx0, y0 = dy0, x1 = dx1, y1 = dy1 } : null,
                },
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return true;
    }

    static ushort[] ReadVram(Gpu gpu)
    {
        if (!Hle.GpuHle.Active || Hle.GpuHle.Backend is not { } be) return gpu.Vram;
        // Hardware-accelerated HLE GPU: VRAM lives on the GL side (same as Host.Screenshot).
        var v = new ushort[Gpu.VramWidth * Gpu.VramHeight];
        be.ReadVram(0, 0, Gpu.VramWidth, Gpu.VramHeight, v);
        return v;
    }

    // Mirrors Host.Screenshot's decode, including the 24-bit packed-RGB case (decoding a
    // 24-bit framebuffer as 15-bit produces striping that reads as VRAM corruption).
    //
    // CAVEAT, stated rather than hidden: in 24-bit mode a display pixel is 1.5 halfwords, so
    // GPU primitive coordinates (always 16-bit VRAM pixels) no longer line up 1:1 with display
    // pixels and the geometry mask would be horizontally scaled. This does not arise for a
    // rasterised game frame — the PS1 GPU cannot draw polygons into a 24-bit framebuffer, so any
    // frame with character geometry in it is 15-bit — but a 24-bit frame here means the caller
    // caught an MDEC/FMV frame and the mask should not be trusted; the pixel decode stays
    // correct so the image itself is still right.
    static (byte, byte, byte) Sample(Gpu gpu, ushort[] vram, int ox, int oy, int x, int y)
    {
        int vy = (oy + y) & (Gpu.VramHeight - 1);
        if (gpu.Display24Bit)
        {
            int bo = (vy * Gpu.VramWidth + ox) * 2 + x * 3;
            return (VramByte(vram, bo), VramByte(vram, bo + 1), VramByte(vram, bo + 2));
        }
        ushort p = vram[vy * Gpu.VramWidth + ((ox + x) & (Gpu.VramWidth - 1))];
        return ((byte)((p & 0x1F) << 3), (byte)(((p >> 5) & 0x1F) << 3), (byte)(((p >> 10) & 0x1F) << 3));
    }

    static byte VramByte(ushort[] vram, int byteOffset)
    {
        int hw = (byteOffset >> 1) & (Gpu.VramWidth * Gpu.VramHeight - 1);
        ushort v = vram[hw];
        return (byte)((byteOffset & 1) == 0 ? v & 0xFF : v >> 8);
    }

    /// <summary>Scanline fill of one captured triangle into a boolean mask, in framebuffer
    /// coordinates (absolute VRAM minus the buffer origin). Half-open on the right/bottom edge
    /// so adjacent triangles of a strip tile without double-covering.</summary>
    static void Fill(bool[] mask, int w, int h, GteCapture.CapturedTri t, int ox, int oy)
    {
        float ax = t.A.X - ox, ay = t.A.Y - oy;
        float bx = t.B.X - ox, by = t.B.Y - oy;
        float cx = t.C.X - ox, cy = t.C.Y - oy;

        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))));
        int maxX = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))));
        int maxY = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
        if (minX > maxX || minY > maxY) return;

        float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        if (MathF.Abs(area) < 1e-6f) return;   // degenerate: no coverage
        float inv = 1f / area;

        for (int y = minY; y <= maxY; y++)
        {
            float py = y + 0.5f;
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f;
                float u = ((bx - ax) * (py - ay) - (by - ay) * (px - ax)) * inv;
                float v = ((px - ax) * (cy - ay) - (py - ay) * (cx - ax)) * inv;
                if (u >= 0f && v >= 0f && u + v <= 1f) mask[y * w + x] = true;
            }
        }
    }

    static void Dilate(bool[] mask, int w, int h, int radius)
    {
        var src = (bool[])mask.Clone();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!src[y * w + x]) continue;
                for (int dy = -radius; dy <= radius; dy++)
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((uint)nx < (uint)w && (uint)ny < (uint)h) mask[ny * w + nx] = true;
                    }
            }
    }

    static int Count(bool[] mask)
    {
        int n = 0;
        foreach (var b in mask) if (b) n++;
        return n;
    }
}
