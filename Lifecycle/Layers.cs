using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MelonLoader.Utils;
using UnityEngine;

namespace MapExpansions.Lifecycle
{
    /// <summary>
    /// The JSON layer of an expansion: entries a map editor wrote, laid over the code definition each time the world
    /// is built. An entry replaces the code entry of the same kind and key; the code entries keep their values in a
    /// snapshot, so a layer that changes between two builds never stacks on its own earlier result.
    /// </summary>
    internal static class Layers
    {
        internal const int Format = 1;

        /// <summary>Layer text set while the game runs, by expansion id. It wins over the loose file and the resource.</summary>
        private static readonly Dictionary<string, string> Live = new(StringComparer.Ordinal);

        /// <summary>The code values of each copy before any layer touched them.</summary>
        private static readonly Dictionary<CopyDefinition, CopyPose> Original = new();

        internal readonly struct CopyPose
        {
            public readonly LocalPoint At;
            public readonly Vector3 OntoOffset;
            public readonly Vector3 OntoRotation;
            public readonly float Scale;

            public CopyPose(CopyDefinition d)
            {
                At = d.At;
                OntoOffset = d.OntoOffset;
                OntoRotation = d.OntoRotation;
                Scale = d.Scale;
            }

            public void Restore(CopyDefinition d)
            {
                d.At = At;
                d.OntoOffset = OntoOffset;
                d.OntoRotation = OntoRotation;
                d.Scale = Scale;
            }
        }

        /// <summary>Owner name of the expansion's own copies, as <see cref="World.MainCopies"/> logs it.</summary>
        internal const string ExpansionOwner = "expansion";

        /// <summary>Owner name of a shop's copies in a layer.</summary>
        internal static string ShopOwner(string shopId) => "shop:" + shopId;

        /// <summary>Path of the loose layer file that wins over the embedded one, next to the mods.</summary>
        internal static string LooseFile(string expansionId) => Path.Combine(MelonEnvironment.ModsDirectory, expansionId, "map.json");

        /// <summary>Replaces the layer of an expansion until the game ends; null drops the replacement.</summary>
        internal static void SetLive(string expansionId, string json)
        {
            if (json == null)
            {
                Live.Remove(expansionId);
                Core.Log.Msg($"layer: '{expansionId}' live layer dropped");
                return;
            }
            Live[expansionId] = json;
            Core.Log.Msg($"layer: '{expansionId}' live layer set, {json.Length} characters; it is used from the next world build on");
        }

        /// <summary>The layer text and where it came from, or null when the expansion has none.</summary>
        internal static (string json, string from) Read(ExpansionDefinition def)
        {
            if (Live.TryGetValue(def.Id, out var live)) return (live, "editor");
            var loose = LooseFile(def.Id);
            if (File.Exists(loose))
            {
                Core.Log.Warning($"layer: '{def.Id}' uses the loose file {loose} instead of its embedded layer");
                return (File.ReadAllText(loose), loose);
            }
            var src = def.Layer;
            if (src?.Assembly == null || string.IsNullOrEmpty(src.ResourceName)) return (null, null);
            using var stream = src.Assembly.GetManifestResourceStream(src.ResourceName);
            if (stream == null)
            {
                var names = string.Join(", ", src.Assembly.GetManifestResourceNames());
                Core.Log.Error($"layer: '{def.Id}' resource '{src.ResourceName}' is not in {src.Assembly.GetName().Name} (it has: {names}); the code definition is built alone");
                return (null, null);
            }
            using var reader = new StreamReader(stream);
            return (reader.ReadToEnd(), src.Assembly.GetName().Name + ":" + src.ResourceName);
        }

