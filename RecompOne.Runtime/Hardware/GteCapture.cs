using RecompOne.Runtime.Hle;

namespace RecompOne.Runtime;

/// <summary>
/// Generic GTE-vertex capture. Correlates the GTE's real 3D transform
/// (<see cref="Gte"/>'s RTPS/RTPT, which computes screen XY from a model-space vertex via the
/// loaded rotation matrix + translation) with the screen-space triangles the game later submits
/// to the GPU (<c>Gpu.HleTri</c>) — the GPU itself only ever sees 2D screen coordinates, so this
/// is the only place the original 3D geometry can be recovered.
///
/// Off by default (<see cref="Enabled"/> = false) — both hook call sites are single gated lines,
/// so a port that never sets this flag is byte-identical to before this existed.
///
/// Nothing here is PS1-game-specific; a game host (e.g. ParasiteEve.PC) flips <see cref="Enabled"/>
/// for a capture window, reads <see cref="Triangles"/>, and hands them to a writer such as
/// <see cref="Export.GltfWriter"/>.
/// </summary>
public static class GteCapture
{
    public static bool Enabled;

    /// <summary>One GTE RTP output: the pre-perspective-divide position (camera/view space —
    /// RT*vertex + TR, NOT world space unless the caller only ever loads a world matrix) plus
    /// the model-space input and the rotation/translation active at capture time, so a consumer
    /// can later reconstruct a different space if it turns out to matter.</summary>
    public readonly record struct Vtx(int Wx, int Wy, int Wz, short Mx, short My, short Mz, short[] Rot, int[] Trans);

    public readonly record struct CapturedVertex(
        float X, float Y, byte R, byte G, byte B, short U, short V,
        bool Has3D, int Wx, int Wy, int Wz, short[]? Rot, int[]? Trans);

    /// <summary>One submitted triangle plus everything needed to reproduce its texel fetches.
    /// <paramref name="TexWin"/> and <paramref name="SemiTrans"/> exist because neither is
    /// recoverable after the fact: the GPU applies the texture window to UVs BEFORE the fetch, so
    /// a material baked without it samples the wrong part of the page, and the per-texel STP bit
    /// only means "translucent" when the primitive itself was semi-transparent. Both are part of
    /// the material identity, not decoration — see <see cref="Export.TextureBaker"/>.</summary>
    public readonly record struct CapturedTri(
        CapturedVertex A, CapturedVertex B, CapturedVertex C,
        bool Textured, ushort TPage, ushort Clut, uint TexWin, bool SemiTrans);

    public static readonly List<CapturedTri> Triangles = new();

    // Stats for the hit/miss report a consumer should always surface (see project notes: this
    // FIFO-by-screen-position match is a heuristic, not a guarantee — instrument it, don't trust it blindly).
    public static int TrisFullyResolved, TrisPartial, TrisUnresolved;

    /// <summary>GPU draw offset in effect while these triangles were captured, i.e. the VRAM
    /// origin of the buffer they were drawn INTO. Captured vertex X/Y are absolute VRAM
    /// coordinates (<c>GpuRaster</c> adds the draw offset before the primitive is forwarded),
    /// so anything correlating the capture with framebuffer PIXELS must subtract this — the
    /// display origin is the OTHER buffer while a double-buffered game is mid-frame.</summary>
    public static int DrawOriginX, DrawOriginY;
    public static bool HasDrawOrigin;

    /// <summary>The GTE projection in force during the capture: <c>screenX = Wx*H/Wz + OfxFixed/65536</c>
    /// (and likewise Y). Recorded because captured positions are pre-perspective-divide view space,
    /// so without these a consumer cannot map the geometry back onto the frame — and a fixed-camera
    /// game sets them PER CAMERA, so a value guessed from one room is wrong in the next.</summary>
    public static int ProjH, ProjOfxFixed, ProjOfyFixed;

    // PS1 models are overwhelmingly triangle strips/fans: a vertex is transformed ONCE by the GTE
    // but consumed by several triangles that share it. A consuming FIFO (dequeue-on-match) starves
    // every triangle after the first one to reference a shared vertex -> "partial" triangles whose
    // missing vertex was never actually unresolved, just already eaten. Fix: keep the LAST GTE
    // output seen at each screen position as a standing (non-consumed) mapping, so every triangle
    // that reuses that screen vertex resolves it. Trade-off (flagged, not hidden): two genuinely
    // different vertices from different objects that land on the identical rounded pixel will
    // collide and the second overwrites the first — rare compared to the shared-vertex case this
    // fixes, but real; a sequence/ordering key would be strictly better if one becomes available.
    static readonly Dictionary<(short sx, short sy), Vtx> _bySxSy = new();

