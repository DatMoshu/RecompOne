using System.Text;
using System.Text.Json;

namespace RecompOne.Runtime.Export;

/// <summary>
/// Minimal, generic glTF 2.0 binary (.glb) writer for <see cref="GteCapture"/> output. Nothing
/// here is PS1/game-specific — a game host picks when to capture and where to write; this just
/// turns a bag of captured triangles into a valid .glb plus a JSON sidecar with the raw capture
/// provenance (rotation/translation, hit/miss stats) that doesn't belong inside the mesh itself.
///
/// v1 scope: geometry (position) + vertex color + UV, grouped into one mesh primitive per
/// material key (PS1 texpage+CLUT). No baked textures yet (no PNG encoder in this project's
/// dependency set) — materials carry texpage/CLUT as glTF `extras` so texturing can be added
/// later without re-capturing. Positions are camera/view space (RT*vertex + TR, pre-perspective-
/// divide) in the units the game's own matrices used, NOT world space — see the sidecar's
/// "space" field.
/// </summary>
public static class GltfWriter
{
    public static void Write(string glbPath, string? sidecarJsonPath = null)
    {
        _bufferViews.Clear(); _accessors.Clear(); _sharedAttrs = new();
        var tris = GteCapture.Triangles;
        var positions = new List<float>();
        var colors = new List<float>();
        var uvs = new List<float>();
        var indices = new List<uint>();
        // material key -> (name, first index into indices, count)
        var byMaterial = new Dictionary<(bool tex, ushort tpage, ushort clut), List<uint>>();
        var screenSpaceIndices = new List<uint>();
        uint vcount = 0;

        void AddVert(GteCapture.CapturedVertex v, bool asScreenSpace)
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
            colors.Add(v.R / 255f); colors.Add(v.G / 255f); colors.Add(v.B / 255f);
            uvs.Add(v.U / 255f); uvs.Add(v.V / 255f);
        }

        foreach (var t in tris)
        {
            bool full = t.A.Has3D && t.B.Has3D && t.C.Has3D;
            AddVert(t.A, !full); AddVert(t.B, !full); AddVert(t.C, !full);
            uint i0 = vcount, i1 = vcount + 1, i2 = vcount + 2;
            vcount += 3;
            if (full)
            {
                var key = (t.Textured, t.TPage, t.Clut);
                if (!byMaterial.TryGetValue(key, out var list)) byMaterial[key] = list = new List<uint>();
                list.Add(i0); list.Add(i1); list.Add(i2);
            }
            else
            {
                screenSpaceIndices.Add(i0); screenSpaceIndices.Add(i1); screenSpaceIndices.Add(i2);
            }
        }

        // ---- build buffer: positions | colors | uvs | indices (one index block per primitive) ----
        using var bin = new MemoryStream();
        using var bw = new BinaryWriter(bin);
        long posOff = bin.Position; foreach (var f in positions) bw.Write(f);
        Pad4(bw);
        long colOff = bin.Position; foreach (var f in colors) bw.Write(f);
        Pad4(bw);
        long uvOff = bin.Position; foreach (var f in uvs) bw.Write(f);
        Pad4(bw);

        var meshPrimitives = new List<object>();
        var materials = new List<object>();
        int matIndex = 0;
        void EmitPrimitive(List<uint> idx, string name, bool textured, ushort tpage, ushort clut)
        {
            if (idx.Count == 0) return;
            long idxOff = bin.Position; foreach (var i in idx) bw.Write(i);
            Pad4(bw);
            int idxBufferView = _bufferViews.Count;
            _bufferViews.Add(new { buffer = 0, byteOffset = idxOff, byteLength = idx.Count * 4, target = 34963 });
            int idxAccessor = _accessors.Count;
            _accessors.Add(new { bufferView = idxBufferView, byteOffset = 0, componentType = 5125 /*UINT*/, count = idx.Count, type = "SCALAR" });
            materials.Add(new
            {
                name,
                pbrMetallicRoughness = new { baseColorFactor = new[] { 1.0, 1.0, 1.0, 1.0 } },
                extras = new { ps1Textured = textured, ps1TPage = tpage, ps1Clut = clut }
            });
            meshPrimitives.Add(new { attributes = _sharedAttrs, indices = idxAccessor, material = matIndex });
            matIndex++;
        }

        // shared vertex attributes (all triangles share one interleaved-by-array-not-struct buffer)
        int posBv = _bufferViews.Count; _bufferViews.Add(new { buffer = 0, byteOffset = posOff, byteLength = positions.Count * 4 });
        int posAcc = _accessors.Count; _accessors.Add(MakeVec3Accessor(posBv, positions));
        int colBv = _bufferViews.Count; _bufferViews.Add(new { buffer = 0, byteOffset = colOff, byteLength = colors.Count * 4 });
        int colAcc = _accessors.Count; _accessors.Add(new { bufferView = colBv, byteOffset = 0, componentType = 5126, count = colors.Count / 3, type = "VEC3" });
        int uvBv = _bufferViews.Count; _bufferViews.Add(new { buffer = 0, byteOffset = uvOff, byteLength = uvs.Count * 4 });
        int uvAcc = _accessors.Count; _accessors.Add(new { bufferView = uvBv, byteOffset = 0, componentType = 5126, count = uvs.Count / 2, type = "VEC2" });
        _sharedAttrs = new { POSITION = posAcc, COLOR_0 = colAcc, TEXCOORD_0 = uvAcc };