        /// <summary>
        /// Puts the code values back and lays the current layer over them. Runs before each build of the world.
        /// A broken layer is logged and leaves the code definition as it is.
        /// </summary>
        internal static void Apply(ExpansionDefinition def)
        {
            var copies = AllCopies(def);
            foreach (var (_, _, d) in copies)
            {
                if (Original.TryGetValue(d, out var pose)) pose.Restore(d);
                else Original[d] = new CopyPose(d);
            }

            Layered[def.Id] = new HashSet<(string, string)>();
            var (json, from) = Read(def);
            if (json == null)
            {
                Core.Log.Msg($"layer: '{def.Id}' has no layer; the code definition is built alone");
                return;
            }

            JsonNode root;
            try
            {
                root = JsonNode.Parse(json);
            }
            catch (Exception e)
            {
                Core.Log.Error($"layer: '{def.Id}' layer from {from} is no valid JSON ({e.Message}); the code definition is built alone");
                return;
            }
            int format = root?["format"]?.GetValue<int>() ?? 0;
            if (format != Format)
            {
                Core.Log.Error($"layer: '{def.Id}' layer from {from} has format {format}, this MapExpansions reads {Format}; the code definition is built alone");
                return;
            }

            var layered = new HashSet<(string, string)>();
            Layered[def.Id] = layered;
            int applied = 0, missing = 0;
            if (root["copies"] is JsonArray entries)
            {
                foreach (var e in entries)
                {
                    string owner = e?["owner"]?.GetValue<string>() ?? ExpansionOwner;
                    string id = e?["id"]?.GetValue<string>();
                    var match = copies.FirstOrDefault(c => c.owner == owner && c.def.Id == id);
                    if (match.def == null)
                    {
                        Core.Log.Warning($"layer: '{def.Id}' entry copy '{owner}/{id}' has no copy of that owner and id in the code; it is left out");
                        missing++;
                        continue;
                    }
                    ReadCopy(e, match.def);
                    layered.Add((owner, id));
                    applied++;
                }
            }
            Core.Log.Msg($"layer: '{def.Id}' layer from {from}: {applied} copies replaced, {missing} without a code entry");
        }

        private static void ReadCopy(JsonNode e, CopyDefinition d)
        {
            if (e["at"] is JsonNode at)
                d.At = new LocalPoint(ReadVector(at["pos"]) ?? d.At.Position, at["yaw"]?.GetValue<float>() ?? d.At.Yaw);
            if (ReadVector(e["ontoOffset"]) is Vector3 off) d.OntoOffset = off;
            if (ReadVector(e["ontoRotation"]) is Vector3 rot) d.OntoRotation = rot;
            if (e["scale"] != null) d.Scale = e["scale"].GetValue<float>();
        }

        private static Vector3? ReadVector(JsonNode n)
        {
            if (n is not JsonArray a || a.Count != 3) return null;
            return new Vector3(a[0].GetValue<float>(), a[1].GetValue<float>(), a[2].GetValue<float>());
        }

        /// <summary>Every copy of the definition with the owner name a layer uses for it.</summary>
        internal static List<(string owner, string shop, CopyDefinition def)> AllCopies(ExpansionDefinition def)
        {
            var list = def.Copies.Select(c => (ExpansionOwner, (string)null, c)).ToList();
            foreach (var s in def.Shops)
                list.AddRange(s.Copies.Select(c => (ShopOwner(s.Id), s.Id, c)));
            return list;
        }

        /// <summary>The layer entry of one copy as it stands in the definition now.</summary>
        internal static JsonObject CopyEntry(string owner, CopyDefinition d)
        {
            var o = new JsonObject { ["id"] = d.Id, ["owner"] = owner };
            if (string.IsNullOrEmpty(d.Onto))
                o["at"] = new JsonObject { ["pos"] = Vector(d.At.Position), ["yaw"] = Round(d.At.Yaw) };
            else
            {
                o["ontoOffset"] = Vector(d.OntoOffset);
                o["ontoRotation"] = Vector(d.OntoRotation);
            }
            // Scales come as fractions like 1 / 0.9; three digits would shift a large model by millimetres.
            o["scale"] = MathF.Round(d.Scale, 5);
            return o;
        }

        private static JsonArray Vector(Vector3 v) => new(Round(v.x), Round(v.y), Round(v.z));

        /// <summary>Millimetres and thousandths of a degree; more digits only make the diff noisy.</summary>
        private static float Round(float f) => MathF.Round(f, 3);

        /// <summary>Writes a layer the way the editor saves it: stable order, indented, one entry per copy.</summary>
        internal static string Serialize(string expansionId, IEnumerable<JsonObject> copies)
        {
            var root = new JsonObject
            {
                ["format"] = Format,
                ["expansion"] = expansionId,
                ["copies"] = new JsonArray(copies.OrderBy(c => c["owner"]!.GetValue<string>(), StringComparer.Ordinal)
                    .ThenBy(c => c["id"]!.GetValue<string>(), StringComparer.Ordinal).Cast<JsonNode>().ToArray()),
            };
            // Indented output uses the platform line end; the file keeps one line end everywhere.
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
        }

        /// <summary>Copies the current layer of each expansion holds, as (owner, id); saving writes these again.</summary>
        internal static readonly Dictionary<string, HashSet<(string owner, string id)>> Layered = new(StringComparer.Ordinal);
    }
}