    public static void OnRtp(int sx, int sy, int wx, int wy, int wz, short mx, short my, short mz, short[] rot, int[] trans)
    {
        if (!Enabled) return;
        // Sample the projection HERE, not at submission. A game that transforms into an ordering
        // table and submits it at the next vblank (PE1) has already reloaded these registers for
        // the following frame by the time the triangle arrives, so reading them in OnTriangle
        // captures the wrong camera: measured on the Theatre rooms, submission-time reads gave
        // h=690 and put the geometry a full 224 px (one back-buffer height) below the frame.
        ProjH = Gte.ProjH; ProjOfxFixed = Gte.ProjOfxFixed; ProjOfyFixed = Gte.ProjOfyFixed;
        _bySxSy[((short)sx, (short)sy)] = new Vtx(wx, wy, wz, mx, my, mz, rot, trans);
    }

    /// <summary>Vertices resolved by the raw GTE screen position vs. by that position plus the
    /// GPU draw offset. The GTE emits screen XY with NO draw offset; the GPU's own vertices have
    /// it added (<c>GpuRaster</c>: <c>v.X = _drawOffsetX + CoordX(vw)</c>), so a double-buffered
    /// game whose back buffer is at y=224 misses every direct match on alternate frames. Measured
    /// on PE1: a single-frame capture taken while the draw offset was (0,224) resolved 0 of 1124
    /// triangles before this second key existed.</summary>
    public static int VertsResolvedDirect, VertsResolvedViaDrawOffset;

    static CapturedVertex Resolve(HleVertex v, int ox, int oy)
    {
        if (_bySxSy.TryGetValue(((short)v.X, (short)v.Y), out var w))
        {
            VertsResolvedDirect++;
            return new CapturedVertex(v.X, v.Y, v.R, v.G, v.B, v.U, v.V, true, w.Wx, w.Wy, w.Wz, w.Rot, w.Trans);
        }
        if ((ox != 0 || oy != 0) && _bySxSy.TryGetValue(((short)(v.X - ox), (short)(v.Y - oy)), out w))
        {
            VertsResolvedViaDrawOffset++;
            return new CapturedVertex(v.X, v.Y, v.R, v.G, v.B, v.U, v.V, true, w.Wx, w.Wy, w.Wz, w.Rot, w.Trans);
        }
        return new CapturedVertex(v.X, v.Y, v.R, v.G, v.B, v.U, v.V, false, 0, 0, 0, null, null);
    }

    /// <summary>Invoked once, just before the FIRST triangle of a capture window is recorded.
    /// A fixed-camera game lays its pre-rendered background down before any 3D geometry, so this
    /// is the instant at which the framebuffer holds the plate and nothing else — see
    /// <see cref="Export.BackgroundPlate"/>. Cleared by <see cref="Reset"/>; re-armed by
    /// <see cref="ClearTriangles"/> so a warm-up frame does not consume it.</summary>
    public static Action? BeforeFirstTriangle;
    static bool _firstFired;

    /// <summary>True once a triangle classified as 3D (at least <see cref="Foreground3DVertices"/>
    /// of its vertices resolved to a GTE transform) has been submitted in this capture window.
    /// Reset by <see cref="ClearTriangles"/>.</summary>
    public static bool Seen3D { get; private set; }

    /// <summary>Vertices that must resolve to the GTE before a triangle counts as 3D. 2 rather
    /// than 1 because the correlation is a screen-position match, so one background vertex that
    /// happens to land on a model vertex's rounded pixel must not condemn a whole quad.</summary>
    public const int Foreground3DVertices = 2;

    /// <summary>Fired immediately AFTER a NON-3D (screen-space) triangle has been rasterised,
    /// but only once <see cref="Seen3D"/> is true — i.e. a 2D primitive laid down LATE, over
    /// geometry. Argument is the triangle's framebuffer bounding box in absolute VRAM pixels
    /// (x, y, w, h).
    ///
    /// <para>This exists because "the plate is everything drawn before the first 3D triangle"
    /// is not true in every room: a game can finish part of its backdrop after it has started
    /// on characters, and a pre-geometry snapshot then catches that part mid-draw. The hook
    /// fires post-raster (not from <see cref="OnTriangle"/>, which runs before the draw) so a
    /// listener can read back what was actually painted.</para></summary>
    public static Action<int, int, int, int>? OnLatePlateTriangle;
    static bool _latePending;
    static int _lateX, _lateY, _lateW, _lateH;

