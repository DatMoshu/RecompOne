using System.Text.Json;
using RecompOne.Runtime.Host;

namespace RecompOne.Runtime.Export;

/// <summary>
/// Splits a captured frame's 3D geometry into the individual FIGURES standing in it, and writes,
/// per figure, every texture page that figure is painted with plus a UV layout template for each
/// — the wireframe of that figure's triangles drawn in the page's own texel space, so an artist
/// can see which island is a face and which is a sleeve and paint inside the lines.
///
/// <para>HOW A TEXTURE IS ATTRIBUTED TO A CHARACTER. Not by where the pixels are and not by hand:
/// by the geometry that samples them. Every captured vertex carries the GTE rotation/translation
/// that transformed it (<see cref="GteCapture.CapturedVertex.Trans"/>), and the PS1 reloads that
/// pair per object — so triangles sharing a translation are one rigid PART by construction. PS1
/// characters are heavily articulated (measured on PE1's opera exterior: 176 parts for three
/// people), so the parts then have to be gathered back up into figures.</para>
///
/// <para>THE GATHERING RULE, AND WHY IT IS A RATIO RATHER THAN A DISTANCE. A figure's parts are
/// its bones: densely chained, each one a short hop from the next. The hop to the NEXT figure is
/// a different order of magnitude. So this builds a minimum spanning tree over the part anchors
/// and cuts it where the sorted edge lengths take their biggest MULTIPLICATIVE step — measured on
/// the opera exterior, within-figure edges run 5..67 units and the two edges joining the three
/// people are 173 and 263, a 2.6x jump with nothing near it. A fixed distance would have to be
/// retuned per game (these are the game's own matrix units, whatever they are) and a
/// bounding-box-overlap rule fails outright, because each character's ground shadow is one wide
/// quad that reaches across the gap and staples all three together. If no step is big enough the
/// room is reported as ONE figure rather than split on noise; <c>gapOverride</c> forces an
/// absolute cut when a human disagrees.</para>
///
/// <para><see cref="Group"/> reports the parts and the edge statistics as well as the figures, so
/// a bad split or a bad merge is visible rather than silently baked into the output.</para>
///
/// <para>Generic: nothing here knows about any game. A host picks the capture window, supplies
/// names for the figures if it has them, and calls <see cref="Write"/>.</para>
/// </summary>
public static class CharacterTextures
{
    public const int PageSize = TextureBaker.PageSize;

    /// <summary>One material a figure uses, with the exact triangles that reference it — the UVs
    /// come from the captured triangles, NOT from deduplicated vertices, because a PS1 vertex is
    /// shared between faces that give it different UVs and collapsing them would draw a wireframe
    /// the game never actually used.</summary>
    public sealed class Mat
    {
        public GltfWriter.MatKey Key;
        public readonly HashSet<(short, short, short, short, short, short)> Uvs = new();
        public int Tris;
    }

    public sealed class Figure
    {
        public string Name = "";
        public int Parts, Tris;
        public double Cx, Cy, Cz;
        public float MinX = float.MaxValue, MinY = float.MaxValue, MinZ = float.MaxValue;
        public float MaxX = float.MinValue, MaxY = float.MinValue, MaxZ = float.MinValue;
        public readonly Dictionary<GltfWriter.MatKey, Mat> Mats = new();
    }

    sealed class Part
    {
        public double Tx, Ty, Tz;
        public float MinX = float.MaxValue, MinY = float.MaxValue, MinZ = float.MaxValue;
        public float MaxX = float.MinValue, MaxY = float.MinValue, MaxZ = float.MinValue;
        public readonly List<GteCapture.CapturedTri> Tris = new();
        public int Owner;
    }

    /// <summary>A step this many times the previous edge is taken as the boundary between two
    /// figures. 2.0 is well clear of the largest within-figure step measured (1.22x) and well
    /// below the between-figure one (2.57x).</summary>
    public const double SplitRatio = 2.0;

