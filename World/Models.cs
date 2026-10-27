using MapExpansions.Lifecycle;
using MapExpansions.World.Glb;
using MelonLoader.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.World
{
    /// <summary>
    /// glTF models of an expansion: read once per file and collider, kept as an inactive template below the
    /// expansion's copy templates, and copied like a main-world template from there.
    /// </summary>
    internal static class Models
    {
        private static readonly Dictionary<(string expansion, string file, ModelCollider collider), GameObject> Cache = new();

        /// <summary>Folder of an expansion's loose model files; a file there wins over an embedded one.</summary>
        internal static string Folder(string expansionId) => Path.Combine(MelonEnvironment.ModsDirectory, expansionId, "models");

        /// <summary>The template of a model, read on first use; null with the reason logged.</summary>
        internal static Transform Template(BuiltExpansion b, string file, ModelCollider collider, Transform templates)
        {
            var key = (b.Id, file, collider);
            if (Cache.TryGetValue(key, out var cached) && cached != null) return cached.transform;
            var (bytes, from) = Bytes(b, file);
            if (bytes == null) return null;
            var result = GlbReader.Read(bytes, Path.GetFileNameWithoutExtension(file));
            if (result == null)
            {
                Core.Log.Error($"models: '{b.Id}' '{file}' from {from} could not be read; it is not placed");
                return null;
            }
            var root = result.Root;
            root.name = "Model " + file;
            root.transform.SetParent(templates, false);
            int colliders = AddColliders(root, collider);
            Cache[key] = root;
            foreach (var note in result.Notes) Core.Log.Warning($"models: '{b.Id}' '{file}': {note}");
            Core.Log.Msg($"models: '{b.Id}' '{file}' read from {from}: {result.Meshes} meshes, {result.Primitives} parts, {result.Materials} materials, " +
                         $"{result.Textures} textures, {result.Vertices} vertices, {result.Triangles} triangles, {colliders} colliders ({collider})");
            return root.transform;
        }

        /// <summary>The model file's bytes: the loose file first, then an embedded resource of the layer's assembly.</summary>
        private static (byte[] bytes, string from) Bytes(BuiltExpansion b, string file)
        {
            if (file.Contains("..") || Path.IsPathRooted(file))
            {
                Core.Log.Error($"models: '{b.Id}' file '{file}' leaves the models folder; it is not read");
                return (null, null);
            }
            var loose = Path.Combine(Folder(b.Id), file);
            var asm = b.Definition.Layer?.Assembly;
            string suffix = "." + file.Replace('/', '.').Replace('\\', '.');
            var resource = asm?.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (File.Exists(loose))
            {
                try
                {
                    if (resource != null) Core.Log.Warning($"models: '{b.Id}' '{file}': the loose file {loose} wins over the embedded {resource}");
                    return (File.ReadAllBytes(loose), loose);
                }
                catch (Exception e)
                {
                    Core.Log.Error($"models: '{b.Id}' reading {loose} failed: {e.Message}");
                    return (null, null);
                }
            }
            if (resource == null)
            {
                Core.Log.Error($"models: '{b.Id}' has no model '{file}': no file {loose} and no resource ending in '{suffix}' in " +
                               $"{asm?.GetName().Name ?? "the layer's assembly (the expansion has no layer)"}");
                return (null, null);
            }
            using var stream = asm.GetManifestResourceStream(resource);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return (ms.ToArray(), asm.GetName().Name + ":" + resource);
        }

        /// <summary>The model files an expansion offers: loose files and embedded ones, by name.</summary>
        internal static List<string> Files(BuiltExpansion b)
        {
            var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var folder = Folder(b.Id);
            if (Directory.Exists(folder))
                foreach (var f in Directory.GetFiles(folder, "*.glb", SearchOption.AllDirectories))
                    files.Add(Path.GetRelativePath(folder, f).Replace('\\', '/'));
            var asm = b.Definition.Layer?.Assembly;
            if (asm != null)
                foreach (var n in asm.GetManifestResourceNames().Where(n => n.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)))
                {
                    // "SerenaFlats.models.kiosk.glb" offers "kiosk.glb".
                    var stem = n.Substring(0, n.Length - 4);
                    files.Add(stem.Substring(stem.LastIndexOf('.') + 1) + ".glb");
                }
            return files.ToList();
        }

        private static int AddColliders(GameObject root, ModelCollider collider)
        {
            var filters = root.GetComponentsInChildren<MeshFilter>(true);
            switch (collider)
            {
                case ModelCollider.Mesh:
                    foreach (var f in filters) f.gameObject.AddComponent<MeshCollider>().sharedMesh = f.sharedMesh;
                    return filters.Length;
                case ModelCollider.Box:
                    var bounds = new Bounds();
                    bool any = false;
                    var toRoot = root.transform.worldToLocalMatrix;
                    foreach (var f in filters)
                    {
                        if (f.sharedMesh == null) continue;
                        var m = toRoot * f.transform.localToWorldMatrix;
                        var mb = f.sharedMesh.bounds;
                        for (int i = 0; i < 8; i++)
                        {
                            var c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                            var p = m.MultiplyPoint3x4(c);
                            if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                            else bounds.Encapsulate(p);
                        }
                    }
                    if (!any) return 0;
                    var box = root.AddComponent<BoxCollider>();
                    box.center = bounds.center;
                    box.size = bounds.size;
                    return 1;
                default:
                    return 0;
            }
        }

        /// <summary>Forgets the templates; they lie below the expansion's root and go with it.</summary>
        internal static void Clear() => Cache.Clear();
    }
}
