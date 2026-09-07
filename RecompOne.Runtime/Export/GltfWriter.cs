using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RecompOne.Runtime.Export;

/// <summary>
/// Minimal, generic glTF 2.0 binary (.glb) writer for <see cref="GteCapture"/> output. Nothing
/// here is PS1/game-specific — a game host picks when to capture and where to write; this just
/// turns a bag of captured triangles into a valid .glb plus a JSON sidecar with the raw capture
/// provenance (rotation/translation, hit/miss stats) that doesn't belong inside the mesh itself.
///
/// <para>Scope: geometry (position) + vertex colour + UV + BAKED TEXTURES, grouped into one mesh
/// primitive per PS1 material key. Positions written by <see cref="Write"/> are camera/view space
/// (RT*vertex + TR, pre-perspective-divide) in the units the game's own matrices used, NOT world
/// space — see the sidecar's "space" field.</para>
///
/// <para>TEXTURES. Each distinct material key is decoded out of VRAM by
/// <see cref="TextureBaker"/> into a 256x256 RGBA PNG, embedded in the .glb's binary chunk (so
/// one file opens complete in any viewer) and also written beside it under
/// <c>&lt;name&gt;_tex/</c> for inspection. Identical pages are deduplicated by content, since many
/// CLUT/window combinations resolve to the same image. The PS1 key is still carried on each
/// material's glTF <c>extras</c> (<c>ps1TPage</c>, <c>ps1Clut</c>, ...) so the mapping between an
/// exported material and the game's own VRAM state stays inspectable.</para>
///
/// <para>UV CONVENTION — WHY 256 AND NOT 255. A PS1 texture page is 256x256 TEXELS and UVs are
/// integer texel indices 0..255 within it: both renderers fetch with <c>uv &amp; 0xFF</c>, never
/// normalising by 255. A baked page is therefore a 256-pixel image whose pixel i must be hit by
/// UV i, which in normalised glTF coordinates is the texel CENTRE <c>(i + 0.5) / 256</c> — not
/// <c>i / 255</c>. The old <c>/255</c> divisor stretched the page by 256/255 and put every sample
/// half a texel off; with NEAREST filtering that mostly rounds back to the right texel and so
/// looked fine, but it lands on the wrong one near the page edge and shifts visibly the moment a
/// viewer filters the texture. Half-texel offset + divisor 256 is exact for both.</para>
///
/// <para>VERTEX COLOUR IS A MODULATOR, AND ITS NEUTRAL IS 128, NOT 255. The GPU computes
/// <c>texel * colour / 128</c> for a textured primitive, so a vertex colour of 128 means
/// "unchanged" and 255 means "double brightness". glTF multiplies COLOR_0 into the base colour,
/// so textured vertices are written <c>c / 128</c> (clamped at 1.0, the most a glTF vertex colour
/// can express) and untextured ones — where the vertex colour IS the final colour — are written
/// <c>c / 255</c>. Writing <c>c / 255</c> for both, as this used to, renders every textured model
/// at half brightness.</para>
/// </summary>
public static class GltfWriter
{
    /// <summary>PS1 material identity: everything that changes which texels a primitive samples.
    /// The texture window and semi-transparency flag are part of it, not decoration — see
    /// <see cref="TextureBaker"/>.</summary>
    public readonly record struct MatKey(bool Tex, ushort TPage, ushort Clut, uint TexWin, bool Semi);