    /// <summary>Split a capture into figures. <paramref name="gapOverride"/> &gt; 0 forces an
    /// absolute anchor-distance cut instead of the automatic ratio rule.</summary>
    public static List<Figure> Group(IReadOnlyList<GteCapture.CapturedTri> tris, double gapOverride,
                                     out int partCount, out string partReport)
    {
        var parts = new Dictionary<(int, int, int), Part>();
        foreach (var t in tris)
        {
            if (!t.A.Has3D || !t.B.Has3D || !t.C.Has3D) continue;
            if (t.A.Trans == null || t.B.Trans == null || t.C.Trans == null) continue;
            var k = ((t.A.Trans[0] + t.B.Trans[0] + t.C.Trans[0]) / 3,
                     (t.A.Trans[1] + t.B.Trans[1] + t.C.Trans[1]) / 3,
                     (t.A.Trans[2] + t.B.Trans[2] + t.C.Trans[2]) / 3);
            if (!parts.TryGetValue(k, out var p))
                parts[k] = p = new Part { Tx = k.Item1, Ty = k.Item2, Tz = k.Item3 };
            p.Tris.Add(t);
            foreach (var v in new[] { t.A, t.B, t.C })
            {
                p.MinX = Math.Min(p.MinX, v.Wx); p.MaxX = Math.Max(p.MaxX, v.Wx);
                p.MinY = Math.Min(p.MinY, v.Wy); p.MaxY = Math.Max(p.MaxY, v.Wy);
                p.MinZ = Math.Min(p.MinZ, v.Wz); p.MaxZ = Math.Max(p.MaxZ, v.Wz);
            }
        }

        var list = parts.Values.ToList();
        int n = partCount = list.Count;
        for (int i = 0; i < n; i++) list[i].Owner = i;
        int Find(int i) { while (list[i].Owner != i) i = list[i].Owner = list[list[i].Owner].Owner; return i; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) list[b].Owner = a; }

