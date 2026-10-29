using System.Text;
using System.Text.Json.Nodes;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.World.Glb
{
    /// <summary>
    /// Reads a binary glTF file (.glb) into an inactive GameObject with meshes, URP Lit materials and textures.
    /// MIT, grown from the reader of Breed to Seed; no S1MAPI (GPL-3).
    ///
    /// <para>What it reads: one embedded BIN buffer; triangle primitives with POSITION, NORMAL, TEXCOORD_0 and
    /// indices; several primitives and materials per mesh; the node tree with TRS or matrices; base colour, normal
    /// and emissive textures from PNG or JPEG images in the buffer; base colour, metallic, roughness and emissive
    /// factors, alpha cutoff, double-sided. What it refuses loudly instead of loading half: external buffers and
    /// image files, sparse accessors, non-triangle primitives, and any extension the file marks as required.</para>
    ///
    /// <para>glTF is right-handed and Unity left-handed: Z is negated on positions and normals, rotations follow, and
    /// the triangle winding is reversed so faces still point out. V of the texture coordinates is flipped.</para>
    /// </summary>
    internal static class GlbReader
    {
        private const uint MagicGltf = 0x46546C67;
        private const uint ChunkJson = 0x4E4F534A;
        private const uint ChunkBin = 0x004E4942;

        private static readonly HashSet<string> KnownExtensions = new(StringComparer.Ordinal) { "KHR_materials_emissive_strength" };

        /// <summary>What a read produced, for the log line of the caller.</summary>
        internal sealed class Result
        {
            public GameObject Root;
            public int Meshes, Primitives, Materials, Textures, Vertices, Triangles;
            public readonly List<string> Notes = new();
        }

        private sealed class Ctx
        {
            public JsonNode Json;
            public byte[] Bin;
            public string Name;
            public Result Result;
            public readonly Dictionary<(int index, bool linear), Texture2D> Textures = new();
            public List<Material> Materials;
            public readonly Dictionary<int, List<(Mesh mesh, int material)>> Meshes = new();
        }

        /// <summary>Reads a GLB; null with the reason logged when the file cannot be used.</summary>
        internal static Result Read(byte[] glb, string name)
        {
            if (glb == null || glb.Length < 20)
            {
                Core.Log.Error($"glb '{name}': {glb?.Length ?? 0} bytes is too small for a GLB");
                return null;
            }
            if (BitConverter.ToUInt32(glb, 0) != MagicGltf)
            {
                Core.Log.Error($"glb '{name}': not a binary glTF file (no 'glTF' magic)");
                return null;
            }
            uint version = BitConverter.ToUInt32(glb, 4);
            if (version != 2)
            {
                Core.Log.Error($"glb '{name}': glTF version {version}; only version 2 is read");
                return null;
            }
            var ctx = new Ctx { Name = name, Result = new Result() };
            int offset = 12;
            while (offset + 8 <= glb.Length)
            {
                int length = (int)BitConverter.ToUInt32(glb, offset);
                uint type = BitConverter.ToUInt32(glb, offset + 4);
                int start = offset + 8;
                if (start + length > glb.Length)
                {
                    Core.Log.Error($"glb '{name}': a chunk of {length} bytes runs past the end of the file");
                    return null;
                }
                if (type == ChunkJson && ctx.Json == null)
                {
                    try
                    {
                        ctx.Json = JsonNode.Parse(Encoding.UTF8.GetString(glb, start, length));
                    }
                    catch (Exception e)
                    {
                        Core.Log.Error($"glb '{name}': the JSON chunk does not parse: {e.Message}");
                        return null;
                    }
                }
                else if (type == ChunkBin && ctx.Bin == null)
                {
                    ctx.Bin = new byte[length];
                    Buffer.BlockCopy(glb, start, ctx.Bin, 0, length);
                }
                offset = start + length;
                if ((offset & 3) != 0) offset += 4 - (offset & 3);
            }
            if (ctx.Json == null)
            {
                Core.Log.Error($"glb '{name}': no JSON chunk");
                return null;
            }
            if (!Supported(ctx)) return null;
            try
            {
                return Build(ctx);
            }
            catch (Exception e)
            {
                Core.Log.Error($"glb '{name}': reading failed: {e}");
                if (ctx.Result.Root != null) Object.Destroy(ctx.Result.Root);
                return null;
            }
        }

        /// <summary>Refuses what the reader would only load half: required extensions, external buffers.</summary>
        private static bool Supported(Ctx ctx)
        {
            var required = Arr(ctx.Json, "extensionsRequired").Select(x => x?.GetValue<string>()).Where(x => x != null && !KnownExtensions.Contains(x)).ToList();
            if (required.Count > 0)
            {
                Core.Log.Error($"glb '{ctx.Name}': requires {string.Join(", ", required)}, which this reader does not know; the model is not loaded");
                return false;
            }
            var used = Arr(ctx.Json, "extensionsUsed").Select(x => x?.GetValue<string>()).Where(x => x != null && !KnownExtensions.Contains(x)).ToList();
            if (used.Count > 0) ctx.Result.Notes.Add($"extensions not read: {string.Join(", ", used)}");
            var buffers = Arr(ctx.Json, "buffers").ToList();
            if (buffers.Count > 1 || buffers.Any(b => b?["uri"] != null))
            {
                Core.Log.Error($"glb '{ctx.Name}': {buffers.Count} buffers, external ones among them; only the GLB's own BIN chunk is read. Export as one .glb with everything embedded");
                return false;
            }
            if (buffers.Count == 1 && ctx.Bin == null)
            {
                Core.Log.Error($"glb '{ctx.Name}': the file names a buffer but has no BIN chunk");
                return false;
            }
            if (Arr(ctx.Json, "images").Any(i => i?["uri"] != null))
            {
                Core.Log.Error($"glb '{ctx.Name}': an image is an external file or data URI; only images in the BIN chunk are read. Export with embedded textures");
                return false;
            }
            return true;
        }

        private static Result Build(Ctx ctx)
        {
            var r = ctx.Result;
            ctx.Materials = Arr(ctx.Json, "materials").Select((m, i) => ReadMaterial(ctx, m, i)).ToList();
            r.Materials = ctx.Materials.Count;
            var root = new GameObject(ctx.Name);
            root.SetActive(false);
            r.Root = root;
            var nodes = ctx.Json["nodes"] as JsonArray;
            var scenes = ctx.Json["scenes"] as JsonArray;
            int sceneIndex = Int(ctx.Json, "scene") ?? 0;
            var sceneRoots = scenes != null && sceneIndex < scenes.Count ? Arr(scenes[sceneIndex], "nodes").Select(n => n.GetValue<int>()).ToList() : null;
            if (nodes != null && sceneRoots != null)
                foreach (int n in sceneRoots) BuildNode(ctx, root.transform, nodes, n, 0);
            else
            {
                // A file without a scene graph: every mesh at the origin.
                var meshes = Arr(ctx.Json, "meshes").ToList();
                for (int m = 0; m < meshes.Count; m++) AttachMesh(ctx, root.transform, m, "mesh" + m);
            }
            if (r.Primitives == 0)
            {
                Core.Log.Error($"glb '{ctx.Name}': no triangle primitive could be built");
                Object.Destroy(root);
                return null;
            }
            return r;
        }

        private static void BuildNode(Ctx ctx, Transform parent, JsonArray nodes, int index, int depth)
        {
            if (index < 0 || index >= nodes.Count || depth > 64) return;
            var node = nodes[index];
            if (node == null) return;
            if (node["skin"] != null) ctx.Result.Notes.Add($"node {index} is skinned; it is drawn in its bind pose");
            var go = new GameObject(Str(node, "name") ?? "node" + index);
            go.transform.SetParent(parent, false);
            SetTransform(go.transform, node);
            if (Int(node, "mesh") is int mesh) AttachMesh(ctx, go.transform, mesh, go.name);
            foreach (var c in Arr(node, "children")) BuildNode(ctx, go.transform, nodes, c.GetValue<int>(), depth + 1);
        }

        private static void SetTransform(Transform t, JsonNode node)
        {
            if (node["matrix"] is JsonArray m && m.Count == 16)
            {
                // Column-major; mirrored on Z by S * M * S with S = diag(1, 1, -1).
                var mat = new Matrix4x4();
                for (int c = 0; c < 4; c++)
                    mat.SetColumn(c, new Vector4(F(m[c * 4]), F(m[c * 4 + 1]), F(m[c * 4 + 2]), F(m[c * 4 + 3])));
                var s = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
                mat = s * mat * s;
                t.localPosition = mat.GetColumn(3);
                t.localRotation = mat.rotation;
                t.localScale = mat.lossyScale;
                return;
            }
            if (node["translation"] is JsonArray tr && tr.Count == 3)
                t.localPosition = new Vector3(F(tr[0]), F(tr[1]), -F(tr[2]));
            if (node["rotation"] is JsonArray ro && ro.Count == 4)
                t.localRotation = new Quaternion(-F(ro[0]), -F(ro[1]), F(ro[2]), F(ro[3]));
            if (node["scale"] is JsonArray sc && sc.Count == 3)
                t.localScale = new Vector3(F(sc[0]), F(sc[1]), F(sc[2]));
        }

        private static void AttachMesh(Ctx ctx, Transform parent, int meshIndex, string name)
        {
            if (!ctx.Meshes.TryGetValue(meshIndex, out var parts))
            {
                parts = ReadMesh(ctx, meshIndex, name);
                ctx.Meshes[meshIndex] = parts;
                if (parts.Count > 0) ctx.Result.Meshes++;
            }
            for (int i = 0; i < parts.Count; i++)
            {
                var (mesh, material) = parts[i];
                var go = parts.Count == 1 ? parent.gameObject : new GameObject(name + "_" + i);
                if (go != parent.gameObject) go.transform.SetParent(parent, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = material >= 0 && material < ctx.Materials.Count ? ctx.Materials[material] : Fallback();
            }
        }

        private static List<(Mesh, int)> ReadMesh(Ctx ctx, int meshIndex, string name)
        {
            var list = new List<(Mesh, int)>();
            var meshes = ctx.Json["meshes"] as JsonArray;
            if (meshes == null || meshIndex < 0 || meshIndex >= meshes.Count)
            {
                ctx.Result.Notes.Add($"mesh {meshIndex} does not exist");
                return list;
            }
            var prims = Arr(meshes[meshIndex], "primitives").ToList();
            for (int p = 0; p < prims.Count; p++)
            {
                var prim = prims[p];
                int mode = Int(prim, "mode") ?? 4;
                if (mode != 4)
                {
                    ctx.Result.Notes.Add($"mesh {meshIndex} primitive {p} has mode {mode}, not triangles; left out");
                    continue;
                }
                var attrs = prim["attributes"];
                if (Int(attrs, "POSITION") is not int posAcc)
                {
                    ctx.Result.Notes.Add($"mesh {meshIndex} primitive {p} has no POSITION; left out");
                    continue;
                }
                var pos = ReadFloats(ctx, posAcc, "VEC3");
                if (pos == null) continue;
                int count = pos.Length / 3;
                var verts = new Vector3[count];
                for (int i = 0; i < count; i++) verts[i] = new Vector3(pos[i * 3], pos[i * 3 + 1], -pos[i * 3 + 2]);
                Vector3[] normals = null;
                if (Int(attrs, "NORMAL") is int nAcc && ReadFloats(ctx, nAcc, "VEC3") is float[] nf && nf.Length == count * 3)
                {
                    normals = new Vector3[count];
                    for (int i = 0; i < count; i++) normals[i] = new Vector3(nf[i * 3], nf[i * 3 + 1], -nf[i * 3 + 2]);
                }
                Vector2[] uvs = null;
                if (Int(attrs, "TEXCOORD_0") is int uvAcc && ReadFloats(ctx, uvAcc, "VEC2") is float[] uf && uf.Length == count * 2)
                {
                    uvs = new Vector2[count];
                    for (int i = 0; i < count; i++) uvs[i] = new Vector2(uf[i * 2], 1f - uf[i * 2 + 1]);
                }
                int[] idx = Int(prim, "indices") is int iAcc ? ReadIndices(ctx, iAcc) : Enumerable.Range(0, count).ToArray();
                if (idx == null || idx.Length < 3) continue;
                for (int i = 0; i + 2 < idx.Length; i += 3) (idx[i + 1], idx[i + 2]) = (idx[i + 2], idx[i + 1]);

                var mesh = new Mesh { name = $"{name}_{p}" };
                if (count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(ToIl2Cpp(verts));
                if (normals != null) mesh.SetNormals(ToIl2Cpp(normals));
                if (uvs != null) mesh.SetUVs(0, ToIl2Cpp(uvs));
                mesh.SetTriangles(ToIl2Cpp(idx), 0);
                if (normals == null) mesh.RecalculateNormals();
                // Normal maps need tangents; the file's own are not read, Unity's from the UVs serve.
                if (uvs != null) mesh.RecalculateTangents();
                mesh.RecalculateBounds();
                mesh.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                list.Add((mesh, Int(prim, "material") ?? -1));
                ctx.Result.Primitives++;
                ctx.Result.Vertices += count;
                ctx.Result.Triangles += idx.Length / 3;
            }
            return list;
        }

        // ---- accessors

        private static readonly Dictionary<string, int> Components = new() { ["SCALAR"] = 1, ["VEC2"] = 2, ["VEC3"] = 3, ["VEC4"] = 4 };

        /// <summary>A float accessor of the given type, normalised integers turned into floats.</summary>
        private static float[] ReadFloats(Ctx ctx, int index, string type)
        {
            var acc = Arr(ctx.Json, "accessors").ElementAtOrDefault(index);
            if (acc == null)
            {
                ctx.Result.Notes.Add($"accessor {index} does not exist");
                return null;
            }
            if (Str(acc, "type") != type)
            {
                ctx.Result.Notes.Add($"accessor {index} is {Str(acc, "type")}, {type} was expected; left out");
                return null;
            }
            if (acc["sparse"] != null)
            {
                ctx.Result.Notes.Add($"accessor {index} is sparse; left out");
                return null;
            }
            int comps = Components[type];
            int ct = Int(acc, "componentType") ?? 0;
            int size = ct == 5126 ? 4 : ct == 5123 || ct == 5122 ? 2 : ct == 5121 || ct == 5120 ? 1 : 0;
            if (size == 0)
            {
                ctx.Result.Notes.Add($"accessor {index} has component type {ct}; left out");
                return null;
            }
            bool norm = acc["normalized"] is JsonValue nv && nv.TryGetValue<bool>(out var b) && b;
            if (ct != 5126 && !norm)
            {
                ctx.Result.Notes.Add($"accessor {index} holds integers that are not normalised; left out");
                return null;
            }
            int count = Int(acc, "count") ?? 0;
            if (!View(ctx, acc, comps * size, out int start, out int stride)) return null;
            var result = new float[count * comps];
            for (int i = 0; i < count; i++)
            {
                int o = start + i * stride;
                for (int c = 0; c < comps; c++)
                {
                    int q = o + c * size;
                    result[i * comps + c] = ct switch
                    {
                        5126 => BitConverter.ToSingle(ctx.Bin, q),
                        5123 => BitConverter.ToUInt16(ctx.Bin, q) / 65535f,
                        5122 => Math.Max(BitConverter.ToInt16(ctx.Bin, q) / 32767f, -1f),
                        5121 => ctx.Bin[q] / 255f,
                        _ => Math.Max((sbyte)ctx.Bin[q] / 127f, -1f),
                    };
                }
            }
            return result;
        }

        private static int[] ReadIndices(Ctx ctx, int index)
        {
            var acc = Arr(ctx.Json, "accessors").ElementAtOrDefault(index);
            if (acc == null) return null;
            int ct = Int(acc, "componentType") ?? 0;
            int size = ct == 5125 ? 4 : ct == 5123 ? 2 : ct == 5121 ? 1 : 0;
            if (size == 0)
            {
                ctx.Result.Notes.Add($"index accessor {index} has component type {ct}; left out");
                return null;
            }
            int count = Int(acc, "count") ?? 0;
            if (!View(ctx, acc, size, out int start, out int stride)) return null;
            var result = new int[count];
            for (int i = 0; i < count; i++)
            {
                int o = start + i * stride;
                result[i] = size == 4 ? (int)BitConverter.ToUInt32(ctx.Bin, o) : size == 2 ? BitConverter.ToUInt16(ctx.Bin, o) : ctx.Bin[o];
            }
            return result;
        }

        private static bool View(Ctx ctx, JsonNode acc, int elementSize, out int start, out int stride)
        {
            start = 0;
            stride = elementSize;
            if (Int(acc, "bufferView") is not int vi || Arr(ctx.Json, "bufferViews").ElementAtOrDefault(vi) is not JsonNode view)
            {
                ctx.Result.Notes.Add("an accessor without a buffer view; left out");
                return false;
            }
            start = (Int(view, "byteOffset") ?? 0) + (Int(acc, "byteOffset") ?? 0);
            if (Int(view, "byteStride") is int s && s > 0) stride = s;
            int count = Int(acc, "count") ?? 0;
            if (ctx.Bin == null || start + (count - 1) * stride + elementSize > ctx.Bin.Length)
            {
                ctx.Result.Notes.Add("an accessor runs past the end of the buffer; left out");
                return false;
            }
            return true;
        }

        // ---- materials and textures

        private static Material ReadMaterial(Ctx ctx, JsonNode m, int i)
        {
            var mat = NewMaterial(Str(m, "name") ?? $"{ctx.Name} material {i}");
            var pbr = m?["pbrMetallicRoughness"];
            var baseColor = Color.white;
            if (pbr?["baseColorFactor"] is JsonArray f && f.Count >= 3)
                baseColor = new Color(F(f[0]), F(f[1]), F(f[2]), f.Count > 3 ? F(f[3]) : 1f);
            Set(mat, "_BaseColor", baseColor);
            Set(mat, "_Color", baseColor);
            if (TextureOf(ctx, pbr?["baseColorTexture"], false, "base colour", mat.name) is Texture2D albedo)
            {
                SetTex(mat, "_BaseMap", albedo);
                SetTex(mat, "_MainTex", albedo);
            }
            SetF(mat, "_Metallic", pbr?["metallicFactor"] is JsonNode mf ? F(mf) : 1f);
            SetF(mat, "_Smoothness", 1f - (pbr?["roughnessFactor"] is JsonNode rf ? F(rf) : 1f));
            if (pbr?["metallicRoughnessTexture"] != null)
                ctx.Result.Notes.Add($"'{mat.name}': the metallic-roughness texture is not read, its factors are");
            if (m?["occlusionTexture"] != null)
                ctx.Result.Notes.Add($"'{mat.name}': the occlusion texture is not read");
            if (TextureOf(ctx, m?["normalTexture"], true, "normal", mat.name) is Texture2D normal)
            {
                SetTex(mat, "_BumpMap", normal);
                SetF(mat, "_BumpScale", m["normalTexture"]?["scale"] is JsonNode ns ? F(ns) : 1f);
                mat.EnableKeyword("_NORMALMAP");
            }
            var emissive = Color.black;
            if (m?["emissiveFactor"] is JsonArray e && e.Count >= 3) emissive = new Color(F(e[0]), F(e[1]), F(e[2]), 1f);
            var emissiveTex = TextureOf(ctx, m?["emissiveTexture"], false, "emissive", mat.name);
            if (emissive.maxColorComponent > 0.001f)
            {
                float strength = m?["extensions"]?["KHR_materials_emissive_strength"]?["emissiveStrength"] is JsonNode es ? F(es) : 1f;
                mat.EnableKeyword("_EMISSION");
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                Set(mat, "_EmissionColor", emissive * Mathf.Max(strength, 0f));
                if (emissiveTex != null) SetTex(mat, "_EmissionMap", emissiveTex);
            }
            switch (Str(m, "alphaMode") ?? "OPAQUE")
            {
                case "MASK":
                    SetF(mat, "_AlphaClip", 1f);
                    SetF(mat, "_Cutoff", m?["alphaCutoff"] is JsonNode ac ? F(ac) : 0.5f);
                    mat.EnableKeyword("_ALPHATEST_ON");
                    break;
                case "BLEND":
                    ctx.Result.Notes.Add($"'{mat.name}': alpha blending is drawn opaque");
                    break;
            }
            if (m?["doubleSided"] is JsonValue ds && ds.TryGetValue<bool>(out var two) && two)
                SetF(mat, "_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            return mat;
        }

        private static Texture2D TextureOf(Ctx ctx, JsonNode info, bool linear, string what, string material)
        {
            if (info == null) return null;
            if ((Int(info, "texCoord") ?? 0) != 0)
            {
                ctx.Result.Notes.Add($"'{material}': the {what} texture uses a second UV set; left out");
                return null;
            }
            if (info["extensions"]?["KHR_texture_transform"] != null)
                ctx.Result.Notes.Add($"'{material}': the {what} texture's transform is not read");
            if (Int(info, "index") is not int ti) return null;
            if (ctx.Textures.TryGetValue((ti, linear), out var cached)) return cached;
            var tex = Arr(ctx.Json, "textures").ElementAtOrDefault(ti);
            if (tex == null || Int(tex, "source") is not int si)
            {
                ctx.Result.Notes.Add($"'{material}': texture {ti} has no image; left out");
                return null;
            }
            var img = Arr(ctx.Json, "images").ElementAtOrDefault(si);
            if (img == null || Int(img, "bufferView") is not int vi || Arr(ctx.Json, "bufferViews").ElementAtOrDefault(vi) is not JsonNode view)
            {
                ctx.Result.Notes.Add($"'{material}': image {si} is not in the buffer; left out");
                return null;
            }
            int start = Int(view, "byteOffset") ?? 0;
            int length = Int(view, "byteLength") ?? 0;
            var bytes = new byte[length];
            Buffer.BlockCopy(ctx.Bin, start, bytes, 0, length);
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = Str(img, "name") ?? $"{ctx.Name} image {si}" };
            if (!ImageConversion.LoadImage(t, bytes, false))
            {
                Core.Log.Error($"glb '{ctx.Name}': image {si} ({Str(img, "mimeType") ?? "no type"}, {length} bytes) does not decode; '{material}' draws without its {what} texture");
                Object.Destroy(t);
                return null;
            }
            Sampler(ctx, tex, t);
            t.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            ctx.Textures[(ti, linear)] = t;
            ctx.Result.Textures++;
            return t;
        }

        private static void Sampler(Ctx ctx, JsonNode tex, Texture2D t)
        {
            var s = Int(tex, "sampler") is int si ? Arr(ctx.Json, "samplers").ElementAtOrDefault(si) : null;
            t.wrapModeU = Wrap(Int(s, "wrapS"));
            t.wrapModeV = Wrap(Int(s, "wrapT"));
            int mag = Int(s, "magFilter") ?? 9729;
            int min = Int(s, "minFilter") ?? 9987;
            t.filterMode = mag == 9728 ? FilterMode.Point : min == 9987 || min == 9986 ? FilterMode.Trilinear : FilterMode.Bilinear;
            t.anisoLevel = 4;
        }

        private static TextureWrapMode Wrap(int? mode) => mode switch
        {
            33071 => TextureWrapMode.Clamp,
            33648 => TextureWrapMode.Mirror,
            _ => TextureWrapMode.Repeat,
        };

        private static Material _fallback;

        private static Material Fallback()
        {
            if (_fallback == null)
            {
                _fallback = NewMaterial("glb fallback");
                Set(_fallback, "_BaseColor", new Color(0.8f, 0.8f, 0.8f));
            }
            return _fallback;
        }

        private static Material NewMaterial(string name)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Core.Log.Error($"glb: the shader 'Universal Render Pipeline/Lit' is not loaded; '{name}' uses the error shader");
                shader = Shader.Find("Hidden/InternalErrorShader");
            }
            var mat = new Material(shader) { name = name };
            mat.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            return mat;
        }

        private static void Set(Material m, string p, Color c)
        {
            if (m.HasProperty(p)) m.SetColor(p, c);
        }

        private static void SetF(Material m, string p, float f)
        {
            if (m.HasProperty(p)) m.SetFloat(p, f);
        }

        private static void SetTex(Material m, string p, Texture t)
        {
            if (m.HasProperty(p)) m.SetTexture(p, t);
        }

        // ---- json and il2cpp helpers

        private static IEnumerable<JsonNode> Arr(JsonNode n, string name) => n?[name] is JsonArray a ? a : Enumerable.Empty<JsonNode>();

        private static string Str(JsonNode n, string name) => n?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        private static int? Int(JsonNode n, string name) => n?[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

        private static float F(JsonNode n) => n.GetValue<float>();

        private static Il2CppSystem.Collections.Generic.List<Vector3> ToIl2Cpp(Vector3[] v)
        {
            var l = new Il2CppSystem.Collections.Generic.List<Vector3>(v.Length);
            foreach (var x in v) l.Add(x);
            return l;
        }

        private static Il2CppSystem.Collections.Generic.List<Vector2> ToIl2Cpp(Vector2[] v)
        {
            var l = new Il2CppSystem.Collections.Generic.List<Vector2>(v.Length);
            foreach (var x in v) l.Add(x);
            return l;
        }

        private static Il2CppSystem.Collections.Generic.List<int> ToIl2Cpp(int[] v)
        {
            var l = new Il2CppSystem.Collections.Generic.List<int>(v.Length);
            foreach (var x in v) l.Add(x);
            return l;
        }
    }
}