        int matGroup = 0;
        foreach (var (key, idx) in byMaterial)
            EmitPrimitive(idx, $"mat_{matGroup++}_tp{key.tpage:X4}_cl{key.clut:X4}", key.tex, key.tpage, key.clut);
        EmitPrimitive(screenSpaceIndices, "background_screenspace", false, 0, 0);

        var gltf = new
        {
            asset = new { version = "2.0", generator = "RecompOne GteCapture exporter" },
            scenes = new[] { new { nodes = new[] { 0 } } },
            scene = 0,
            nodes = new[] { new { mesh = 0, name = "room" } },
            meshes = new[] { new { primitives = meshPrimitives, name = "room" } },
            materials,
            accessors = _accessors,
            bufferViews = _bufferViews,
            buffers = new[] { new { byteLength = bin.Length } },
        };

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

        if (sidecarJsonPath != null)
        {
            var sidecar = new
            {
                space = "camera/view (RT*vertex + TR, pre-perspective-divide) — NOT world space; consistent within one fixed-camera room, not comparable across rooms/camera cuts",
                trianglesTotal = tris.Count,
                trianglesFullyResolved3D = GteCapture.TrisFullyResolved,
                trianglesPartial = GteCapture.TrisPartial,
                trianglesUnresolvedScreenSpaceOnly = GteCapture.TrisUnresolved,
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
        using var bin = new MemoryStream();
        using var bw = new BinaryWriter(bin);

        var nodes = new List<object>();
        var meshes = new List<object>();
        var materials = new List<object>();
        var perObjectStats = new List<object>();
        int matIndex = 0;

        for (int oi = 0; oi < objects.Count; oi++)
        {
            var obj = objects[oi];
            int triCount = obj.TriCount;
            perObjectStats.Add(new { objectIndex = oi, vertices = obj.Verts.Count, triangles = triCount, anchor = new[] { obj.Tx, obj.Ty, obj.Tz } });
            if (obj.Verts.Count == 0 || triCount == 0) continue;

            var positions = new List<float>(obj.Verts.Count * 3);
            var colors = new List<float>(obj.Verts.Count * 3);
            var uvs = new List<float>(obj.Verts.Count * 2);
            foreach (var v in obj.Verts)
            {
                positions.Add(v.x); positions.Add(v.y); positions.Add(v.z);
                colors.Add(v.r / 255f); colors.Add(v.g / 255f); colors.Add(v.b / 255f);
                uvs.Add(v.u / 255f); uvs.Add(v.v / 255f);
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
                materials.Add(new
                {
                    name = $"obj{oi}_tp{key.tpage:X4}_cl{key.clut:X4}",
                    pbrMetallicRoughness = new { baseColorFactor = new[] { 1.0, 1.0, 1.0, 1.0 } },
                    extras = new { ps1Textured = key.tex, ps1TPage = key.tpage, ps1Clut = key.clut }
                });
                primitives.Add(new { attributes = attrs, indices = idxAcc, material = matIndex });
                matIndex++;
            }
            if (primitives.Count == 0) continue;

            meshes.Add(new { primitives, name = $"object_{oi}" });
            nodes.Add(new { mesh = meshes.Count - 1, name = $"object_{oi}" });
        }

        var gltf = new
        {
            asset = new { version = "2.0", generator = "RecompOne GteAccumulator exporter" },
            scenes = new[] { new { nodes = Enumerable.Range(0, nodes.Count).ToArray() } },
            scene = 0,
            nodes,
            meshes,
            materials,
            accessors = _accessors,
            bufferViews = _bufferViews,
            buffers = new[] { new { byteLength = bin.Length } },
        };

        var json = JsonSerializer.Serialize(gltf);
        var jsonBytes = Encoding.UTF8.GetBytes(PadJson(json));
        var binBytes = bin.ToArray();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(glbPath))!);
        using var fs = new FileStream(glbPath, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        w.Write(0x46546C67u); w.Write(2u);
        w.Write((uint)(12 + 8 + jsonBytes.Length + 8 + binBytes.Length));
        w.Write((uint)jsonBytes.Length); w.Write(0x4E4F534Au); w.Write(jsonBytes);
        w.Write((uint)binBytes.Length); w.Write(0x004E4942u); w.Write(binBytes);

        if (sidecarJsonPath != null)
        {
            var sidecar = new
            {
                space = "model space per object (R^T * (view - TR) / 4096) — each object at its own local origin, NOT yet placed into one shared world/room frame",
                objectCount = objects.Count,
                totalTrianglesAllObjects = GteAccumulator.TotalTriangles,
                objects = perObjectStats,
            };
            File.WriteAllText(sidecarJsonPath, JsonSerializer.Serialize(sidecar, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    static readonly List<object> _bufferViews = new();
    static readonly List<object> _accessors = new();
    static object _sharedAttrs = new();

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