    public static void Write(string glbPath, string? sidecarJsonPath = null)
    {
        _bufferViews.Clear(); _accessors.Clear();
        var tris = GteCapture.Triangles;
        var positions = new List<float>();
        var colors = new List<float>();
        var uvs = new List<float>();
        var byMaterial = new Dictionary<MatKey, List<uint>>();
        var screenSpaceIndices = new List<uint>();
        uint vcount = 0;

        void AddVert(GteCapture.CapturedVertex v, bool asScreenSpace, bool textured)
        {
            if (asScreenSpace)
            {
                // No 3D source: place as a flat plate in screen space (X right, Y down -> up, Z=0)
                // so the pre-rendered background plate is still visible for review, distinguishable
                // by node name from real geometry rather than silently dropped.
                positions.Add(v.X); positions.Add(-v.Y); positions.Add(0f);
            }
            else
            {
                positions.Add(v.Wx); positions.Add(v.Wy); positions.Add(v.Wz);
            }
            AddColor(colors, v.R, v.G, v.B, textured);
            AddUv(uvs, v.U, v.V);
        }

        foreach (var t in tris)
        {
            bool full = t.A.Has3D && t.B.Has3D && t.C.Has3D;
            AddVert(t.A, !full, t.Textured); AddVert(t.B, !full, t.Textured); AddVert(t.C, !full, t.Textured);
            uint i0 = vcount, i1 = vcount + 1, i2 = vcount + 2;
            vcount += 3;
            if (full)
            {
                var key = new MatKey(t.Textured, t.TPage, t.Clut, t.TexWin, t.SemiTrans);
                if (!byMaterial.TryGetValue(key, out var list)) byMaterial[key] = list = new List<uint>();
                list.Add(i0); list.Add(i1); list.Add(i2);
            }
            else
            {
                screenSpaceIndices.Add(i0); screenSpaceIndices.Add(i1); screenSpaceIndices.Add(i2);
            }
        }

        // Materials are baked lazily, as each primitive is emitted, so a material carries the
        // name of the primitive that first used it. Nothing reaches disk until WriteGlb, so a
        // decode failure still aborts before a half-written .glb exists.
        var baker = new Bakery(glbPath);

        using var bin = new MemoryStream();
        using var bw = new BinaryWriter(bin);
        long posOff = bin.Position; foreach (var f in positions) bw.Write(f); Pad4(bw);
        long colOff = bin.Position; foreach (var f in colors) bw.Write(f); Pad4(bw);
        long uvOff = bin.Position; foreach (var f in uvs) bw.Write(f); Pad4(bw);

        int posBv = _bufferViews.Count; _bufferViews.Add(new { buffer = 0, byteOffset = posOff, byteLength = positions.Count * 4 });
        int posAcc = _accessors.Count; _accessors.Add(MakeVec3Accessor(posBv, positions));
        int colBv = _bufferViews.Count; _bufferViews.Add(new { buffer = 0, byteOffset = colOff, byteLength = colors.Count * 4 });
        int colAcc = _accessors.Count; _accessors.Add(new { bufferView = colBv, byteOffset = 0, componentType = 5126, count = colors.Count / 3, type = "VEC3" });
        int uvBv = _bufferViews.Count; _bufferViews.Add(new { buffer = 0, byteOffset = uvOff, byteLength = uvs.Count * 4 });
        int uvAcc = _accessors.Count; _accessors.Add(new { bufferView = uvBv, byteOffset = 0, componentType = 5126, count = uvs.Count / 2, type = "VEC2" });
        var attrs = new { POSITION = posAcc, COLOR_0 = colAcc, TEXCOORD_0 = uvAcc };

        var meshPrimitives = new List<object>();
        void EmitPrimitive(List<uint> idx, string name, MatKey key)
        {
            if (idx.Count == 0) return;
            long idxOff = bin.Position; foreach (var i in idx) bw.Write(i); Pad4(bw);
            int idxBv = _bufferViews.Count;
            _bufferViews.Add(new { buffer = 0, byteOffset = idxOff, byteLength = idx.Count * 4, target = 34963 });
            int idxAcc = _accessors.Count;
            _accessors.Add(new { bufferView = idxBv, byteOffset = 0, componentType = 5125 /*UINT*/, count = idx.Count, type = "SCALAR" });
            meshPrimitives.Add(new { attributes = attrs, indices = idxAcc, material = baker.MaterialIndex(key, name) });
        }

        int matGroup = 0;
        foreach (var (key, idx) in byMaterial)
            EmitPrimitive(idx, $"mat_{matGroup++}_tp{key.TPage:X4}_cl{key.Clut:X4}", key);
        // The screen-space leftovers are, by definition, geometry with no GTE transform — the
        // pre-rendered plate and the 2D HUD. Kept as an untextured primitive so it is visible and
        // separable by name rather than silently dropped. In a game whose plate is not GPU
        // geometry at all (PE1) this is empty and no material is created for it.
        EmitPrimitive(screenSpaceIndices, "background_screenspace", new MatKey(false, 0, 0, 0, false));

        baker.WriteImages(bin, bw, _bufferViews);

        var gltf = new
        {
            asset = new { version = "2.0", generator = "RecompOne GteCapture exporter" },
            scenes = new[] { new { nodes = new[] { 0 } } },
            scene = 0,
            nodes = new[] { new { mesh = 0, name = "room" } },
            meshes = new[] { new { primitives = meshPrimitives, name = "room" } },
            materials = baker.Materials,
            images = baker.Images,
            samplers = baker.Samplers,
            textures = baker.Textures,
            accessors = _accessors,
            bufferViews = _bufferViews,
            buffers = new[] { new { byteLength = bin.Length } },
        };

        WriteGlb(glbPath, gltf, bin);
        baker.DumpPngs();

        if (sidecarJsonPath != null)
        {
            var sidecar = new
            {
                space = "camera/view (RT*vertex + TR, pre-perspective-divide) — NOT world space; consistent within one fixed-camera room, not comparable across rooms/camera cuts",
                uvConvention = "TEXCOORD_0 = (texel + 0.5) / 256 — PS1 UVs are integer texel indices 0..255 within a 256x256 page; sample NEAREST",
                vertexColorConvention = "COLOR_0 modulates: textured = c/128 (128 is neutral on PS1, clamped at 1.0), untextured = c/255",
                // Without this a consumer cannot put the geometry back on the screen at all, since
                // positions are pre-perspective-divide. CAVEAT, measured rather than assumed: this
                // is the LAST projection the GTE was configured with during the capture, and PE1
                // reloads these registers PER OBJECT within a single frame — so it reprojects the
                // object that was transformed last, and other objects in the same room may need a
                // different h/ofx/ofy. Treat it as a starting point, not ground truth; per-object
                // projection would have to be recorded per vertex alongside rot/trans.
                projection = new
                {
                    formula = "screenX = x*h/z + ofx, screenY = y*h/z + ofy (screen pixels, Y down)",
                    caveat = "last-sampled GTE state; PE1 varies projection per object within a frame",
                    h = GteCapture.ProjH,
                    ofx = GteCapture.ProjOfxFixed / 65536.0,
                    ofy = GteCapture.ProjOfyFixed / 65536.0,
                    drawOrigin = new { x = GteCapture.DrawOriginX, y = GteCapture.DrawOriginY },
                },
                trianglesTotal = tris.Count,
                trianglesFullyResolved3D = GteCapture.TrisFullyResolved,
                trianglesPartial = GteCapture.TrisPartial,
                trianglesUnresolvedScreenSpaceOnly = GteCapture.TrisUnresolved,
                vertsResolvedDirect = GteCapture.VertsResolvedDirect,
                vertsResolvedViaDrawOffset = GteCapture.VertsResolvedViaDrawOffset,
                textures = baker.Report(),
                sampleMatrices = tris.Where(t => t.A.Has3D).Take(5)
                    .Select(t => new { rotation = t.A.Rot, translation = t.A.Trans }).ToArray(),
            };
            File.WriteAllText(sidecarJsonPath, JsonSerializer.Serialize(sidecar, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>
    /// Writes one node+mesh PER accumulated object (see <see cref="GteAccumulator"/>) into a single
    /// .glb — the model-space union recovered across many frames, already deduped and separated by
    /// object. Unlike <see cref="Write"/>, positions here are genuine per-object MODEL space (not
    /// camera/view space): each object was un-rotated/un-translated by its own captured R,TR before
    /// accumulation, so there is no shared-camera caveat to carry, but objects are not yet placed
    /// back into a common world frame — each sits at its own local origin (glTF node identity
    /// transform) — a follow-up step if the room needs everything in one coherent scene.
    /// </summary>
    public static void WriteObjects(string glbPath, IReadOnlyList<GteAccumulator.ObjAccum> objects, string? sidecarJsonPath = null)
    {
        _bufferViews.Clear(); _accessors.Clear();
        var baker = new Bakery(glbPath);   // materials baked lazily, named after their first user

        using var bin = new MemoryStream();
        using var bw = new BinaryWriter(bin);

        var nodes = new List<object>();
        var meshes = new List<object>();
        var perObjectStats = new List<object>();

        for (int oi = 0; oi < objects.Count; oi++)
        {
            var obj = objects[oi];
            int triCount = obj.TriCount;
            perObjectStats.Add(new { objectIndex = oi, vertices = obj.Verts.Count, triangles = triCount, anchor = new[] { obj.Tx, obj.Ty, obj.Tz } });
            if (obj.Verts.Count == 0 || triCount == 0) continue;

            // A vertex is shared across this object's primitives, so its colour has to use ONE
            // convention; textured is the overwhelming majority for a character model, and an
            // untextured primitive here would simply read slightly dark rather than wrong.
            bool anyTextured = obj.Indices.Keys.Any(k => k.Tex);
            var positions = new List<float>(obj.Verts.Count * 3);
            var colors = new List<float>(obj.Verts.Count * 3);
            var uvs = new List<float>(obj.Verts.Count * 2);
            foreach (var v in obj.Verts)
            {
                positions.Add(v.x); positions.Add(v.y); positions.Add(v.z);
                AddColor(colors, v.r, v.g, v.b, anyTextured);
                AddUv(uvs, v.u, v.v);
            }
            long posOff = bin.Position; foreach (var f in positions) bw.Write(f); Pad4(bw);
            long colOff = bin.Position; foreach (var f in colors) bw.Write(f); Pad4(bw);
            long uvOff = bin.Position; foreach (var f in uvs) bw.Write(f); Pad4(bw);

            int posBv = _bufferViews.Count; _bufferViews.Add(new { buffer = 0, byteOffset = posOff, byteLength = positions.Count * 4 });
            int posAcc = _accessors.Count; _accessors.Add(MakeVec3Accessor(posBv, positions));
            int colBv = _bufferViews.Count; _bufferViews.Add(new { buffer = 0, byteOffset = colOff, byteLength = colors.Count * 4 });
            int colAcc = _accessors.Count; _accessors.Add(new { bufferView = colBv, byteOffset = 0, componentType = 5126, count = colors.Count / 3, type = "VEC3" });
            int uvBv = _bufferViews.Count; _bufferViews.Add(new { buffer = 0, byteOffset = uvOff, byteLength = uvs.Count * 4 });
            int uvAcc = _accessors.Count; _accessors.Add(new { bufferView = uvBv, byteOffset = 0, componentType = 5126, count = uvs.Count / 2, type = "VEC2" });
            var attrs = new { POSITION = posAcc, COLOR_0 = colAcc, TEXCOORD_0 = uvAcc };

            var primitives = new List<object>();
            foreach (var (key, idx) in obj.Indices)
            {
                if (idx.Count == 0) continue;
                long idxOff = bin.Position; foreach (var i in idx) bw.Write(i); Pad4(bw);
                int idxBv = _bufferViews.Count; _bufferViews.Add(new { buffer = 0, byteOffset = idxOff, byteLength = idx.Count * 4, target = 34963 });
                int idxAcc = _accessors.Count; _accessors.Add(new { bufferView = idxBv, byteOffset = 0, componentType = 5125, count = idx.Count, type = "SCALAR" });
                primitives.Add(new { attributes = attrs, indices = idxAcc, material = baker.MaterialIndex(key, $"obj{oi}_tp{key.TPage:X4}_cl{key.Clut:X4}") });
            }
            if (primitives.Count == 0) continue;

            meshes.Add(new { primitives, name = $"object_{oi}" });
            nodes.Add(new { mesh = meshes.Count - 1, name = $"object_{oi}" });
        }

        baker.WriteImages(bin, bw, _bufferViews);

        var gltf = new
        {
            asset = new { version = "2.0", generator = "RecompOne GteAccumulator exporter" },
            scenes = new[] { new { nodes = Enumerable.Range(0, nodes.Count).ToArray() } },
            scene = 0,
            nodes,
            meshes,
            materials = baker.Materials,
            images = baker.Images,
            samplers = baker.Samplers,
            textures = baker.Textures,
            accessors = _accessors,
            bufferViews = _bufferViews,
            buffers = new[] { new { byteLength = bin.Length } },
        };

        WriteGlb(glbPath, gltf, bin);
        baker.DumpPngs();

        if (sidecarJsonPath != null)
        {
            var sidecar = new
            {
                space = "model space per object (R^T * (view - TR) / 4096) — each object at its own local origin, NOT yet placed into one shared world/room frame",
                uvConvention = "TEXCOORD_0 = (texel + 0.5) / 256 — PS1 UVs are integer texel indices 0..255 within a 256x256 page; sample NEAREST",
                objectCount = objects.Count,
                totalTrianglesAllObjects = GteAccumulator.TotalTriangles,
                textures = baker.Report(),
                objects = perObjectStats,
            };
            File.WriteAllText(sidecarJsonPath, JsonSerializer.Serialize(sidecar, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    // ---- texture baking / glTF material plumbing -------------------------------------------
    //
    // One VRAM snapshot is taken for the whole export: every material in a capture is decoded
    // from the same frame's VRAM, which is the only self-consistent choice — re-reading per
    // material would mix frames if the game streams textures mid-export.
    sealed class Bakery
    {
        readonly ushort[]? _vram = TextureBaker.Snapshot();
        readonly string _texDir;
        readonly Dictionary<MatKey, int> _matIndex = new();
        readonly Dictionary<string, int> _imageByContent = new();   // sha256 of PNG -> image index
        readonly List<byte[]> _png = new();
        readonly List<string> _pngName = new();
        readonly List<object> _images = new();
        readonly List<object> _textures = new();
        readonly List<object> _materials = new();
        readonly List<object> _report = new();
        int _missingVram, _untextured;

        public Bakery(string glbPath) =>
            _texDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(glbPath))!,
                                   Path.GetFileNameWithoutExtension(glbPath) + "_tex");

        public IReadOnlyList<object> Materials => _materials;
        public IReadOnlyList<object> Images => _images;
        public IReadOnlyList<object> Textures => _textures;
        // NEAREST magnification is not a stylistic choice: the PS1 point-samples, and filtering a
        // baked page bleeds neighbouring atlas entries (and the transparent-black border) across
        // every sprite edge. REPEAT matches the hardware's `uv & 0xFF` wrap.
        public IReadOnlyList<object> Samplers { get; } =
            new[] { new { magFilter = 9728, minFilter = 9728, wrapS = 10497, wrapT = 10497 } };

        public int MaterialIndex(MatKey key, string? name)
        {
            if (_matIndex.TryGetValue(key, out var mi)) return mi;
            mi = _materials.Count;
            _matIndex[key] = mi;

            int? texIndex = null;
            TextureBaker.PageStats st = default;
            if (key.Tex && _vram != null)
            {
                var rgba = TextureBaker.Bake(_vram, key.TPage, key.Clut, key.TexWin, key.Semi, out st);
                var png = TextureBaker.EncodePng(rgba);
                var hash = Convert.ToHexString(SHA256.HashData(png));
                if (!_imageByContent.TryGetValue(hash, out var img))
                {
                    img = _png.Count;
                    _png.Add(png);
                    _pngName.Add($"tp{key.TPage:X4}_cl{key.Clut:X4}_tw{key.TexWin:X5}{(key.Semi ? "_semi" : "")}.png");
                    _imageByContent[hash] = img;
                }
                texIndex = _textures.Count;
                _textures.Add(new { sampler = 0, source = img });
            }
            else if (key.Tex) _missingVram++;
            else _untextured++;

            var (twMaskX, twMaskY, twOffX, twOffY) = TextureBaker.UnpackTexWindow(key.TexWin);
            var extras = new
            {
                ps1Textured = key.Tex,
                ps1TPage = key.TPage,
                ps1Clut = key.Clut,
                ps1Depth = (key.TPage >> 7) & 3,          // 0 = 4bpp, 1 = 8bpp, 2/3 = 15bpp direct
                ps1BlendMode = (key.TPage >> 5) & 3,      // 0 avg, 1 additive, 2 subtractive, 3 quarter
                ps1SemiTransparent = key.Semi,
                ps1TexWindow = new { maskX = twMaskX, maskY = twMaskY, offX = twOffX, offY = twOffY },
                ps1TransparentTexels = st.TransparentTexels,
                ps1StpTexels = st.StpTexels,
            };
            // MASK, not OPAQUE: a PS1 texel of 0x0000 is a hole, and the whole point of baking
            // alpha is that those holes stay holes instead of becoming black boxes.
            _materials.Add(texIndex is int ti
                ? new
                {
                    name = name ?? $"tp{key.TPage:X4}_cl{key.Clut:X4}",
                    pbrMetallicRoughness = new
                    {
                        baseColorFactor = new[] { 1.0, 1.0, 1.0, 1.0 },
                        baseColorTexture = new { index = ti, texCoord = 0 },
                        metallicFactor = 0.0,
                        roughnessFactor = 1.0,
                    },
                    alphaMode = key.Semi ? "BLEND" : "MASK",
                    alphaCutoff = 0.5,
                    doubleSided = true,
                    extras,
                }
                : (object)new
                {
                    name = name ?? $"untextured_bm{(key.TPage >> 5) & 3}{(key.Semi ? "_semi" : "")}",
                    pbrMetallicRoughness = new
                    {
                        baseColorFactor = new[] { 1.0, 1.0, 1.0, 1.0 },
                        metallicFactor = 0.0,
                        roughnessFactor = 1.0,
                    },
                    alphaMode = "OPAQUE",
                    doubleSided = true,
                    extras,
                });

            if (key.Tex)
                _report.Add(new
                {
                    tpage = $"0x{key.TPage:X4}",
                    clut = $"0x{key.Clut:X4}",
                    depth = st.Depth switch { 0 => "4bpp", 1 => "8bpp", _ => "15bpp" },
                    semi = key.Semi,
                    transparentTexels = st.TransparentTexels,
                    stpTexels = st.StpTexels,
                    opaqueTexels = st.OpaqueTexels,
                });
            return mi;
        }

        /// <summary>Append every deduplicated PNG to the .glb binary chunk and declare the
        /// matching bufferViews/images. Called once, after all geometry is written.</summary>
        public void WriteImages(MemoryStream bin, BinaryWriter bw, List<object> bufferViews)
        {
            for (int i = 0; i < _png.Count; i++)
            {
                long off = bin.Position;
                bw.Write(_png[i]);
                Pad4(bw);
                int bv = bufferViews.Count;
                bufferViews.Add(new { buffer = 0, byteOffset = off, byteLength = _png[i].Length });
                _images.Add(new { bufferView = bv, mimeType = "image/png", name = _pngName[i] });
            }
        }

        /// <summary>Also drop the baked pages next to the .glb as loose PNGs. The .glb is already
        /// self-contained; these exist so a texture can be eyeballed or diffed without opening a
        /// 3D viewer, which is how a wrong CLUT or a wrong transparency rule is actually caught.</summary>
        public void DumpPngs()
        {
            if (_png.Count == 0) return;
            Directory.CreateDirectory(_texDir);
            for (int i = 0; i < _png.Count; i++)
                File.WriteAllBytes(Path.Combine(_texDir, _pngName[i]), _png[i]);
        }

        public object Report() => new
        {
            vramAvailable = _vram != null,
            distinctMaterials = _materials.Count,
            texturedMaterials = _textures.Count,
            untexturedMaterials = _untextured,
            texturedButNoVram = _missingVram,
            distinctImagesAfterDedup = _png.Count,
            pngDir = _texDir,
            pages = _report,
        };
    }

    // PS1 modulation neutral is 128 (texel * colour / 128), so a textured vertex colour divides by
    // 128 and clamps; an untextured one IS the colour and divides by 255. See the class remarks.
    static void AddColor(List<float> dst, byte r, byte g, byte b, bool textured)
    {
        float d = textured ? 128f : 255f;
        dst.Add(MathF.Min(1f, r / d)); dst.Add(MathF.Min(1f, g / d)); dst.Add(MathF.Min(1f, b / d));
    }

    // Texel CENTRE within a 256-texel page — see the class remarks on why this is not /255.
    static void AddUv(List<float> dst, short u, short v)
    {
        dst.Add((u + 0.5f) / TextureBaker.PageSize);
        dst.Add((v + 0.5f) / TextureBaker.PageSize);
    }

    static void WriteGlb(string glbPath, object gltf, MemoryStream bin)
    {
        var json = JsonSerializer.Serialize(gltf, new JsonSerializerOptions { });
        var jsonBytes = Encoding.UTF8.GetBytes(PadJson(json));
        var binBytes = bin.ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(glbPath))!);
        using var fs = new FileStream(glbPath, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        w.Write(0x46546C67u);              // "glTF"
        w.Write(2u);                        // version
        w.Write((uint)(12 + 8 + jsonBytes.Length + 8 + binBytes.Length));
        w.Write((uint)jsonBytes.Length); w.Write(0x4E4F534Au); w.Write(jsonBytes); // JSON chunk
        w.Write((uint)binBytes.Length); w.Write(0x004E4942u); w.Write(binBytes);   // BIN chunk
    }

    static readonly List<object> _bufferViews = new();
    static readonly List<object> _accessors = new();

    static object MakeVec3Accessor(int bv, List<float> data)
    {
        float minx = float.MaxValue, miny = float.MaxValue, minz = float.MaxValue;
        float maxx = float.MinValue, maxy = float.MinValue, maxz = float.MinValue;
        for (int i = 0; i + 2 < data.Count; i += 3)
        {
            minx = Math.Min(minx, data[i]); maxx = Math.Max(maxx, data[i]);
            miny = Math.Min(miny, data[i + 1]); maxy = Math.Max(maxy, data[i + 1]);
            minz = Math.Min(minz, data[i + 2]); maxz = Math.Max(maxz, data[i + 2]);
        }
        if (data.Count == 0) { minx = miny = minz = maxx = maxy = maxz = 0; }
        return new
        {
            bufferView = bv, byteOffset = 0, componentType = 5126, count = data.Count / 3, type = "VEC3",
            min = new[] { minx, miny, minz }, max = new[] { maxx, maxy, maxz }
        };
    }

    static void Pad4(BinaryWriter bw) { while (bw.BaseStream.Position % 4 != 0) bw.Write((byte)0); }
    static string PadJson(string json) { while ((json.Length % 4) != 0) json += " "; return json; }
}
