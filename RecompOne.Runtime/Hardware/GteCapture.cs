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

    public readonly record struct CapturedTri(CapturedVertex A, CapturedVertex B, CapturedVertex C, bool Textured, ushort TPage, ushort Clut);

    public static readonly List<CapturedTri> Triangles = new();

    // Stats for the hit/miss report a consumer should always surface (see project notes: this
    // FIFO-by-screen-position match is a heuristic, not a guarantee — instrument it, don't trust it blindly).
    public static int TrisFullyResolved, TrisPartial, TrisUnresolved;

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
        _bySxSy[((short)sx, (short)sy)] = new Vtx(wx, wy, wz, mx, my, mz, rot, trans);
    }

    static CapturedVertex Resolve(HleVertex v)
    {
        var key = ((short)v.X, (short)v.Y);
        if (_bySxSy.TryGetValue(key, out var w))
            return new CapturedVertex(v.X, v.Y, v.R, v.G, v.B, v.U, v.V, true, w.Wx, w.Wy, w.Wz, w.Rot, w.Trans);
        return new CapturedVertex(v.X, v.Y, v.R, v.G, v.B, v.U, v.V, false, 0, 0, 0, null, null);
    }

    public static void OnTriangle(HleVertex a, HleVertex b, HleVertex c, bool textured, ushort tpage, ushort clut)
    {
        if (!Enabled) return;
        var ca = Resolve(a); var cb = Resolve(b); var cc = Resolve(c);
        int hits = (ca.Has3D ? 1 : 0) + (cb.Has3D ? 1 : 0) + (cc.Has3D ? 1 : 0);
        if (hits == 3) TrisFullyResolved++;
        else if (hits == 0) TrisUnresolved++;
        else TrisPartial++;
        var tri = new CapturedTri(ca, cb, cc, textured, tpage, clut);
        if (GteAccumulator.Enabled)
            GteAccumulator.Accumulate(tri); // multi-frame model-space union (recovers backface-culled geometry)
        else
            Triangles.Add(tri); // single-frame snapshot path (kept for the original one-shot --export-room mode)
    }

    public static void Reset()
    {
        Triangles.Clear();
        _bySxSy.Clear();
        TrisFullyResolved = TrisPartial = TrisUnresolved = 0;
    }
}
