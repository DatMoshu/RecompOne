namespace RecompOne.Runtime;

/// <summary>
/// Multi-frame model-space accumulation, on top of <see cref="GteCapture"/>. PS1 titles backface-cull
/// on the CPU (typically via the GTE's NCLIP), so a single frame's draw stream only ever contains the
/// polygons facing the camera at that instant — the far side of a model is never submitted at all, and
/// no amount of correlation fixing recovers data that was never sent.
///
/// Fix: every captured vertex already carries the rotation matrix R and translation TR active when the
/// GTE transformed it (view = R*v/4096 + TR). R is orthonormal, so R^-1 = R^T, and we can undo the
/// transform to recover the MODEL-SPACE vertex v, which — for a rigid object — stays constant frame to
/// frame even as the object turns or walks. Accumulate deduped model-space triangles across many frames
/// while the object moves, and the union of "whichever side faced the camera that frame" converges
/// toward the whole mesh. Distinct translations cluster into distinct objects for free (each character/
/// prop has its own R,TR per frame), which is also the mechanism that separates a room's soup of
/// triangles into individual meshes.
///
/// Generic and game-agnostic like <see cref="GteCapture"/>: off by default, a game host drives the
/// capture window (how long, whether to nudge input to encourage turning) and hands the result to a
/// writer such as <see cref="Export.GltfWriter"/>.
/// </summary>
public static class GteAccumulator
{
    public static bool Enabled;

    public sealed class ObjAccum
    {
        public double Tx, Ty, Tz; // last-seen translation; doubles as this object's anchor for reclustering
        public readonly Dictionary<(int, int, int), int> VertIndex = new();
        public readonly List<(float x, float y, float z, byte r, byte g, byte b, short u, short v)> Verts = new();
        public readonly HashSet<(int, int, int, ushort, ushort)> SeenTris = new();
        public readonly Dictionary<(bool tex, ushort tpage, ushort clut), List<uint>> Indices = new();
        public int TriCount => Indices.Values.Sum(l => l.Count / 3);
    }

    static readonly List<ObjAccum> _objects = new();
    // PS1 world units; opera-exterior's three characters stand well apart (100s of units) so this is
    // generous without merging them — tune per room if objects turn out too close together.
    const double AnchorMergeDistSq = 220.0 * 220.0;

    public static IReadOnlyList<ObjAccum> Objects => _objects;
    public static int TotalTriangles { get { int n = 0; foreach (var o in _objects) n += o.TriCount; return n; } }

    public static void Reset() => _objects.Clear();

    static ObjAccum FindOrCreate(double tx, double ty, double tz)
    {
        ObjAccum? best = null; double bestD = double.MaxValue;
        foreach (var o in _objects)
        {
            double dx = o.Tx - tx, dy = o.Ty - ty, dz = o.Tz - tz;
            double d = dx * dx + dy * dy + dz * dz;
            if (d < bestD) { bestD = d; best = o; }
        }
        if (best != null && bestD <= AnchorMergeDistSq) { best.Tx = tx; best.Ty = ty; best.Tz = tz; return best; }
        var n = new ObjAccum { Tx = tx, Ty = ty, Tz = tz };
        _objects.Add(n);
        return n;
    }

    static int AddVertex(ObjAccum o, double mx, double my, double mz, byte r, byte g, byte b, short u, short v)
    {
        var key = ((int)Math.Round(mx), (int)Math.Round(my), (int)Math.Round(mz));
        if (o.VertIndex.TryGetValue(key, out var idx)) return idx;
        idx = o.Verts.Count;
        o.Verts.Add(((float)mx, (float)my, (float)mz, r, g, b, u, v));
        o.VertIndex[key] = idx;
        return idx;
    }

    static (double, double, double) ToModel(GteCapture.CapturedVertex v)
    {
        var r = v.Rot!; var t = v.Trans!;
        double vx = v.Wx - t[0], vy = v.Wy - t[1], vz = v.Wz - t[2];
        // model = R^T * (view - TR) / 4096 (R stored row-major, 4096-scale fixed point, orthonormal)
        double mx = (r[0] * vx + r[3] * vy + r[6] * vz) / 4096.0;
        double my = (r[1] * vx + r[4] * vy + r[7] * vz) / 4096.0;
        double mz = (r[2] * vx + r[5] * vy + r[8] * vz) / 4096.0;
        return (mx, my, mz);
    }

    /// Feed one fully-resolved triangle. Safe to call every frame for hundreds of frames — dedup
    /// bounds memory/time to the mesh's actual unique vertex/face count, not frame count x face count.
    public static void Accumulate(GteCapture.CapturedTri t)
    {
        if (!Enabled) return;
        if (!t.A.Has3D || !t.B.Has3D || !t.C.Has3D) return;
        if (t.A.Rot == null || t.A.Trans == null || t.B.Trans == null || t.C.Trans == null) return;

        var (mx0, my0, mz0) = ToModel(t.A);
        var (mx1, my1, mz1) = ToModel(t.B);
        var (mx2, my2, mz2) = ToModel(t.C);
        var obj = FindOrCreate(
            (t.A.Trans[0] + t.B.Trans[0] + t.C.Trans[0]) / 3.0,
            (t.A.Trans[1] + t.B.Trans[1] + t.C.Trans[1]) / 3.0,
            (t.A.Trans[2] + t.B.Trans[2] + t.C.Trans[2]) / 3.0);

        int ia = AddVertex(obj, mx0, my0, mz0, t.A.R, t.A.G, t.A.B, t.A.U, t.A.V);
        int ib = AddVertex(obj, mx1, my1, mz1, t.B.R, t.B.G, t.B.B, t.B.U, t.B.V);
        int ic = AddVertex(obj, mx2, my2, mz2, t.C.R, t.C.G, t.C.B, t.C.U, t.C.V);
        int lo = Math.Min(ia, Math.Min(ib, ic)), hi = Math.Max(ia, Math.Max(ib, ic));
        int mid = ia + ib + ic - lo - hi;
        if (!obj.SeenTris.Add((lo, mid, hi, t.TPage, t.Clut))) return; // face already known

        var matKey = (t.Textured, t.TPage, t.Clut);
        if (!obj.Indices.TryGetValue(matKey, out var list)) obj.Indices[matKey] = list = new List<uint>();
        list.Add((uint)ia); list.Add((uint)ib); list.Add((uint)ic);
    }
}