        double Dist(int a, int b)
        {
            double dx = list[a].Tx - list[b].Tx, dy = list[a].Ty - list[b].Ty, dz = list[a].Tz - list[b].Tz;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // Prim's MST over the part anchors. n is the number of moving objects in one frame -- a
        // few hundred at most -- so the dense O(n^2) form is both simpler and faster here than a
        // heap, and it needs no adjacency structure.
        var mst = new List<(double w, int a, int b)>();
        if (n > 1)
        {
            var used = new bool[n];
            var best = new double[n];
            var from = new int[n];
            Array.Fill(best, double.MaxValue); Array.Fill(from, -1);
            best[0] = 0;
            for (int it = 0; it < n; it++)
            {
                int u = -1;
                for (int i = 0; i < n; i++) if (!used[i] && (u < 0 || best[i] < best[u])) u = i;
                used[u] = true;
                if (from[u] >= 0) mst.Add((best[u], from[u], u));
                for (int v = 0; v < n; v++)
                    if (!used[v]) { double d = Dist(u, v); if (d < best[v]) { best[v] = d; from[v] = u; } }
            }
        }

        double cut;
        string cutWhy;
        if (gapOverride > 0) { cut = gapOverride; cutWhy = $"forced cut > {cut:F1}"; }
        else if (mst.Count < 2) { cut = double.MaxValue; cutWhy = "one part group, no cut"; }
        else
        {
            var sorted = mst.Select(e => e.w).OrderBy(w => w).ToList();
            // Look for the jump only in the upper half: the short bone-to-bone hops at the bottom
            // of the list are noisy in ratio terms (5.0 -> 11.0 is a 2.2x step and means nothing).
            double bestRatio = 1.0; cut = double.MaxValue;
            for (int i = Math.Max(1, sorted.Count / 2); i < sorted.Count - 1; i++)
            {
                if (sorted[i] <= 0.0001) continue;
                double r = sorted[i + 1] / sorted[i];
                if (r > bestRatio) { bestRatio = r; cut = sorted[i]; }
            }
            if (bestRatio < SplitRatio) { cut = double.MaxValue; cutWhy = $"no step >= {SplitRatio:F1}x (largest {bestRatio:F2}x) — treated as one figure"; }
            else cutWhy = $"cut > {cut:F1} (step {bestRatio:F2}x, median edge {sorted[sorted.Count / 2]:F1})";
        }

        foreach (var (w, a, b) in mst) if (w <= cut) Union(a, b);

        var byRoot = new Dictionary<int, Figure>();
        for (int i = 0; i < list.Count; i++)
        {
            int r = Find(i);
            if (!byRoot.TryGetValue(r, out var fig)) byRoot[r] = fig = new Figure();
            var p = list[i];
            fig.Parts++;
            fig.MinX = Math.Min(fig.MinX, p.MinX); fig.MaxX = Math.Max(fig.MaxX, p.MaxX);
            fig.MinY = Math.Min(fig.MinY, p.MinY); fig.MaxY = Math.Max(fig.MaxY, p.MaxY);
            fig.MinZ = Math.Min(fig.MinZ, p.MinZ); fig.MaxZ = Math.Max(fig.MaxZ, p.MaxZ);
            foreach (var t in p.Tris)
            {
                if (!t.Textured) continue;   // an untextured face has no page for an artist to paint
                var key = new GltfWriter.MatKey(t.Textured, t.TPage, t.Clut, t.TexWin, t.SemiTrans);
                if (!fig.Mats.TryGetValue(key, out var mat)) fig.Mats[key] = mat = new Mat { Key = key };
                mat.Tris++;
                mat.Uvs.Add((t.A.U, t.A.V, t.B.U, t.B.V, t.C.U, t.C.V));
                fig.Tris++;
            }
        }

        var figs = byRoot.Values.Where(f => f.Tris > 0).ToList();
        foreach (var f in figs)
        {
            f.Cx = (f.MinX + f.MaxX) / 2.0; f.Cy = (f.MinY + f.MaxY) / 2.0; f.Cz = (f.MinZ + f.MaxZ) / 2.0;
        }
        // Left to right as seen on screen: screenX = x*h/z + ofx with z > 0, so ordering by view
        // X is ordering by screen X, and a human can name them off a screenshot without guessing.
        figs.Sort((a, b) => a.Cx.CompareTo(b.Cx));

        partReport = $"{n} parts, {cutWhy}";
        return figs;
    }