    /// <summary>Called by the GPU immediately after a captured triangle has been rasterised.
    /// Separate from <see cref="OnTriangle"/>, which necessarily runs before the draw.</summary>
    public static void AfterTriangleDrawn()
    {
        if (!_latePending) return;
        _latePending = false;
        OnLatePlateTriangle?.Invoke(_lateX, _lateY, _lateW, _lateH);
    }

    public static void OnTriangle(HleVertex a, HleVertex b, HleVertex c, bool textured, ushort tpage, ushort clut,
        uint texWin = 0, bool semiTrans = false)
    {
        if (!Enabled) return;
        if (!_firstFired) { _firstFired = true; BeforeFirstTriangle?.Invoke(); }
        int ox = 0, oy = 0;
        if (Runtime.Gpu is { } gpu) { ox = DrawOriginX = gpu.DrawOffsetX; oy = DrawOriginY = gpu.DrawOffsetY; HasDrawOrigin = true; }
        var ca = Resolve(a, ox, oy); var cb = Resolve(b, ox, oy); var cc = Resolve(c, ox, oy);
        int hits = (ca.Has3D ? 1 : 0) + (cb.Has3D ? 1 : 0) + (cc.Has3D ? 1 : 0);
        if (hits == 3) TrisFullyResolved++;
        else if (hits == 0) TrisUnresolved++;
        else TrisPartial++;
        if (hits >= Foreground3DVertices) Seen3D = true;
        else if (Seen3D && OnLatePlateTriangle != null)
        {
            int x0 = (int)MathF.Floor(MathF.Min(ca.X, MathF.Min(cb.X, cc.X)));
            int y0 = (int)MathF.Floor(MathF.Min(ca.Y, MathF.Min(cb.Y, cc.Y)));
            int x1 = (int)MathF.Ceiling(MathF.Max(ca.X, MathF.Max(cb.X, cc.X)));
            int y1 = (int)MathF.Ceiling(MathF.Max(ca.Y, MathF.Max(cb.Y, cc.Y)));
            _lateX = x0; _lateY = y0; _lateW = x1 - x0 + 1; _lateH = y1 - y0 + 1;
            _latePending = true;
        }
        var tri = new CapturedTri(ca, cb, cc, textured, tpage, clut, texWin, semiTrans);
        if (GteAccumulator.Enabled)
            GteAccumulator.Accumulate(tri); // multi-frame model-space union (recovers backface-culled geometry)
        else
            Triangles.Add(tri); // single-frame snapshot path (kept for the original one-shot --export-room mode)
    }

    /// <summary>Drop the captured triangles but KEEP the accumulated GTE screen-position map.
    ///
    /// <para>Needed by any consumer that wants exactly one frame of triangles WITH their 3D
    /// correlation. A game that transforms its models into an ordering table and submits that
    /// table at the next vblank (PE1 does) performs the GTE work for a frame BEFORE the frame
    /// whose triangles carry it — so a capture armed with a full <see cref="Reset"/> and closed
    /// one frame later sees the triangles with an empty map and resolves nothing. Arm, let a
    /// frame go by to fill the map, then <c>ClearTriangles</c> and take the next frame.</para></summary>
    public static void ClearTriangles()
    {
        Triangles.Clear();
        TrisFullyResolved = TrisPartial = TrisUnresolved = 0;
        VertsResolvedDirect = VertsResolvedViaDrawOffset = 0;
        _firstFired = false; Seen3D = false; _latePending = false;
    }

    public static void Reset()
    {
        Triangles.Clear();
        _bySxSy.Clear();
        TrisFullyResolved = TrisPartial = TrisUnresolved = 0;
        VertsResolvedDirect = VertsResolvedViaDrawOffset = 0;
        DrawOriginX = DrawOriginY = 0; HasDrawOrigin = false;
        _firstFired = false; BeforeFirstTriangle = null;
        Seen3D = false; _latePending = false; OnLatePlateTriangle = null;
    }
}