    /// <summary>Bake and write every figure's pages and UV templates into <paramref name="dir"/>,
    /// plus a manifest. Returns the number of PNG files written.</summary>
    public static int Write(string dir, IReadOnlyList<Figure> figures, out string summary)
    {
        Directory.CreateDirectory(dir);
        var vram = TextureBaker.Snapshot();
        int written = 0;
        var manifest = new List<object>();

        foreach (var fig in figures)
        {
            var mats = new List<object>();
            var baked = new Dictionary<GltfWriter.MatKey, byte[]>();
            foreach (var mat in fig.Mats.Values.OrderByDescending(m => m.Tris))
            {
                var key = mat.Key;
                string stem = FileStem(fig.Name, key);
                var files = new List<string>();

                byte[]? rgba = null;
                TextureBaker.PageStats st = default;
                if (vram != null)
                {
                    rgba = TextureBaker.Bake(vram, key.TPage, key.Clut, key.TexWin, key.Semi, out st);
                    baked[key] = rgba;
                    WritePng(Path.Combine(dir, stem + ".png"), rgba);
                    files.Add(stem + ".png");
                    written++;
                }

                // The template: the page as the game shows it, with holes made legible as a dark
                // checkerboard (transparent-black and opaque-black are different things and look
                // identical otherwise), and this figure's own triangles wired over the top.
                if (rgba != null)
                {
                    WriteTemplate(Path.Combine(dir, stem + "_uv.png"), rgba, mat.Uvs);
                    files.Add(stem + "_uv.png");
                    written++;
                }

                // ...and the same wireframe alone on transparency, so it drops straight in as a
                // guide LAYER over the paintable page in any editor and can be hidden again.
                var guide = new byte[PageSize * PageSize * 4];
                foreach (var (u0, v0, u1, v1, u2, v2) in mat.Uvs)
                {
                    Line(guide, u0, v0, u1, v1); Line(guide, u1, v1, u2, v2); Line(guide, u2, v2, u0, v0);
                }
                WritePng(Path.Combine(dir, stem + "_uvguide.png"), guide);
                files.Add(stem + "_uvguide.png");
                written++;

                int uMin = 255, uMax = 0, vMin = 255, vMax = 0;
                foreach (var (u0, v0, u1, v1, u2, v2) in mat.Uvs)
                {
                    foreach (var (u, v) in new[] { (u0, v0), (u1, v1), (u2, v2) })
                    {
                        uMin = Math.Min(uMin, u & 0xFF); uMax = Math.Max(uMax, u & 0xFF);
                        vMin = Math.Min(vMin, v & 0xFF); vMax = Math.Max(vMax, v & 0xFF);
                    }
                }

                mats.Add(new
                {
                    overrideFile = stem + ".png",
                    uvTemplate = stem + "_uv.png",
                    uvGuideLayer = stem + "_uvguide.png",
                    tpage = $"0x{key.TPage & 0x1FF:X4}",
                    clut = $"0x{key.Clut & 0x7FFF:X4}",
                    texWindow = $"0x{key.TexWin:X5}",
                    depth = ((key.TPage >> 7) & 3) switch { 0 => "4bpp", 1 => "8bpp", _ => "15bpp" },
                    semiTransparent = key.Semi,
                    triangles = mat.Tris,
                    distinctUvTriangles = mat.Uvs.Count,
                    uvBounds = new { uMin, uMax, vMin, vMax },
                    transparentTexels = st.TransparentTexels,
                    stpTexels = st.StpTexels,
                });
            }

            // THE SHEET THAT MATTERS. A PS1 4bpp page carries ONE set of indices and a separate
            // 16-colour CLUT per body part, so no single CLUT decodes the whole character: bake
            // through the jacket's palette and the face comes out as noise. The composite fixes
            // that by decoding each texel through the CLUT of the material whose triangles
            // actually cover it — every island in its own true colours, the character laid out
            // as one page.
            //
            // And because each part only ever samples its own island, the same image is also a
            // LOSSLESS whole-character override: written as _clALL it matches every CLUT on the
            // page, and each part still finds the right colours where it looks. Texels no
            // triangle covers stay transparent, i.e. "leave the game's texel alone", so an island
            // belonging to some other model on a shared page is not touched.
            var sheets = new List<object>();
            foreach (var page in fig.Mats.Values.Where(m => baked.ContainsKey(m.Key))
                                               .GroupBy(m => (m.Key.TPage & 0x1FF, m.Key.TexWin)))
            {
                var members = page.OrderByDescending(m => m.Tris).ToList();
                var cover = new int[PageSize * PageSize];
                Array.Fill(cover, -1);
                // Least-used material first so the dominant one wins any overlap.
                for (int i = members.Count - 1; i >= 0; i--)
                    foreach (var (u0, v0, u1, v1, u2, v2) in members[i].Uvs)
                        FillTri(cover, i, u0, v0, u1, v1, u2, v2);
                Dilate(cover);

                var comp = new byte[PageSize * PageSize * 4];
                int covered = 0;
                for (int p = 0; p < cover.Length; p++)
                {
                    int m = cover[p];
                    if (m < 0) continue;
                    var src = baked[members[m].Key];
                    comp[p * 4] = src[p * 4]; comp[p * 4 + 1] = src[p * 4 + 1];
                    comp[p * 4 + 2] = src[p * 4 + 2]; comp[p * 4 + 3] = src[p * 4 + 3];
                    if (comp[p * 4 + 3] != 0) covered++;
                }

                string cstem = $"{Mods.TextureOverride.Prefix}{Sanitise(fig.Name)}_tp{page.Key.Item1:X4}_clALL"
                             + (page.Key.TexWin != 0 ? $"_tw{page.Key.TexWin:X5}" : "");
                WritePng(Path.Combine(dir, cstem + ".png"), comp);
                var allUvs = members.SelectMany(m => m.Uvs).ToList();
                WriteTemplate(Path.Combine(dir, cstem + "_uv.png"), comp, allUvs);
                var cguide = new byte[PageSize * PageSize * 4];
                foreach (var (u0, v0, u1, v1, u2, v2) in allUvs)
                {
                    Line(cguide, u0, v0, u1, v1); Line(cguide, u1, v1, u2, v2); Line(cguide, u2, v2, u0, v0);
                }
                WritePng(Path.Combine(dir, cstem + "_uvguide.png"), cguide);
                written += 3;

                sheets.Add(new
                {
                    overrideFile = cstem + ".png",
                    uvTemplate = cstem + "_uv.png",
                    uvGuideLayer = cstem + "_uvguide.png",
                    tpage = $"0x{page.Key.Item1:X4}",
                    matchesEveryClutOnThisPage = true,
                    cluts = members.Select(m => $"0x{m.Key.Clut & 0x7FFF:X4}").ToArray(),
                    triangles = members.Sum(m => m.Tris),
                    texelsCovered = covered,
                });
            }

            manifest.Add(new
            {
                name = fig.Name,
                parts = fig.Parts,
                triangles = fig.Tris,
                materials = fig.Mats.Count,
                wholeCharacterSheets = sheets,
                viewSpaceBounds = new { x = new[] { fig.MinX, fig.MaxX }, y = new[] { fig.MinY, fig.MaxY }, z = new[] { fig.MinZ, fig.MaxZ } },
                textures = mats,
            });
        }

        var doc = new
        {
            what = "Per-character texture pages + UV layout templates, baked out of VRAM for one captured frame.",
            attribution = "Figures are derived from the capture: triangles are grouped by the GTE translation active when they were transformed (one rigid part each), then parts are merged into figures by view-space proximity. No hard-coded positions.",
            startHere = "For each character, the *_clALL.png sheet is the whole model on one page (each island decoded through its own CLUT) and *_clALL_uv.png is that sheet with every triangle wired over it. Repaint the _clALL sheet and you have repainted the character. The per-CLUT files beside it are the same page seen through one body part's palette, for precise work.",
            howToRepaint = "Paint over <file>.png using <file>_uv.png (or _uvguide.png as a layer) to see where each island lands. Keep it 256x256 RGBA. A pixel with alpha < 128 keeps the game's original texel, so a partial repaint works — which is also why the transparent areas of a _clALL sheet are safe to leave alone.",
            howToLoad = "Drop the repainted <file>.png into the art folder (--art-dir=, default games/<Game>/media/art) with 'pe1.art' in settings.json ActiveMods. The tp/cl/tw fields in the name ARE the key the renderer matches; the label before them is only for you. Use _clALL instead of a CLUT to match every palette on that page. Saving the file reloads it live.",
            silhouetteNote = "A texel the game stores as 0x0000 is a hole and stays a hole; an override changes colours, not the outline.",
            uvConvention = "UV template pixel (u,v) is exactly the texel the GPU fetches for raw UV (u,v) — the texture window is already baked in.",
            vramAvailable = vram != null,
            figures = manifest,
        };
        File.WriteAllText(Path.Combine(dir, "characters.json"),
            JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));

        summary = string.Join(" | ", figures.Select(f =>
            $"{f.Name}: {f.Mats.Count} tex, {f.Tris} tris, {f.Parts} parts, x[{f.MinX:F2},{f.MaxX:F2}]"));
        return written;
    }

    /// <summary>The filename convention, in one place because both halves of the feature depend on
    /// it: the label is for humans and ignored by the loader, the hex fields are the match key,
    /// and <c>_tw</c> is omitted when the texture window is 0 (the normal case) so the common name
    /// stays short.</summary>
    public static string FileStem(string label, GltfWriter.MatKey key)
    {
        string s = $"{Mods.TextureOverride.Prefix}{Sanitise(label)}_tp{key.TPage & 0x1FF:X4}_cl{key.Clut & 0x7FFF:X4}";
        if (key.TexWin != 0) s += $"_tw{key.TexWin:X5}";
        return s;
    }

    static string Sanitise(string s)
    {
        var a = s.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray();
        var r = new string(a).Trim('-');
        return r.Length == 0 ? "figure" : r;
    }

    // ---- image helpers -----------------------------------------------------------------

    // Transparent-black and opaque-black are DIFFERENT things on the PS1 and identical to the eye
    // in a PNG viewer, so the template marks the holes. Without this, "the sleeve island ends
    // here" and "the sleeve is black here" look the same and the artist paints into a hole.
    static void Checkerboard(byte[] rgba)
    {
        for (int y = 0; y < PageSize; y++)
            for (int x = 0; x < PageSize; x++)
            {
                int o = (y * PageSize + x) * 4;
                if (rgba[o + 3] != 0) continue;
                byte c = (byte)((((x >> 3) + (y >> 3)) & 1) == 0 ? 26 : 46);
                rgba[o] = c; rgba[o + 1] = c; rgba[o + 2] = (byte)(c + 10); rgba[o + 3] = 255;
            }
    }

    const byte LineR = 255, LineG = 0, LineB = 255;

    /// <summary>How much the flattened <c>_uv</c> template is magnified. A character puts several
    /// hundred triangles on one 256-texel page, and at 1:1 a one-pixel wireframe covers more of
    /// the sheet than it reveals -- the islands disappear under their own outlines. The template
    /// is a REFERENCE image so it can be as large as it needs to be; the <c>_uvguide</c> layer
    /// stays 1:1 because that one has to sit pixel-for-pixel over the paintable page.</summary>
    public const int TemplateScale = 4;

    static void Px(byte[] rgba, int w, int x, int y)
    {
        if ((uint)x >= (uint)w || (uint)y >= (uint)w) return;
        int o = (y * w + x) * 4;
        rgba[o] = LineR; rgba[o + 1] = LineG; rgba[o + 2] = LineB; rgba[o + 3] = 255;
    }

    /// <summary>Fill one UV triangle into a per-texel owner map. Top-left-ish half-open rule via a
    /// plain barycentric test with a small epsilon: this is a coverage mask for compositing, not a
    /// rasteriser that has to match the GPU, and a texel claimed by two adjacent triangles of the
    /// same material is the same answer either way.</summary>
    static void FillTri(int[] owner, int who, int x0, int y0, int x1, int y1, int x2, int y2)
    {
        x0 &= 0xFF; y0 &= 0xFF; x1 &= 0xFF; y1 &= 0xFF; x2 &= 0xFF; y2 &= 0xFF;
        int minX = Math.Min(x0, Math.Min(x1, x2)), maxX = Math.Max(x0, Math.Max(x1, x2));
        int minY = Math.Min(y0, Math.Min(y1, y2)), maxY = Math.Max(y0, Math.Max(y1, y2));
        double area = (x1 - x0) * (double)(y2 - y0) - (x2 - x0) * (double)(y1 - y0);
        if (Math.Abs(area) < 1e-9)
        {
            // Degenerate in UV (a whole strip mapped to one line of texels is common on PS1
            // models). Walk its edges instead of dropping it, or those texels never get composited.
            LineFill(owner, who, x0, y0, x1, y1); LineFill(owner, who, x1, y1, x2, y2); LineFill(owner, who, x2, y2, x0, y0);
            return;
        }
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                double w0 = ((x1 - x0) * (py - y0) - (px - x0) * (y1 - y0)) / area;
                double w1 = ((px - x0) * (y2 - y0) - (x2 - x0) * (py - y0)) / area;
                if (w0 < -0.02 || w1 < -0.02 || w0 + w1 > 1.02) continue;
                owner[y * PageSize + x] = who;
            }
    }

    static void LineFill(int[] owner, int who, int x0, int y0, int x1, int y1)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            if ((uint)x0 < PageSize && (uint)y0 < PageSize) owner[y0 * PageSize + x0] = who;
            if (x0 == x1 && y0 == y1) break;
            int e2 = err * 2;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>Grow the coverage by one texel. The renderer's texel rounding can land a fragment
    /// just outside the triangle's UV footprint, and an un-composited texel there shows as a
    /// one-pixel seam of the original art along every island edge.</summary>
    static void Dilate(int[] owner)
    {
        var copy = (int[])owner.Clone();
        for (int y = 0; y < PageSize; y++)
            for (int x = 0; x < PageSize; x++)
            {
                if (copy[y * PageSize + x] >= 0) continue;
                for (int dy = -1; dy <= 1 && owner[y * PageSize + x] < 0; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((uint)nx >= PageSize || (uint)ny >= PageSize) continue;
                        if (copy[ny * PageSize + nx] < 0) continue;
                        owner[y * PageSize + x] = copy[ny * PageSize + nx];
                        break;
                    }
            }
    }

    static void Line(byte[] rgba, int x0, int y0, int x1, int y1) => Line(rgba, PageSize, 1, x0, y0, x1, y1);

    static void Line(byte[] rgba, int w, int scale, int x0, int y0, int x1, int y1)
    {
        int half = scale / 2;
        x0 = (x0 & 0xFF) * scale + half; y0 = (y0 & 0xFF) * scale + half;
        x1 = (x1 & 0xFF) * scale + half; y1 = (y1 & 0xFF) * scale + half;
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            Px(rgba, w, x0, y0);
            if (x0 == x1 && y0 == y1) break;
            int e2 = err * 2;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>Nearest-neighbour magnification -- nearest, not smooth, because the artist has to
    /// be able to count texels on the template and see which one an island edge lands on.</summary>
    static byte[] Upscale(byte[] rgba, int scale)
    {
        int w = PageSize * scale;
        var dst = new byte[w * w * 4];
        for (int y = 0; y < w; y++)
            for (int x = 0; x < w; x++)
                Array.Copy(rgba, ((y / scale) * PageSize + x / scale) * 4, dst, (y * w + x) * 4, 4);
        return dst;
    }

    static void WritePng(string path, byte[] rgba) => WritePng(path, rgba, PageSize);

    static void WritePng(string path, byte[] rgba, int w)
    {
        var raw = new byte[(w * 4 + 1) * w];
        int o = 0;
        for (int y = 0; y < w; y++)
        {
            raw[o++] = 0;   // PNG per-row filter byte: none
            Array.Copy(rgba, y * w * 4, raw, o, w * 4);
            o += w * 4;
        }
        PngWriter.WriteRgba(path, w, w, raw);
    }

    /// <summary>The flattened reference: the page as the game shows it, holes made legible (a
    /// transparent texel and an opaque black one look identical otherwise and an artist paints
    /// into the hole), magnified, with the wireframe over the top.</summary>
    static void WriteTemplate(string path, byte[] page,
        IEnumerable<(short, short, short, short, short, short)> uvs)
    {
        var flat = (byte[])page.Clone();
        Checkerboard(flat);
        int w = PageSize * TemplateScale;
        var big = Upscale(flat, TemplateScale);
        foreach (var (u0, v0, u1, v1, u2, v2) in uvs)
        {
            Line(big, w, TemplateScale, u0, v0, u1, v1);
            Line(big, w, TemplateScale, u1, v1, u2, v2);
            Line(big, w, TemplateScale, u2, v2, u0, v0);
        }
        WritePng(path, big, w);
    }
}
